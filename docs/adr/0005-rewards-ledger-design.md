# 5. Rewards ledger design and no cross-service employee validation in v1

## Status
Accepted

## Context
The platform needed an internal currency employees can be granted, both manually (a
manager/admin action) and automatically (a welcome bonus on `EmployeeHired`). Two design
questions came up immediately: how to represent a balance that changes over time from two
independent sources, and whether `RewardsService` should verify an `EmployeeId` actually
exists in `EmployeeService` before granting to it.

## Decision

**Append-only ledger, cached balance.** `Wallet.Balance` is never written directly - every
change goes through a `Transaction` row (`Amount`, `Reason`, `Source`, an optional
`GrantedByAccountId`), and `Wallet.Balance` is updated in the same `SaveChanges()` call
that inserts the `Transaction`. This is the same reasoning as denormalizing
`DepartmentName` onto `Employee`: the cache and the fact that produced it are written
atomically, so nothing ever needs to reconcile them, and reading a wallet never requires
summing the whole ledger. `Source` (`ManualGrant` / `WelcomeBonus`) makes the two grant
paths distinguishable after the fact and is what the welcome bonus's idempotency check
(`Transactions.Any(t => t.EmployeeId == id && t.Source == WelcomeBonus)`) keys on -
backed by a partial unique index on `(EmployeeId) WHERE Source = 'WelcomeBonus'`, added
after the check-alone version was shown to grant the bonus repeatedly (16 of 16 attempts)
under genuinely concurrent/redelivered processing of the same `EmployeeHired` event; the
check without the constraint only ever protected sequential redelivery, not a real race.

**One writer, two callers.** `CurrencyGrantWriter` holds the wallet-lookup-or-create +
transaction-insert + outbox-enqueue sequence exactly once. `RewardsController.Grant`
(manual, `CanEdit`-gated, actor from the caller's JWT `sub`) and `WelcomeBonusConsumer`
(automatic, actor `null`, triggered by `EmployeeHired` on `employee.events`) both call it
and then own their own `SaveChanges()` - matching how `HireEmployeeHandler` enqueues its
outbox message and saves once at the end.

**No synchronous validation that `EmployeeId` exists.** `POST /api/rewards/grants` accepts
any `Guid` as `EmployeeId` without calling EmployeeService to confirm it's real. Doing that
would mean a new gRPC server surface on EmployeeService (currently only a gRPC *client*,
never a server) built solely to guard one `CanEdit`-gated endpoint against a caller with
trusted, non-public credentials making a typo. The failure mode of skipping the check is a
wallet nobody ever reads, not a corrupted one - `Wallet`/`Transaction` rows are keyed
entirely by `EmployeeId` with no foreign key into another service's schema (ADR 0001:
schema-per-service, no cross-schema FKs), so an invalid id can't violate any constraint or
corrupt another employee's balance.

## Consequences
- Reading a balance is always an indexed lookup on `Wallet`, never a ledger scan - the
  ledger exists for audit/history, not as the hot read path.
- A manual grant to a nonexistent `EmployeeId` fails silently from the caller's
  perspective (200 OK, wallet nobody will ever see) rather than a 404. Acceptable for a
  `CanEdit`-gated endpoint used by trusted admin/manager tooling; would be revisited if
  this endpoint were ever exposed to less-trusted callers.
- `Amount` is signed and not constrained to be positive at the domain level (only the
  manual-grant endpoint validates `Amount > 0`) - deliberately, so a future currency shop
  can debit the same `Wallet`/`Transaction` model by writing negative amounts through the
  same `CurrencyGrantWriter`, without a schema change.
- The welcome bonus is deliberately the only automatic trigger in v1. Other domain events
  (`EmployeeTransferred`, `DepartmentCreated`, etc.) were surveyed as candidates during
  planning and left out - not because they couldn't grant currency, but because "should a
  transfer or a department change ever pay out, and how much" wasn't a product decision
  anyone had actually made yet, and guessing would have been worse than not building it.
