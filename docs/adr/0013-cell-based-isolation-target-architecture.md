# 13. Cell-based isolation, Temporal, and Citus: target architecture (not yet implemented)

## Status
Proposed - design only. This platform runs as one stack serving all traffic; there are no
cells, no Temporal workflows, and no distributed Postgres. Wave 4 of the
reliability-roadmap planning artifact, and the largest architectural change it proposes -
gated hard on real triggers, not a target to build toward on its own schedule.

## Context
Today, one failure in any shared component (the single Postgres instance, the single Kafka
broker) affects every client and every feature at once - there is no blast-radius
boundary anywhere in the platform. That's an acceptable trade at current scale (Wave 0-2
close the gaps that make the shared components themselves more durable); it stops being
acceptable only once a single incident actually taking down 100% of usage is a real,
recurring cost, not a hypothetical one.

## Trigger
- A real incident where a single bad deploy or data issue affected every client/tenant at
  once, where a cell boundary would have contained it to a fraction of them.
- The hire saga (today: choreography across EmployeeService, DirectoryService,
  AuthService, RewardsService per ADR 0003) grows past what makes choreography legible -
  more steps, more compensations, or a real requirement for human-in-the-loop approval
  mid-saga.
- RewardsService's ledger (`Wallet`/`Transaction`) outgrows a single Postgres instance's
  write capacity, which is the concrete signal for "shard this," not a scale estimate made
  in advance.

## Decision

**Cells over naive horizontal scaling.** A cell is a full vertical slice - its own service
instances, its own data shard - for a disjoint subset of clients. AWS's own published
numbers on shuffle sharding (Builders' Library) put the blast radius of a naive
load-balanced failure at ~100% of clients affected, versus low single digits with shuffle
sharding on comparable hardware; that gap is the entire argument for this wave.

**Citus before YugabyteDB, and only for the Rewards ledger first.** Citus is a Postgres
extension (not a fork), so it keeps the query layer, tooling, and operational knowledge
this platform already has; sharded by `employee_id`, which is the natural write-locality
key for `Wallet`/`Transaction`. YugabyteDB (Apache 2.0, reuses Postgres's own query layer)
is the fallback only if multi-region *write* availability for strongly-consistent data
becomes a real requirement - CockroachDB was considered and rejected on licensing (no
longer fully open source) and Vitess was rejected as MySQL-ecosystem tooling this platform
has no other use for.

**Temporal replaces the hire saga's choreography, only once the saga outgrows
choreography's legibility.** Today's two-service choreography (ADR 0003) is the right call
at its current size - there's no "brain" to inspect because there's nothing complex enough
to need one yet. Temporal's durable execution model (workflow state, timeouts, and
compensations as ordinary code) is the answer once that stops being true; migrating means
replacing the choreographed Kafka events with an explicit Temporal workflow definition, not
bolting Temporal onto the existing event shape.

**A cell router, deliberately kept as the most boring component in the system.** It holds
a small, cacheable client-to-cell mapping and nothing else - no business logic, no
database write path of its own. A router that becomes a new single point of failure
defeats the entire purpose of this wave; on any internal failure it falls back to its last
known-good mapping rather than refusing to route.

## Consequences
- This is real complexity, not a free architectural upgrade: N cells means N times the
  operational surface for anything that isn't shared infrastructure. Wave 0's "budget of
  complexity" principle applies most strongly here.
- A cell boundary only helps if the thing that fails is actually cell-scoped - a shared
  Kafka cluster or shared Kubernetes control plane failing still takes every cell down with
  it, so this wave's value depends on Waves 1-2 already having reduced how much is still
  genuinely shared.
- Nothing here is implemented. The hire saga stays choreographed, the Rewards ledger stays
  on one Postgres instance, and there is exactly one cell (the whole platform) until a
  trigger above fires.
