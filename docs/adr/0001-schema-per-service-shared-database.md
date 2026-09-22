# 1. Schema-per-service in one shared Postgres database

## Status
Accepted

## Context
Each service (DirectoryService, AuthService, EmployeeService, AuditService) owns its own
data and should not reach into another service's tables directly. The usual textbook
answer is "one database per service." Running a separate Postgres *instance* per service
is real operational overhead for a project this size - more containers, more connection
pools, more backup targets - for isolation this project doesn't currently need (single
deployment, no per-service scaling requirements, no separate teams).

## Decision
One Postgres instance, one database (`platform`), with a dedicated schema per service
(`directory`, `auth`, `employee`, `audit`) declared in `docker/postgres/init-databases.sql`.
Each service's EF Core `DbContext` sets `HasDefaultSchema(...)` to its own schema and
connects with `Search Path=<schema>,public` in its connection string. No domain
service's code references another service's schema - the one deliberate exception is
McpServer, which reads `directory.*` and `employee.*` directly through Dapper. It isn't
a domain service with data of its own; it's a read-only reporting layer over both, and
crossing the schema boundary is the point rather than a leak (see
[0008](0008-mcp-server-cross-schema-reporting-layer.md)).

## Consequences
- Logical isolation (schema boundaries, no cross-schema foreign keys) without the
  operational cost of separate database instances.
- All services currently share one Postgres superuser (`postgres`) rather than
  per-service database roles - a real gap if this needed to run multi-tenant or with
  untrusted service code, acceptable for a single-deployment portfolio project. Flagged
  as a known simplification, not something to fix reflexively.
- Splitting a schema out to its own physical database later is a straightforward
  `pg_dump`/`pg_restore` of that schema, not a rearchitecture - the schema boundary is
  already the right cut line if it's ever needed.
