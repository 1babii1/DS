# 34. The employee card: a read model with a way to see your own write

## Status
Accepted. Related: [0007](0007-elasticsearch-cross-service-search.md) (the first read model built from events),
[0025](0025-read-replica-and-read-your-writes.md) (the same problem for the replica, solved with an LSN),
[0031](0031-event-sourced-wallets.md) (where the wallet version comes from), [0004](0004-no-api-gateway-aggregation.md).

## Context
A screen that shows an employee wants their department and position (EmployeeService), their status (EmployeeService, driven by
AuthService), and their balance (RewardsService). Fanning out three calls per view couples availability and latency to every
service (ADR 0004). Building a copy from events is the project's usual answer, and it has one known cost: the copy trails the
source, so a user who has just granted a bonus can open the card and not see it.

CQRS in the sense of a separate model for reads was already present in pieces: the wallets' projections (0031), the search index
(0007), the org time machine (0019). This is the piece that was missing: a read model for a screen, joining facts from different
services, **with a stated answer to staleness**, not only a statement that it is eventually consistent.

## Decision
**EmployeeService serves `GET /api/employees/{id}/card`: its own employee row joined with a copy of the wallet it keeps from
RewardsService's events.** No new service. Everything else on the card is already this service's own data and is always current;
only the balance is a copy, so only the balance can be stale, and the card says how stale.

- **The copy** is `employee_wallets` (balance, the wallet version it came from, when it changed at the source, when it was taken).
  `CurrencyGranted` now carries `WalletVersion`, the version of the wallet's event history after the change (additive; 0031 has the
  number already). A welcome-bonus **reversal** is published too (marked `Source = WelcomeBonusReversal`; NotificationService
  ignores that source), because a balance copy that never hears of a reversal is wrong.
- **One statement applies an event**: insert the first, otherwise overwrite only `WHERE existing version < incoming version`.
  Redelivery changes nothing (not even the time the copy was taken), an older event arriving late cannot move the balance back,
  and twelve instances applying one employee's events at once end at the highest version. No lock, no read-then-write.
- **The way around staleness is a version the writer hands back and the reader waits for** (the same idea as 0025's LSN, with the
  wallet version in place of an LSN). A grant answers with `X-Wallet-Version`. A card request carrying `X-Min-Wallet-Version: n`
  polls the copy (every 25 ms, up to 2 s) until it has reached `n`. If it has not, the card is returned anyway, **marked behind**
  (`consistent: false`, header `X-Card-Consistent: false`) instead of hanging or lying. Every card also states the version and the
  time its balance is as of.
- **What it is not:** the other services' facts are not authoritative here. Rebuilding the wallet copy means replaying
  `rewards.events.v2` (reset the consumer group's offsets); the version guard makes the replay harmless. There is no rebuild from
  this service's own data, because it does not own the wallet.

Alternatives considered: a **separate card service** (a new service and a new deploy unit for one table; "ask first" territory
and no more isolation than a table), **fan-out at read time** (rejected by 0004), **synchronous projection** (impossible across
services; it is what 0031 does inside one), and **optimistic update on the client only** (cheap, and complementary, but it hides
a stale server rather than reporting it).

## Consequences
- **The staleness is bounded and visible, not removed.** After a grant, a plain card read can show the old balance for the length
  of the outbox-to-consumer path; a read that passes the version never shows it, at the cost of waiting up to the same path.
- **The frontend must carry the header.** The BFF route's allow-list and the grant call have to pass `X-Wallet-Version` through
  to the next card read; that wiring is not done (the backend contract is). Without it the card is simply eventually consistent.
- **Another reason `CurrencyGranted` is a contract:** three consumers now read it with their own reader schemas
  (Notification, Employee, and the saga's use of `Source`), each checked against the producer by the contract tests.
- **Events from before `WalletVersion` existed are not applied** (they cannot be ordered); the next change brings the copy
  up to date. An employee whose wallet has not changed since shows no balance until it does. A one-off replay fixes it.
- **A reversal publishes an event nobody on the bus reads except the card**; cheap, but it is a message with only one reason to exist.

## What is and is not verified
Against a real Postgres through the real consumer and handler: no wallet yet gives no balance and says so; an event gives
balance and version; an older event after a newer one does not regress it; a redelivery leaves the row untouched (even the time
taken); a reversal lowers the balance; an event without a version is left out; twelve events applied at once in random order end
at the highest version; a read asking for a version waits for it and returns it consistent; a read waiting in vain returns the
card marked behind after the limit; a missing employee is nothing. Rewards: the grant answers with the right wallet version and
publishes it; the reversal is published with the source and the version. Notification: a reversal changes nothing it shows.
All event contracts through the registry. Mutation-checked: no version guard in the statement (three tests fail), no waiting
for the version (two fail), no reversal recognition in Notification (fails).

Not verified: **the lag on the live stack.** The copy stores when the balance changed at the source and when it was taken, so
the lag is measurable from the data, but it was not measured end to end through Kafka here; the figure of "milliseconds" is the
expectation, not a result. Also not verified: the frontend passing the header, several EmployeeService instances consuming
(the statement is safe by construction, nothing ran two), and the replay of a long history.
