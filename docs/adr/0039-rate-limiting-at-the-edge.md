# 39. Rate limits at the one ingress, not in Redis

## Status
Accepted. Related: [0036](0036-redis-failure-and-sentinel.md), [0038](0038-load-shedding.md).

## Context
Each service limits writes (30 a minute) and the auth endpoints (5 a minute) per client address, in memory, with ASP.NET Core's
rate limiter. That counts per instance. With one instance it is the stated limit; with three behind a balancer each counts only
what reaches it, so the real limit is three times the configured one, and it varies with how requests happen to be spread. The roadmap
item was "distributed rate limiting through Redis".

## Decision
**Put the limit where the traffic is not yet spread: nginx, the only ingress.** Every request to every service passes it once,
so a limit there is a limit on the whole fleet whatever the number of instances. The services keep their own limiter as a
second layer (it is what protects one instance from traffic that does not come through nginx, and what the tests exercise).

- Writes (any method but GET, HEAD, OPTIONS) share one budget per client address **per service** (`/api/employees`, `/api/rewards`, ...), as when each service counted its own: 30 a minute with a burst of 10.
- POSTs to `/auth` and `/connect` get a tighter one: 5 a minute, burst 3. Other methods there are not limited.
- Over the limit: **429**. The numbers repeat the services' defaults and have to be kept in step by hand; that duplication is the
  price of the approach.

Alternatives: **Redis-backed limiter in each service** (a fixed-window counter in Lua; the usual answer). It would also be exact across
instances, but it adds Redis as a dependency to the services that do not use it today (Auth, Employee, Rewards), needs a policy for
Redis being down (fail open, which is no limit at all exactly when something is wrong; or fail to the in-memory one, which is the
problem again), and puts a network round trip on every request to do what nginx does for free in the process that already
holds the connection. **A limit divided by the number of instances** is brittle: it needs to know the count and assumes even spread.
Redis becomes the better answer if limits must be per user or per token rather than per address, which nginx cannot see, or if services
are reached other than through nginx.

## What was measured
The real `nginx.conf` in front of stub upstreams (`scripts/edge-limit-drill.sh`), one client:

| Requests | Result |
|---|---|
| 100 GET /api/employees | 100 passed (reads are not limited) |
| 100 POST /api/employees | 11 passed (one plus the burst of 10), 89 were 429 |
| 100 POST /api/rewards, straight after | 11 passed the limiter (the 502s are the missing stub upstream), 89 were 429: its own budget |
| 20 POST /auth/login | 4 passed (one plus the burst of 3), 16 were 429 |
| 20 GET /auth/login | 20 passed (only POSTs are limited there) |

## Consequences
- **The client address is the BFF's.** The frontend's server calls the backend, so every user arrives from the same address
  and shares one budget: 30 writes a minute for everyone together. The services' own limiters have the same property today
  (nothing forwards the end user's address), so this is not new, but it was hidden behind the limit being per instance and
  never hit. It needs the BFF to pass the user's address and nginx to trust it (`real_ip`); not done. Until then the numbers are a
  bound on the whole application's write rate, which is too low for real use and fine for a demo. This is the main thing to fix before
  anyone relies on this, and it is raised in the frontend issue.
- **A limit in nginx is invisible to the services' tests;** only the drill exercises it. It is not in CI.
- **nginx is one process on one machine here;** a second nginx would have its own counters (the same problem one level up).

## What is and is not verified
The drill above, once: the five rows. Not verified: the limit with several service instances behind nginx (the argument is
structural, nothing ran two); the interaction with the frontend's real traffic pattern (see the consequence above); the log line a
limited request produces.
