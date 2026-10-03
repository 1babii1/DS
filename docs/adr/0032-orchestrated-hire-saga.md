# 32. The hire is an orchestrated saga with a deadline and compensations

## Status
Accepted. Related: [0003](0003-choreography-saga.md) (the choreography this refines),
[0028](0028-no-kafka-transactions.md) (idempotent effects), [0031](0031-event-sourced-wallets.md) (what the bonus reversal
appends to).

## Context
Hiring a person starts three things in three services: EmployeeService records the hire, AuthService makes a login account,
RewardsService grants a welcome bonus. ADR 0003 chose choreography: each service reacts to events and nobody holds the whole
story. That worked for the happy path and for one failure (the account cannot be made, so the employee is marked
`ProvisioningFailed`), and it left three gaps that choreography cannot close without a coordinator:
- **Nothing happens when nothing happens.** If the account event never arrives, the employee stays `PendingProvisioning`
  forever: no service owns "it has been too long".
- **"Where is this hire?" has no single answer.** The account is in Auth, the bonus in Rewards, the status in Employee.
- **Undoing is one-sided.** A failed account marked the employee but did not take back a bonus already granted, and a bonus
  that arrives late after a decision to give up was never reconsidered.

## Decision
**EmployeeService orchestrates the onboarding with a persisted state machine (`HireSaga`), one row per hire in `hire_sagas`.**
- The saga **starts in the hire's own transaction** (`Started`, with a deadline of two minutes, configurable).
- It **follows facts**: `AccountProvisioned` and `CurrencyGranted` (only the welcome bonus; the new `Source` field says which)
  record that step; both in time → `Completed`.
- It **acts on time**: a worker (every 15 s, on every instance) asks for sagas still `Started` and past their deadline. Time is
  an input like any other, so the clock is injected and tests move it.
- It **compensates**: on `AccountProvisioningFailed` or a missed deadline the employee becomes `ProvisioningFailed` and
  `HireCompensationRequested` is published stating exactly which steps to undo (`RevokeAccount`, `ReverseBonus`). Auth locks
  the account for good and revokes its sessions and tokens; Rewards appends a negative `WelcomeBonusReversal` to the wallet's
  history.
- **A step that finishes after the decision** (the bonus lands a second after the deadline) was not covered by the first
  request, so the saga sends a second, narrower `HireCompensationRequested`. The saga remembers what it already asked for.
- **Every step is idempotent and one transaction**: the saga row, the employee's status and the compensation event commit
  together. Two steps for one saga at once are arbitrated by the row's `xmin` version: the loser fails, the consumer retries,
  and the retry sees the new state. The deadline worker's failures are logged and retried on the next pass.
- **Participants stay dumb and idempotent**: Auth finds the account locked and does nothing the second time; Rewards finds the
  reversal in the wallet's history, so a repeat and a simultaneous pair both end with exactly one reversal (the primary key of
  the events is the arbiter, as in 0031). Neither knows the deadline or the other service.
- The reversal is **not published as news**: a negative `CurrencyGranted` would make Notification tell someone they received a
  negative bonus.

Why orchestration here and not elsewhere: the process has a deadline, a compensation that depends on which steps happened, and
a late-arrival case; those are state, and state wants one owner. The rest of the system stays choreographed (ADR 0003 stands for
events that merely inform).

## Consequences
- **EmployeeService now depends on two more topics' events** (`rewards.events.v2` joins `auth.events.v2`) and owns the
  process. If it is down, no deadline fires; when it returns the worker catches up. Nothing is lost, onboarding is just late to be undone.
- **Orchestrator is a coupling point**: adding a step (say, a laptop request) means a flag in the saga and a field in the
  compensation event, not a new listener chain.
- **A compensation is a request, not a guarantee.** There is no acknowledgement back; a participant that is down retries from
  Kafka, and one that dead-letters leaves the saga saying "requested" while the account is still open. The dead-letter table is the alarm.
- **Reversal can make a balance negative** if the person already spent the bonus; nothing spends yet, so this is not handled.
- **A hire from before this change has no saga** and is ignored by it.

## What is and is not verified
Tests, each mutation-checked (the guard removed, the test fails): the state machine without a database (in time, deadline,
failed account, a late step, idempotent repeats; 8 tests); the coordinator against real Postgres with a movable clock (start in
the hire's transaction, completion undoes nothing, a missed deadline undoes the account and announces once, a late bonus is
announced in a second event, a failed account, an unknown employee, and two simultaneous steps where `xmin` refuses the loser);
Auth locks and revokes sessions, once, and leaves an account alone when it is not asked to; Rewards reverses the bonus in
history, ledger and balance, exactly once among eight simultaneous requests on a wallet with history, and publishes nothing;
all event contracts through the registry (producer and each consumer's own reader schema).

Not verified: the whole chain live across the three services on Kafka with a deliberately stopped Auth (the unit and
integration seams are covered, the end-to-end drill was not run); the saga under several EmployeeService instances; the
migration `AddHireSagas` on the long-lived development database; behaviour when a participant dead-letters the compensation.
