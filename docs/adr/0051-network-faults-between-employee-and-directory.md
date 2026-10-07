# 51. A slow or broken network between two services: what a hire does, measured with Toxiproxy

## Status
Accepted as a measurement; the changes it found are made in the next record. Related: [0036](0036-redis-failure-and-sentinel.md),
[0038](0038-load-shedding.md), [0045](0045-bulkheads-and-measured-limits.md), [0044](0044-mutual-tls-between-services.md).

## Context
The resilience work so far removed things whole: a Kafka broker killed, Redis gone, a replica promoted. A network is usually worse than
dead: it is slow, or it resets one connection in three, and the first thing that hurts is the thread waiting. EmployeeService validates
every hire against DirectoryService over gRPC, with a retry (3 attempts, 200 ms doubling), a circuit breaker (50% failures over 30 s, at
least 5 calls, open for 15 s) and a 5 s timeout per attempt. Nothing had shown what a hire does under that policy when the call is degraded
by degrees.

## What was done
`scripts/toxiproxy-drill.sh`: a real DirectoryService and a real EmployeeService (the stack's images, EmployeeService on a private Postgres
and no Kafka), with [Toxiproxy](https://github.com/Shopify/toxiproxy) on the gRPC call between them. A constant stream of hires (k6, 4 virtual
users, `load-tests/k6/toxiproxy-hire.js`) runs for 30 s per step while the proxy is made worse, then restored. The breaker's own events are read
from the log lines Polly writes. The reference data is created once, and the sign-in once, because the sign-in has a limit of five a minute.

## Measured
One machine, one EmployeeService instance, 4 users each sending the next hire as soon as the last returned (closed loop, so the rate falls as
latency rises; a fixed arrival rate would queue instead).

| step | hires ok | failed | p50 ms | p95 ms | p99 ms | first ok at | retries | breaker opened / half-open / closed |
|---|---|---|---|---|---|---|---|---|
| baseline | 10429 | 0 | 10 | 16 | 20 | 1.2 s | 0 | 0 / 0 / 0 |
| latency 500 ms (jitter 100) | 230 | 0 | 533 | 600 | 605 | 0.6 s | 0 | 0 / 0 / 0 |
| latency 2 s (jitter 200) | 60 | 0 | 2103 | 2189 | 2204 | 2.0 s | 0 | 0 / 0 / 0 |
| latency 6 s (past the 5 s timeout) | 0 | 32 (all 503) | 1415 | 21467 | 21467 | none | 96 | 1 / 0 / 0 |
| connection reset | 0 | 88 (all 503) | 1413 | 1420 | 1425 | none | 264 | 2 / 2 / 0 |
| directory unreachable (proxy off) | 0 | 28737 (all 503) | 3 | 6 | 24 | none | 12 | 1 / 1 / 0 |
| recovered (toxics removed) | 5658 | 8734 (all 503) | 4 | 19 | 33 | 9.1 s | 3 | 0 / 1 / 1 |

## What it shows
1. **Added latency passes straight through.** 500 ms of network is 500 ms on the hire, and the throughput of four users falls from about 350
   hires a second to 8. The policy does nothing about slowness below the timeout, which is the right default for one call and the wrong one
   for a whole service: nothing here limits how many hires wait at once on a slow Directory.
2. **A hire can wait 21 seconds.** At 6 s of latency the first four hires each spent four attempts of 5 s plus the pauses: 21.5 s, with no
   deadline for the hire as a whole. The breaker opens only after enough attempts have *finished*, so it cannot help the requests already waiting.
3. **An open breaker does not make a hire fail fast; it makes it fail in 1.4 s.** After the breaker opened, the other 28 hires of the 6 s step
   (and every one in the reset step) failed at 1.4 s: the retry strategy treats the breaker's rejection as a failure worth retrying and sleeps
   200 + 400 + 800 ms between attempts that cannot succeed. The log shows `OnRetry ... The circuit is now open`.
4. **A refused connection takes a different path and is fast: 3 ms.** With the proxy off, the failure is `Unavailable: Error connecting to
   subchannel (Connection refused)` raised by the gRPC client's own connection manager, *before* a request reaches the HTTP handler chain
   where retry and breaker live. They are not involved (12 retries in 28,737 failures). That is fast and cheap, and it also means the policy
   written for this call does not govern the most common failure of all.
5. **Recovery took 9.1 s from the moment the network came back** (first successful hire), with 8,734 hires refused in that time. Two things
   hold it: the breaker staying open for its 15 s from when it last opened, and the channel's own reconnect back-off. This drill does not
   separate them.
6. **A reset connection makes the breaker flap:** two openings and two half-open probes in one step, each probe failing and reopening it.

## Consequences
- Items 2 and 3 are defects of the policy as configured, not of the network, and are fixed and re-measured in [0052](0052-directory-call-deadlines-and-fast-rejection.md).
- Item 4 (a refused connection never reaching the policy) is fixed in [0053](0053-directory-call-policy-at-the-call-level.md).
- Items 1 and 5 are properties to know and to state, not to fix here: a limit on concurrent calls to one dependency (a bulkhead, 0045) is
  the answer to item 1 and has not been applied to this call.
- Toxiproxy's latency toxic delays the downstream direction only (replies); a symmetric delay or a bandwidth limit was not tried, nor packet loss
  (a TCP proxy cannot drop packets, only stall and reset).

## What is and is not verified
Run end to end as above, three times (10 s steps to find the harness faults, then twice at 30 s; the table is the last 30 s run; the shapes
agreed between runs, the counts of the slow steps vary by a few). The harness faults found on the way and fixed: the write rate limit
(30 a minute) answered 429 and looked like a network effect, and a sign-in per step hit the sign-in limit. Not verified: more than one
EmployeeService instance, an open-loop arrival rate, mutual TLS on the proxied call, and any other call (Postgres, Kafka, Redis, Ollama).
