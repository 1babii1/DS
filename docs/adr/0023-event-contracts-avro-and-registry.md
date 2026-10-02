# 23. Event contracts: Avro schemas, a registry as the compatibility arbiter, additive-only evolution

## Status
Accepted and implemented (steps 1-5). This ADR records the decision and what has been verified.
Builds on [0002](0002-outbox-pattern-for-integration-events.md) (outbox) and [0003](0003-choreography-saga-for-hire-employee.md)
(choreography). Complements the contract that [`ConsumerContractTests`](../../backend/EventContracts.Tests/ConsumerContractTests.cs)
could only check by name and shape, at one moment in time.

## Context
How to change an event day to day: [the runbook](../runbooks/event-schema-evolution.md).

Twenty-two event types cross four Kafka topics as JSON. A consumer keeps its own local copy of each record, on purpose,
so services deploy independently. What was missing was *time*: nothing said whether today's event could still be read by
yesterday's consumer, or yesterday's stored event by tomorrow's code. The second matters here more than usual,
because AuditService keeps every event and the org time machine ([0019](0019-org-time-machine-from-the-event-log.md))
replays the whole history through the current code. An incompatible change would not fail in CI; it would silently
mis-fold last March.

## Decision
- **Avro**, with the schema of each event in a `.avsc` file next to the record that publishes it
  (`.../IntegrationEvents/Schemas/`). The file in git is the source of truth; a registry is the arbiter, not the source.
- **A schema registry (Apicurio, through its Confluent-compatible API)** decides compatibility. Subjects are
  `<topic>-<namespace>.<Record>` (the Confluent client's TopicRecordName strategy), e.g.
  `employee.events.v2-ds.employee.EmployeeHired`, so each event type in a shared topic evolves on its own. Compatibility
  is `BACKWARD_TRANSITIVE`: any new version must be able to read every earlier one, which is exactly what the replay needs.
- **Additive-only evolution. Breaking changes are not allowed; they are replaced by add and deprecate.**
  - *Removing a field:* the field stays in the schema, marked `"doc": "Deprecated"`, with a default; new code stops
    filling it. Old readers keep seeing it (as its default), new ones ignore it.
  - *Renaming a field:* add the new field, make the old optional, fill both for a while, then stop using the old one.
  - *Changing a type:* widen it with a union (`["int","string"]`) for the transition.
  - New fields always have a default, usually `null` through `["null", T]`.
- **The encoding happens when the outbox row is written**, in the same transaction; the publisher (and later a CDC
  connector) relays bytes without changing them. The outbox row gains a nullable `AvroPayload` (bytea) next to the JSON
  `Payload`, which stays during the migration. The encoder is **synchronous** (it runs where the row is staged, inside
  the domain transaction) and can only use schema ids it already knows: a background warm-up registers the service's
  schemas, retrying until the registry answers, and until it has, `TryEncode` says no and the row carries only the JSON.
  So a registry outage never fails a hire or a grant; it only delays Avro. The publishing step encodes such rows from
  their JSON when it publishes. It is off unless `SchemaRegistry:Url` is configured. *(Step 2; publishing is step 3.)*
- **Consumers own a reader schema** beside their local record copy (`.../Consumers/Schemas/`, embedded in the consumer's
  assembly), declaring exactly the fields that consumer's record has. CI asks the registry whether each reader schema can
  read what the producer's latest schema writes, so a reader that needs a field the producer does not have (and has no
  default for) fails the build. AuditService reads whole records and has none.
- **One mechanism in the shared consumer base.** `KafkaRetryConsumer` now reads the message value as bytes. A value that
  starts with a zero byte is Avro in the Confluent wire format (JSON never does) and is decoded through the registry into
  the same JSON the handlers have always received (logical types back to Guid text, ISO times, numbers), so a consumer can
  read a JSON topic and an Avro topic with no code of its own, and its record copies and handling do not change. With a
  reader schema for the event, Avro's resolution applies (undeclared fields dropped, missing ones defaulted); without
  one the writer's whole record is returned. A message that can never be decoded (corrupt, not Avro) goes to
  `dead_letters` with its bytes in base64; one that cannot be decoded *yet* (the registry is unreachable, no decoder
  configured) makes the consumer stall on it and ask again, as it already does for a database that is down.
- **All six consumers (steps 4a and 4b):** Audit, Notification, Search, Rewards, Auth and Employee all go through the shared
  decoder; the five with record copies have 17 reader schemas between them, one per event they consume. A test keeps each
  reader schema equal to its record (same fields, same optionality) and runs the real path for every one: the producer's
  bytes, decoded with the consumer's own reader schema, deserialized into the consumer's own record, with only the
  declared fields coming through.
- **AuditService (step 4a):** decodes whole records, stores the JSON and the id of the schema it was written with
  (`entries.SchemaId`, null for JSON), and treats `directory.events.v2` and `directory.events` as one source.
- **Migration through new topics `*.events.v2`, then only those.** Step 3 published to both the JSON topic and the Avro
  one, tracked apart per outbox row; step 4 moved the six consumers over; step 5 (this state) publishes **only** the Avro
  topics and the consumers read only those. The old `*.events` topics are no longer written, read or provisioned and
  expire on their own retention. The two bookkeeping columns of the dual phase were dropped again (one migration per
  producer); the history of how it was done is in the commit log.
  - **A row is sent as the bytes staged when it was written** or, if the registry was unreachable then, **made from its
    stored JSON when it is published**, by the same conversion as a live event (a test requires identical bytes for all
    23 events). The JSON `Payload` therefore stays in the outbox: it is the readable record of what was written and the
    source for that case. (The first plan said to drop the JSON column; keeping it is what lets a registry outage delay
    publishing without failing a hire or a grant.)
  - **Without a registry the publisher refuses to start** ("SchemaRegistry:Url must be set"); there is no JSON fallback.
  - **A registry that forgets is worse than none.** Every message carries a schema id; an empty registry makes everything
    already in Kafka unreadable. So the registry stores in Postgres (its own `registry` database on the shared server,
    created by a one-shot job because `init-databases.sql` only runs on a fresh volume) and a restart was checked to keep
    the same 28 subjects and the same ids. It is part of the default stack now, not an opt-in profile.
  - The headers (`message-id`, `message-type`, `occurred-at`) and the key are what they always were.
- **Avro, not Protobuf or JSON Schema.** Avro's resolution of writer and reader schemas is the mechanism the replay
  needs; the registry ecosystem (including Debezium, roadmap item 9) speaks it natively. gRPC between services stays
  Protobuf: a synchronous contract with generated stubs, a different problem.

## The gate
`scripts/check_schemas.py`, run in CI as the job "Event schema compatibility": a throwaway registry is started, every
schema released on the base branch is registered in order, then the change's; it fails on
1. a schema the registry refuses (new field without default, changed type, ...);
2. a **removed field**: the registry allows this under BACKWARD (a new reader simply ignores it), but our rule is "never
   remove", so it is checked separately, together with a deleted or renamed schema;
3. a schema outside the producer's folder, a file name that differs from the record name, or the wrong namespace.

It also registers a deliberately breaking schema first and requires the registry to refuse it, so a gate that has stopped
working fails the job instead of passing everything. The services also set `BACKWARD_TRANSITIVE` on each subject themselves when they register, so the registry refuses an
incompatible schema at runtime too, not only in CI. A test in `EventContracts.Tests` keeps each schema equal to its C#
record: same fields, and "optional in C#" means a union with `null` and a default.

## Consequences
- **Evolution is slower on purpose.** A rename is two releases. That is the price of a log that can always be re-read.
- **The registry is on the publishing path, not on the writing path.** A hire or a grant is committed without it (the
  bytes are staged only when the schema ids are already known); publishing waits for it, so an outage delays events
  and shows up as a growing pending count, and consumers wait on a message they cannot decode yet. It must be kept
  (hence the Postgres storage) and backed up with the rest of the data: lose it and the history in Kafka stops being
  readable, though the audit log, which stores JSON, is unaffected.
- **A schema id the registry does not know** (the registry was rebuilt empty, a message from another environment) is
  an answer, not an outage: that message is set aside in `dead_letters` with its bytes, and the consumer moves on.
- **Deprecated fields accumulate.** Nothing removes them; a schema that has grown too long needs a deliberate, separate
  decision (a new event type), not an edit.
- **Avro on a `decimal`:** amounts use the `decimal` logical type, precision 18, scale 2, matching `numeric(18,2)`.
  `DateTimeOffset` is carried as the instant (`timestamp-millis`); the offset the producer held is not.

## What is and is not verified
Spike: the .NET Confluent Avro serializer and deserializer round-trip a record against Apicurio 3.0.7 through
`/apis/ccompat/v7` with pre-registered schemas (magic byte and schema id on the wire); the registry refuses a field added
without a default (HTTP 409). The gate was mutation-checked: a required field added, a field removed, a type changed, a
schema deleted are each rejected; an additive optional field passes. The schema/record test fails on a renamed field, a
flipped optionality, a missing schema and an orphan schema file.

Step 2 (encoding, nothing published yet): every one of the 22 events is encoded by the code the outbox writers use and
decoded by the real Confluent deserializer against a real registry, with optional fields both empty and filled; the schema
id in the bytes is the one registered under `<topic>-<namespace>.<Record>`; nothing is encoded before the schemas are
registered; money comes back exactly whatever scale it was written with and more than two decimals is refused, not
rounded; each producing assembly carries its schemas as embedded resources; each of the four outbox writers stores the
Avro bytes next to the JSON when the encoder is ready and only the JSON when it is not (mutation-checked: scale padding,
id byte order, the refusal, the attach in each writer, the embedded resources). The four migrations are generated and add
one nullable column.

A finding worth keeping: the Avro .NET writer applies logical types itself and expects their natural CLR values (`Guid`,
`DateTime`, `AvroDecimal`), not the base representation; passing the base values fails at write time with a cast error.

`OutboxMessageRedriven` (written by every service when an operator redrives a parked message) goes through the same outbox
and now has a schema, owned by the shared code (`ds.ops`), embedded in every service's catalog, and a record of its own
instead of an anonymous object.

Step 3 (dual publishing, since retired by step 5): the per-row decision is a function of its transport and is unit-tested for both topics, the
Avro side failing after the JSON side succeeded (the JSON is not sent again), a registry that is down, rows from before
Avro (never sent to it), no registry or no Avro topic (JSON only), and a row done on both sides; the pending-rows query
is run by Postgres over rows in every state; the bytes made from stored JSON equal the bytes made from the live event for
all 23 events, optional fields empty and filled; the redrive audit event is encoded; the Avro backlog is counted apart
from the JSON one. Mutation-checked: the JSON side marked before the Avro side, the legacy-row guard, the configured
check, both halves of the pending predicate, two conversions on the JSON path. **Run live** on the local stack: DirectoryService
rebuilt with a registry configured registered its seven schemas under `directory.events.v2-...`, and a row inserted into
its outbox was published to both topics (`ProcessedAt` and `AvroPublishedAt` set, its bytes made at publish time).

Step 4b, run live: the whole local stack rebuilt with the registry and the `.v2` topics on. By accident the registry
container was down when the services came up (it is in-memory and had exited): the consumers logged "could not decode,
will try again", the publishers kept the Avro side owed, nothing was dead-lettered, and when the registry was started again
the services registered their 28 subjects by themselves and an `EmployeeTerminated` row inserted into Employee's outbox
reached the audit log through the Avro topic (`SchemaId` set, `AvroPublishedAt` set). That is a real, if unplanned, outage
drill, not a test; it did not cover the case where a registry comes back *empty* after consumers had cached schema ids
(the services here were new, so they had no stale ids).

Not verified yet: a registry that restarts empty under running services (their cached schema ids would be stale), reading
the Avro topics in each consumer with real domain events (only a harmless `EmployeeTerminated` was sent, not a hire),
retiring the old topics (step 5). The registry is in-memory (a restart forgets it); CI starts a fresh one per run, so what
the gate compares against is the base branch's files, not a persistent registry.

Step 5 (Avro only): the publisher sends bytes staged at write time or made from the stored JSON, marks a row done only
after the send, and a broker or registry failure leaves it pending (unit-tested); an unknown schema id is set aside while
a registry that is down or erroring is waited out (mutation-checked). **Run live:** the registry rebuilt on Postgres, the
whole stack rebuilt with only the Avro topics, an event inserted into Employee's outbox reached the audit log with its
schema id, nothing was dead-lettered, and a restart of the registry kept all 28 subjects and the same ids.

Not verified in step 5: a real hire end to end through every consumer (it needs a signed-in session, and a hire would
create an account and a bonus for a person who does not exist); stale messages from the in-memory registry era that were
left on the dev topics (their ids may now point at other schemas: development data only); behaviour on a registry that
loses its database.

Step 6 (the claim itself): through the real registry, encoder and decoder, an event written under version 1 is read by a
schema three versions later (the new fields take their defaults), an event written under version 3 is read by a consumer
still on version 1 (only its declared fields come through), every version in a history is read by the latest reader, and a
required field added to an event is refused by the registry with a conflict. The registry's refusal depends on the
compatibility rule the service sets on each subject at registration; removing that line fails the test.
