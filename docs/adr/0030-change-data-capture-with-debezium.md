# 30. Change data capture with Debezium instead of the polling publisher

## Status
Accepted as an opt-in mode (`OUTBOX_MODE=Cdc`, `--profile cdc`); polling stays the default. Builds on
[0002](0002-outbox-pattern-for-integration-events.md) (outbox) and [0023](0023-event-contracts-avro-and-registry.md) (the outbox
row already carries the event as Avro bytes, which is what made this possible).

## Context
The outbox publisher polls its table every two seconds. That costs a query per service per poll when nothing happens, and it puts
a floor under delivery latency (an event waits on average one second for the next poll). Change data capture reads the database's
own write-ahead log instead: an insert is seen as it commits, and nothing polls.

## Decision
**Debezium reads each producing service's outbox table from the WAL and produces the row's Avro bytes to the service's
`*.events.v2` topic.** One connector per service (four), each with a logical replication slot and a publication of that one
table, using Debezium's `EventRouter` transform:
- the row's `AvroPayload` column (already the Confluent wire format, written by the service in the same transaction as the
  change) is the Kafka value, passed through byte for byte (`ByteArrayConverter`); no second schema registry round trip, no
  second encoding;
- the key is the row's `AggregateId`; the headers `message-id`, `message-type` and `occurred-at` come from the row's
  `Id`, `Type` and `OccurredAt` columns, the same envelope the polling publisher sent, so no consumer changes.

**In CDC mode the service publishes nothing itself and the write path gets stricter.**
- A row can only leave as the bytes staged when it was written: Debezium cannot make them from the JSON the way the polling
  publisher can. So staging must succeed or the write fails (`CdcEventAvroEncoder`). The schema ids are cached in memory once
  the registry has been reached, so this only bites a service that has not yet reached the registry since it started: the
  availability coupling is narrower than "the registry must be up", and real.
- No publisher marks rows, so the writer marks the row delivered as it inserts it, and an `OutboxCleaner` deletes delivered rows
  older than a day (the log, not the table, is Debezium's source, so deleting is safe).
- **Never both modes for one service**: every event would be sent twice. Switching back means deleting the connectors **and
  dropping the replication slots** (see consequences).

## Measured
`scripts/delivery-latency.sh` after the k6 hire chain: time from an outbox row being written to the audit log having stored it
(the whole path, publish and consume).

| Mode | Events | p50 | p95 | max |
|---|---|---|---|---|
| polling (2 s interval) | 20 | 1 022 ms | 1 672 ms | 1 924 ms |
| CDC | 32 | 670 ms | 811 ms | 833 ms |

Polling's median is what the interval predicts (about half of 2 s plus the consumer); CDC removes the interval, and what is left
(about 0.6 s) is Debezium's own batching plus Kafka and the consumer. A sample of tens of events from one run each, on one
machine: the direction and the rough size are real, the exact milliseconds are not.

## Consequences
- **An inactive replication slot retains WAL without limit.** If a connector is stopped, or Connect is down for a day, Postgres
  keeps every segment since the slot's position and the disk fills. A production use needs `max_slot_wal_keep_size` and an alert
  on `pg_replication_slots` lag; neither is set. Removing a connector does not remove its slot: switching back to polling
  requires `pg_drop_replication_slot('debezium_<service>')` (done by hand in the run recorded here).
- **`wal_level=logical`** is now set on the primary for everyone (compose), whether or not CDC is on. A physical standby works
  with it (checked: the replica of ADR 0025 is unaffected by the setting).
- **Debezium is another failure domain**: Connect and its three internal topics, four connectors, one more thing to monitor.
  Connectors that fail stay failed until restarted; nothing here restarts them or alerts.
- **A row with no Avro bytes can never be delivered in this mode** (and the write that would make one is refused), so rows
  written by the polling mode before the switch, which may lack bytes, are not sent: the connectors start with
  `snapshot.mode=no_data` and only see new inserts.
- **Redrive and parked rows do not apply** in CDC mode (the connector retries, not the service).
- **Two things found by running it, kept here because the symptoms are cryptic:** `EventRouter` requires its timestamp field to be
  INT64 (a `timestamptz` column fails with "Field 'OccurredAt' is not of type INT64", so `OccurredAt` goes in a header and the
  record timestamp is the source's), and a heartbeat record is not an outbox event and reaches the byte converter as a struct, so
  heartbeats are off.

## What is and is not verified
Run live: Postgres on `wal_level=logical`; Kafka Connect with SASL to the broker; four connectors RUNNING with their slots; the
four producers in CDC mode; the k6 hire chain (499/499, 0% failed) with the events reaching the audit log with schema ids and
no dead letters; then back to polling (connectors and slots removed) with the same chain passing. Unit tests: staging in both
modes (a CDC row is marked delivered at once and a polling row is not; an event that cannot be encoded refuses the write in CDC
mode and does not in polling mode; the mode is read from configuration and defaults to polling), mutation-checked.

Not verified: every consumer's reading of CDC-produced messages individually (the audit log's was; the others use the same
decoder and the same headers, and the k6 hires reached them, but I did not inspect each); behaviour when Connect restarts under
load; the slot-retention hazard (stated from Postgres's behaviour, not provoked); several connector tasks; exactly-once at the
connector (Debezium is at-least-once, so a restart can repeat an event; consumers are idempotent for that reason, ADR 0028).
