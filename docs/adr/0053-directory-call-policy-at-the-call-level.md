# 53. The Directory call's policy sits on the call, and the breaker is told how far back to look

## Status
Accepted. Follows [0051](0051-network-faults-between-employee-and-directory.md) (item 4) and amends [0052](0052-directory-call-deadlines-and-fast-rejection.md):
the numbers of 0052 (8 s in all, 3 s an attempt, no retry of a refusal) stay; where the policy sits and two of its numbers change.

## Context
0051 found that a refused connection (nothing listening) raised `Unavailable: Error connecting to subchannel` inside the gRPC client's connection
manager, before any HTTP handler ran, so a retry and a circuit breaker built as HTTP handlers never saw it: 12 retries in 28,737 failures. That is
the commonest failure of a dependency, and the policy written for the call did not govern it.

## Decision
- **The policy is a client interceptor around the unary call** (`DirectoryGrpcResilienceInterceptor`), not an HTTP handler. Order: total deadline (8 s),
  retry (3, 200 ms doubling), circuit breaker; one attempt is bounded by the call's own gRPC deadline (3 s). One singleton holds the breaker.
- **Only "the dependency is unwell" is retried and counted:** `Unavailable` and `DeadlineExceeded`. A `NotFound`, `PermissionDenied` or `Unauthenticated`
  is an answer; it is returned at once and does not move the breaker. The breaker's refusal and the total deadline are turned into `RpcException`s
  (`Unavailable`, `DeadlineExceeded`), so the adapter above still sees only gRPC statuses.
- **The breaker looks back 10 s, not 30,** and **stays open 5 s, not 15.** Both were measured, below.
- **Retry and breaker events are logged** in the form Polly's own telemetry writes, since the HTTP handler that wrote them is gone.

Alternatives: **gRPC's built-in retry policy** (retries `Unavailable` without Polly, but there is no breaker in it, and the two would multiply attempts),
**a Polly handler plus a gRPC retry** (the same multiplication), and **leaving it and documenting the blind spot** (what 0051 did).

## Measured
Same drill (`scripts/toxiproxy-drill.sh`), 30 s a step, with the breaker at 10 s of memory and 15 s open:

| step | hires ok | failed | p50 / p95 / max ms | first ok at | retries | breaker opened / half-open / closed |
|---|---|---|---|---|---|---|
| baseline | 11309 | 0 | 9 / 15 / 1175 | 1.2 s | 0 | 0 / 0 / 0 |
| latency 500 ms | 228 | 0 | 532 / 605 / 612 | 0.4 s | 0 | 0 / 0 / 0 |
| latency 2 s | 60 | 0 | 2114 / 2187 / 2205 | 2.1 s | 0 | 0 / 0 / 0 |
| latency 6 s | 0 | 44332 (5 took over 1 s) | 2 / 3 / 6672 | none | 9 | 2 / 1 / 0 |
| connection reset | 0 | 63588 | 2 / 3 / 222 | none | 2 | 2 / 2 / 0 |
| directory unreachable | 0 | 60857 | 2 / 3 / 205 | none | 2 | **2 / 2 / 0** |
| recovered | 7441 | 31473 | 2 / 8 / 203 | 15.0 s | 1 | 1 / 2 / 1 |

1. **A refused connection now reaches the policy:** the breaker opens and probes during the "unreachable" step, which under the HTTP handlers it never did.
2. **The window matters as much as the threshold.** The breaker opens at half of the calls in its window. After a busy, healthy half minute (6,500 calls in the
   baseline step), twelve hires waiting 8 s each never made half of 30 s of calls, and every one of them waited the full 8 s: seen twice. With 10 s of
   memory the same sequence opened the breaker after the first slow wave and refused 18,867 calls in 2 ms. This is a property of ratio breakers, not of this
   one: a dependency that goes silent also *reduces* the traffic that would prove it, while old successes still count.
3. **Recovery is bounded by how long the breaker stays open.** With 15 s the first success after the network returned was at exactly 15.0 s. With 5 s
   (run over three steps of 20 s: baseline, unreachable, recovered) it was at 5.0 s, with 62 retries and 20 calls slower than 1 s during the outage (the
   probes). A refused connection costs nothing to probe; a probe against a slow dependency costs up to 3 s, one call every 5 s.
4. **A guess that did not hold:** the library's default maximum reconnect back-off is 120 s, and I expected a long outage to leave the channel idle long
   after the Directory was back. A 75 s outage under the old policy recovered at 0.0 s. The setting was added, measured against that, and removed.

## Consequences
- **A shorter memory is more nervous.** Five calls and half failing within 10 s now open it; a burst of transient errors in a quiet service can open it
  for 5 s. The minimum of five calls is what guards that; no test of a flapping dependency was made.
- **Recovery after an outage is up to 5 s plus the time of one probe,** instead of "whenever the channel next tried".
- A hire refused by the open breaker is a 503 in milliseconds, as in 0052.

## What is and is not verified
Eight tests on the interceptor and the real client: an open breaker refuses at once and is not retried; the total deadline holds when nothing answers;
a transient failure is retried and the next answer returned; a refusal of the credentials or a not-found is neither retried nor counted (three codes);
callers waiting on a silent dependency open the breaker so later attempts are refused; and **a real refused connection through the DI-built client is
retried with pauses, and the call after the breaker opened is refused in under 100 ms across scopes**. Mutation-checked three ways (interceptor not
registered; every status transient; no total timeout), each caught by the expected test. The drill above; the 5 s break measured only on its own three
steps. All 66 EmployeeService integration tests pass. Not verified: other callers' policies, several instances (each holds its own breaker), mutual TLS
on this call, an open-loop arrival rate.
