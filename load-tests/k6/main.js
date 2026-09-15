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
      vus: 2,
      iterations: 10,
      maxDuration: "30s",
    },
    // Read-heavy traffic against DirectoryService: listing departments and
    // semantic search, the two GET endpoints a real UI would call constantly.
    read_traffic: {
      executor: "constant-vus",
      exec: "readTraffic",
      vus: 15,
      duration: "30s",
      startTime: "5s",
    },
    // A smaller, steadier stream of writes (hire an employee end to end:
    // location -> department -> position -> employee) to see how the outbox
    // + Kafka path holds up under concurrent inserts, not just reads.
    write_traffic: {
      executor: "constant-vus",
      exec: "writeTraffic",
      vus: 5,
      duration: "30s",
      startTime: "5s",
    },
  },
  thresholds: {
    http_req_failed: ["rate<0.01"],
    "http_req_duration{scenario:read_traffic}": ["p(95)<300"],
    "http_req_duration{scenario:write_traffic}": ["p(95)<800"],
    write_errors: ["count==0"],
  },
};

let cachedToken;

function authHeaders() {
  if (!cachedToken) {
    cachedToken = getAdminToken();
  }
  return { Authorization: `Bearer ${cachedToken}`, "Content-Type": "application/json" };
}

export function authSmoke() {
  const token = getAdminToken();
  check(token, { "got an access token": (t) => typeof t === "string" && t.length > 0 });
  sleep(1);
}

export function readTraffic() {
  const headers = authHeaders();

  const rootsRes = http.get(`${DIRECTORY_BASE_URL}/api/departments/roots`, { headers });
  check(rootsRes, { "roots: 200": (r) => r.status === 200 });

  const searchRes = http.get(
    `${DIRECTORY_BASE_URL}/api/departments/search?query=engineering%20teams&limit=5`,
    { headers },
  );
  check(searchRes, { "search: 200": (r) => r.status === 200 });

  sleep(1);
}

export function writeTraffic() {
  const headers = authHeaders();
  const unique = `${__VU}${__ITER}${Date.now()}`;

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

  sleep(1);
}
