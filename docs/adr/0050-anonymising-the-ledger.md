# 50. The ledger is anonymised, not erased: amounts stay, who they belonged to goes

## Status
Accepted. Related: [0005](0005-rewards-ledger-design.md), [0031](0031-event-sourced-wallets.md) (the history this rewrites the key of),
[0043](0043-personal-data-inventory.md), [0048](0048-erasing-a-person-at-the-employee-record.md), [0049](0049-erasing-an-account.md).

## Context
RewardsService keeps a ledger for accounting: what was granted, when, how much, in what balance. Asked to erase a person, deleting their rows
would change the totals the ledger exists to give. What identifies the person in it is narrower than the rows: the employee id the wallet is
keyed by, a free-text reason ("Thanks Ann for the talk"), and, where they were the one granting, their account id. Whether the law allows
keeping the anonymised amounts is not an engineering question; the engineering question is making sure what is kept no longer says who.

## Decision
**`POST /api/rewards/subjects/erase`** (administrator, fresh step-up; subjects are employee ids or account ids, the link table says which
account belongs to which employee) rewrites the ledger in one transaction, holding appends to the event store off while it runs:
- the wallet's **history moves to a new random id** that is written nowhere (events, ledger rows and the wallet row all follow it). There is
  no mapping to undo it; the person-to-wallet link is gone, not hidden. Versions, amounts, dates, sources and transaction ids are untouched,
  so the balance still folds to the same number and the projections can still be rebuilt from the events;
- the **reason** is replaced with `[erased]` in the events and the ledger rows;
- where the subject is an **account that granted**, its id is removed from what it granted (the amounts stay), its daily agent quota
  counters are deleted;
- the **employee-account link** is deleted;
- **tombstones** (`erased_subjects`: the ids, a time) are written, so a redelivered or replayed `EmployeeHired` or `AccountProvisioned` creates
  no new wallet under the erased id, and a manual grant to it is refused with a conflict.

It is idempotent: a repeat finds nothing under the old id and changes nothing.

Alternatives: **deleting the rows** (the totals change, and the wallet's event history, the source of truth, would have holes the fold refuses),
**hashing the employee id** (the id space is small and known to every other service; a hash of it can be matched), **encrypting the reason and the
id under a per-person key** (what the audit log does, 0046; right for an immutable log, heavier than needed where a rewrite in place is possible
and the key is not otherwise useful), and **leaving it** as the runbook said until now.

## Consequences
- **Totals and history survive; the person does not.** Anyone holding an old copy of an id (a log line, the card's wallet copy in EmployeeService,
  a message still on Kafka) can no longer find the wallet by it; the wallet is not reachable from the person.
- **The balance of an erased wallet cannot be read back to a person, and the person cannot be paid again**: a grant to the erased id is a 409.
- **The employee card** (0034) keeps its copy of the balance keyed by the employee id until that employee row is erased (0048) and its wallet copy
  ages out; the copy holds a number and a version, no text.
- **Outbox rows and Kafka** still hold `CurrencyGranted` with the employee id until retention; consumers' copies are covered by 0046 and 0047.
- **The amounts, dates and sources can still single someone out** to a reader who knows a payment they received (a 100 on the day of hire). This is
  anonymisation of identifiers, not a guarantee against inference.
- The **migration** adds `erased_subjects` and nothing else.

## What is and is not verified
Against the real database: the ledger, events and wallet are rekeyed and the reason blanked with every amount and the total unchanged; the history
folds to the same balance and rebuilding the projections gives the same wallets; erasing a grantor removes their id from the ledger rows, the event
data and the quota counters; an account id finds the employee through the link and the link goes; a repeat is harmless; a welcome-bonus event after
the erasure creates nothing; over HTTP, an unauthenticated caller, a non-admin and an admin without a step-up cannot (nothing touched), an admin with
one can, a later grant is refused, and zero or more than 50 subjects is a bad request. The five tests that need the behaviour were watched failing
against an implementation that did nothing. Not verified: against the live stack, concurrent grants arriving during an erasure beyond what the table
lock guarantees, an erased subject's dead letters, and backups.
