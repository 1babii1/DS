# 42. Row-level security for multi-tenancy: what it gives, what bypasses it, and the pooler trap (drilled, not applied)

## Status
Accepted as a decision about **how** to do it if the platform ever has tenants. Nothing in the services uses it yet. The platform
has no tenant concept today (one organisation, one database), so a policy would protect against nothing; what this ADR records is
the experiment that shows what the mechanism does here and which traps sit between it and this stack.
Related: [0001](0001-schema-per-service-shared-database.md), [0024](0024-connection-pooling.md).

## Context
Multi-tenancy in a shared database has three shapes: a database or a schema per tenant (strong isolation, many objects), a
`tenant_id` column filtered in every query (cheap, and one forgotten `WHERE` away from showing a customer another's data), or the
column plus **PostgreSQL row-level security**, where the database itself filters and a forgotten `WHERE` returns the caller's rows
instead of everyone's. RLS is the middle path with a safety net. The questions are practical: does the policy hold, who is exempt,
and does it survive the stack's connection pooler?

## Decision
**If tenants are added, use a `tenant_id` column plus RLS, set per transaction.** The shape that works, shown by
`scripts/rls-drill.sh` (Postgres and PgBouncer in throwaway containers):

- `ENABLE` and `FORCE ROW LEVEL SECURITY` on the table, and a policy
  `USING (tenant = NULLIF(current_setting('app.tenant_id', true), '')::uuid)` with the same `WITH CHECK`. A missing or empty setting
  matches nothing: **it fails closed**.
- **The tenant is passed as a transaction-local setting** (`SET LOCAL`, or `set_config(name, value, true)`), once per transaction.
- **The application must not connect as a superuser or as the table's owner.** A separate role without `BYPASSRLS`.

## What was found
| Case | Result |
|---|---|
| Superuser, no tenant given | **5 of 5 rows.** A superuser is not subject to the policy, `FORCE` or not |
| Application role, no tenant given | 0 rows (fails closed) |
| Application role, tenant A, `SELECT` with no `WHERE` | 3 rows of 5: the forgotten filter is contained |
| Application role, tenant B, the same query | 2 rows of 5 |
| Application role, tenant A, inserting a row for tenant B | Refused: *new row violates row-level security policy* |
| **Through PgBouncer in transaction mode:** one client sets the tenant for its **session** (`SET`), another client that never set one counts | **3 rows: the second client sees the first's tenant.** The setting stayed on the shared server connection |
| Same pooler, restarted; a client sets the tenant with `SET LOCAL` inside a transaction, then another client that set nothing counts | 2 rows for the first, **0** for the second: nothing leaked |

## Consequences
- **Every service connects as `postgres`, a superuser, in docker-compose** (and in the chart's examples). RLS would do nothing
  for any of them. Adopting it means a role per service with only the rights it needs, created by the database bootstrap, and
  connection strings that use it. That is the largest part of the work and is useful on its own.
- **A session-level tenant setting through a transaction pooler is a cross-tenant data leak,** not a bug that fails loudly: the
  wrong rows come back and nothing errors. The setting has to be transaction-local and applied on the same connection as the
  statements it governs; with EF Core that means setting it at the start of each transaction (an interceptor or an explicit
  transaction around every unit of work), not once when a connection is opened.
- **PgBouncer's `TRACK_EXTRA_PARAMETERS` is not a defence.** It tracks a few startup parameters (the stack uses it for
  `search_path`); it does not track arbitrary custom settings set with `SET`.
- **Migrations and background consumers** run without a request and so without a tenant: they need an explicit decision
  (a role that bypasses the policy for migrations, a tenant carried in the message for consumers).
- **Performance:** a policy adds its predicate to every query on the table; with the tenant as the leading column of the indexes
  that is cheap, otherwise it is not. Not measured here.

## What is and is not verified
Verified by the drill, once, on PostgreSQL 16 with PgBouncer: every row of the table above. The pooler leak was produced
deliberately with a pool of one server connection, which makes it certain; with a larger pool it would be intermittent, which is
worse. Not verified: RLS with EF Core (an interceptor setting the tenant per transaction), with the stack's Dapper queries and
`search_path` handling, with a read replica, or with prepared statements in the pooler; the performance cost; the migration of
existing data to a first tenant.
