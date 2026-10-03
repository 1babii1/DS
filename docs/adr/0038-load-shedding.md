# 38. Load shedding: turn requests away quickly instead of answering all of them slowly

## Status
Accepted. Related: [0036](0036-redis-failure-and-sentinel.md) (the same stance for a dependency: bounded cost when something is
wrong), [docs/architecture/degradation-matrix.md](../architecture/degradation-matrix.md).

## Context
No HTTP service limited how much it worked on at once. A service whose capacity is bounded by something small (a database
connection pool, a downstream call) and which is offered more than that does not slow down gracefully: Kestrel accepts every
request, they all queue for the small thing, and each waits behind all the others. Latency grows with the backlog, callers time
out, and the work already done for them is wasted. Past capacity, the service answers everyone slowly, which for the callers is the same as not at all.

## Decision
**Every HTTP service puts a concurrency limit and a short queue in front of its handlers** (`Shared.Resilience.LoadShedding`,
configured under `LoadShedding:`; defaults 256 at once, 64 waiting, a wait of at most one second).
- Past the limit a request waits in the queue; if the queue is full, or it waits longer than the maximum, it is refused at once
  with **503**, `Retry-After: 1` and the usual error envelope (`service.overloaded`).
- **The queue serves the newest first**, so when it fills it is the oldest waiter, whose caller is closest to giving up, that is
  dropped. (Measured against oldest-first on the same load: the accepted p95 was 70 ms against 102 ms.)
- **Never shed:** health probes (shedding them makes an overloaded instance look dead and invites a restart at the worst moment),
  the SignalR hubs and the MCP endpoint (long-lived connections would hold a place for as long as they last).
- Placed right after exception handling, before authentication and routing, so a refused request costs almost nothing.
- Applied to Directory, Auth, Employee, Rewards, Notification, Audit and Search. McpServer is not covered.

## What was measured
In this process, on a real Kestrel: an endpoint that does 50 ms of work through a gate of 8 (a stand-in for a connection pool),
80 clients asking back to back for 4 s, ten times what the gate serves:

| | Completed | Turned away | p95 of the completed |
|---|---|---|---|
| No limit | 712 | 0 | **503 ms** |
| With shedding (8 at once, 8 waiting, 150 ms wait) | 648 | 12,288 | **70 ms** |

The service completed 91% as much work and the callers it served waited a seventh as long. Below capacity (6 clients) nothing
is turned away; the health probe answered every time while 80 clients hammered the rest.

The 12,288 refusals are the other side of the table, and they are a lot: these clients retried after 20 ms. Each refusal is
cheap for the server, but clients that retry immediately turn a shed into a storm. `Retry-After` is the answer, and a client
that ignores it gets the same treatment on every attempt. Nothing in the frontend honours it yet.

## Consequences
- **The default limits are not tuned to any service.** 256 is above what any of these services does at once today; the setting
  matters when it is lowered to a service's real capacity, which has to be found per service by measurement (not done here).
- **Refusing is a decision to lose requests.** A write refused with 503 is not done; callers need to retry, and writes that can
  be retried safely need the idempotency key (ADR 0037 for the hire; the grant has had one). Writes without one are not safe to retry.
- **This is not a bulkhead.** One class of requests can still use up all the places for the others (a flood of searches can
  starve logins). Separate limits per class of request are the next step and are not done.
- **Per instance:** each instance counts its own concurrency. With several, the total is their sum, which is the right thing
  for protecting each one and says nothing about the system.

## What is and is not verified
The three tests above, run three times each, stable: overload (shedding clearly better, nothing shed without it, a retry hint
present, the server's count matches the clients'), no shedding below capacity, probes never shed. Mutation-checked:
oldest-first still passes the tests (the difference is real but small; documented rather than asserted).

Not verified: any real service under load (the numbers are from a synthetic endpoint; no k6 run against the stack), a service whose
capacity is a real database pool, the frontend's reaction to 503 with `Retry-After`, the effect on the SignalR hubs when the
rest of the service is shedding.
