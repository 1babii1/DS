// A stream of grants spread over several RewardsService instances while the drill kills and restarts some of them (scripts/multi-instance-drill.sh).
// Each logical grant has one Idempotency-Key; when an instance does not answer, the same grant is sent to another with the same key, as a
// client would. What is counted: grants the service confirmed, grants whose fate is unknown (every try failed), and the keys, so that the
// drill can check afterwards that no key produced two ledger rows and none that was confirmed is missing.
import http from "k6/http";
import { sleep } from "k6";
import { Counter } from "k6/metrics";
import { getAdminToken } from "./lib/auth.js";

const BASES = (__ENV.REWARDS_URLS || "http://localhost:5341,http://localhost:5342,http://localhost:5343").split(",");
const VUS = Number(__ENV.VUS || 8);
const DURATION = __ENV.DURATION || "60s";
const AMOUNT = 10;
const PACE = Number(__ENV.PACE || 0.4); // seconds a user waits after each grant; the outbox publisher moves about ten messages a second per instance
const ids = JSON.parse(`[${open("./.multi-ids.txt").trim()}]`);

const confirmed = new Counter("grants_confirmed");
const unknown = new Counter("grants_unknown");
const retried = new Counter("grants_retried_elsewhere");

export const options = {
  scenarios: { grants: { executor: "constant-vus", vus: VUS, duration: DURATION, gracefulStop: "30s" } },
};

export function setup() {
  return { token: getAdminToken() };
}

export default function (data) {
  const employee = ids[Math.floor(Math.random() * ids.length)];
  const key = `mi-${__VU}-${__ITER}-${Date.now()}`;
  const body = JSON.stringify({ employeeId: employee, amount: AMOUNT, reason: "multi-instance drill" });
  const headers = { Authorization: `Bearer ${data.token}`, "Content-Type": "application/json", "Idempotency-Key": key };
  const first = Math.floor(Math.random() * BASES.length);
  for (let attempt = 0; attempt < 3; attempt++) {
    const res = http.post(`${BASES[(first + attempt) % BASES.length]}/api/rewards/grants`, body, { headers, timeout: "10s" });
    if (res.status === 200) {
      confirmed.add(1);
      if (attempt > 0) retried.add(1);
      console.log(`GRANT ${key} ${employee}`);
      sleep(PACE);
      return;
    }
    if (res.status !== 0 && res.status < 500) break; // an answer that is not a failure of the instance: do not try elsewhere
  }
  unknown.add(1);
  console.log(`UNKNOWN ${key} ${employee}`);
  sleep(PACE);
}
