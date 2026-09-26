# Load test baseline

Numbers from a real `k6 run` against the full docker-compose stack on this machine (16
cores, 27 GiB RAM, Docker on Linux) — not estimated, not from a staging environment. Rerun
locally to verify: `docker compose up -d`, then follow
[`load-tests/k6/README.md`](../../load-tests/k6/README.md).

## What this run found before it produced a single usable number

The first two attempts at collecting this baseline didn't fail on the platform - they found
two real bugs in the load test script itself, both the same shape: a k6 scenario that
retries a failed request with **no backoff**, which turns a single, expected rejection into
a self-sustaining request storm against whatever rejected it.

1. **Every VU logging in independently.** `main.js` originally called `getAdminToken()`
   once per VU. AuthService rate-limits `/auth/login` to 5 attempts/minute per IP (fixed
   window, no queue - deliberately tight against credential stuffing). k6's 22 VUs all
   share the host's IP under
   `--network host`, so all but the first ~5 logins got `429`, and the failure path threw
   with no delay - the next iteration retried immediately. Result: **352,589 requests in
   35 seconds**, 99.99% failing, all of it self-inflicted load against a rate limiter doing
   exactly its job. Fixed by logging in once, in k6's `setup()`, before any scenario starts,
   and handing that one token to every VU - the same thing a real browser session does.
2. **Write-path checks returning before `sleep()`.** Once login was fixed, the same shape
   of bug showed up one layer in: `writeTraffic`'s early `return` on a failed check skipped
   the scenario's `sleep(1)`, so a single `429` from DirectoryService's write-path limiter
   (also 30/60s per IP) turned into the same kind of storm - **144,626 failed writes in
   30 seconds**. Fixed by wrapping the iteration body in `try { ... } finally { sleep(...) }`
   so a failure always paces the same as success.

Both fixes are in `load-tests/k6/main.js`; the reasoning is in the code comments next to
each one, not just here.

## The actual ceiling this exposed

DirectoryService rate-limits **search** and **write** endpoints identically: 30 requests
per 60 seconds, per source IP, fixed window, no queue (`RateLimiting:Search:PermitLimit`,
`RateLimiting:Write:PermitLimit` in `DirectoryService/Program.cs`). That's a real, load-test-verified
ceiling - not a documented assumption:

| Endpoint class | Verified ceiling (per source IP) |
|---|---|
| `/auth/login` | 5 / minute |
| `GET /api/departments/search` (semantic, calls Ollama) | 30 / minute |
| `POST /api/{locations,departments,positions}` | 30 / minute |

`GET /api/departments/roots` carries no limiter, which is why it's the one scenario run at
full concurrency below - it's the only one where 15 VUs measures the platform rather than
the limiter.

This is a real trade-off worth naming, not just a note in passing: every VU here shares one
IP, which is also exactly the failure mode of **many real users behind one corporate NAT or
proxy** - the same 30/60s budget that protected this benchmark from itself would throttle a
whole office hitting search at once. Fine for this platform's actual scale; the honest
answer if this needed to scale past "roughly one active office" is a per-user or
per-token budget layered on top of (not instead of) the per-IP one, not a wider IP-based
window.

## Results

Stack: 8 .NET services + Postgres + Kafka + Redis + Elasticsearch + Ollama, all on one
Docker host, `docker compose up -d`, no scaling, no PgBouncer (see ADR-0010's Wave-1 plan
for what changes that).

| Scenario | Load | Checks | p50 | p90 | p95 | Failed |
|---|---|---|---|---|---|---|
| `auth_smoke` (full `authorization_code`+PKCE login) | 1 VU, 3 sequential logins | 3/3 | - | - | - | 0% |
| `read_roots_traffic` (`GET /api/departments/roots`, unthrottled) | 15 VUs, 30s | 100% | 2.2 ms | 3.4 ms | **4.2 ms** | 0% |
| `read_search_traffic` (semantic search, rate-limit-paced) | 1 VU, 30s | 100% | 29.2 ms | 37.4 ms | **39.9 ms** | 0% |
| `write_traffic` (full hire chain: location → department → position → employee, rate-limit-paced) | 1 VU, 30s | 100% | 7.3 ms | 15.1 ms | **17.1 ms** | 0% |

481/481 checks passed, 0% `http_req_failed` across 490 total requests in the fixed run.
`write_traffic`'s 17.1ms p95 covers all four writes in the hire chain end-to-end through
nginx, including the outbox insert on each one - not a cached read, not a mock.

## What this baseline does and doesn't claim

- This measures **one unscaled instance on one machine**, not the platform under real
  production load - there is no second replica of anything, no PgBouncer, no Kafka cluster.
  It's the number to compare against once Wave 1 (ADR-0010) actually lands, not a claim
  about how the platform behaves at scale today.
- `read_search_traffic` and `write_traffic` are intentionally paced *under* the rate
  limiter's ceiling, not stress-tested against it - concurrent-write *correctness* (races,
  double-grants) is covered separately, directly against the database, where the tests
  that prove it don't have a shared-IP rate limiter standing between them and the thing
  they're racing:
  - `WelcomeBonusConsumerTests` - 16 concurrent/redelivered attempts at the same welcome
    bonus produce exactly one grant.
  - `GrantCurrencyTests.Concurrent_requests_with_the_same_idempotency_key_apply_exactly_one_grant`
    - 8 concurrent requests with one `Idempotency-Key` produce exactly one transaction.
  - `AccountControllerTests.Concurrent_registrations_to_the_same_email_create_exactly_one_account`
    - 8 concurrent registrations to the same email produce exactly one account.
  - `OwnWalletTests.Own_wallet_reads_as_zero_between_the_bonus_being_granted_and_the_account_being_linked`
    - documents (doesn't hide) a real read-side race between a grant landing and the
      account-lookup projection catching up.
- Re-run this after any infrastructure change and diff the table - a baseline that's never
  compared against a second measurement is a number, not evidence of anything changing.
