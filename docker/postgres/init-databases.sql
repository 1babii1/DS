-- Runs once, on first Postgres container start (docker-entrypoint-initdb.d).
-- Single "platform" database shared by all services, isolated by schema.
CREATE EXTENSION IF NOT EXISTS ltree;

CREATE SCHEMA IF NOT EXISTS directory;
-- AuthService (phase 1), EmployeeService (phase 2), AuditService (phase 3) add their own schemas here as they land.
