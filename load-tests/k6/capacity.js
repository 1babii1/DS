// Finds where one DirectoryService instance stops keeping up, and what load shedding does past that point (ADR 0045).
// Ramps virtual users on a read that needs a token, and reports requests per second, the latency of the requests that were served,
// and how many were turned away (503). Run it against an instance with shedding effectively off and one with real limits, and
// compare. Not part of main.js: it is meant to overload a single instance on purpose.
//
//   docker run --rm --network host -v "$(pwd)":/scripts -w /scripts -e DIRECTORY_BASE_URL=http://localhost:5301 grafana/k6 run capacity.js
import http from "k6/http";
import { Counter, Trend } from "k6/metrics";
import { getAdminToken } from "./lib/auth.js";

const BASE = __ENV.DIRECTORY_BASE_URL || "http://localhost:5129";
const PATH = __ENV.CAPACITY_PATH || "/api/departments/roots";
const served = new Trend("served_duration", true);
const shed = new Counter("shed_503");
const ok = new Counter("served_200");

export const options = {
  scenarios: {
    ramp: {
      executor: "ramping-vus",
      startVUs: 5,
      stages: [
        { duration: "15s", target: 10 },
        { duration: "15s", target: 40 },
        { duration: "15s", target: 120 },
        { duration: "15s", target: 300 },
        { duration: "15s", target: 600 },
      ],
      gracefulRampDown: "5s",
    },
  },
  summaryTrendStats: ["avg", "p(95)", "max"],
};

export function setup() {
  return { token: getAdminToken() };
}

export default function (data) {
  const response = http.get(`${BASE}${PATH}`, { headers: { Authorization: `Bearer ${data.token}` }, timeout: "10s" });
  if (response.status === 200) {
    ok.add(1);
    served.add(response.timings.duration);
  } else if (response.status === 503) {
    shed.add(1);
  }
}
