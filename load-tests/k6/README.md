# Load tests

k6 scenarios exercising the stack through nginx and the individual services directly.

## Scenarios (`main.js`)

- **auth_smoke** — 10 full `authorization_code` + PKCE login round trips (2 VUs). Not a
  volume test: each login writes an OpenIddict authorization code and token to Postgres,
  so this just checks the login path stays fast and correct, not how it behaves under a
  login storm.
- **read_traffic** — 15 constant VUs for 30s hitting `GET /api/departments/roots` and
  `GET /api/departments/search` (semantic search), the two reads a real UI calls constantly.
- **write_traffic** — 5 constant VUs for 30s running the full hire chain end to end:
  create a location, a department, a position, then hire an employee through the nginx
  gateway. Exercises the outbox → Kafka path under concurrent inserts, not just reads.

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

## What this caught

This test isn't just a smoke check — running it surfaced three real, pre-existing bugs
that manual testing had never hit:

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
