// A steady stream of hires through EmployeeService while the network between it and DirectoryService is made worse by Toxiproxy
// (scripts/toxiproxy-drill.sh sets the toxic, then runs this once per phase). Each hire makes EmployeeService ask DirectoryService over
// gRPC to validate the department and the position, so what the proxy does to that call shows up here as the hire's latency and status.
// Reference data (a location, a department, a position) is created once in setup() straight on DirectoryService, not through the proxy.
import exec from "k6/execution";
import http from "k6/http";
import { Counter, Trend } from "k6/metrics";
import { getAdminToken } from "./lib/auth.js";

const DIRECTORY = __ENV.DIRECTORY_BASE_URL || "http://localhost:5301";
const EMPLOYEE = __ENV.EMPLOYEE_BASE_URL || "http://localhost:5311";
const PHASE = __ENV.PHASE || "phase";
const VUS = Number(__ENV.VUS || 4);
const DURATION = __ENV.DURATION || "30s";

const STATUSES = [200, 400, 401, 403, 409, 429, 500, 502, 503, 504];
const hireMs = new Trend("hire_ms", true);
const byStatus = new Counter("hire_by_status");
const sampled = {};
const ok = new Counter("hire_ok");
const failed = new Counter("hire_failed");
const fastFail = new Counter("hire_fast_fail"); // an error answered in under 100 ms: the circuit breaker (or a refused connection), not a wait
const slowFail = new Counter("hire_slow_fail"); // an error that took 1 s or more: a timeout or the retries spent on one
const idemSeed = `${Date.now()}`;
let loggedFirstOk = false;

export const options = {
  scenarios: { hires: { executor: "constant-vus", vus: __ENV.MODE === "prepare" ? 1 : VUS, duration: __ENV.MODE === "prepare" ? "1s" : DURATION, gracefulStop: "40s" } },
  summaryTrendStats: ["avg", "p(50)", "p(95)", "p(99)", "max"],
  thresholds: Object.fromEntries(STATUSES.map((c) => [`hire_by_status{status:${c}}`, []])),
};

// MODE=prepare creates the reference data once and prints it (and a token) for the phases that follow; a phase takes it from the environment,
// so that a run of seven phases logs in and fills the directory once, not seven times.
export function setup() {
  if (__ENV.TOKEN) {
    return { token: __ENV.TOKEN, departmentId: __ENV.DEPARTMENT_ID, positionId: __ENV.POSITION_ID };
  }
  const token = getAdminToken();
  const headers = { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };
  const unique = `tox${idemSeed}`;
  const location = http.post(`${DIRECTORY}/api/locations`, JSON.stringify({ locationRequest: { name: `Tox HQ ${unique}`, address: { street: `Main ${unique}`, city: "Almaty", country: "Kazakhstan" }, timezone: "Asia/Almaty" } }), { headers });
  const department = http.post(`${DIRECTORY}/api/departments`, JSON.stringify({ request: { name: { value: `Tox Dept ${unique}` }, identifier: { value: unique.slice(0, 30) }, locationsIds: [{ value: location.json("result") }] } }), { headers });
  const departmentId = department.json("result");
  const position = http.post(`${DIRECTORY}/api/positions`, JSON.stringify({ request: { name: { value: `Tox Role ${unique}` }, departmentIds: [{ value: departmentId }] } }), { headers });
  const prepared = { token, departmentId, positionId: position.json("result") };
  if (__ENV.MODE === "prepare") console.log(`PREPARED ${JSON.stringify(prepared)}`);
  return prepared;
}

export default function (data) {
  if (__ENV.MODE === "prepare") return;
  const n = `${__VU}-${__ITER}-${Date.now()}`;
  const res = http.post(
    `${EMPLOYEE}/api/employees`,
    JSON.stringify({ fullName: `Tox Employee ${n}`, email: `tox-${n}@portfolio.local`, departmentId: data.departmentId, positionId: data.positionId }),
    { headers: { Authorization: `Bearer ${data.token}`, "Content-Type": "application/json", "Idempotency-Key": `tox-${n}` }, timeout: "60s" },
  );
  hireMs.add(res.timings.duration);
  byStatus.add(1, { status: String(res.status) });
  if (res.status !== 200 && !sampled[res.status]) {
    sampled[res.status] = true;
    console.log(`SAMPLE ${res.status} after ${Math.round(res.timings.duration)} ms: ${String(res.body).slice(0, 200)}`);
  }
  if (res.status === 200) {
    ok.add(1);
    if (!loggedFirstOk) {
      loggedFirstOk = true;
      console.log(`FIRST_OK ${Date.now() - exec.scenario.startTime}`);
    }
  } else {
    failed.add(1);
    if (res.timings.duration < 100) fastFail.add(1);
    if (res.timings.duration >= 1000) slowFail.add(1);
  }
}

export function handleSummary(data) {
  if (__ENV.MODE === "prepare") return {};
  const m = (name, key) => (data.metrics[name] && data.metrics[name].values[key]) || 0;
  const line = {
    phase: PHASE,
    ok: m("hire_ok", "count"),
    failed: m("hire_failed", "count"),
    fastFail: m("hire_fast_fail", "count"),
    slowFail: m("hire_slow_fail", "count"),
    p50: Math.round(m("hire_ms", "p(50)")),
    statuses: Object.fromEntries(STATUSES.map((c) => [c, m(`hire_by_status{status:${c}}`, "count")]).filter(([, n]) => n > 0)),
    p95: Math.round(m("hire_ms", "p(95)")),
    p99: Math.round(m("hire_ms", "p(99)")),
    max: Math.round(m("hire_ms", "max")),
  };
  return { stdout: `PHASE_RESULT ${JSON.stringify(line)}\n` };
}
