-- Runs once, on first Postgres container start (docker-entrypoint-initdb.d).
-- Single "platform" database shared by all services, isolated by schema.
CREATE EXTENSION IF NOT EXISTS ltree;
CREATE EXTENSION IF NOT EXISTS vector;

CREATE SCHEMA IF NOT EXISTS directory;
CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS employee;
CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS rewards;
CREATE SCHEMA IF NOT EXISTS notification;
CREATE SCHEMA IF NOT EXISTS search;

-- The schema registry keeps its schemas here (see docker-compose.yml, ADR 0023); it is not a service schema.
CREATE DATABASE registry;
