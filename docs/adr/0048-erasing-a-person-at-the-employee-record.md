# 48. Erasing a person at the record that owns it: EmployeeService anonymises, the row stays

## Status
Accepted for EmployeeService. Related: [0043](0043-personal-data-inventory.md) (where personal data is), [0046](0046-erasing-a-person-from-the-audit-log.md)
and [0047](0047-erasing-a-person-from-search-and-notifications.md) (the copies), the runbook `docs/runbooks/erase-a-person.md`.
AuthService's account and the ledger are **not** done here; see the end.

## Context
Erasing the copies (0046, 0047) while the record they were copied from still holds the name and address is half an answer. EmployeeService
owns the employee row: full name and e-mail, plus a failure reason that can quote them. Nothing removed or changed it; terminating only
set a status.

## Decision
- **`POST /api/employees/{id}/erase`** (can-edit and a fresh step-up, as terminating is) replaces the full name with `[erased]` and the
  e-mail with `erased-<id>@erased.invalid`, and clears the provisioning failure reason.
- **The row is kept, not deleted.** Other services hold this id (the wallet copy, audit entries, the saga's records, notifications' links).
  Deleting the row would leave them pointing at nothing and make "who did this" unanswerable in an audit; what stays is what happened
  (status, department, position, dates), not who the person was.
- **The e-mail stays unique** by carrying the id, because the column has a unique index and two erased people must not collide.
- **Only someone who has left** (terminated, or a hire that failed) can be erased; an active employee is refused with a conflict.
  Erasing a person who still works here would leave a working account without a name; the order is terminate, then erase.
- **Idempotent:** erasing again succeeds and changes nothing that matters.
- **No event is published.** The copies elsewhere are removed by their own endpoints, called by the operator (the runbook), because an
  event naming the person would itself carry the data being removed, and a new contract is a decision of its own.

Alternatives considered: **deleting the row** (above), **a hash in place of the name** (a hash of a name or an address can be
reversed by guessing and still identifies), and **an event that makes every service erase** (the event carries the identifier and
would have to be stored and replayable; also an ask-first contract change).

## Consequences
- The outbox rows of the hire (`EmployeeHired` carries name and e-mail) are removed by the outbox cleaner after its retention, not by this
  call; until then they exist, and any unprocessed row would still be published with the name.
- Kafka keeps `EmployeeHired` until topic retention ends (7 days by default), and the consumers that stored it are covered by 0046/0047.
- The employee card's wallet copy holds a balance and a version, no personal fields, and stays.

## What is and is not verified
Against the real database through the real handler: a terminated employee loses name and e-mail, the row and the status stay; an active
employee is refused and unchanged; erasing twice succeeds; two erased people do not collide on the unique address; an unknown id is not found.
The tests were watched failing against a handler that did nothing. Not verified: the endpoint through HTTP with a real token (same step-up
path as the one verified for 0046/0047), the dead-letter table of this service, backups.

**Not done, and why:** AuthService's account (deletion exists in `AccountDeletionService`; whether it satisfies erasure, with the security events
already published, is a question for that service) and RewardsService's ledger, which is kept for accounting and where erasure is a legal question
before it is an engineering one. Both are recorded in the runbook as gaps.
