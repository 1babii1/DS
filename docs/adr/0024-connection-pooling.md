# 24. A connection pooler in front of Postgres

## Status
Accepted. Related: [0001](0001-schema-per-service-shared-database.md) (one Postgres, a schema per service).

## Context
Every service instance keeps its own Npgsql connection pool, and a Postgres connection is a whole server process. Eight
services share one Postgres whose default ceiling is 100 connections. The arithmetic is simple and unforgiving: instances
times pool size across services passes the ceiling long before the machine is busy, and the failure is not graceful: the
next client is refused ("sorry, too many clients already") and a request fails that would only have had to wait.

## Decision
**PgBouncer in transaction mode between the services and Postgres.** A server connection belongs to a client only for the
length of one transaction; between transactions it serves someone else. 20 real connections serve hundreds of clients, and
the excess waits in the pooler's queue instead of being refused.

- **Runtime connections go through it, migrations do not.** The EF migration bundles take session-level locks, which
  transaction pooling does not keep, so they connect directly.
- **Three things transaction pooling breaks, each checked rather than assumed:**
  1. *The search path.* DirectoryService's Dapper queries rely on `Search Path=directory,public` set at connection start
     (the model's own SQL is schema-qualified, the Dapper SQL is not). The pooler has to track that startup parameter
     (`track_extra_parameters`) or it is silently lost when a server connection changes hands. Checked by connecting
     through the pooler with and without the option, and by the k6 hire chain, which exercises those queries.
  2. *Prepared statements.* Npgsql prepares statements per connection; `max_prepared_statements` makes PgBouncer
     (1.21+) track them at the protocol level. The stack runs 1.26.
  3. *Session-level locks and settings.* The one advisory lock in the code (AuthService's signing-key rotation) is
     `pg_advisory_xact_lock`, transaction-scoped, so it is safe. There is no `LISTEN`, `SET` or temp table in application
     code (searched).
- **Alternative considered: PgCat**, which also routes reads to replicas. That is the next roadmap item (replicas); the
  pooler can be swapped then, and this ADR's constraints (search path, prepared statements, session locks) carry over.

## Measured
`scripts/pool-drill.sh`: 300 concurrent clients each running a 20 ms transaction, for 10 seconds, against the Postgres
that has `max_connections = 100`:

| | Result |
|---|---|
| Straight at Postgres | refused: `FATAL: sorry, too many clients already` |
| Through PgBouncer (pool of 20) | all 300 served, **0 failed transactions**, ~960 transactions/s, average latency 311 ms |

The latency is the queue, and it is the price: 20 server connections at 20 ms each cap throughput near 1000/s, and 300
clients share that. It is an honest queue instead of an error. The same 20 ms transactions straight at an idle Postgres
take about 20 ms; the pooler trades latency under overload for not failing.

Load test (`load-tests/k6`, warm second run, one run each so no variance estimate): p95 roots 4.2 ms (4.0 direct), search
22.2 ms (17.5), writes 12.6 ms (11.7): within what one run can distinguish from noise, a little higher. The first run
after the services were recreated showed 744 ms on writes and 36/52 ms on reads; the next run did not. I take it for cold
start (new processes, empty caches) but did not separate that from the pooler by a controlled run, so treat it as unexplained
until it is.

## Consequences
- **One more component on the path of every query.** Its failure is a database outage for the services; it restarts
  automatically and has a health check, but it is a single instance (HA pooling is out of scope here).
- **The pool size is a decision, not a default.** 20 is where this laptop's Postgres is comfortable; the right number is
  the database's real parallelism (cores, I/O), not the number of clients. Raising it moves the queue into Postgres.
- **Monitoring is not wired:** `SHOW POOLS` / `SHOW STATS` work (`docker exec pgbouncer ...`), but there is no exporter,
  so the queue length (`cl_waiting`) and `maxwait` are not on a dashboard. That is the first thing to add before relying on
  it.
- **Per-connection state does not survive between transactions.** Any new code that needs session state must use a
  transaction-scoped form or connect directly.

## What is and is not verified
Run live: all seven services recreated on the pooler, healthy; the k6 hire chain over it (499/499 checks, 0% failed);
`SHOW POOLS` shows 12 client connections served by 2 server connections at rest; the drill above. Not verified: behaviour when
the pooler restarts under load; a failover of Postgres behind it (roadmap); the effect of the pool size on the
write-heavy parts under real contention.
