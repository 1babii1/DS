# 9. Postgres backup and restore

## Status
Accepted

## Context
Every service's data lives in one Postgres instance, schema-per-service (ADR 0001) -
including Rewards' currency ledger and Auth's account/signing-key data. That instance runs
as a single container with one volume and, until now, no backup of any kind: losing the
volume (a bad `docker volume rm`, a corrupted disk, a botched migration) loses every
service's data at once, with no way back. This was the first item flagged when auditing the
platform for what a "very large, reliability-focused organization" would already have that
this one didn't (see the reliability-roadmap planning artifact) - it's also the simplest one
to close, since it needs no new infrastructure, only a script and a documented procedure.

## Decision

**`pg_dump`/`pg_restore` in custom format (`-Fc`), not plain SQL.** Custom format is
compressed, supports parallel restore (`pg_restore -j`), and lets a restore be scoped to
one schema/table without re-running the dump - useful for the schema-per-service layout
here, where a bad migration might only need one service's schema rolled back, not every
service's.

**`scripts/backup-postgres.sh`** runs `pg_dump` inside the running `postgres` container as
the `postgres` OS user (the same trust-auth path the container's own healthcheck already
uses) and copies the resulting dump out via `docker cp` - no Postgres client tooling
required on the host, and no password ever passes through the script's own arguments or
shell history.

**`scripts/restore-postgres.sh` restores into a new scratch database, never into
`platform` directly** - it refuses that database name outright. A real disaster-recovery
restore (replacing `platform` itself) means stopping every service first, since they all
hold open connections that would otherwise race the restore; that's a different, more
disruptive operation than "prove this backup is restorable," and is documented below as a
manual procedure rather than scripted, so it's never one command away from being run by
accident against a database every service is actively using.

**No automated schedule yet.** This is a script to run, not a cron job or a systemd timer -
this platform has no production deployment target today, so "automated on what schedule,
alerting whom on failure" has no real answer yet. Wiring it to a schedule is explicitly
deferred to whatever actually operates a production instance of this platform (see the
roadmap's Kubernetes/CloudNativePG wave), not invented here as a guess.

## Disaster-recovery procedure (manual, deliberately not scripted)

1. Stop every service that connects to `platform` (`docker compose stop` for every service
   except `postgres` itself) - they must not hold connections during the restore.
2. `docker exec -u postgres postgres psql -c "SELECT pg_terminate_backend(pid) FROM
   pg_stat_activity WHERE datname = 'platform' AND pid <> pg_backend_pid();"` to be sure
   nothing is still attached.
3. `docker exec -u postgres postgres psql -c "DROP DATABASE platform;"` then `CREATE
   DATABASE platform;`.
4. `docker cp <dump-file> postgres:/tmp/restore.dump && docker exec -u postgres postgres
   pg_restore -d platform /tmp/restore.dump`.
5. Restart every service stopped in step 1.

## Consequences
- RPO is "since the last manual backup run" - there is no continuous WAL archiving/PITR
  here, only point-in-time dumps. A CloudNativePG-managed instance with continuous backup
  (the roadmap's target architecture) would close that gap; this ADR only closes "there is
  no backup at all."
- `restore-postgres.sh`'s scratch-database restore was written but **not live-verified in
  this session** - the sandboxed docker environment this work ran in lost its running
  containers mid-session (unrelated to this change; the whole dev stack disappeared, not
  just Postgres), so the actual dump → restore → row-count-diff round trip could not be
  completed and confirmed here. Both scripts should be run once against a real
  docker-compose Postgres before this ADR's claim of "restorable" is treated as verified
  rather than "written to the same standard as everything else in this codebase, but
  unproven." This is the honest state to hand off, not a gap to paper over.
- The dump captures data only, not the Kafka/Elasticsearch state that several projections
  (Notification's `AccountLookup`, SearchService's index, RewardsService's own
  `AccountLookup`) are rebuilt from - a restore that rolls Postgres back further than
  Kafka's retention window (`KafkaTopicProvisioner`'s now-explicit 7-day `retention.ms`)
  leaves those projections unable to fully recover from replay alone.
