# DS — Directory & Employee Platform

A small distributed system for managing an organization's structure — departments,
locations, positions, and the employees assigned to them. Built as a portfolio project
to work through a specific set of distributed-systems problems end to end, not to be a
product: transactional outbox and idempotent consumers over Kafka, optimistic
concurrency, gRPC between services with resilience policies, local JWT validation, and
a semantic search layer over the same data using local embeddings.

## Services

| Service | Role | Protocols |
|---|---|---|
| **DirectoryService** | source of truth for org structure (departments, locations, positions), ltree hierarchy, pgvector semantic search | REST + gRPC server, Kafka producer |
| **AuthService** | OpenIddict OIDC provider (`authorization_code` + PKCE, `client_credentials`), ASP.NET Identity | REST + OIDC |
| **EmployeeService** | employee records, hiring, transfers, termination | REST, gRPC client → DirectoryService, Kafka producer |
| **AuditService** | append-only record of every event published on the bus | Kafka consumer, REST (read-only) |
| **NotificationService** | independent consumer group proving the event bus is a real fan-out, not wiring built for one reader | Kafka consumer |
| **McpServer** | read-only [MCP](https://modelcontextprotocol.io) tools (semantic search, org tree, employee lookup) for AI assistants | Streamable HTTP, JWT-authenticated |

All REST traffic goes through nginx at `/`; gRPC between DirectoryService and
EmployeeService is internal-only, never exposed to the host. See
[`docs/adr/`](docs/adr/) for why each of these decisions was made, not just what they
are.

## Running it

```bash
docker compose up -d
```

Brings up Postgres (with `ltree` and `pgvector`), Kafka (KRaft, no Zookeeper), Redis,
Ollama (pulls `nomic-embed-text` on first start), Seq, nginx, and all six services.
Migrations run in dedicated, gated containers before the service that needs them starts
— not inline at every boot, which would fight itself under `restart: always` if a
migration ever failed partway. Health checks (`/health/live`, `/health/ready`) gate
`docker compose`'s own view of readiness for every web service.

```bash
curl http://localhost/api/departments/roots        # 401 without a token — everything behind nginx requires one
```

`.env.example` documents the one thing worth overriding for a non-default local setup
(`POSTGRES_PASSWORD`); copy it to `.env` if you need to change it.

| Exposed on localhost | What |
|---|---|
| `:80` | nginx — every REST/OIDC endpoint and `/mcp` |
| `:5434` | Postgres (`platform` database, schema-per-service) |
| `:9092` | Kafka |
| `:11434` | Ollama |
| `:8081`, `:5341` | Seq UI / ingestion |

Internal-only (never published to the host): DirectoryService's gRPC port, Redis, and
every service's own HTTP port — nginx is the only way in.

## Tech stack

.NET 10 / ASP.NET Core, EF Core 10 + Npgsql, Dapper for read-heavy catalogue queries,
[CSharpFunctionalExtensions](https://github.com/vkhorikov/CSharpFunctionalExtensions)
(`Result<T, Error>` end to end, no exceptions for expected failure), FluentValidation,
Serilog → Seq, OpenIddict, Confluent.Kafka, HybridCache over Redis, Testcontainers +
Respawn for integration tests, k6 for load tests.

## What each service does with concurrency and failure

Not an exhaustive list — the point is these were deliberately designed for, not
discovered as bugs after the fact:

- **Transactional outbox** in every producer: the domain write and the outbox row
  commit in one transaction, a background publisher drains it to Kafka. No dual-write
  gap between "saved to the database" and "the rest of the system finds out."
- **Idempotent consumers**: AuditService dedupes by the outbox message's own id via a
  unique index, so at-least-once Kafka delivery can't double-record an event.
- **Retry, then dead-letter, then stall**: a message AuditService can't process gets
  three attempts, then goes to a `dead_letters` table (readable at
  `GET /api/audit/dead-letters`) rather than being silently dropped. If even that write
  fails — the database itself being down — the consumer stalls on that exact offset and
  retries every few seconds instead of skipping past a record it never saved.
- **Optimistic concurrency** on Employee via Postgres's own `xmin`: two concurrent
  transfers of the same employee produce one success and one `409 Conflict`, never a
  silent lost update.
- **gRPC resilience**: EmployeeService's calls to DirectoryService carry retry,
  a circuit breaker, and a timeout — a DirectoryService blip doesn't fail every hire
  outright, and a real outage surfaces as `503`, not an opaque `500`.
- **Rate limiting** on `/auth/login`, `/auth/register`, and semantic search — partitioned
  per client IP, not a shared global bucket.

## Testing

```bash
dotnet test backend/backend.slnx
```

39 tests across five integration test projects (DirectoryService, EmployeeService,
AuditService, AuthService, McpServer), each spinning up its own Postgres via
Testcontainers rather than sharing state or mocking the database. What they cover is
deliberately not "everything" — see each project's own tests for what's in scope and
why; a few things (a live database going down mid-request, for instance) are exercised
by hand against the real stack instead of automated, and that's noted where it applies
rather than left implicit.

CI (`.github/workflows/ci.yml`) builds and runs the full suite on every push and pull
request, plus a separate job that builds every service's Docker image without pushing
it, to catch a broken Dockerfile before `docker compose up` does.

```bash
cd load-tests/k6
docker run --rm --network host -v "$(pwd)":/scripts -w /scripts grafana/k6 run main.js
```

## Project layout

```
backend/
  {Service}/
    {Service}.Domain             # entities, value objects, invariants — no framework dependencies
    {Service}.Application        # handlers, validation, Result<T, Error>
    {Service}.Infrastructure.*   # EF Core, Dapper, gRPC clients, Kafka
    {Service}.Web                # controllers, Program.cs, DI wiring
    {Service}.IntegrationTests   # Testcontainers + WebApplicationFactory
  Shared/                        # Envelope, Error, outbox, health checks, CORS — nothing service-specific
docs/adr/                        # architecture decisions, written from why, not backfilled generically
load-tests/k6/                   # load test scenarios
docker/                          # nginx, Postgres init
frontend/                        # Next.js client (separate concern, own README)
```

## Status

Backend is functionally complete for its current scope and load-tested. `docs/adr/`
covers the two decisions written up so far (schema-per-service, transactional outbox);
the rest of the architecture's reasoning currently lives in commit messages rather than
in a document — worth finishing, not yet done.
