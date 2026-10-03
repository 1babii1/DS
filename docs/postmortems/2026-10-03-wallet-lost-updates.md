# Postmortem: concurrent grants could silently lose money, and answered 500 on a new wallet

Blameless. Written in the language of [docs/slo.md](../slo.md): there was no outage and no error budget was spent; this is a
correctness defect found before it was seen by a user. It is included because it is the most instructive failure this project has had,
and because the way it was found says more about the process than the fix does.

| | |
|---|---|
| Severity | High in kind (a currency balance silently short), none in effect (no evidence it happened) |
| Introduced | 2026-09-16, with RewardsService (`27d11da`) |
| Found | 2026-10-03, by a test written for something else |
| Fixed | 2026-10-03, [ADR 0031](../adr/0031-event-sourced-wallets.md), PR #138 |
| User impact | None known. The development database was checked: 84 wallets, 84 balances equal to the sum of their ledger rows |

## What happened

A wallet kept a balance and an append-only ledger of grants. A grant read the balance, added its amount in memory and wrote the
result back. Two grants to the same wallet at once each read the old balance, and the later write erased the earlier one. Every ledger
row was present; the balance was short, with no error anywhere.

Reproduced as a failing test before the fix: eight simultaneous grants of 10 to an existing wallet with 100 in it left **110 instead
of 180**. The same race on a wallet that did not exist yet ended differently: the losers collided on the wallet's primary key, the
controller took that for an idempotency race, and the request ended in an unhandled exception (HTTP 500).

## Timeline

- **2026-09-16**: RewardsService created, with the cached balance updated in step with the ledger ([ADR 0005](../adr/0005-rewards-ledger-design.md)).
- **2026-09-23**: a race in the welcome bonus is found and closed (`8b7cd0b`): a check-then-act with no constraint behind it,
  reproduced as sixteen duplicate bonuses. The fix is a partial unique index and a test with overlapping requests. The same day,
  `Idempotency-Key` becomes required on grants.
- **2026-09-30**: a daily quota for assistant grants is added, with a test of simultaneous requests sharing a key.
- **2026-10-03**: while writing the concurrency test for event sourcing (a different feature), a test of eight grants to **one**
  wallet is written for the first time. It fails with 110, not 180. The fix and its test land the same day.

## Why it was not found sooner

1. **Every concurrent test granted to different employees.** The 09-23 fix looked at one race (the welcome bonus) and its test hit
   exactly that. The quota test raced one key. None raced two grants to one wallet, which is the case that matters. A concurrency
   test that exercises the mechanism but not the shared state proves the mechanism and says nothing about the state.
2. **The 09-23 fix was the right fix for the wrong question.** It asked "can the welcome bonus be granted twice?" The question
   behind it, "can two writers to one wallet corrupt it?", was not asked, although the same code path was in front of the author.
3. **The ledger row made the loss invisible.** Because the ledger was complete, any audit of "is every grant recorded?" passed.
   Only a comparison of the balance with the sum of the ledger would have shown it, and nothing compared them.
4. **A new wallet failed loudly and an existing wallet failed silently.** The loud case (500) was the lucky one, and it was never exercised.

## What went well

- The defect was found by writing a test first, and the test was run against the unfixed code and failed for the stated reason
  before anything was changed, so the reproduction is a fact, not an inference.
- The project's own earlier lesson (a race test must be seeded against an aggregate that already has related rows) is what made
  the second test (an existing wallet) get written.
- The fix removed the class of bug rather than the instance: the history is the source of truth, concurrency is decided by a primary
  key, and the cached balance is set from the history, never added to.

## What went poorly

- Money was involved and the first concurrency work on it came a month after creation.
- The check that would have caught it (balance equals sum of ledger) was possible from day one and was not written.
- The fix is a larger change than the defect needed to be stopped. That was a judgement call (a patch to the UPDATE would also
  have closed this instance); it was made because a currency ledger is the wrong place to leave the next instance open.

## Action items

| Action | State |
|---|---|
| Event-sourced wallet, balance derived from history, conflicts retried (ADR 0031) | Done |
| Regression tests: eight simultaneous grants to a new and to an existing wallet | Done |
| A test that damages the projections and shows a rebuild restores them | Done |
| Reconcile balance against ledger in the migration that introduced the events | Done (84 of 84 agreed on the dev database) |
| A recurring check that every wallet's balance equals the sum of its ledger, as a metric or an alert | **Open.** Cheap to write; would have made this visible on day one |
| A review question for any change that touches shared state: "what does a second concurrent writer to this row do?" | Open; recorded in AGENTS.md as the known-pitfall about race tests seeded against an aggregate with related rows |
