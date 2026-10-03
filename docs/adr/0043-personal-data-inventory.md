# 43. Personal data: an inventory that is checked, and the erasure problem it exposes

## Status
Accepted. The inventory and its check are built. The remedy for what the inventory shows (field encryption, crypto-shredding,
access auditing) is **designed here and not built**. Related: [0027](0027-audit-entries-partitioned-by-month.md) (the audit log
keeps payloads), [0023](0023-event-contracts-avro-and-registry.md) (the events), [0019](0019-org-time-machine-from-the-event-log.md).

## Context
The roadmap item was "PII: field encryption, access audit". Encrypting fields is a technique; before choosing it, the question is
where personal data is and what is supposed to happen to it. Nothing in the repository answered that. Events are the widest
channel: they leave the service that owns the data, are read by several services, and one of the readers (the audit log) stores
the payload as JSON **and keeps it for good** (retention exists and is off, ADR 0027).

## Decision
**1. An inventory of personal data in integration events, kept in `docs/security/pii-inventory.json`, and a test that fails when it
is wrong** (`PiiInventoryTests`, in the contract tests that already run in CI).
- Every field of a producer event whose name suggests a person or where to find one (email, phone, name, address, birth, passport,
  ssn, street, postal, city, country, zip, an IP address) must be listed either as `pii` (with a category and the purpose it
  serves) or as `notPii` (with the reason). A new field of that kind in a schema fails the build until someone has decided.
- An inventory entry for a field that no longer exists fails too, and a field cannot be both.
- It does not find personal data under a name the pattern does not know. What it prevents is the accidental addition of the
  obvious ones, which is how this kind of data spreads. The regular expression is the contract, and it is in the test.

**2. What the inventory shows (the point of having it).** 23 personal fields in 11 events (one full name, 12 email fields, 10 IP address fields):
- **Login events carry the email and the IP address of every attempt** (`LoginSucceeded`, `LoginFailed`, `AccountLockedOut`,
  `PasswordChanged`, `AllSessionsRevoked`, `EmailChanged` with the old and the new address, three administrator actions with the
  target's address). `LoginFailed` can name an address that belongs to no account.
- **`AccountDeleted` carries the deleted person's email and IP address.** The account is deleted; its description is written
  into an event, stored by the audit log as JSON, and kept. The right to erasure and an append-only log that is the source of the
  org time machine pull in opposite directions, and today the log wins by default.
- The directory's names and the office addresses are organisational, not personal, and are listed as such so that the decision
  is visible.

**3. The remedy, designed and not built.** In order of preference for this platform:
1. **Do not carry what the consumer can look up.** Most events could carry the subject's id and let a reader ask the owner for the
   email when it needs it. It leaves the personal data in one place that can be erased. Not possible for the security trail, whose
   value is that it states what happened at the time.
2. **Crypto-shredding for what must be carried.** The personal fields of an event are encrypted with a key per subject
   (envelope encryption: a key-encryption key in the secret store, a data key per person in a table the owner keeps); erasing a
   person is deleting their data key, after which every copy in every log and index is unreadable at once. Field encryption alone
   (one key for everything) protects against a copied database and does nothing for erasure, so it is not enough.
   Searching by an encrypted email needs a keyed hash (a blind index) stored beside it.
3. **Redacting the audit payload on erasure** would give up append-only, and with it the claim the time machine rests on.
- **Access audit** (who read a person's record) does not exist: reads of employee data are not recorded. It would be an event per
  read of a person's record by someone other than the person, written to the same trail.

## Consequences
- A change to an event schema now has one more review question, answered in a file next to the schema's tests.
- The inventory covers **events only**. The tables (employees, accounts, sessions, the notification text, the search index) and
  the logs also hold personal data and are not inventoried; the same method applies (an EF model scan) and is not done.
- Until the remedy is built, **erasing a person is not possible**: deleting an account leaves their email and IP in the audit log
  and in every consumer that stored the event.

## What is and is not verified
The test runs against the real schemas: all three checks pass, and a `PhoneNumber` field added to a schema fails the first with the
name of the field (checked by hand, then removed; the first version of the pattern did not catch it because it only matched at the
end of a name, which is why the pattern now matches `phone` anywhere). Not verified: anything about the remedy. It has not been
prototyped, its cost to the contract tests, the registry and the replay of old events is unknown, and the legal side of erasure
against an audit obligation is outside what an engineering ADR can settle.
