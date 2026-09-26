# 7. Elasticsearch-backed cross-service search

## Status
Accepted

## Context
Issue #99 asked for one authenticated `GET /api/search` covering employees, departments,
positions, locations, and audit history, so the frontend's workspace search could stop
being limited to the fixed navigation map. The issue itself ruled out the obvious naive
approach - fanning a per-keystroke query out to every backend service live - as
"incomplete, expensive, and can bypass the intended service-boundary and permission
decisions," and asked for an indexed read model with a documented index strategy instead
of a full-table scan.

Two decisions were needed: where the read model that backs search actually lives, and how
it matches/ranks text.

## Decision

**Materialize, don't fan out** - the same principle ADR 0004 (no API Gateway aggregation)
already anticipated: "solve it then, informed by what that page actually needs." This is
exactly the evidence ADR 0004 said would justify revisiting that stance, and the backbone
for it already existed: every write-side service publishes domain events through the same
outbox → Kafka pattern (ADR 0002), and denormalized read models built by consuming those
events were already used three times in this codebase (`Employee.DepartmentName`,
`RewardsService.Wallet`, `NotificationService.AccountLookup`). `SearchService`'s
`DomainEventsConsumer` is a fifth independent consumer group on the same topics
Audit/Rewards/Notification already read (`directory.events`, `employee.events`,
`auth.events`, `rewards.events`) - structurally identical retry/dead-letter machinery, just
building a search index instead of an audit log or a notification feed.

Two entities - Position and Location - published no domain events at all before this work;
`PositionCreated`/`LocationCreated` were added first (mirroring `DepartmentCreatedEvent`'s
shape) specifically so all five kinds could be indexed the same way, rather than shipping a
partial 3-of-5 search.

**Elasticsearch, not hand-rolled Postgres `pg_trgm`/`tsvector`.** The issue's own ranking
requirement - "exact match, prefix match, then token/substring match; stable tie-breakers"
- is exactly what Elasticsearch's `search_as_you_type` field type plus a `multi_match`
query with `type: bool_prefix` was built for: mapping `Title` that way generates
`Title._2gram`/`Title._3gram` subfields that make the required ranking fall out of one
query, instead of a hand-written `CASE WHEN` ranking expression re-implementing what a
search engine already does. Verified directly: an integration test asserts an exact match
ranks at or above a prefix match, which ranks above a plain substring match, against a real
(Testcontainers) Elasticsearch instance.

This is a different, complementary technique from McpServer's existing pgvector semantic
search over departments (`search_departments`, `DirectoryService`'s `DepartmentEmbedding` +
Ollama) - that tool answers "what's conceptually similar," meant for an LLM client
composing a natural-language query; this endpoint answers "what matches these keystrokes,"
meant for a human typing into a search box. Neither replaces the other, and this work
leaves the embedding pipeline untouched.

**One unified index, one document shape (`SearchDocument`), a `Kind` discriminator** -
`employee`/`department`/`position`/`location`/`audit` - rather than five separate indices
or five separate C# document types. Every kind shares the same queryable shape (`Title`,
`Subtitle`, `SearchText`, `IsActive`, `OccurredAt`), which is also what makes one
`multi_match` query answer "all kinds" or "these specific kinds" (a `terms` filter on
`Kind`) without five separate round trips.

**Every Kafka message becomes an audit-kind document, deliberately without its payload** -
just `EventType`/`SourceService` (derived from the topic name)/`AggregateId`/`OccurredAt`.
This is intentionally a *separate*, smaller copy of what `AuditService` already persists,
not a query against `audit.entries` directly: `SearchService` doesn't read another
service's schema (ADR 0001 - schema-per-service, no cross-schema reads), and re-deriving
from the same event stream it's already consuming for the entity kinds costs nothing extra
that a live cross-service audit query wouldn't also cost. Deliberately no `Payload` in the
document: search must never surface an internal event body a caller couldn't otherwise see.

**Document `_id` is deterministic, not random** - `"{kind}:{sourceId}"` for entity documents,
the Kafka `message-id` for audit documents. Indexing twice with the same `_id` is an upsert
by construction, which is what makes Kafka's at-least-once redelivery safe here *without* a
manual "already processed" check - the one place in this codebase Elasticsearch's own
semantics replace the Postgres-unique-index idempotency pattern used by every other
consumer (`DeadLetterEntry.MessageId`, `Notification.SourceMessageId`, etc.). Dead-letter
tracking for messages that exhaust every retry still lives in a small Postgres `search`
schema (one table) - a consumer's record of its own processing failures is operational
bookkeeping, not part of the search index itself.

**`[Authorize]` only, no finer-grained per-result authorization.** The issue asks that "a
result from an unauthorized source is not discoverable through search," but no record-level
ACL system exists anywhere in this codebase today - every other list/get endpoint
(`GET /api/employees`, `GET /api/departments`, ...) is authorized at the endpoint level
only, any authenticated caller sees every record. Matching that granularity here is a
documented decision, not an oversight: inventing per-record authorization for search alone,
when nothing else in the platform has it, would create an inconsistency search couldn't
actually close on its own.

**503, not partial results, on an Elasticsearch outage.** The issue asks to "define whether
a downstream outage returns an all-or-nothing 503 or an explicitly marked partial result."
That question assumes a fan-out design with several independent downstreams that can fail
separately - exactly what materializing into one index avoids. With one unified index,
there is only one dependency to be down; `Error.Unavailable` (503) is the accurate answer,
not an invented "partial" case with nothing to be partial about.

## Consequences
- A new infrastructure dependency (Elasticsearch) exists in this stack for the first time,
  single-node and unsecured (`xpack.security.enabled=false`) for local/dev use - the same
  posture already accepted for Kafka's SASL/PLAIN setup in this project, not a production
  security stance.
- `CurrencyGranted`/`EmployeeTransferred` arriving before the `AccountProvisioned`/
  `DepartmentCreated`/`PositionCreated` event it depends on for name resolution finds no
  match yet and is indexed without it (or, for the employee-lookup case, skipped) - the same
  documented event-ordering caveat as ADR 0006's `NotificationService.AccountLookup`.
- Building and deploying this surfaced two real environment bugs worth recording since
  they'll recur for any future Elasticsearch work in this repo: (1) `Testcontainers.
  Elasticsearch` and an older-pinned `Testcontainers.PostgreSql` load incompatible versions
  of the shared `DotNet.Testcontainers` core assembly if not kept on the same release train
  - central package management now pins both together; (2) Elasticsearch's default disk
  watermarks (85%/90%/95%) refuse shard allocation on dev machines that routinely sit above
  that just from build caches and other containers' volumes, with a cluster status of "red"
  and no log line pointing at "disk usage" - both the docker-compose service and any future
  Testcontainers-based Elasticsearch instance need these relaxed explicitly for a dev/test
  single-node deployment.
- Department `Path` (the ltree hierarchy) isn't in search results - `DepartmentCreatedEvent`
  doesn't carry it and `DepartmentMovedEvent` doesn't carry a new one either; adding it would
  mean changing an event three existing consumers already depend on, a larger decision this
  issue didn't need to make. `Identifier` is the department subtitle instead.
- Frontend search UI (debounced input, grouped results, route mapping) is explicitly out of
  scope - this work is backend-complete and was verified live end-to-end through nginx with
  a real admin JWT (a fresh write generating a Kafka event, indexed, and retrievable via
  search within moments, with the documented exact/prefix/substring ranking order holding).
