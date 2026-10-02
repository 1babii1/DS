# Changing a table without taking the service down

For [ADR 0029](../adr/0029-expand-contract-migrations.md). The rule: **no single deployment may contain a step that breaks the
version running before it.** A change that would is split into steps, each safe on its own, with the breaking step last and taken
only once nothing old is running. The SQL below is what `backend/Evolution.Tests` runs against a real Postgres.

## The ordering

| # | Step | Safe while the old version runs? |
|---|---|---|
| 1 | **Expand**: add the new thing (column, table, index); keep old and new in step | yes: only adds |
| 1b | **Backfill** existing rows, in batches | yes |
| 2 | **Deploy** the new version (it uses the new thing) | yes: the old one still works |
| 3 | **Wait** until no old version runs (every instance replaced, no job or consumer still on it) | |
| 4 | **Contract**: drop the old thing | only now |

If anything goes wrong before step 4, the change is reversible by deploying the old version: the old schema is still all there.

## Rename a column (`full_name` -> `display_name`)

**1. Expand** (one migration; additive only):

```sql
SET lock_timeout = '2s';                       -- see "Locks" below
ALTER TABLE people ADD COLUMN display_name text;

CREATE FUNCTION people_names_in_step() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        NEW.display_name := COALESCE(NEW.display_name, NEW.full_name);
        NEW.full_name    := COALESCE(NEW.full_name, NEW.display_name);
    ELSE   -- whichever column the statement changed wins; the other follows
        IF NEW.full_name IS DISTINCT FROM OLD.full_name AND NEW.display_name IS NOT DISTINCT FROM OLD.display_name THEN
            NEW.display_name := NEW.full_name;
        ELSIF NEW.display_name IS DISTINCT FROM OLD.display_name AND NEW.full_name IS NOT DISTINCT FROM OLD.full_name THEN
            NEW.full_name := NEW.display_name;
        END IF;
    END IF;
    RETURN NEW;
END $$ LANGUAGE plpgsql;

CREATE TRIGGER people_names_in_step BEFORE INSERT OR UPDATE ON people
    FOR EACH ROW EXECUTE FUNCTION people_names_in_step();
```

**1b. Backfill** (before the new version is deployed, otherwise it reads empty names for old rows). On a big table do it in
batches (`WHERE id BETWEEN ... AND display_name IS NULL`), so no statement holds locks for long:

```sql
UPDATE people SET display_name = full_name WHERE display_name IS NULL;
```

**2. Deploy** the version that reads and writes `display_name`.

**4. Contract** (a later migration, after step 3):

```sql
SET lock_timeout = '2s';
DROP TRIGGER people_names_in_step ON people;
DROP FUNCTION people_names_in_step();
ALTER TABLE people DROP COLUMN full_name;
ALTER TABLE people ALTER COLUMN display_name SET NOT NULL;   -- the invariant the change was for
```

In EF terms: step 1 and step 4 are two separate generated migrations (`dotnet ef migrations add` for each, the trigger in a
`migrationBuilder.Sql` block of the first), shipped in different releases.

## The same pattern for other changes

| Change | Expand | Contract |
|---|---|---|
| Add a NOT NULL column | add it nullable (with a default if the app can supply one), backfill, then `SET NOT NULL` last | |
| Drop a column | stop reading and writing it in code first; deploy | then drop it |
| Change a column's type | add a column of the new type, keep in step (as above), backfill, move reads, then drop | drop the old column |
| Split a table | create the new tables, write to both, backfill, move reads, stop writing the old | drop the old table |
| Add an index | `CREATE INDEX CONCURRENTLY` (outside a transaction; EF needs `suppressTransaction: true`) | |

## Locks

- `ALTER TABLE ... ADD COLUMN` (nullable, no default, or a constant default on PG 11+) is instant, **but** it needs a moment of
  `ACCESS EXCLUSIVE` on the table. If a long transaction is touching the table, the `ALTER` waits, and **every query that arrives
  after it queues behind it**: a quick change becomes an outage. So every DDL migration sets a short `lock_timeout` and, if it
  fails, is simply retried (the test holds a lock and shows the failure is a fast `55P03`, not a pile-up).
- Do not run a backfill as one statement over a big table: it holds row locks for its whole length and bloats the table. Batch it.
- The audit-log partitioning ([0027](../adr/0027-audit-entries-partitioned-by-month.md)) is the counter-example: a table rebuild
  that takes an exclusive lock for the length of a copy. It was acceptable at that size and is called out there as the thing to
  plan around, not to copy.

## Check before contracting

- Every instance of every service that touches the table runs the new version (`docker compose ps`, the deployment's rollout).
- Nothing else reads the old column: other services never do (each owns its schema), but **jobs, reports, dashboards and
  manual queries** might. Search for the column name.
- For an event contract, the same rule is the schema rule: deprecate, never remove ([runbook](event-schema-evolution.md)).
