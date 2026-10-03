# 46. Erasing a person from the audit log: a key per subject, destroyed on request

## Status
Accepted and built for the audit log. Implements the first part of the remedy [0043](0043-personal-data-inventory.md) designed
(crypto-shredding for what must be carried); the other places personal data lives are not covered, and the section on that is
the point of this ADR as much as the mechanism is. Related: [0027](0027-audit-entries-partitioned-by-month.md), [0019](0019-org-time-machine-from-the-event-log.md).

## Context
The audit log is append-only and keeps each event's payload as JSON. Some payloads carry personal data, and a request to erase a
person cannot be met by deleting rows (the log is the source of the org history, ADR 0019) or by rewriting them (it stops being a
log). What it can be met by is making the personal fields unreadable while keeping the fact that the event happened.

## Decision
**The audit log stores the personal fields of a payload encrypted under a key of the person they are about, and erasing the person
destroys the key.**
- Which fields: `PiiCatalog`, which a test requires to equal the personal-data inventory (`docs/security/pii-inventory.json`), so a
  field declared personal there cannot stay in the clear here.
- **Subject** = the event's key (its aggregate id): an account id, an employee id, or, for a failed login, the address that was
  tried. Each subject has a random 256-bit data key in `audit.subject_keys`, wrapped by a **master key** from configuration
  (`Audit:PiiMasterKeyBase64`, a secret; `AUDIT_PII_MASTER_KEY_BASE64` in the environment contract).
- **Sealing:** AES-GCM, with the subject and the field name as authenticated data, so a sealed value cannot be moved to another field
  or another person. In the payload the value is replaced by an envelope (`{"$pii":"v1","s":<subject>,"n":..,"c":..}`).
- **Reading** (the raw log for an administrator, the org-history replay) goes through the vault and gets the plaintext back; for
  an erased subject it gets the marker `[erased]`.
- **Erasing:** `POST /api/audit/subjects/erase` (administrator, step-up) takes up to 50 subjects. For each it empties the data key and
  keeps the row as a tombstone with the time, so a later event for the same subject is stored redacted instead of getting a new key.
  The act is recorded in the log itself, with a SHA-256 of each subject and who asked, never the subject. It is idempotent.
- **No master key means no encryption:** the vault passes payloads through and logs a warning. That is the development default, and
  a deployment that must not store personal data in the clear has to set the key (nothing enforces it).

## Consequences
- **What stays after erasure:** the event type, the time, the id of the aggregate, and every non-personal field. The org time machine
  still builds, with `[erased]` where the name was.
- **What erasure does not reach:**
  - **Rows written before the key was set** are in the clear. There is no backfill; they would have to be rewritten once.
  - **The aggregate id** of an event is a column and is not encrypted. For the failed-login event it is the address that was tried, so
    that address stays in the log after the subject is erased. Fixing it means hashing the key for that event type.
  - **Other copies of the data:** the same events are consumed by Search (the index), Notification (message text), Employee and
    Rewards (lookups), and the dead-letter tables keep payloads of messages that failed; none of these is touched. Erasing a person
    from the platform needs each owner to erase theirs; this does only the audit log.
  - **Backups taken before the erasure** hold the key. Erasure is complete only once those age out.
- **The master key is now the most sensitive secret in the audit service.** Losing it is the same as erasing everyone; leaking it
  with a database copy exposes everything. There is no re-keying: rotating it means unwrapping and re-wrapping every subject key.
- **Cost:** one extra read of `subject_keys` per subject per request (cached for the request) and a JSON parse for events that have
  personal fields; the replay parses only payloads that contain an envelope.
- **Correction to 0043.** That ADR said the audit log stores the deleted account's email and IP. In the default stack the audit
  service subscribes only to the directory and employee topics, so what it stores is `EmployeeHired` (a name and an email). The
  security events of AuthService, which carry the most personal data, reach it only if its topic list is extended; this mechanism
  covers them if they do.

## What is and is not verified
Against a real Postgres through the real consumer, with the stored bytes inspected: personal fields are not in the stored payload and
come back through the vault; an event with none is untouched; erasing makes both stored events of a subject unreadable while the
event types and the id remain; the data key is emptied (checked on the row); another subject stays readable; erasing twice is harmless
and counts once; an event arriving after the erasure is stored redacted and makes no new key; a sealed value moved to another field
or another subject does not open; eight events for a new subject at once end under one key and all read back; the vault is a
pass-through without a master key; the org history builds with an erased employee; the endpoint records the act without the subject
and refuses none and more than 50 subjects; the catalog equals the inventory. Mutation-checked: no sealing (seven tests fail), the
key not emptied on erasure, the field name left out of the authenticated data.

Not verified: the endpoint through HTTP with the real authorization and step-up (the controller is called directly, as for the other
audit endpoints); the cost at the size of a real log; a master key change; the Auth events flowing through, since the stack does not
subscribe to them.
