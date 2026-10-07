# 54. Several instances of one service, one of them killed: what holds and what does not

## Status
Accepted as a measurement; what it found is changed in [0055](0055-one-outbox-publisher-and-configurable-partitions.md). Related:
[0002](0002-transactional-outbox.md) (the publisher), [0028](0028-no-kafka-transactions.md) (idempotent consumers),
[0031](0031-event-sourced-wallets.md), [0034](0034-employee-card-read-model.md) (the consumer measured here), [0037](0037-idempotency-key-on-hire.md).

## Context
Everything the project says about delivery was run against one instance of each service and a topic of one partition. Three ADRs end with
"not verified: several instances consuming" (0034, 0046, 0050). Two things change when a service runs more than once: each instance runs the
polling outbox publisher, and a Kafka consumer group splits partitions among its members. Nothing had been run to see what either does.

## What was done
`scripts/multi-instance-drill.sh`: three RewardsService and three EmployeeService instances over one Postgres schema each and one Kafka broker,
all private to the drill. Grants are posted by four k6 users to a random Rewards instance, each with its own `Idempotency-Key`; when an instance
does not answer, the same grant is sent to another with the same key. Mid-run, `rewards_b` and `employee_c` are killed with SIGKILL and started
again. When the load ends the books are checked: every confirmed grant has a ledger row, no key has two, each wallet equals its ledger, the card's
copy in Employee equals each wallet in balance and version, and the messages on the topic are counted against the outbox rows that produced them.
Topics are created up front with the number of partitions under test. 300 employees, about 10 grants a second (a heavier rate where stated).

## Measured (before any change)
| run | rows | confirmed / unknown | books agree | messages on the bus per outbox row | consumers of `rewards.events.v2` |
|---|---|---|---|---|---|
| 1 partition, kills | 572 | (counter not captured) | yes | 1277 / 572 = **x2.23** | **one** member, partition 0 |
| 6 partitions, kills | 570 | (counter not captured) | yes | 1266 / 570 = **x2.22** | three members, two partitions each |
| 6 partitions, no kills, 10/s | 435 | 435 / 0 | yes | 1175 / 435 = **x2.70** | |
| 6 partitions, kills, 10/s | 419 | 419 / 0 | yes | 971 / 419 = **x2.32** | |
| 6 partitions, no kills, ~65/s | 1899 | 1899 / 0 | yes | 1899 / 1899 = x1.00 | |

In the last row 1,053 rows were still unpublished when the load ended and took 40 s to clear (about 26 a second: three publishers each moving a
batch of 20 every 2 s, and taking turns on a backlog).

## What it shows
1. **Correctness held under `kill -9`.** In every run each confirmed grant had exactly one ledger row, the keys equalled the rows, every wallet equalled
   its ledger, and the card's copy matched the wallet in balance and in version, with the Employee consumer group rebalancing after one member died.
   This is what the idempotency key (0037), the version-guarded statement (0034) and the event-sourced wallet (0031) are for, and it is now shown with
   several instances and a kill and not only with one.
2. **The same event goes on the bus two or three times.** Every instance polls the outbox and reads the same oldest rows; nothing says a row is
   taken. At about the rate one publisher can move (10 a second) the three instances mostly find the same rows: 2.2 to 2.7 messages per row. Under a
   backlog they read different rows by accident of timing and the duplicates vanish, which is why the effect is easy to miss with a load test of
   the usual heavy kind. Consumers absorb it (that is what the version guard is for), but every consumer pays for each copy.
3. **Topics always had one partition**, hard-coded in the provisioner. A consumer group of three on such a topic has one working member and two idle
   ones: adding instances of a consumer added nothing, and the fix for scaling was not configurable. Created up front with six partitions, the same
   group split them two each.
4. **A publisher moves at most about ten messages a second,** 20 rows per 2 s, and it slept the 2 s even after a full batch.

## Consequences
- The books are right with several instances; what was wrong is cost and a ceiling, found and fixed in 0055.
- Per-key order was **not** measured: with duplicates and several partitions the order in which one wallet's events reach a consumer was not checked.
  The card is safe against disorder (the version guard); another consumer that is not would need the same look.

## What is and is not verified
Run end to end as above, six times on this machine. Not verified: more than three instances; the other services' consumers and publishers (the
mechanism is shared code, but only Rewards and Employee were run); the Search, Notification and Audit consumers under a rebalance; the pause a
rebalance causes (the lag was zero at the end, not measured during); a kill of the instance that happens to be publishing, at the moment it is
publishing, which the random kill hit by chance if at all.
