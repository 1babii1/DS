# Erasing a person

What the platform can do today when someone asks to be forgotten, and what it cannot. Read it as a checklist with its gaps marked.
The design is in [ADR 0043](../adr/0043-personal-data-inventory.md) (where personal data is), [0046](../adr/0046-erasing-a-person-from-the-audit-log.md)
(the audit log) and [0047](../adr/0047-erasing-a-person-from-search-and-notifications.md) (search and notifications).

## Find the identifiers

A person has several: the **employee id** (EmployeeService), the **account id** (AuthService; the employee's account is linked
to the employee id by the `AccountProvisioned` event), and, if someone tried to sign in with their address and failed, **the address
itself**, which is the key of those events. Collect all of them first.

## Step 1: the services that hold copies (administrator, with a fresh step-up)

Each call takes up to 50 subjects (employee ids, account ids, addresses) and is safe to repeat.

| Service | Call | What it removes |
|---|---|---|
| AuditService | `POST /api/audit/subjects/erase` | The key under which the person's personal fields in audit payloads are encrypted is destroyed; the fields become unreadable (shown as `[erased]`) |
| SearchService | `POST /api/search/subjects/erase` | The employee document, its staged embedding, and audit-kind documents carrying an identifier; later events are not indexed |
| NotificationService | `POST /api/notifications/subjects/erase` | Notifications addressed to the person's account, the employee-account link; later events create nothing |

Body: `{"subjects": ["<employee id>", "<account id>", "<address>"]}`. Through nginx, the paths are those above.

## Step 2: the services that own the data (not done by any call here)

- **EmployeeService** owns the employee record (name, email). Terminate first, then `POST /api/employees/{id}/erase` (administrator, fresh step-up): name and
  address are replaced, the row stays ([ADR 0048](../adr/0048-erasing-a-person-at-the-employee-record.md)). Active employees are refused.
- **AuthService** owns the account and its sessions. `POST /admin/accounts/{id}/erase` (administrator, fresh step-up) revokes its tokens, removes
  the account, passkeys and sessions, and publishes a deletion event without the address ([ADR 0049](../adr/0049-erasing-an-account.md)). Do step 1
  for the account id first or after; its earlier events keep the address until then.
- **RewardsService** owns the wallet and its ledger. `POST /api/rewards/subjects/erase` (administrator, fresh step-up; employee or account ids) moves the wallet's
  history to an id nobody holds, blanks the reasons and removes a grantor's id; amounts and totals stay ([ADR 0050](../adr/0050-anonymising-the-ledger.md)).

## What stays, whatever you do

- **Dead-letter tables** of every consumer keep the payloads of messages that failed; they are not cleared by the calls above.
- **The audit log's rows written before `AUDIT_PII_MASTER_KEY_BASE64` was set** are in the clear; there is no backfill.
- **The `aggregate id` column of the audit log**: for a failed sign-in it is the address tried, in the clear.
- **Notifications addressed to someone else about the person** (the failure notice sent to whoever hired them) are not found by subject.
- **Backups and replicas** taken earlier hold everything until they expire; the audit key is gone from live data only.
- **Logs and traces** are not covered.
- **Kafka itself** keeps events until their retention ends.

Whether this is enough to meet a legal obligation is not an engineering question; what is above is what is true.
