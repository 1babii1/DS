# 49. Erasing an account: operator-initiated, tokens revoked, an event that names no one

## Status
Accepted. Related: [0043](0043-personal-data-inventory.md), [0046](0046-erasing-a-person-from-the-audit-log.md),
[0048](0048-erasing-a-person-at-the-employee-record.md), the runbook `docs/runbooks/erase-a-person.md`.

## Context
AuthService already deleted an account: `AccountDeletionService`, self-service, gated by the person's own password. Looked at as an
answer to "erase this person", it had four gaps:
1. **Only the person could trigger it.** Someone who has left, or who asked by e-mail, cannot type their password. An operator had no way.
2. **Tokens.** It revoked the authorizations but not the tokens themselves; a refresh token should stop working when the account goes, not
   when its authorization is next consulted.
3. **The event that recorded it carried the person.** `AccountDeleted` was published with the e-mail address and the IP of the request, to a
   bus that keeps events and to consumers (the audit log) that store them: deleting the account published the very data being deleted.
4. **Nothing said how it related to the other services' copies**; the runbook only said that deletion exists.

## Decision
- **`POST /admin/accounts/{id}/erase`** (administrator, fresh step-up, the auth rate limit): the same removal as self-service, without the
  password. Refused for the operator's own account (self-service is the path for that) and for the last administrator; an unknown id is a 404
  (so a repeat answers 404, which for an erasure means "already gone").
- **Both paths share one removal** (`AccountDeletionService.RemoveAsync`): revoke the subject's **tokens and** authorizations, delete passkeys and
  sessions, delete the account (Identity removes its roles, claims, logins, two-factor data), then record the deletion.
- **`AccountDeleted` now carries the account id, a time, and the marker `[erased]` in place of the address, with no IP.** The contract keeps its
  fields (no schema change), so consumers keep working; the audit log's catalogue still lists those fields as personal, which now protects
  older events. A test reads the outbox row and checks the address is not in it.

Alternatives: **a new event type for erasure** (a contract change, with no consumer that needs it), and **keeping the address for the record**
(the whole point is that it is not kept; the id says which account and when).

## Consequences
- **The account's earlier events keep their address:** logins, lockouts and password changes were published with it, and sit in Kafka until
  retention ends and in the audit log until `POST /api/audit/subjects/erase` is called for the account id (the runbook's first step).
- **A repeat of the operator's call is a 404, not a 204.** Erase is idempotent in effect, not in status code.
- **Dead letters** of this service may still hold messages naming the person; not cleared here.
- The employee's record is a separate call (0048); the order in the runbook is: copies, then account, then employee record.

## What is and is not verified
Against the real database and the real token flow (158 tests in the service pass): an erased account is gone and its refresh token no longer
works; the deletion event's stored payload does not contain the address; erasing needs a fresh step-up (403 without); an operator cannot erase
themselves, and an unknown id is a 404. The first three of the new tests were watched failing against an endpoint that answered 501. Not verified
here: the last-administrator refusal on this path (the same check the self-service path has, covered there), and the live stack.
