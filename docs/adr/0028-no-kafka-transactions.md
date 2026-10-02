# 28. Kafka transactions are not used: the outbox and idempotent consumers are the exactly-once story

## Status
Accepted. A decision *not* to build something, written down because it is a standard item on every "exactly-once" checklist and
the reasoning is the point. Builds on [0002](0002-outbox-pattern-for-integration-events.md).

## Context
"Exactly-once" in Kafka means two different things, and the checklist item conflates them:
- **Kafka transactions** (`transactional.id`, `isolation.level=read_committed`): atomic *consume offset commit + produce to
  Kafka topics*. They make a Kafka-to-Kafka stream processor exactly-once.
- **Effectively-once processing**: a message may be *delivered* more than once, but its *effect* happens once.

Every consumer in this platform reads a message and then **writes to Postgres** (a notification, a wallet, an account, an audit
row, a search document). Every producer writes to Postgres and then needs a message sent. Neither is Kafka-to-Kafka.

## Decision
**Do not use Kafka transactions. Get the same guarantee from two patterns that are already here.**

**On the way out, the transactional outbox** ([0002](0002-outbox-pattern-for-integration-events.md)): the event is written to
the service's own database in the same local transaction as the change it describes; a publisher sends it afterwards and marks
it done only after the broker acknowledged. This is at-least-once (a crash between the send and the mark resends), and that is
fine because of the next point.

**On the way in, an idempotent consumer** (an inbox in spirit): the effect of a message is guarded by a constraint in the
consumer's own database, so processing it twice changes nothing the second time. This is what the code does, per consumer:

| Consumer | What makes a redelivery harmless |
|---|---|
| Audit | `recorded_messages` primary key on the message id, written with the entry in one transaction ([0027](0027-audit-entries-partitioned-by-month.md)) |
| Notification | a unique `SourceMessageId` on the notification |
| Rewards (welcome bonus) | a partial unique index per employee and source; the check-then-act `Any()` is only a fast path, the index is the guard (a past 16x duplicate was reproduced with only the check) |
| Auth (account) | a unique `EmployeeId` on the account |
| Employee (provisioning) | the state transition is idempotent by construction |
| Search | the document id is deterministic (`kind:sourceId`), so indexing twice overwrites |

**Why not both, for belt and braces?** Because Kafka transactions cannot cover the part that matters. A Kafka transaction and
a Postgres transaction cannot be made atomic with each other without two-phase commit (XA), which is slow, couples availability
of the two systems and is widely avoided for that reason. What a Kafka transaction would add here is atomicity between "offset
committed" and "something produced to Kafka", and no consumer produces to Kafka directly; the ones that publish do it through
their outbox. A consumer that wrote to Postgres inside a Kafka transaction would still have a window between the two commits,
closed by exactly the same idempotence this design already relies on, so the transaction would buy nothing and cost a
`transactional.id` to manage per instance, producer fencing, and `read_committed` consumers.

## Consequences
- **A consumer's guard must be a constraint, not a check.** The recurring bug in this style is an `Any()` before the insert
  with no database constraint behind it: it passes tests that run one message at a time and duplicates under concurrency
  (Rewards had exactly this, found and fixed). A new consumer needs a unique key on whatever its effect is, and a test that
  delivers the same message concurrently.
- **Processing is at-least-once, so effects must tolerate repeats.** An effect that cannot be made idempotent (sending an email)
  needs its own record of "sent", written before or with the send; none of the consumers here send anything external.
- **Ordering is per key, not global.** Events of one aggregate share a Kafka key and a partition, so they arrive in order; events
  of different aggregates do not, which is why consumers that join two events (the welcome bonus needs the employee and the
  account) tolerate either arriving first.
- **When this would change:** a consumer that reads from Kafka and produces to Kafka without a database in between (a stream
  enrichment step) is the case Kafka transactions are for. If one is added, use them there.

## What is and is not verified
The table above is read from the code (each guard exists as described); the tests that deliver duplicates and concurrent
duplicates are in each service's consumer tests (Audit, Notification, Rewards, Auth). Not verified here: that *every* consumer test delivers the same message *concurrently*. Rewards and Audit do (Audit's was
added with this ADR, and was checked to fail when the recorded id is not written); Notification, Auth and Employee deliver it
twice in sequence, which is the gap the second bullet of "Consequences" is about.
