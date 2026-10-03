// How long the employee card's balance lags a grant, and what a read that asks for its own write costs (ADR 0034). Each iteration grants
// 10 to a different employee through Rewards, takes the wallet version from X-Wallet-Version, and immediately reads the card from Employee
// with X-Min-Wallet-Version set to it. Used by scripts/card-lag-drill.sh, which starts the services this talks to.
import exec from "k6/execution";
import http from "k6/http";
import { sleep } from "k6";
import { Counter, Trend } from "k6/metrics";
import { getAdminToken } from "./lib/auth.js";

const REWARDS = __ENV.REWARDS_BASE_URL || "http://localhost:5321";
const EMPLOYEE = __ENV.EMPLOYEE_BASE_URL || "http://localhost:5322";
const ids = JSON.parse(`[${open("./.card-lag-ids.txt").trim()}]`);

const waited = new Trend("waited_ms", true);
const granted = new Counter("grants_ok");
const failed = new Counter("grant_failed");
const consistent = new Counter("consistent_cards");
const stale = new Counter("stale_cards");

export const options = {
  scenarios: { grants: { executor: "shared-iterations", vus: 4, iterations: ids.length, maxDuration: "5m" } },
  summaryTrendStats: ["avg", "p(50)", "p(95)", "max"],
};

export function setup() {
  return { token: getAdminToken() };
}

export default function (data) {
  // A random pause, so that grants land at random moments of the publisher's polling cycle instead of in step with it: a closed loop that
  // grants again the moment the last card came back measures the cycle, not the lag.
  sleep(Math.random() * 2);
  const n = exec.scenario.iterationInTest;
  const employee = ids[n % ids.length];
  const headers = { Authorization: `Bearer ${data.token}`, "Content-Type": "application/json", "Idempotency-Key": `lag-${n}-${Date.now()}` };
  const grant = http.post(`${REWARDS}/api/rewards/grants`, JSON.stringify({ employeeId: employee, amount: 10, reason: "lag drill" }), { headers });
  if (grant.status !== 200) {
    failed.add(1);
    return;
  }
  granted.add(1);
  const version = grant.headers["X-Wallet-Version"];

  const card = http.get(`${EMPLOYEE}/api/employees/${employee}/card`, {
    headers: { Authorization: `Bearer ${data.token}`, "X-Min-Wallet-Version": version },
    timeout: "10s",
  });
  waited.add(card.timings.duration);
  if (card.status === 200 && card.json("consistent") === true) {
    consistent.add(1);
  } else {
    stale.add(1);
  }
}
