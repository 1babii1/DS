# 45. Bulkheads, and what a real service's limit looks like when measured

## Status
Accepted. Extends [0038](0038-load-shedding.md), which left two things open: one class of request could still starve another, and
the limits were defaults nobody had measured.

## Context
0038 put one overall limit in front of every service. Two weaknesses were written down with it. A flood of one kind of request
(a slow report, a login that hashes a password) can use up every place and starve the quick requests that share the process, so
the limit protects the service and not its users. And the number, 256, was a guess: tuned to nothing.

## Decision
### Bulkheads
**A class of request can have a limit of its own inside the overall one** (`LoadShedding:Classes:<name>`: path prefixes, a
maximum number at once, a queue). A request of a class needs a place in its class and a place overall; the class refuses first, so the
slow work fills its own compartment and the overall limit stays free for everything else. Refusals are counted by who refused
(`ShedBy`), which says whether it was the class or the whole service that was full.

First use: **AuthService's credential endpoints** (`/auth/login`, `/auth/register`, `/auth/forgot-password`,
`/auth/reset-password`, `/connect/token`) are a class of 16 with a queue of 16. Password hashing is the expensive work an
unauthenticated caller can ask for in bulk; it now fills a compartment and not the service. The signing keys other services
validate tokens with are deliberately not in it, because they are what everything else depends on.

### Measured limits
`load-tests/k6/capacity.js` ramps virtual users up to 600 against one instance and reports requests served per second, the
latency of those, and the requests turned away. Run against **DirectoryService, one instance limited to one CPU**, reading
`/api/departments/roots` (a cached read that needs a token), with Postgres, PgBouncer and Redis of the dev stack, once per setting:

| Limit (at once / waiting) | Served per second | p95 of those served | Turned away |
|---|---|---|---|
| Effectively none (5000 / 5000) | 1,240 | 372 ms | 0 |
| 32 / 32 | 1,389 | 286 ms | 0 |
| 8 / 8 | 757 | 400 ms | 27,518 (32% of requests) |

The reading: one CPU served about 1,200 requests a second from five users; adding hundreds more only queued them. A limit of 32
was a little better than none (12% more served, a quarter lower p95), with nothing refused because a wait of up to 200 ms was
enough to absorb the burst. A limit of 8 was **worse on both counts**: it refused a third of the traffic and the p95 of what was served
did not improve. A limit set below what the service can really do in parallel throws away capacity.

## What was measured, in a bulkhead test
Real Kestrel, an overall limit of 8; 40 clients flood a 300 ms endpoint while another client sends quick requests every 20 ms:

| | Quick requests served | Quick requests refused | Quick p95 |
|---|---|---|---|
| No class (the flood uses the overall limit) | 9 | 111 (93%) | 197 ms |
| The flood has a class of 3 | 131 | 0 | 11 ms |

The flood was refused 4,425 times by its own class and the overall limit refused nothing.

## Consequences
- **The defaults stay as they were** (256 and 64): one run on one machine does not justify changing them, and the 32 versus none
  difference is of a size a second run might erase. What is recommended is the method: run `capacity.js` against the instance, find
  the load where throughput stops rising, and set the limit above the number of requests that are in the service at that point
  (here a few tens), never below it. The k6 client and the service shared the machine, so the absolute figures are not a capacity plan.
- **A class is a decision about what is expensive,** and a wrong guess is a self-inflicted outage for that kind of request. Only
  one class exists, and it is the one with an obvious cost.
- Classes are matched by path prefix, so a route moved without updating the configuration silently leaves its class.
- Still per instance, as in 0038.

## What is and is not verified
The bulkhead behaviour by two tests (the starvation comparison above, and a request outside every class limited only by the overall
limit); mutation-checked (ignoring the classes brings the starvation back). The shipped Auth configuration by two tests. The capacity
table above: one run per row, so the differences between the first two rows are indications.

**The credentials class under a real login flood** (`CredentialsFloodTests`: the real AuthService on a real Postgres, 24 clients logging
in with the right password of a confirmed account, so that every request hashes it, for 5 seconds, while a probe asks for the discovery
document every 30 ms; the overall limit set to 8 so that it can be reached, the class shrunk to 3 so that it fits inside it):

| | Discovery document served | Refused | Logins completed |
|---|---|---|---|
| No class (its limit set so high it is none) | **1** | 141 | 245 |
| The credentials class | **142** | 0 | 103 |

Without the class the flood used every place and the discovery document, which every other service and client fetches, was refused 99%
of the time. With it, nothing was refused. The price is paid where intended: the logins themselves ran at about 21 a second against 49,
because at most 3 hash at once. The shipped class allows 16, not 3, and the right number for logins is the same question as every other
limit in this ADR: found by measurement, not by this test, whose numbers only show the direction.

Not verified: classes in the other services; the tight limit with a larger `MaxQueueWait`, which might behave differently; a service
whose bottleneck is the database rather than the CPU.
