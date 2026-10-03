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

- **EmployeeService** owns the employee record (name, email). There is no operation that deletes it; terminating marks it, and the data stays.
- **AuthService** owns the account and its sessions. Account deletion exists (`AccountDeletionService`); it removes the account, and
  its security events had already been published.
- **RewardsService** owns the wallet and its ledger. A ledger is kept for accounting; nothing here removes or anonymises it.

## What stays, whatever you do

- **Dead-letter tables** of every consumer keep the payloads of messages that failed; they are not cleared by the calls above.
- **The audit log's rows written before `AUDIT_PII_MASTER_KEY_BASE64` was set** are in the clear; there is no backfill.
- **The `aggregate id` column of the audit log**: for a failed sign-in it is the address tried, in the clear.
- **Notifications addressed to someone else about the person** (the failure notice sent to whoever hired them) are not found by subject.
- **Backups and replicas** taken earlier hold everything until they expire; the audit key is gone from live data only.
- **Logs and traces** are not covered.
- **Kafka itself** keeps events until their retention ends.

Whether this is enough to meet a legal obligation is not an engineering question; what is above is what is true.
