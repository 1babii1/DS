// One hire through the chain, with the Employee and Directory addresses chosen by the caller, to see what the hire does when the call
// between the two services needs mutual TLS (ADR 0044). Creates a location, a department and a position in Directory over its REST
// port, then hires through Employee, which asks Directory over gRPC to validate the assignment. Prints the status of the hire.
//
//   DIRECTORY_BASE_URL=http://localhost:5301 EMPLOYEE_BASE_URL=http://localhost:5311 k6 run mtls-hire.js
import http from "k6/http";
import { check } from "k6";
import { getAdminToken } from "./lib/auth.js";

const DIRECTORY = __ENV.DIRECTORY_BASE_URL || "http://localhost:5301";
const EMPLOYEE = __ENV.EMPLOYEE_BASE_URL || "http://localhost:5311";

export const options = { vus: 1, iterations: 1 };

export function setup() {
  return { token: getAdminToken() };
}

export default function (data) {
  const headers = { Authorization: `Bearer ${data.token}`, "Content-Type": "application/json" };
  const unique = `mtls${Date.now()}`;

  const location = http.post(
    `${DIRECTORY}/api/locations`,
    JSON.stringify({ locationRequest: { name: `mTLS HQ ${unique}`, address: { street: `Main ${unique}`, city: "Almaty", country: "Kazakhstan" }, timezone: "Asia/Almaty" } }),
    { headers },
  );
  check(location, { "location: 200": (r) => r.status === 200 });
  const locationId = location.json("result");

  const department = http.post(
    `${DIRECTORY}/api/departments`,
    JSON.stringify({ request: { name: { value: `mTLS Dept ${unique}` }, identifier: { value: unique.slice(0, 30) }, locationsIds: [{ value: locationId }] } }),
    { headers },
  );
  check(department, { "department: 200": (r) => r.status === 200 });
  const departmentId = department.json("result");

  const position = http.post(
    `${DIRECTORY}/api/positions`,
    JSON.stringify({ request: { name: { value: `mTLS Role ${unique}` }, departmentIds: [{ value: departmentId }] } }),
    { headers },
  );
  check(position, { "position: 200": (r) => r.status === 200 });
  const positionId = position.json("result");

  const hire = http.post(
    `${EMPLOYEE}/api/employees`,
    JSON.stringify({ fullName: `mTLS Employee ${unique}`, email: `${unique}@portfolio.local`, departmentId, positionId }),
    { headers },
  );
  console.log(`HIRE status=${hire.status} body=${hire.body}`);
}
