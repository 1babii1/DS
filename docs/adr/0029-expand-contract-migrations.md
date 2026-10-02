# 29. Schema changes that never break the version running before them

## Status
Accepted. Related: [0023](0023-event-contracts-avro-and-registry.md) (the same discipline for events),
[0027](0027-audit-entries-partitioned-by-month.md) (a migration that does not follow it, on purpose).

## Context
Services are deployed one instance at a time, so for the length of a rollout two versions run against one database: the one
being replaced and the one replacing it. A migration that renames a column, or makes one NOT NULL, breaks whichever of them does
not know about it. The usual fix, "stop the world, migrate, start", is downtime, which a rolling deployment exists to avoid.

## Decision
**Expand / contract.** A change is split into steps that are each safe while the previous version runs, with the one step that
would break it (dropping the old shape) last and taken only after the old version is gone. For a rename: add the new column and
a trigger that keeps the two equal whichever one a statement writes, backfill, deploy the new version, and only later drop the
old column. The steps, the SQL, the equivalents for other changes, the lock rules and the pre-contract checklist are in
[the runbook](../runbooks/zero-downtime-migrations.md).

**Rehearsed, not just written down.** `backend/Evolution.Tests` runs the choreography against a real Postgres with an "old"
and a "new" application (two sets of SQL statements) and checks, at each step, what each can and cannot do.

**Two rules that are not obvious from the pattern:**
- **`lock_timeout` on every DDL step.** `ADD COLUMN` is instant but briefly needs an exclusive lock; behind a long transaction it
  waits, and every later query queues behind it, so a fast change becomes an outage. With a short `lock_timeout` it fails fast
  and is retried.
- **The backfill comes before the new version, and is batched.** Otherwise the new version reads empty values for existing rows,
  and one big `UPDATE` holds locks and bloats the table.

## Consequences
- **A rename costs two releases.** That is the price, and the reason to prefer an additive change where one will do.
- **The trigger is temporary scaffolding.** It exists only between expand and contract; leaving it in place forever is the
  failure mode (two columns that must stay equal, with no one remembering why). The runbook's checklist ends at its removal.
- **The rehearsal uses a scratch table, not a production one.** No real service table was renamed to demonstrate this (that
  would be churn for its own sake); the next real change that needs it has a tested recipe.
- **EF Core does not know the pattern.** Step 1 and step 4 are separate generated migrations in separate releases, the trigger in
  a `Sql` block of the first. Nothing enforces that the contract is never shipped together with the expand: a reviewer has to.

## What is and is not verified
Against a real Postgres: both versions read and write and always agree during the expand phase (on new rows and existing ones,
in both directions); both running concurrently (two writers, 400 inserts and 200 renames) lose and corrupt nothing; existing
rows are empty in the new column until the backfill (so the order matters); the contract step is the one that breaks the old
version, leaves every row to the new one, and installs NOT NULL; without the trigger an old-version write leaves the new column
stale; a held lock makes the expand fail fast with `55P03`. Mutation-checked: dropping the new-to-old half of the trigger, the
backfill, and the NOT NULL.

Not verified: a real service rolling through the change with real traffic; a large table (batch size, duration); the
`CREATE INDEX CONCURRENTLY` row of the table (stated from Postgres's documented behaviour, not run).
