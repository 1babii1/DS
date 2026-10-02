# 23. Event contracts: Avro schemas, a registry as the compatibility arbiter, additive-only evolution

## Status
Accepted. Implementation is in steps (see "Rollout"); this ADR records the decision and what each step has verified so far.
Builds on [0002](0002-outbox-pattern-for-integration-events.md) (outbox) and [0003](0003-choreography-saga-for-hire-employee.md)
(choreography). Complements the contract that [`ConsumerContractTests`](../../backend/EventContracts.Tests/ConsumerContractTests.cs)
could only check by name and shape, at one moment in time.

## Context
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
- **Consumers own a reader schema** beside their local record copy; CI checks it against the producer's latest. *(Reader
  schemas and the CI check arrive with the first consumer that has one; AuditService reads whole records and has none.)*
- **One mechanism in the shared consumer base.** `KafkaRetryConsumer` now reads the message value as bytes. A value that
  starts with a zero byte is Avro in the Confluent wire format (JSON never does) and is decoded through the registry into
  the same JSON the handlers have always received (logical types back to Guid text, ISO times, numbers), so a consumer can
  read a JSON topic and an Avro topic with no code of its own, and its record copies and handling do not change. With a
  reader schema for the event, Avro's resolution applies (undeclared fields dropped, missing ones defaulted); without
  one the writer's whole record is returned. A message that can never be decoded (corrupt, not Avro) goes to
  `dead_letters` with its bytes in base64; one that cannot be decoded *yet* (the registry is unreachable, no decoder
  configured) makes the consumer stall on it and ask again, as it already does for a database that is down.
- **AuditService (step 4a):** decodes whole records, stores the JSON and the id of the schema it was written with
  (`entries.SchemaId`, null for JSON), and treats `directory.events.v2` and `directory.events` as one source.
- **Switching a consumer to the Avro topics is configuration:** `EVENT_TOPIC_SUFFIX=.v2` together with
  `SCHEMA_REGISTRY_URL`. A consumer that moves reads the Avro topic from the start; what it already consumed from the JSON
  topic is skipped by message id.
- **AuditService decodes and stores JSON plus the schema version**, so the log outlives the registry and `OrgReplay`
  is unchanged. *(Later steps.)*
- **Migration through new topics `*.events.v2`**, with dual publishing while consumers move, then the old topics and
  the JSON column go. *(Step 3 is dual publishing; the rest is later.)*
  - **Two sides, tracked apart.** A row is owed to the old topic until `ProcessedAt` and to the Avro topic until
    `AvroPublishedAt`. The JSON side is marked done before the Avro side is tried, so a failure on one side never makes
    the other repeat; a row owed only its Avro side comes back on the next poll for just that. Consumers still dedupe
    by message id, so the unavoidable at-least-once duplicate is harmless.
  - **Which rows are owed to Avro.** Only rows written while a registry was configured (`AvroExpected`). Rows written
    before are never sent to the Avro topic, so switching it on does not replay history. A row written while the registry
    was unreachable is owed too: its bytes are made from its stored JSON at publish time, by the same conversion as a
    live event (a test requires identical bytes for all 23 events), and publishing waits for the registry rather than
    skipping the row.
  - **A registry outage delays Avro, never the JSON topics or the write.** The backlog is visible as
    `outbox_avro_pending_messages`.
  - It is off unless `SchemaRegistry:Url` is set (compose: `SCHEMA_REGISTRY_URL`, with `--profile contracts`).
  - The headers (`message-id`, `message-type`, `occurred-at`) and the key are the same on both topics.
- **Avro, not Protobuf or JSON Schema.** Avro's resolution of writer and reader schemas is the mechanism the replay
  needs; the registry ecosystem (including Debezium, roadmap item 9) speaks it natively. gRPC between services stays
  Protobuf: a synchronous contract with generated stubs, a different problem.

## The gate (this step)
`scripts/check_schemas.py`, run in CI as the job "Event schema compatibility": a throwaway registry is started, every
schema released on the base branch is registered in order, then the change's; it fails on
1. a schema the registry refuses (new field without default, changed type, ...);
2. a **removed field**: the registry allows this under BACKWARD (a new reader simply ignores it), but our rule is "never
   remove", so it is checked separately, together with a deleted or renamed schema;
3. a schema outside the producer's folder, a file name that differs from the record name, or the wrong namespace.

It also registers a deliberately breaking schema first and requires the registry to refuse it, so a gate that has stopped
working fails the job instead of passing everything. A test in `EventContracts.Tests` keeps each schema equal to its C#
record: same fields, and "optional in C#" means a union with `null` and a default.

## Consequences
- **Evolution is slower on purpose.** A rename is two releases. That is the price of a log that can always be re-read.
- **The registry is not on the runtime path** in this step, so its failure does not stop a hire. When encoding moves
  into the outbox (a later step) the *schema id* will be needed at write time; that step will state how a registry outage
  is handled (cache of known ids; a producer can only publish schemas it has already registered).
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

Step 3 (dual publishing): the per-row decision is a function of its transport and is unit-tested for both topics, the
Avro side failing after the JSON side succeeded (the JSON is not sent again), a registry that is down, rows from before
Avro (never sent to it), no registry or no Avro topic (JSON only), and a row done on both sides; the pending-rows query
is run by Postgres over rows in every state; the bytes made from stored JSON equal the bytes made from the live event for
all 23 events, optional fields empty and filled; the redrive audit event is encoded; the Avro backlog is counted apart
from the JSON one. Mutation-checked: the JSON side marked before the Avro side, the legacy-row guard, the configured
check, both halves of the pending predicate, two conversions on the JSON path. **Run live** on the local stack: DirectoryService
rebuilt with a registry configured registered its seven schemas under `directory.events.v2-...`, and a row inserted into
its outbox was published to both topics (`ProcessedAt` and `AvroPublishedAt` set, its bytes made at publish time).

Not verified yet: reading the Avro topic with a consumer (the broker needs credentials I did not use; the wire format is
checked by the Confluent deserializer in tests), consumers on reader schemas, AuditService on decoded events, retiring
the old topics, and the other three producers live (same code; only DirectoryService was rebuilt). The registry is
in-memory (a restart forgets it); CI starts a fresh one per run, so what the gate compares against is the base branch's
files, not a persistent registry.
