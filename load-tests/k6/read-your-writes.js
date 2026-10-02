import http from "k6/http";
import { check, sleep } from "k6";
import { Counter } from "k6/metrics";
import { getAdminToken } from "./lib/auth.js";

// Read-your-writes drill for the read replica (ADR 0025). Needs DirectoryService reading from a replica
// (ConnectionStrings__DirectoryServiceReadDb) whose replay is held back (REPLICA_APPLY_DELAY, e.g. 3s), so that a read
// straight after a write would be stale. Each iteration creates a location, then looks for it twice:
//   - presenting the consistency token the write returned (what the BFF does for a signed-in person), and
//   - presenting nothing (a reader who has made no write of their own).
// The first must always find it; the second finds it only once the replica has caught up, which is what shows the lag is real.
//
//   docker run --rm --network host -v "$(pwd)":/scripts -w /scripts grafana/k6 run read-your-writes.js

const BASE = __ENV.DIRECTORY_BASE_URL || "http://localhost:5129";
const TOKEN_HEADER = "X-Consistency-Token";

const withTokenFound = new Counter("with_token_found");
const withTokenMissed = new Counter("with_token_missed");
const noTokenFound = new Counter("no_token_found");
const noTokenMissed = new Counter("no_token_missed");
const tokenMissing = new Counter("write_without_token");

export const options = {
  vus: 1,
  iterations: 20,
  thresholds: {
    with_token_missed: ["count==0"],
    write_without_token: ["count==0"],
  },
};

export function setup() {
  return { token: getAdminToken() };
}

function found(res, name) {
  return res.status === 200 && res.json("items").some((l) => l.name === name);
}

export default function (data) {
  const auth = { Authorization: `Bearer ${data.token}`, "Content-Type": "application/json" };
  const unique = `${__ITER}${Date.now()}`;
  const name = `RYW ${unique}`;

  const write = http.post(
    `${BASE}/api/locations`,
    JSON.stringify({
      locationRequest: { name, address: { street: `Main ${unique}`, city: "Almaty", country: "Kazakhstan" }, timezone: "Asia/Almaty" },
    }),
    { headers: auth },
  );
  if (!check(write, { "write: 200": (r) => r.status === 200 })) {
    return;
  }
  const consistencyToken = write.headers[TOKEN_HEADER];
  if (!consistencyToken) {
    tokenMissing.add(1);
    return;
  }

  const url = `${BASE}/api/locations?search=${encodeURIComponent(name)}`;
  const withToken = http.get(url, { headers: { ...auth, [TOKEN_HEADER]: consistencyToken } });
  const withoutToken = http.get(url, { headers: auth });

  (found(withToken, name) ? withTokenFound : withTokenMissed).add(1);
  (found(withoutToken, name) ? noTokenFound : noTokenMissed).add(1);

  // Stay well inside the write limiter (30/60s per IP).
  sleep(3);
}
