# 27. The audit log, partitioned by month

## Status
Accepted. Related: [0019](0019-org-time-machine-from-the-event-log.md) (the time machine reads this table),
[0023](0023-event-contracts-avro-and-registry.md) (the log stores JSON, so it outlives the registry).

## Context
`audit.entries` only grows: every event on the bus becomes a row, forever, and its rows are written and read by time. A table
that only grows is a table whose maintenance (vacuum, index rebuilds, backups) and any thought of retention grow with it as a
single object. Partitioning by event time turns "the log" into a set of months that can be maintained, moved or dropped one at a
time.

## Decision
**`audit.entries` is partitioned by `RANGE ("OccurredAt")`, one partition per UTC month,** plus a default partition that
catches anything outside every month, so an insert never fails for want of a partition. A background service on every instance
keeps the current month and three ahead (`AuditPartitions:MonthsAhead`) and is idempotent, so two instances doing it at once is
harmless. Creating a month that already has rows in the default partition moves them first (Postgres refuses otherwise), all in
one transaction.

**Retention is available and off.** `AuditPartitions:RetentionMonths` > 0 drops whole partitions wholly before the cutoff
(detach, then drop). Nothing sets it. See the trade-off below before anyone does.

**The one thing partitioning took away, and where it went.** A unique index on a partitioned table has to contain the
partition key, so `MessageId` can no longer be unique on `entries`, and the primary key becomes `(Id, OccurredAt)`. "This Kafka
message is recorded once" is the property that makes the consumer safe under redelivery, so it moved to a small unpartitioned
table, `audit.recorded_messages` (a uuid and a time), written **in the same transaction as the entry**. It also survives
retention: dropping old entry partitions leaves the ids, so an old message redelivered is still recognized.

**The migration** rebuilds the table (EF cannot express a partitioned table): the generated operations change the key and
create `recorded_messages`; added to that, by hand, SQL backfills the ids, renames the old table aside, creates the partitioned
one with its indexes, creates a default partition and the months from the oldest event through three ahead, copies the rows and
drops the old table. This is an exception to "migrations are generated": the generated part is kept, the part EF cannot say is
raw SQL, and the migration's down path restores a plain table. It takes an exclusive lock for the length of the copy: fine for
this log, the thing to plan around for a large one.

## Consequences
- **Retention shortens the time machine.** The org time machine folds the log from its start; drop the oldest partitions and
  "the org in March" is answerable only from whatever survived. Retention is therefore a decision about how far back history
  matters, not a housekeeping switch, and doing it safely would need a snapshot of the folded state at the cutoff (not built).
- **Range queries on event time prune partitions**: a query for one month reads one partition (checked with `EXPLAIN`). The
  time machine's own query (everything up to a date) reads every partition up to that date, so it gains nothing from pruning:
  partitioning here pays for maintenance and retention, not for replay speed.
- **Time zone:** partition bounds are UTC instants (`+00`). An event one second into 1 March UTC belongs to March whatever the
  session's time zone; a test sets a different one to prove it.
- **`recorded_messages` grows forever** (16 bytes plus a time per message). That is the price of idempotency that outlives
  retention; it is small and could itself be pruned by age once redelivery of that old a message is impossible (retention of
  the broker), which is not done.
- **The seed script for the demo history** (`scripts/org-history`) had to learn the new shape: it relied on the unique index.

## What is and is not verified
Against a real Postgres, each case in its own container: the migration moves every row into the right month and records every
message; partitions exist from the first event through three months ahead with UTC bounds (checked under another session time
zone); a message id is recorded once across partitions (a second insert fails); an event outside every partition lands in the
default and moves when its month is created, idempotently; a range query reads only its partition (`EXPLAIN`); retention drops
whole old months, keeps a month that is not wholly before the cutoff and leaves the recorded ids; migrating back restores one
plain table with every row. Mutation-checked: no id recorded (duplicates appear), the default-partition move removed, the
retention boundary off by a month. **Run live:** the migration on the dev stack's audit schema (974 rows moved, 790 into
September and 184 into October, 974 ids recorded, nothing in the default partition) and a new event written into the right
partition afterwards.

Not verified: the migration on a log of real size (time and lock); two instances maintaining partitions at the same instant;
retention in production use (nothing enables it); performance, which was not measured at all.
