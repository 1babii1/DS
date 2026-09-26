# 14. Multi-region deployment: target architecture (not yet implemented)

## Status
Proposed - design only. This platform runs in one region (in practice, one docker-compose
host); nothing here is live. Wave 5, the last and most expensive wave of the
reliability-roadmap planning artifact - deliberately gated on an external requirement, not
a technical milestone, because losing a whole region is the failure mode every earlier wave
already spends significant effort not causing internally.

## Context
Every earlier wave (cells, Kafka replication, Postgres failover) reduces how much damage a
failure *inside* one region can do. None of them help if the region itself becomes
unreachable. Building for that is a materially different, materially more expensive
problem - active-passive or active-active data replication across a real network boundary,
with real speed-of-light latency between them - and isn't worth taking on speculatively.

## Trigger
- A contractual or regulatory availability requirement that explicitly survives losing a
  region (most "just be reliable" pressure does not actually require this - it requires
  Waves 0-4 done well, which is a much cheaper bar to clear).
- Measured latency complaints from users in a region far from wherever this platform
  actually runs, where the fix is genuinely "run closer to them," not "optimize the
  existing region."

## Decision

**Consistency class decides replication mode per data owner - not one global choice.**
This follows directly from the consistency map already established for this platform:

| Data | Consistency | Cross-region mode |
|---|---|---|
| Auth (accounts, sessions, signing keys) | strong | active-passive |
| Rewards (wallets, transactions) | strong | active-passive |
| Directory/Employee | causal | active-active, tolerant of brief staleness |
| Search, Notifications | eventual | active-active |

Money and identity get **active-passive**: one region takes writes, the other replicates
asynchronously and is promoted deliberately on failover. Active-active for a strongly
consistent ledger would need a real conflict-resolution scheme (CRDTs, last-writer-wins)
that doesn't exist for this data and shouldn't be invented under pressure - a naive
last-writer-wins on a wallet balance is a lost transaction, not a merge. Data that's
already modeled as eventually-consistent projections (SearchService's index,
NotificationService's feed) can run active-active safely, because they're rebuildable by
design already.

**Edge routing decides region by health, not by request content.** Anycast/CDN-level
routing sends a client to the nearest healthy region; it does not need to understand what's
inside a request to do that, which keeps the routing layer itself simple and low-risk.

**Kafka event replication (MirrorMaker 2) mirrors both directions for eventual-consistency
topics, one direction only for anything feeding the active-passive stores** - matching the
same asymmetry as the data layer itself, not a single blanket replication policy.

## Consequences
- RTO/RPO for a region failover is a decision this ADR does not make - it depends entirely
  on the actual trigger (a contractual SLA sets the number; a latency complaint doesn't need
  a failover story at all, only closer edge routing). That number gets defined against the
  real requirement when this wave actually starts, not guessed at here.
- This is the most expensive wave in the plan by a wide margin: duplicate infrastructure
  per region, cross-region network cost, and a failover procedure that has to be drilled
  regularly to be trusted (an untested failover is exactly the same problem Wave 0's backup
  script called out for restores - a plan nobody has run is a hypothesis).
- Nothing here is implemented. The platform runs in one region until an external
  requirement above actually exists.
