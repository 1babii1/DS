# 52. The call to DirectoryService gets a deadline of its own, and an open breaker refuses at once

## Status
Accepted. Amended by [0053](0053-directory-call-policy-at-the-call-level.md): the policy now sits on the call, with a shorter memory and open time; the 8 s, 3 s and no-retry-of-a-refusal here stay. Follows [0051](0051-network-faults-between-employee-and-directory.md), which measured what the policy did and found two defects.

## Context
With Toxiproxy on the gRPC call between EmployeeService and DirectoryService, 0051 found that at 6 s of added latency the first hires waited
21.5 s (four attempts of 5 s and the pauses between them, with no deadline for the call as a whole), and that once the circuit breaker had opened,
every hire still waited 1.4 s before failing, because the retry strategy retried the breaker's own refusal and slept 200 + 400 + 800 ms between
attempts that could not succeed.

## Decision
`DirectoryGrpcResilience` (one type, so a test can build the same pipeline) now orders the strategies as
**total timeout (8 s) → retry → circuit breaker → attempt timeout (3 s)**, and the retry **does not retry a `BrokenCircuitException`**.
- The total timeout is outermost, so one hire's call to Directory cannot take longer than 8 s however many attempts and pauses fit.
- The attempt timeout goes from 5 s to 3 s. A lookup that takes 15 ms at the 99th percentile in the baseline has no business waiting 5 s; 3 s still
  leaves room for the 2 s step below passing.
- Everything else is unchanged: three retries, 200 ms doubling, a breaker at 50% over 30 s with at least five calls and 15 s open.

Alternatives: **no retry at all** (a reset connection is the case retries exist for, and the third test pins that a transient failure is still
retried), **a lower breaker threshold** (does not touch either defect), and **a bulkhead on this call** (answers a different finding of 0051, the
unbounded number of hires waiting on a slow Directory; not done here).

## Measured
Same drill, same machine, 30 s per step, before (0051) and after.

| step | before: failed, p50 / p95 / max | after: failed, p50 / p95 / max |
|---|---|---|
| latency 500 ms | 0, ok at 533 / 600 / 608 ms | 0, ok at 530 / 608 / 614 ms |
| latency 2 s | 0, ok at 2103 / 2189 / 2204 ms | 0, ok at 2052 / 2194 / 2203 ms |
| latency 6 s | 32 failed: 1415 ms / 21467 ms / 21467 ms | **6552 failed: 4 ms / 8 ms / 8033 ms** |
| connection reset | 88 failed: 1413 / 1420 / 1434 ms | **25306 failed: 4 / 7 / 223 ms** |
| directory unreachable | 28737 failed: 3 / 6 / 1416 ms | 28162 failed: 4 / 6 / 211 ms |

The slow steps are unchanged, as intended (a slow answer inside the limits still succeeds). In the two steps that were defective, a hire that
cannot succeed now says so in a few milliseconds once the breaker is open, and the longest wait fell from 21.5 s to 8.0 s, the total deadline.
The failed count is higher because the closed-loop users send their next hire at once.

## Consequences
- **A directory slower than 3 s per attempt now fails where it used to succeed at up to 5 s.** That is the point of the change and also a loss for
  a Directory that is slow but working; the numbers are for this workload (a lookup of a few milliseconds) and are to be revisited if the call ever
  does more.
- **A refused hire is a 503 within milliseconds once the breaker is open,** so a client that retries immediately now sends more requests per second
  into an open breaker than before, when each took 1.4 s. They are cheap to refuse; they are not free.
- **The recovery time was not shown to improve.** The first successful hire after the network came back was at 9.1 s before and 0.0 s after; the
  breaker's own timing decides which (it is open for 15 s from when it last opened, and each step ends at a different point of that cycle), so
  neither number is a claim about the change. The bound is the break duration plus the channel's reconnect back-off.
- A connection that is *refused* still never reaches this policy (0051 item 4): the gRPC client fails it before the HTTP handlers.

## What is and is not verified
Three tests on the pipeline the service builds, with shorter times, watched failing against the old policy (an open breaker took more than 150 ms
and was retried; a call that never answers ran past its deadline) and passing after; a third pins that a transient failure is still retried and a slow
answer inside the limits still succeeds. The drill above, once before and once after. All 61 EmployeeService integration tests pass. Not verified: the
same change on other calls (Ollama, HIBP, the McpServer clients use their own policy), two instances of EmployeeService, and the effect of the 3 s
attempt timeout on a Directory that is merely busy.
