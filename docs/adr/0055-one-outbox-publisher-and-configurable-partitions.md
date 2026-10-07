# 55. One outbox publisher at a time, topics with a configurable number of partitions, and a publisher that does not sleep on a full batch

## Status
Accepted. Follows [0054](0054-several-instances-of-one-service.md). Related: [0002](0002-transactional-outbox.md), [0024](0024-connection-pooling.md),
[0030](0030-change-data-capture-with-debezium.md), [0035](0035-kafka-replication-and-broker-failure.md).

## Context
0054 measured three things about running a service more than once: every instance published the same outbox rows (2.2 to 2.7 messages per row at a
moderate rate), topics could only have one partition, and a publisher moved about ten messages a second.

## Decision
- **One publisher at a time per outbox.** A publishing cycle runs in a transaction that first takes a *transaction-level* advisory lock named by a hash of
  the outbox table (`pg_try_advisory_xact_lock`, `OutboxLeadership`). An instance that does not get it skips the cycle and tries at the next poll.
  The lock is released when the transaction ends, committed or not, so an instance killed mid-cycle (its connection drops) hands over at the next
  poll. It is transaction-level on purpose: a session-level lock, the usual leader-election form, does not work through PgBouncer in transaction
  mode (0024), which is how the services connect.
- **`Kafka:TopicPartitions`** (default 1, as before) sets the partitions a topic is created with, next to the replication factor of 0035. It applies
  when a topic is created; an existing topic keeps its partitions.
- **`Outbox:BatchSize` and `Outbox:PollInterval`** (defaults 20 and 2 s, as before) can be set, and a cycle that found a full batch **goes again at
  once** instead of sleeping, so the pause bounds the latency of a quiet outbox and not the throughput of a busy one.

Alternatives: **claim rows with `FOR UPDATE SKIP LOCKED`** (lets instances publish different rows in parallel, but two instances can then publish two
events of one aggregate out of order, so it needs a per-aggregate guard and an index on the aggregate column, a migration in every service),
**do nothing** (consumers are idempotent; it costs 2.2 times the bus traffic and the consumers' work for each copy), **Debezium** (0030: one connector, no
duplicates; opt-in and more infrastructure, and it does not change the default), and **a session-level advisory lock** (does not survive the pooler).

## Measured
Same drill, three instances of each service, six partitions, 45 s, before and after:

| run | messages on the bus per outbox row | rows unpublished when the load ended / seconds to clear |
|---|---|---|
| before, ~10 grants/s, no kills | x2.70 | 4 / 3 |
| before, ~10 grants/s, kills | x2.32 | 2 / 24 |
| before, ~65 grants/s | x1.00 | 1053 / 40 |
| **after**, ~10 grants/s, no kills | **x1.00** (431 / 431) | 0 / 1 |
| **after**, ~65 grants/s, no kills | **x1.00** (2938 / 2938) | **16 / 1** |
| **after**, ~10 grants/s, kills, 60 s | **x1.00** (573 / 573) | |

In every run after the change the books agreed (each confirmed grant has one row, keys equal rows, each wallet its ledger, the card's copy equal to the
wallet in balance and version), and the six partitions were split two to each of three consumers.

## Consequences
- **One instance publishes, the others wait.** The throughput is that of one publisher, now unthrottled (the heavy run moved 2,938 rows with a backlog of
  16 at the end); a second instance adds availability, not publishing capacity. A publisher stuck in a long cycle delays every instance's events, where
  before the others would at least have published some.
- **Order is simpler, not guaranteed:** one publisher at a time reads the oldest rows first and publishes in that order, so one aggregate's events reach
  the topic in the order they were written; that is what the single publisher gives, it is not a property tested separately.
- **Failover is at most one poll interval (2 s) after the holder's connection ends.** The test shows the lock is released when the cycle's transaction ends;
  the time to take over after a SIGKILL of the holder was not isolated in the drill.
- **A crash after Kafka acknowledged and before the commit still duplicates** those rows (at-least-once, as in 0002); the lock removes the routine
  duplication, not the crash window.
- **`Kafka:TopicPartitions` does nothing for a topic that exists.** Moving one to more partitions is an operation on the broker, as with replication in 0035.
- **The cycle holds a database transaction open while it publishes** (a batch of at most `BatchSize` rows to Kafka). A longer batch holds it longer.

## What is and is not verified
Written test-first: two tests on a real Postgres (a second instance is turned away while one is publishing; when the first ends or dies the next takes
over) and two on the partition setting (default one, read from configuration, passed to the topic specification), each watched failing against a stub
that did nothing. The drill above. The whole backend suite (17 test assemblies) passes except one McpServer test that cannot start its host here because the machine's limit of 128 inotify instances is used up by editor language servers; it fails the same way on a commit that does not have this change. Not verified: the other services' publishers (the code is
shared, only Rewards' was run), a lock holder killed at the moment it publishes, `FOR UPDATE SKIP LOCKED` as the alternative (not built, so not
measured), order of one aggregate's events end to end, and Debezium mode, which does not use this publisher.
