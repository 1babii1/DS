# 37. Idempotency-Key on the hire

## Status
Accepted. Related: [0005](0005-rewards-ledger-design.md) (the grant endpoint already works this way),
[0028](0028-no-kafka-transactions.md) (idempotent effects), [0032](0032-orchestrated-hire-saga.md).

## Context
`POST /api/rewards/grants` has had an `Idempotency-Key` since the ledger was built. The other write that creates something a
client cannot cheaply undo, `POST /api/employees` (the hire), had only the email's unique index. That index stops a second
person, but a client that retries a hire whose response it lost (a timeout, a double click, a mobile network) gets "an employee
with this email already exists", an error for a request that in fact succeeded, and no way to learn the id of the hire it made.

## Decision
**The hire accepts an optional `Idempotency-Key` header** (1 to 200 characters).
- **A retry returns the original result.** The key, a hash of what the caller chose (name, email, department, position; not the
  actor, which comes from the token) and the resulting employee id are stored in `idempotency_records`. A repeated key with the
  same request answers with the same id and does nothing else: no directory call, no write, no second event.
- **The same key with a different request is a conflict** (`employee.idempotency_key.reused`, 409), never someone else's result.
- **The record is written in the hire's own transaction**, with the employee, the onboarding saga and the outbox message. They
  exist together or not at all; a hire that fails validation leaves its key free for the corrected retry.
- **Racing requests are decided by the database.** `(Scope, Key)` is unique. Requests with one key that all pass the first
  lookup before any commits collide at the save, and the losers write nothing. Which unique index reports first, the key's or the
  email's (a retry carries the same email), is not something to rely on, so the handler treats both as "an earlier request got
  there" and lets the stored record decide: found, its result is the answer; not found, the original error stands.
- **Optional, not required**, unlike the grant: the frontend does not send it yet, and a required header would break the hire
  form. Without it, behaviour is unchanged. Making it required is a later, contract-visible step.
- **Scope:** the hire only. The Directory creates (department, position, location) are not covered; they have their own natural
  unique keys and no money or onboarding hanging off them.

## Consequences
- **Records are kept forever** for now (a row per keyed hire). A retention policy (the key only matters while a client may still
  retry) is not written.
- **A key is a client's promise to generate a new one per intended action.** A form that reuses one key for different people gets
  the conflict above, which is the right outcome and a confusing one; the frontend has to generate the key per submission.
- Another migration (`AddIdempotencyRecords`, generated).

## What is and is not verified
Real handler, real Postgres: a retry returns the same id and leaves one employee, one saga and one outbox message; without a key
a retry still fails on the email as before; the same key for a different request is refused and hires nobody; different keys are
different hires; a failed hire leaves its key free; empty and overlong keys are refused; eight requests racing on one key give
exactly one hire and every caller gets its id. Mutation-checked: no early lookup, no race recovery, no request-hash comparison.

**A finding about the test itself:** the first version of the race test passed even with the recovery code removed. The requests
rarely overlapped, so the first usually finished before the others read the key, and the path the test existed for never ran.
It now holds every request inside the handler (the stubbed directory call waits 400 ms) so all of them read "no such key" before
any commits; with that, removing the recovery fails it. A race test that does not force the overlap proves nothing.

Not verified: the header through the BFF and nginx (the frontend must forward and send it; see the frontend issue), many
instances (the unique index is the arbiter, nothing ran two), retention.
