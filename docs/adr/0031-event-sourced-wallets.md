# 31. Wallets are event-sourced

## Status
Accepted. Related: [0005](0005-rewards-ledger-design.md) (the ledger design this builds on),
[0028](0028-no-kafka-transactions.md) (idempotent effects).

## Context
The wallet used to be a row holding a balance, updated in step with an append-only ledger of transactions (ADR 0005): the
ledger was the record and the balance a cache of its sum. Writing the failing test for "two grants to one employee at once"
showed the cache was not safe:

- with the wallet already existing, eight overlapping grants of 10 each left a balance of **110 instead of 180**: each read the
  old balance, added its amount in memory and wrote the result back, so later writes erased earlier ones, with no error and
  with every ledger row present;
- with a new wallet, the same race made the losers collide on the wallet's primary key and the controller mistook that for an
  idempotency race, ending in an unhandled exception (an HTTP 500).

Every other concurrent test in the suite grants to different employees, which is why neither had been seen. In a currency
ledger a silently short balance is the worst kind of defect, so the fix is worth more than a patch to the UPDATE.

## Decision
**The wallet's history is the source of truth, and the balance and the ledger are derived from it (event sourcing).**
- **`wallet_events`**: one row per fact about a wallet: `(StreamId = the employee, Version, EventType, Data, OccurredAt)`.
  `(StreamId, Version)` is the primary key. Today there is one event type, `WalletAdjusted` (amount, reason, source, who,
  and the id of the ledger row it produces), because the wallet only ever receives grants; a spend would be a negative
  adjustment, not a new mechanism.
- **`WalletAggregate`** rebuilds a wallet by folding its events and decides a change itself: it hands out the next version and
  refuses a second welcome bonus because **its own history** already holds one. Rules live with the state they depend on.
- **Concurrency control is the primary key.** Two writers that both read version N and both append N+1 cannot both commit:
  the loser's whole transaction (event, projections, outbox message) is rolled back and it **re-reads and decides again**,
  up to six times with a short jittered pause, instead of failing a request that was fine. Only that collision is retried;
  the welcome-bonus index and an idempotency key mean something else and are handled where they are understood.
- **Projections are written in the same transaction as the event** (so reads are as fast and as consistent as before): the
  wallet row's balance is *set* from the fold, never added to, and the ledger row is derived from the event. Nothing reads the
  cached balance to compute the next one.
- **Rebuilding is the repair.** `WalletProjections.RebuildAsync` drops and rebuilds `wallets` and `transactions` from the
  events in one transaction, with appends held off while it runs. If a projection is ever wrong, that is the fix; a test damages
  the projections and shows the rebuild restores exactly the same wallets and ledger.
- **Existing data becomes the first events.** The migration turns each ledger row into an event (versioned per wallet in the
  order written, carrying everything the ledger row holds) and then sets each wallet's balance to what that history says, which
  also repairs any wallet the old race had left short.

## Consequences
- **Two stores of the same facts for now:** the events and the ledger rows say the same thing; the ledger table is a projection
  kept because the API and the reports read it. Dropping it is possible later, not needed.
- **Reading a wallet's own history on every grant** costs a read of its events. A wallet's history is short (grants, not
  clicks), so no snapshots are kept; a wallet with thousands of events would need them.
- **Retries make a grant's latency variable under contention** (a few milliseconds per round) and bound its worst case at six
  attempts; a seventh collision surfaces as an error instead of looping.
- **The agent quota is still a separate counter** (`agent_grant_usage`, an atomic upsert in the same transaction); it is a limit
  on callers, not a fact about a wallet, and is not event-sourced. A retried attempt does not spend it twice (its transaction
  is abandoned uncommitted).
- **Event shape is now a contract of its own** (`WalletAdjusted` data is read by the rebuild and the migration): changes to it
  follow the same additive rule as integration events ([0023](0023-event-contracts-avro-and-registry.md)).

## What is and is not verified
Test first: the two concurrency tests above were written and run against the old code and failed for exactly the reasons
described (110 against 180, and the exception), then pass. Aggregate decisions without a database (fold, version, the welcome
bonus once, a history with a gap refused); the store (two events cannot claim one version); rebuilding the projections after
damaging them gives back the same wallets and ledger; a grant takes its balance from the history not the cached row; the
migration on a real Postgres starting from a ledger with a short wallet (events in order, balances corrected, the rebuild
agrees with the ledger the migration found). The whole Rewards suite (55 tests, including these) and the chaos tests (the welcome bonus under a
frozen broker and a frozen database) pass. Mutation-checked: no retries, the welcome-bonus rule, the balance taken from the
cache, the balance reconciliation in the migration.

Not verified: the migration on the long-lived development database (it runs when the stack is next brought up; the
development data had no wallet out of step with its ledger, 84 of 84 agreed); contention beyond eight writers on one wallet;
the retry's effect on request latency; behaviour with several RewardsService instances (the primary key is the arbiter, so it
should hold, nothing ran two).
