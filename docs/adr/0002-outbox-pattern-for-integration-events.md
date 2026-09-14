# 2. Transactional outbox for cross-service events

## Status
Accepted

## Context
DirectoryService and EmployeeService need to tell the rest of the system when something
happens (`DepartmentCreated`, `EmployeeHired`, ...) so AuditService and NotificationService
can react. The naive approach - write to Postgres, then publish to Kafka - is a dual write:
if the process crashes between the two, or the Kafka publish fails, the event is silently
lost even though the domain change committed successfully.

## Decision
Every domain write that needs to notify the rest of the system also writes an
`OutboxMessage` row in the *same* database transaction (`Shared/Outbox/OutboxMessage.cs`,
written via `IOutboxWriter`). A generic background service (`OutboxPublisher<TContext>`)
polls each service's outbox table, publishes pending rows to Kafka, and only marks a row
processed after Kafka acknowledges the produce. Delivery is at-least-once: a message can
be published more than once (crash between produce and marking processed), so consumers
must dedupe by message id - AuditService does this with a unique constraint on
`MessageId`; NotificationService tolerates duplicates by design (a duplicate log line is
harmless).

Kafka topics are provisioned explicitly via an `AdminClient` before any subscribe or
produce (`KafkaTopicProvisioner`), rather than relying on broker auto-create - a consumer
subscribing before a topic exists can otherwise miss messages for a long time under
librdkafka's metadata-refresh backoff (found and fixed during Phase 3 implementation).

## Consequences
- No dual-write data loss: the domain write and the intent-to-publish are atomic.
- Consumers own deduplication, not the publisher - a deliberate trade-off (at-least-once
  is much simpler to implement correctly than exactly-once).
- A single outbox publisher replica is assumed. Running multiple replicas of the same
  service without a claim/lock mechanism on outbox rows (e.g. `SELECT ... FOR UPDATE SKIP
  LOCKED`) would let two replicas publish the same row concurrently - still safe given
  consumer dedup, but wasteful. Not a concern at current scale (one replica per service),
  noted as a known limitation rather than solved preemptively.
