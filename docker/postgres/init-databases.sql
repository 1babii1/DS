-- Runs once, on first Postgres container start (docker-entrypoint-initdb.d).
-- Single "platform" database shared by all services, isolated by schema.
CREATE EXTENSION IF NOT EXISTS ltree;

CREATE SCHEMA IF NOT EXISTS directory;
CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS employee;
-- AuditService (phase 3) adds its own schema here as it lands.
