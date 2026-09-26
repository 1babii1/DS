# 10. Kubernetes migration: target architecture (not yet implemented)

## Status
Proposed - design only. `docker-compose.yml` remains the actual deployment; nothing in
this ADR is live. This documents Wave 1 of the reliability-roadmap planning artifact's
migration path, ahead of building it, so the shape is agreed before infrastructure work
starts rather than discovered mid-migration.

## Context
Every service today runs as exactly one docker-compose replica against one Postgres
container with no connection pooler in front of it. That's the right amount of
infrastructure for the platform's actual current scale - Wave 0 (backups, per-grant
idempotency, resilience on outgoing calls, explicit Kafka retention, UUIDv7) closed the
gaps that were free to close without adding new moving parts. Kubernetes is not one of
those free gaps: it trades a docker-compose file every engineer on this project can read
top to bottom for a control plane, a scheduler, and an entirely different local-dev story.
That trade is only worth making once a concrete trigger below actually fires - not because
"a large organization would use Kubernetes."

## Trigger (any one of these, not a calendar date)
- A service needs a second replica for real (load, or zero-downtime deploys), which the
  current single-instance-everywhere design has never had to support.
- Postgres connections become the actual bottleneck: today each replica opens its own
  Npgsql pool against a shared `max_connections=100`, and a second replica of anything
  makes that arithmetic worse before it makes anything faster.
- A deploy needs to happen without stopping the whole stack, which `docker compose up
  --build` cannot do today.

## Decision

**Aspire AppHost, not hand-written Helm charts, generates the Kubernetes manifests.**
This is a .NET platform; Aspire's AppHost already describes the same service topology
`docker-compose.yml` does today (which service depends on which, what each one's
connection strings/env vars are), and starting with Aspire 13.3 that same AppHost model
generates a Helm chart and Gateway API routes directly. One topology description for both
local dev and the cluster, instead of hand-maintaining `docker-compose.yml` and a
parallel set of Kubernetes YAML that drifts from it.

**CloudNativePG for Postgres**, not a hand-rolled StatefulSet or Patroni. It's a CNCF
project (graduated January 2025), manages failover through the Kubernetes API instead of
a separate etcd/Patroni stack, and reported failover times are in the 5-10 second range in
independent comparisons - acceptable for this platform's actual availability needs without
adopting a second consensus system on top of Kubernetes' own. Continuous backup to object
storage replaces `scripts/backup-postgres.sh`'s manual dump for this deployment target
(the script stays the right tool for docker-compose, which isn't going away for local
dev).

**PgBouncer in transaction mode, in front of Postgres**, the moment a second replica of
anything exists - not before, since one replica per service already fits comfortably
under `max_connections=100` today. Transaction mode is compatible with the
`pg_advisory_xact_lock` pattern already used (AuthService's key rotation, RewardsService's
welcome-bonus guard - both scoped to one transaction). Requires PgBouncer 1.21+ with a
non-zero `max_prepared_statements` if Npgsql's automatic prepared-statement caching stays
enabled, or transaction-mode pooling silently breaks prepared statements.

**Valkey replaces the `redis:alpine` image**, nothing else changes. Valkey is the
Linux Foundation fork of Redis (BSD-licensed, protocol-compatible, the default on AWS
ElastiCache) - a same-day image swap for HybridCache and the SignalR backplane, not a
client-code change, made now rather than later specifically because it's free and the
licensing trade only gets more relevant the more this platform depends on it.

**Gateway API replaces nginx's path-routing config**, using Envoy Gateway as the
implementation. The Kubernetes-maintained ingress-nginx controller reaches end-of-life on
March 24, 2026; Gateway API is the project's own recommended successor, with
`ingress2gateway` available to convert the existing routing rules mechanically rather than
hand-rewriting them. Envoy Gateway also gets this platform a real load-balancing/health-
check story (nginx today only does passive `max_fails`) and a place to put a global rate
limiter later, without a second product to operate for that.

**Two replicas per service, KEDA for the Kafka consumers.** Once PgBouncer and Gateway API
are in place, going from one replica to two is a one-line change per service - the
prerequisite work is what makes it safe, not the replica count itself. KEDA scales
consumer replicas by Kafka lag rather than CPU, capped at partition count for ordered
topics (Rewards, the hire saga) unless a topic is deliberately moved to Kafka's share-group
consumption model, which trades per-key ordering for higher parallelism and is out of
scope for this wave.

## Migration order
1. Aspire AppHost topology description, validated by deploying to a throwaway cluster
   alongside (not instead of) `docker-compose.yml` - both must produce a working stack
   before compose is retired.
2. CloudNativePG + PgBouncer, proven with a real failover drill (kill the primary pod,
   measure actual recovery time against the ~5-10s CloudNativePG reports elsewhere) before
   any service depends on it for real traffic.
3. Valkey swap (low-risk, can land independently of the rest).
4. Gateway API replacing nginx, migrated route by route via `ingress2gateway` output,
   diffed against the current `docker/nginx/nginx.conf` before cutover.
5. Second replica + KEDA, service by service, starting with whichever service's trigger
   condition actually fired.

## Consequences
- Local development changes: `docker compose up` today gets a whole stack running with
  zero cluster to manage. Whatever replaces it (`aspire run` against a local Kubernetes,
  or compose staying the dev-only path indefinitely) needs its own decision when this
  migration actually starts - not assumed here.
- New operational surface: cluster upgrades, node management, CloudNativePG's own
  failure modes, Gateway API's learning curve for whoever operates this next. Wave 0's
  "bujet of complexity" principle applies here more than anywhere else in the roadmap -
  this is the single biggest complexity increase in the whole plan, which is exactly why
  it's gated on a real trigger instead of scheduled.
- Nothing in this ADR is implemented. `docker-compose.yml`, `docker/nginx/nginx.conf`, and
  `scripts/backup-postgres.sh` remain the actual deployment until a trigger above fires and
  someone picks up the migration order.
