# 33. Sharding the audit log: measured with Citus, not adopted

## Status
Accepted as a decision **not to shard yet**, with the experiment kept so the decision can be revisited with numbers. Related:
[0001](0001-schema-per-service-shared-database.md) (each service owns its data), [0027](0027-audit-entries-partitioned-by-month.md) (the table
that would be sharded).

## Context
One Postgres holds every service's schema. Splitting by service (ADR 0001) already spreads the load across owners, but a single
table can still outgrow one machine. The audit log is the candidate: it only grows, every event on the bus becomes a row, and
it is written continuously. The question is not "can we shard" but "what does sharding cost this table, and what does it
buy?" Answering it needed a cluster, so the experiment (`docker/citus/`, `scripts/citus-drill.sh`) stands up Citus 14.2 on
Postgres 18 (a coordinator and two workers) beside a plain Postgres of the same settings, loads the **same 3,000,000 audit-shaped
events** into both and runs the same workloads.

The table is distributed by `AggregateId`: one entity's whole history lives on one shard (the time machine's per-entity read
and any "what happened to X" query touch one node), and the key spreads because there are many entities. 32 shards, kept as
the monthly partitions of ADR 0027 inside each shard.

## What was measured
All on one machine, so the three "nodes" share CPU and disk: the figures show the **cost of the architecture, not what more
hardware would give**. Plain Postgres versus Citus (2 workers); `pgbench`, 15-20 s per run:

| Workload | Plain | Citus | |
|---|---|---|---|
| Entity history by `AggregateId` (16 clients) | 4,082 tps, 3.9 ms | 5,962 tps, 2.7 ms | one shard; faster |
| One week across all entities (4 clients) | 114 tps, 35 ms | 53 tps, 76 ms | all 32 shards; slower |
| Whole org up to a date (the time machine, 2 clients) | 9.1 tps, 219 ms | 16.4 tps, 122 ms | all shards, in parallel; faster |
| Insert one entry (16 clients) | 5,829 tps, 2.7 ms | 3,537 tps, 4.5 ms | one extra hop |
| Insert entry **and** its recorded message id in one transaction (16 clients) | 5,826 tps, 2.7 ms | 1,283 tps, 12.5 ms | two shards: a distributed transaction |

Adding a third worker and rebalancing while 16 clients kept reading by entity: **0 failed transactions, no visible dip** in
throughput (5.2k to 6.4k tps across the minute); shards per worker went from 224/224 to 154/154/140. The rebalance ran as a
background job and used `block_writes` mode, so writes to a moving shard wait; writes were not exercised during the move.
A duplicate message id is rejected across the cluster (the message table is distributed by its own id, so its primary key is
enforced on the shard that owns the id).

## Decision
**Do not shard the audit log now.** The one real gain (per-entity reads and the whole-org scan) is modest at this size, and the
costs land exactly on the property ADR 0027 worked hardest to protect:
- **The write that matters gets 4.5 times slower and more fragile.** "Record the message id and the entry in one transaction"
  is what makes the consumer safe under redelivery. With the entry sharded by entity and the id by message, that is a
  distributed (two-phase) transaction: 1.3k against 5.8k tps here, and a new way to fail halfway. Colocating them would need the
  message id inside the entity key, which makes the id no longer globally unique by constraint.
- **Keys must contain the distribution column,** so the primary key becomes `(AggregateId, Id, OccurredAt)` and the time-range
  query that retention and the dashboards use fans out to every shard (53 against 114 tps).
- **The migration is a rewrite** (ADR 0027 already showed how heavy that is) and EF does not model distributed tables: the
  schema would be raw SQL, as the partitions already are.
- **The platform would grow a second kind of database** to operate, back up and fail over, for a table that is a fraction of
  one disk.

**When to revisit:** the table no longer fits one node's disk or write rate (not near; the whole log is 3M rows in the
experiment), or reads by entity become the bottleneck and a read replica ([0025](0025-read-replica-and-read-your-writes.md)) is not enough. The experiment, the schema
and the workloads are kept so the same numbers can be taken at that size. Sharding by a key that already partitions the
workload (a tenant, if the platform becomes multi-tenant) would not have the two-store problem and is the likelier first use.

## Consequences
- Nothing in the services changes; this is a decision plus a reproducible experiment.
- `scripts/citus-drill.sh up|load|bench|scale|down` re-runs it in a few minutes.

## What is and is not verified
Run on this machine: the load, all five workloads on both systems, the rebalance under a read workload, the single-shard plan
for an entity query (one task) and the fan-out for a time range (32 tasks), the duplicate id rejected across the cluster.
Not verified: separate machines (so no scale-out claim either way), writes during a rebalance, the failure of a worker or of
the coordinator (the coordinator is a single point here), a distributed transaction that fails halfway, Citus with the real
migrations and EF, and the effect of the `MessageId` index (the plain table has none either; the experiment compares equal
shapes, not tuned ones).
