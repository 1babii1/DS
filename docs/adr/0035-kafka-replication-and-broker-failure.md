# 35. Kafka replication: three copies, two in sync, and what a broker failure costs

## Status
Accepted. Related: [0002](0002-transactional-outbox.md) (the publisher this configures), [0028](0028-no-kafka-transactions.md)
(why duplicates are handled by the consumers), [0030](0030-change-data-capture-with-debezium.md) (the other producer).

## Context
The stack runs one Kafka broker. That is right for development and wrong as a statement about the design: the outbox
(0002) promises that a committed change reaches the bus, and a bus on one machine keeps that promise only until the machine
goes. Two things decide whether a broker failure loses or doubles events, and neither was set explicitly:
- **How many copies a topic has, and how many must have a write before it counts.** The provisioner created every topic with one
  replica, with no minimum.
- **What the producer waits for and whether it retries safely.** The publisher used the client's defaults.

## Decision
- **Topics are created with a configurable replication factor and `min.insync.replicas`** (`Kafka:TopicReplicationFactor`,
  `Kafka:TopicMinInsyncReplicas`). The defaults stay 1 and unset, so a single development broker keeps working and is not given a
  rule it cannot meet. Against a cluster the values are 3 and 2: a write is accepted only when two copies hold it, and a second
  failure makes the topic refuse writes rather than accept them on one machine.
- **The outbox producer sets `acks=all` and `enable.idempotence=true` explicitly.** `acks=all` means "every in-sync replica has
  it", which with `min.insync.replicas=2` is at least two. Idempotence makes the broker recognise a retried batch, so a lost
  acknowledgement does not become a duplicate. Both are tested as configuration, because nothing else would notice a regression.
- **A reference cluster for the drill:** `docker/kafka-ha/` (three KRaft nodes, each broker and controller; default RF 3,
  `min.insync.replicas=2`, unclean leader election off) and `scripts/kafka-ha-drill.sh`. It is separate from the main stack,
  which keeps one broker with SASL.
- **Debezium's connector** produces through Kafka Connect, whose producer is configured by the Connect worker, not by this code;
  it is not covered by the change above.

## What was measured
Three brokers, one partition, RF 3. Killing the partition leader (`docker kill`) while a producer sent 4,000 numbered messages
a few milliseconds apart, then reading the topic from the beginning:

| Producer and topic | Leader killed mid-stream |
|---|---|
| `acks=all`, idempotent, `min.insync.replicas=2` | 4000 of 4000 arrived, 0 missing, 0 duplicates; producer paused about one second for the election |
| `acks=1`, no idempotence, `min.insync.replicas=1` (three runs) | 4000 of 4000 arrived, 0 missing, 0 duplicates in each |

The honest reading of the second row: **a leader failure alone did not lose acknowledged writes even with `acks=1`**, because the
followers copy within milliseconds and the one that became leader already had everything. The settings are not what that
case needs. They matter in the case that follows.

**The case that does lose data: the copies disappear first, the leader after.** Both followers killed, 1,000 more writes sent to
the lone leader, then the leader killed and the followers restarted:

| | Acknowledged in the phase with the leader alone | In the topic afterwards (of 2,000 sent) |
|---|---|---|
| `acks=1`, `min.insync.replicas=1` | 1,000 | **1,000: the 1,000 acknowledged writes are gone** |
| `acks=all`, `min.insync.replicas=2` | 0 (the leader refused) | 1,000: everything acknowledged survived |

That is the whole argument for the two numbers: the cluster that accepts a write on a single machine tells the producer it is
safe, and the next failure proves it was not. The safe configuration turns that into an error the outbox can retry, which it
does, because the row stays unprocessed until the publish is acknowledged.

## Consequences
- **A second broker failure stops publishing** for the affected topics (availability is given up for durability on purpose). The
  outbox keeps accumulating committed rows meanwhile and publishes when a second copy returns: delivery is late, not lost.
  Consumers read from the remaining in-sync copy where one exists.
- **Write latency rises** by a replica round trip. Not measured here (the drill is about loss, and all three brokers share one machine).
- **Existing topics keep their old replica count.** Settings apply at creation; moving a live single-replica topic to RF 3 is a
  partition reassignment, which this change does not do.
- **Duplicates are still possible downstream of the producer** (a consumer reprocessing after a crash): that is what idempotent
  consumers are for (0028). Producer idempotence only closes the producer's own retry.

## What is and is not verified
Run against the real cluster: the leader-kill table above (one safe run, three weak runs) and the two shrink runs (one each).
Unit tests for the configuration: a single broker gets RF 1 and no minimum, the cluster settings give 3 and 2, they are read from
configuration, the outbox producer has `acks=all` and idempotence on. Mutation-checked: `acks=1`, a hard-coded replication factor.

Not verified: the platform's services running against the cluster (their SASL setup, the `Kafka__` settings in compose, and the
consumers' behaviour across a leader change were not exercised; the drill used console tools and the perf-test); more than one
partition; the producer's behaviour with `delivery.timeout.ms` shorter than an election; Debezium on the cluster; the latency cost.
