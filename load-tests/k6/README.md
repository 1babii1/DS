# Load tests

k6 scenarios exercising the stack through nginx and the individual services directly.

## Scenarios (`main.js`)

- **auth_smoke** — 3 full `authorization_code` + PKCE login round trips, one VU, sequential.
  Not a volume test: each login writes an OpenIddict authorization code and token to
  Postgres, and `/auth/login` is itself rate-limited to 5/minute per IP, so this checks the
  login path stays fast and correct without tripping the same limiter it's measuring.
- **read_roots_traffic** — 15 constant VUs for 30s hitting `GET /api/departments/roots`,
  which carries no rate limit — the one scenario that measures real concurrent-read
  capacity rather than a limiter's ceiling.
- **read_search_traffic** — 1 VU, paced under DirectoryService's 30/60s-per-IP search
  limiter, hitting `GET /api/departments/search` (semantic search, backed by Ollama).
- **write_traffic** — 1 VU, paced under the matching 30/60s-per-IP write limiter, running
  the full hire chain end to end: create a location, a department, a position, then hire an
  employee through the nginx gateway. Exercises the outbox → Kafka path through the real
  HTTP path, not just reads. Concurrent-write *correctness* under contention is covered
  separately, directly against the database (see `docs/benchmarks/baseline.md`) - a
  shared-IP rate limiter sitting between k6 and the thing it's racing would only get in the
  way of that, not test it.

All three traffic scenarios log in exactly once, in `setup()`, and share that one token -
see "What this caught" below for what happens when a scenario doesn't.

## Running it

Requires the stack up (`docker compose up -d`) and Docker to run k6 without installing it
locally:

```bash
cd load-tests/k6
docker run --rm --network host -v "$(pwd)":/scripts -w /scripts grafana/k6 run main.js
```

`--network host` lets k6 hit `localhost` the same way your browser would; no compose
network name to look up.

### Environment overrides

| Variable | Default | Used for |
|---|---|---|
| `AUTH_BASE_URL` | `http://localhost:5130` | AuthService (login, PKCE, token) |
| `DIRECTORY_BASE_URL` | `http://localhost:5129` | DirectoryService (locations, departments, positions, search) |
| `GATEWAY_BASE_URL` | `http://localhost` | nginx, used only for `POST /api/employees` |
| `ADMIN_EMAIL` / `ADMIN_PASSWORD` | seeded admin creds | login |

Pass them with `-e`, e.g. `docker run ... -e DIRECTORY_BASE_URL=http://directory_service:5129 ...`
if running k6 attached to the compose network instead of the host.

Numbers from the most recent run live in
[`docs/benchmarks/baseline.md`](../../docs/benchmarks/baseline.md).

## What this caught

Running this test surfaced real bugs, in the load test script itself and in the platform.

**In the script:** two self-inflicted request storms, same root cause. `main.js` originally
had every VU call `getAdminToken()` independently, and `writeTraffic` returned early on a
failed check *before* its `sleep()` - so the first `429` from a rate limiter (login:
5/min/IP; write: 30/60s/IP) triggered an immediate, unpaced retry that kept the limiter's
budget permanently exhausted for the rest of the run. One produced 352,589 requests in 35s
(99.99% failing); the other, 144,626 failed writes in 30s. Neither was a platform bug - both
limiters did exactly their job - but a load test that can turn "one 429" into "a
quarter-million requests" is itself worth fixing before trusting any number it produces.
Full writeup, including the actual rate-limit ceilings this exposed, in
`docs/benchmarks/baseline.md`.

**In the platform:** three real, pre-existing bugs that manual testing had never hit:

1. **`DepartmentPath` rejected digits.** Its regex only allowed `[a-zA-Z.-]`, while the
   sibling `DepartmentIdentifier` validator allows alphanumeric. Any identifier with a
   number in it passed identifier validation and then failed department creation with
   "Invalid department path" — never noticed because every manual test happened to use
   letters-only identifiers; the load test needed unique identifiers per iteration and
   generated ones with digits immediately.
2. **nginx cached a stale upstream IP.** `proxy_pass http://employee_service:5131` resolves
   once at nginx startup; after the container behind it was recreated several times during
   other work, nginx kept routing to an IP nothing was listening on anymore, returning 502s
   with zero application-side errors to explain why. Fixed with Docker's embedded DNS
   resolver and a variable in `proxy_pass` so nginx re-resolves per request instead of
   caching forever.
3. **Redis was configured as `localhost:6379`.** That's correct on a dev machine (Redis's
   port is published to the host) but wrong inside the DirectoryService container, where
   Redis lives in a separate container reachable at `redis:6379`. Every cache write from
   `POST /api/departments` tried to connect to nothing and ate the client's connect-retry
   timeout before falling through — a ~5-6s tax on every department creation under any
   concurrency, invisible in the SQL logs (the actual INSERT ran in single-digit ms) and
   invisible with a single manual request (only shows up once something else is happening
   concurrently enough to notice the pattern). Classic "works on my machine, silently
   degrades under Docker" bug.
