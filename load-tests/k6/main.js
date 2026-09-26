import http from "k6/http";
import { check, sleep } from "k6";
import { Counter } from "k6/metrics";
import { getAdminToken } from "./lib/auth.js";

const DIRECTORY_BASE_URL = __ENV.DIRECTORY_BASE_URL || "http://localhost:5129";
const GATEWAY_BASE_URL = __ENV.GATEWAY_BASE_URL || "http://localhost";

const writeErrors = new Counter("write_errors");

export const options = {
  scenarios: {
    // A handful of full login round trips (register -> authorize -> token),
    // not load-tested at volume: each one writes an OpenIddict authorization
    // code and token to Postgres, so this is measuring the auth path exists
    // and is fast, not simulating a login storm.
    auth_smoke: {
      executor: "shared-iterations",
      exec: "authSmoke",
      vus: 1,
      iterations: 3,
      maxDuration: "30s",
    },
    // GET /api/departments/roots carries no per-IP rate limit (unlike search and every
    // write endpoint below), so this is the one scenario that can genuinely run at k6's
    // full concurrency and measure real listing throughput, not a rate limiter's ceiling.
    read_roots_traffic: {
      executor: "constant-vus",
      exec: "readRootsTraffic",
      vus: 15,
      duration: "30s",
      startTime: "5s",
    },
    // Semantic search (GET /api/departments/search) is rate-limited to 30/60s per IP
    // (DirectoryService's "search" policy - it calls Ollama, expensive enough to protect).
    // One VU, spaced comfortably under that budget: this measures genuine search latency,
    // not how fast 429s come back once several concurrent VUs blow through 30 requests in
    // the scenario's first two seconds. See docs/benchmarks/baseline.md for the throughput
    // ceiling this limiter itself implies.
    read_search_traffic: {
      executor: "constant-vus",
      exec: "readSearchTraffic",
      vus: 1,
      duration: "30s",
      startTime: "5s",
    },
    // The write endpoints (locations/departments/positions via DirectoryService, employees
    // via EmployeeService through nginx) share a 30/60s-per-IP "write" policy. Each
    // iteration here is 4 writes, so the same budget allows roughly one iteration every 8s
    // from a single source IP - one VU, paced to stay under that, measures the outbox ->
    // Kafka path's genuine end-to-end latency through the real HTTP path. Concurrent-write
    // *correctness* under contention (double-grants, races) is already covered where it
    // belongs - RewardsService's own integration tests exercise that directly against the
    // database, without a shared-IP rate limiter standing between the test and the thing
    // it's actually trying to race.
    write_traffic: {
      executor: "constant-vus",
      exec: "writeTraffic",
      vus: 1,
      duration: "30s",
      startTime: "5s",
    },
  },
  thresholds: {
    http_req_failed: ["rate<0.01"],
    "http_req_duration{scenario:read_roots_traffic}": ["p(95)<300"],
    "http_req_duration{scenario:read_search_traffic}": ["p(95)<300"],
    "http_req_duration{scenario:write_traffic}": ["p(95)<800"],
    write_errors: ["count==0"],
  },
};

// AuthService rate-limits /auth/login to 5 attempts/minute per IP (fixed window, no queue -
// deliberately tight, see AuthService's rate-limiting signal). k6's VUs all share the
// host's IP under --network host, so every VU independently calling getAdminToken() - the
// original shape of this script - meant 22 concurrent logins from one IP, all but the
// first ~5 rejected with 429, and (worse) authHeaders() re-threw on every failed login with
// no backoff, so a failed VU immediately retried and never once reached the traffic it was
// meant to generate. A real UI logs in once and reuses the session; this script does the
// same - one login in setup(), before any scenario starts, its token handed to every VU.
export function setup() {
  return { token: getAdminToken() };
}

function authHeaders(token) {
  return { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };
}

// Deliberately small and sequential (not parallel VUs): this measures the login path's own
// latency, and the login rate limiter's 5/minute budget has to cover setup()'s one login
// plus these - staying well under it is what keeps this scenario actually testing login
// speed instead of testing the rate limiter.
export function authSmoke() {
  const token = getAdminToken();
  check(token, { "got an access token": (t) => typeof t === "string" && t.length > 0 });
  sleep(1);
}

export function readRootsTraffic(data) {
  const headers = authHeaders(data.token);

  const rootsRes = http.get(`${DIRECTORY_BASE_URL}/api/departments/roots`, { headers });
  check(rootsRes, { "roots: 200": (r) => r.status === 200 });

  sleep(1);
}

export function readSearchTraffic(data) {
  const headers = authHeaders(data.token);

  const searchRes = http.get(
    `${DIRECTORY_BASE_URL}/api/departments/search?query=engineering%20teams&limit=5`,
    { headers },
  );
  check(searchRes, { "search: 200": (r) => r.status === 200 });

  // 30/60s budget over 1 VU means one call every 2s at most; 2.5s leaves headroom instead
  // of running the limiter's fixed window right up against its edge.
  sleep(2.5);
}

// try/finally, not a bare sequence of early-returns: DirectoryService's own write-path
// rate limiter (30/60s per IP, fixed window, no queue - see DirectoryService's Program.cs)
// rejects a chunk of these once several VUs share one IP, and every early return on a
// failed check used to skip straight past sleep(1) - turning a single 429 into a
// zero-delay retry loop that then kept the limiter's budget permanently exhausted for the
// rest of the run (the same failure shape the login storm below setup() had, just on the
// write path instead of login). The limiter doing its job is expected and desirable here;
// this only makes sure hitting it can't turn into a self-inflicted request storm.
export function writeTraffic(data) {
  const headers = authHeaders(data.token);
  const unique = `${__VU}${__ITER}${Date.now()}`;

  try {
    writeTrafficIteration(headers, unique);
  } finally {
    // 4 writes/iteration against a 30/60s budget: ~8s is the floor to stay under it from
    // one VU. 9s leaves a little headroom.
    sleep(9);
  }
}

function writeTrafficIteration(headers, unique) {
  const locationRes = http.post(
    `${DIRECTORY_BASE_URL}/api/locations`,
    JSON.stringify({
      locationRequest: {
        name: `Load Test HQ ${unique}`,
        address: { street: `Main ${unique}`, city: "Almaty", country: "Kazakhstan" },
        timezone: "Asia/Almaty",
      },
    }),
    { headers },
  );
  if (!check(locationRes, { "location: 200": (r) => r.status === 200 })) {
    writeErrors.add(1);
    return;
  }
  const locationId = locationRes.json("result");

  const identifier = `loadtest${unique}`.slice(0, 30);
  const departmentRes = http.post(
    `${DIRECTORY_BASE_URL}/api/departments`,
    JSON.stringify({
      request: {
        name: { value: `Load Test Dept ${unique}` },
        identifier: { value: identifier },
        locationsIds: [{ value: locationId }],
      },
    }),
    { headers },
  );
  if (!check(departmentRes, { "department: 200": (r) => r.status === 200 })) {
    writeErrors.add(1);
    return;
  }
  const departmentId = departmentRes.json("result");

  const positionRes = http.post(
    `${DIRECTORY_BASE_URL}/api/positions`,
    JSON.stringify({
      request: {
        name: { value: `Load Test Role ${unique}` },
        departmentIds: [{ value: departmentId }],
      },
    }),
    { headers },
  );
  if (!check(positionRes, { "position: 200": (r) => r.status === 200 })) {
    writeErrors.add(1);
    return;
  }
  const positionId = positionRes.json("result");

  const employeeRes = http.post(
    `${GATEWAY_BASE_URL}/api/employees`,
    JSON.stringify({
      fullName: `Load Test Employee ${unique}`,
      email: `loadtest${unique}@portfolio.local`,
      departmentId,
      positionId,
    }),
    { headers },
  );
  check(employeeRes, { "employee: 200": (r) => r.status === 200 }) || writeErrors.add(1);
}
