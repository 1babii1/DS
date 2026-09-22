# 11. Kafka cluster, Debezium CDC, and event contracts: target architecture (not yet implemented)

## Status
Proposed - design only. Kafka runs as the single `apache/kafka:3.9.0` broker in
`docker-compose.yml` today; nothing here is live. Wave 2 of the reliability-roadmap
planning artifact, depends on Wave 1 (Kubernetes) actually landing first - a 3-node KRaft
cluster is a Kubernetes-native ask (StatefulSet + PodDisruptionBudget), not something worth
hand-rolling in docker-compose for a platform at this scale.

## Context
The broker runs with `NumPartitions=1` hardcoded in `KafkaTopicProvisioner` and
`ReplicationFactor=1` - losing that one broker loses every topic's data outright (Wave 0
made the retention explicit, but explicit retention on a single replica is still zero
redundancy). The transactional-outbox path itself (write domain change + outbox row in one
transaction, `OutboxPublisher` polls and marks processed after Kafka acks) is architected
correctly and doesn't need to change; what needs to change is the infrastructure under it
and, separately, how that publish step works.

## Trigger
- A second Kafka broker becomes necessary for any reason (the single broker becomes a
  capacity or availability concern), which is also the point at which running it outside
  Kubernetes stops making sense.
- `OutboxPublisher`'s polling becomes visible in load on the Postgres primary, or a second
  replica of a service means two pollers racing (ADR 0002 explicitly limits this to one
  publisher per service today).
- A consumer and its producer end up owned by different teams/contexts, at which point an
  informally-agreed event shape (today: "each consumer keeps its own local copy of the
  contract") stops being enough.

## Decision

**Kafka 4.2, KRaft, 3 voting nodes, `ReplicationFactor=3` / `min.insync.replicas=2`.**
Kafka 4.0 removed ZooKeeper entirely; KRaft needs a minimum of 3 voters for real quorum
tolerance. RF=3 with `min.insync.replicas=2` is what actually survives losing one broker
without losing an unacknowledged write - `ReplicationFactor=1` today does not survive
losing the only broker at all.

**Debezium's outbox event router replaces `OutboxPublisher`'s poll loop**, reading the same
outbox table each service already writes to via Postgres logical replication (WAL), not a
second write path. This removes the "one poller per service" constraint from ADR 0002 (no
`SKIP LOCKED`-based multi-poller support exists today) and cuts publish latency from
poll-interval to WAL-tail latency. Migrated service by service, and never with both
Debezium and the old `OutboxPublisher` enabled for the same service at once - that
double-publishes every event.

**Tiered storage (KIP-405, GA since Kafka 3.9) for long retention on topics
SearchService's Elasticsearch index rebuilds from.** Wave 0 made retention explicit at
Kafka's own 7-day default; if a full index rebuild from Kafka needs a longer window than
that, tiered storage makes long retention cheap (cold segments move to object storage)
instead of requiring more broker disk.

**Share groups (KIP-932, production-ready in Kafka 4.2) only for consumers that don't need
per-key ordering** - SearchService's indexing consumer, NotificationService's push
consumer, AuditService. Explicitly **not** for anything consuming the hire saga or Rewards
grant events, where per-employee ordering matters; those stay on classic consumer groups,
capped at partition count (and at whatever KEDA's `allowIdleConsumers` setting allows for
autoscaling beyond that, which is off by default for exactly this reason).

**AsyncAPI 3 documents + a schema registry (Apicurio), once a consumer and its producer
cross a team boundary** - not before. Each consumer owning a local copy of the event
contract works today because one person/session can see both sides change together; a
registry with compatibility rules is what catches a breaking change when that's no longer
true. This is the one piece of this wave gated on an organizational trigger, not a
technical one.

## Consequences
- Nothing here is implemented. The single-broker, `RF=1`, `NumPartitions=1` setup from
  Wave 0 (now with explicit `retention.ms`) remains what actually runs.
- Debezium migrated one service at a time is itself a migration with a rollback point per
  service - each service can be reverted to `OutboxPublisher` independently if something
  goes wrong, rather than an all-or-nothing cutover.
- A 3-node Kafka cluster is real infrastructure cost (3x the broker resources of today) -
  justified only once the trigger above actually fires, same principle as every other wave.
