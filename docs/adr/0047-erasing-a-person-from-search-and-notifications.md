# 47. Erasing a person from search and notifications

## Status
Accepted and built. Completes the copies [0046](0046-erasing-a-person-from-the-audit-log.md) named as not covered, for the two services that
hold them; the runbook [erase-a-person](../runbooks/erase-a-person.md) lists what remains. Related: [0043](0043-personal-data-inventory.md).

## Context
The audit log was only one place. The same events are consumed by SearchService, which indexes a document for each employee (the full name
and the address as its search text) and one for every message it sees (whose text is the message key, and for a failed sign-in the key is the
address that was tried), and by NotificationService, which stores notifications addressed to accounts and the link between an employee
and the account. Neither is the owner of the data; both are copies, and a copy that cannot be erased makes erasing the owner's data pointless.

## Decision
**Each copy erases itself on request and remembers that it did.**
- `POST /api/search/subjects/erase` and `POST /api/notifications/subjects/erase`, administrators only, with a fresh step-up, up to 50
  subjects, idempotent. Both register the policies they name (the first version of the audit endpoint did not; see below).
- **Search** deletes the employee document and its staged embedding, and the audit-kind documents whose text contains the subject (a
  delete-by-query), for an employee id and for an address alike.
- **Notification** treats the subject as an account id or an employee id, finds the account through the link, deletes the notifications
  addressed to it and the link, and marks the subject and the ids it resolved to.
- **A tombstone per subject** (`erased_subjects`, a migration in each service) is checked first by the consumer: an event keyed by an
  erased subject is skipped entirely, so a late event or a replay of the topic does not bring the person back. Notification checks the
  recipient too, because the key of an event is not always the person it notifies (the failure notice goes to whoever hired them).
- **Not a shared mechanism:** three services, three small implementations. Each differs in what it holds, and a shared library would
  have forced a common shape on things that have none.

## Consequences
- **The runbook is the contract** (`docs/runbooks/erase-a-person.md`): an administrator calls the three services, and it says in the
  same place what no call here reaches (the owners' own data, ledgers, dead letters, backups, logs).
- **Tombstones are themselves a list of subjects that were erased,** kept for good. They are identifiers, not personal data about anyone,
  but a list of the ids and addresses someone asked to erase says that they asked.
- **Notification cannot find notifications that mention a person but are addressed to someone else.**
- **Order matters little but the owner must stop publishing:** if the owning service goes on emitting events for the person, the copies
  stay empty only because of the tombstones.

## What is and is not verified
Real consumers, real databases and, for Search, a real Elasticsearch. Notification: erasing by employee id removes the notifications of the
account and the link; erasing by account id finds the same person through the link; another person is untouched; a late event for an erased
person creates nothing; a notification addressed to an erased account is not created even when the event is about someone else (and one
for an account that was not erased is); erasing twice is harmless; none and too many subjects are refused. Search: erasing an employee removes the document and the
staged embedding; erasing an address removes the audit documents carrying it; another person stays; a late event indexes nothing; twice is
harmless; none and too many are refused. Mutation-checked: the late-event guard in each service, and the recipient check in Notification.
For both: the application's own authorization setup resolves every policy its controllers name.

**A defect of the previous step, found here and fixed in #169:** the audit erase endpoint is marked `[RequireStepUp]`, but AuditService had
never registered that policy, so the endpoint would have failed for everyone at request time. The tests call controllers directly and
could not see it. Both new services register it, and each has a test that asks the real authorization setup for every policy a controller names.

Through HTTP on the running stack (dev admin, sign-in, e-mail step-up code, refreshed token): before step-up all three endpoints answer 403, after it 200 and a repeat 200 (Audit `newly` 1). Nothing to delete was seeded, so the counts of removed documents are 0; only the authorization path is shown.

Not verified: the delete-by-query on an index of real size; Notification's
dead letters; the three calls as one operation (they are three calls, and if one fails the others have already happened).
