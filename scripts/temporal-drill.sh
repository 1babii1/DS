#!/usr/bin/env bash
# The hire's onboarding as a Temporal workflow against the saga of ADR 0032, with the process that carries it killed (ADR 0056).
# A hire is made, the EmployeeService instance that took it is killed with SIGKILL, the onboarding deadline (30 s here) passes while no
# instance exists, and a new instance is started: the hire has to be undone, once, and the script says how long that took after the start.
# The other services are not run, so no account and no bonus ever arrive and every hire ends at its deadline. The same is done with the saga
# in place of the workflow. A third case stops Temporal itself around a hire.
#
# Starts a private Postgres (the employee schema), a DirectoryService on the stack's network (its REST and gRPC ports), Temporal's dev server
# and EmployeeService instances. Tokens come from the stack's AuthService. Needs the image ds-employee:temporal (build backend/EmployeeService/Dockerfile
# from backend/) and dsporfolio-directory_service, or EMP_IMAGE / DIR_IMAGE.
#
#   scripts/temporal-drill.sh                 # all three cases
#   CASES=workflow,saga scripts/temporal-drill.sh   # a subset: workflow, saga, server-down, precision
set -euo pipefail

# When a drill stops for any reason, say where and show what the services it started last said: a failure on a CI runner is otherwise a bare exit code.
set -E
trap 'echo "the drill stopped at line $LINENO: $BASH_COMMAND" >&2' ERR
show_logs() {
  for c in dir_tp temporal_tp emp_tp_a emp_tp_b; do
    docker inspect "$c" >/dev/null 2>&1 || continue
    echo "--- $c ($(docker inspect -f '{{.State.Status}}' "$c"))" >&2
    docker logs --tail 25 "$c" 2>&1 | sed 's/\x1b\[[0-9;]*m//g' | cut -c1-220 >&2
  done
}
cd "$(dirname "$0")/.."
cases="${CASES:-workflow,saga,server-down,precision}"
net=dsporfolio_default
emp_image="${EMP_IMAGE:-ds-employee:temporal}"
dir_image="${DIR_IMAGE:-dsporfolio-directory_service:latest}"
timeout_secs=30

cleanup() { docker rm -f pg_tp dir_tp temporal_tp emp_tp_a emp_tp_b >/dev/null 2>&1 || true; }
[ -n "${KEEP:-}" ] || trap 'status=$?; [ "$status" = 0 ] || show_logs; cleanup' EXIT
cleanup

docker run -d --name pg_tp --network $net -e POSTGRES_PASSWORD=tppw -e POSTGRES_DB=platform pgvector/pgvector:pg18 >/dev/null
until docker exec pg_tp pg_isready -U postgres -d platform >/dev/null 2>&1; do sleep 1; done
sleep 2
docker exec pg_tp psql -U postgres -d platform -q -c "CREATE SCHEMA employee" >/dev/null
# The migration bundle has to be named as the entrypoint (ADR 0044).
docker run --rm --network $net --entrypoint ./efbundle "$emp_image" \
  --connection "Server=pg_tp;Port=5432;Database=platform;User Id=postgres;Password=tppw;Search Path=employee,public" >/dev/null

common=(-e ASPNETCORE_ENVIRONMENT=Docker -e Outbox__Mode=Polling -e SchemaRegistry__Url=http://schema_registry:8080/apis/ccompat/v7
  -e Kafka__SaslUsername=kafka -e Kafka__SaslPassword=changeme-kafka)
docker run -d --name dir_tp --network $net -p 5301:5129 "${common[@]}" \
  -e "ConnectionStrings__DirectoryServiceDb=Server=pgbouncer;Port=5432;Database=platform;User Id=postgres;Password=postgres;Search Path=directory,public" \
  -e "ConnectionStrings__Redis=redis:6379,password=changeme-redis" -e Kafka__Topics__0=employee.events.v2 -e Kafka__GroupId=directory-tp-drill "$dir_image" >/dev/null

start_temporal() {
  docker run -d --name temporal_tp --network $net -p 17233:7233 -p 18233:8233 temporalio/temporal:latest \
    server start-dev --ip 0.0.0.0 --port 7233 --ui-port 8233 >/dev/null
  for _ in $(seq 1 60); do curl -s -o /dev/null http://localhost:18233 && break; sleep 1; done
  sleep 3
}

# mode name port
start_employee() {
  local mode="$2"
  docker run -d --name "$1" --network $net -p "$3":5131 "${common[@]}" \
    -e "ConnectionStrings__EmployeeServiceDb=Server=pg_tp;Port=5432;Database=platform;User Id=postgres;Password=tppw;Search Path=employee,public" \
    -e RateLimiting__Write__PermitLimit=1000000 -e ASPNETCORE_URLS=http://*:5131 -e Kafka__BootstrapServers=no-such-broker:9092 \
    -e Kafka__Topics__0=auth.events.v2 -e Kafka__GroupId="$1-drill" -e Directory__GrpcAddress=http://dir_tp:5179 \
    -e HireSaga__OnboardingTimeout=00:00:${timeout_secs} -e HireOrchestration__Mode="$mode" -e HireOrchestration__Address=temporal_tp:7233 \
    "$emp_image" >/dev/null
  for _ in $(seq 1 60); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:"$3"/health/live)" = 200 ] && return 0; sleep 1; done
  echo "instance $1 did not come up" >&2
  return 1
}

for _ in $(seq 1 60); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5301/health/live)" = 200 ] && break; sleep 2; done
prep_out=$(docker run --rm --network host -v "$PWD/load-tests/k6":/scripts -w /scripts -e MODE=prepare -e DIRECTORY_BASE_URL=http://localhost:5301 \
  -e EMPLOYEE_BASE_URL=http://localhost:5311 -e ADMIN_EMAIL -e ADMIN_PASSWORD grafana/k6 run --quiet toxiproxy-hire.js 2>&1; echo "[k6 exited with $?]")
prep=$(echo "$prep_out" | grep -o 'PREPARED {.*}' | sed 's/^PREPARED //' | sed 's/\\"/"/g;s/}.*$/}/' || true)
if [ -z "$prep" ]; then
  echo "the sign-in and the reference data could not be prepared; k6 said:" >&2
  echo "$prep_out" | tail -15 | cut -c1-300 >&2
  exit 1
fi
token=$(python3 -c 'import json,sys; print(json.loads(sys.argv[1])["token"])' "$prep")
dept=$(python3 -c 'import json,sys; print(json.loads(sys.argv[1])["departmentId"])' "$prep")
pos=$(python3 -c 'import json,sys; print(json.loads(sys.argv[1])["positionId"])' "$prep")

q() { docker exec pg_tp psql -U postgres -d platform -tAc "$1"; }
hire() { # port -> employee id
  local out
  out=$(curl -s -X POST "http://localhost:$1/api/employees" -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
    -H "Idempotency-Key: tp-$(date +%s%N)" -d "{\"fullName\":\"Drill $(date +%s%N)\",\"email\":\"tp-$(date +%s%N)@portfolio.local\",\"departmentId\":\"$dept\",\"positionId\":\"$pos\"}")
  python3 -c 'import json,sys; print(json.loads(sys.argv[1])["result"])' "$out"
}
status_of() { q "SELECT \"Status\" FROM employee.employees WHERE \"Id\" = '$1'"; }   # PendingProvisioning, Active, ProvisioningFailed
undone_events() { q "SELECT count(*) FROM employee.outbox_messages WHERE \"Type\" = 'HireCompensationRequested' AND \"AggregateId\" = '$1'"; }
wait_for_failed() { # id, max seconds -> prints seconds waited, or "never"
  local started=$SECONDS
  while [ $((SECONDS - started)) -lt "$2" ]; do
    [ "$(status_of "$1")" = ProvisioningFailed ] && { echo "$((SECONDS - started))"; return; }
    sleep 1
  done
  echo never
}

failures=0
# A drill that only prints is a measurement; one that exits non-zero when a number is wrong can run unattended (the nightly drills of ADR 0058).
check() { # label ok|no
  if [ "$2" = ok ]; then echo "PASS  $1"; else echo "FAIL  $1"; failures=$((failures + 1)); fi
}

kill_case() { # label mode port
  local label="$1" mode="$2" port="$3"
  echo "=== $label: the instance is killed with the timer running, the ${timeout_secs} s deadline passes with no instance alive"
  start_employee emp_tp_a "$mode" "$port"
  local id t0
  id=$(hire "$port")
  t0=$SECONDS
  echo "hired $id; status $(status_of "$id")"
  sleep 4
  docker kill -s KILL emp_tp_a >/dev/null
  echo "[$((SECONDS - t0)) s] instance killed"
  while [ $((SECONDS - t0)) -lt $((timeout_secs + 15)) ]; do sleep 1; done
  echo "[$((SECONDS - t0)) s] the deadline passed $((SECONDS - t0 - timeout_secs)) s ago; no instance has been running; status still $(status_of "$id"); compensation events $(undone_events "$id")"
  docker rm -f emp_tp_a >/dev/null
  local t1=$SECONDS
  start_employee emp_tp_b "$mode" "$port"
  local up=$((SECONDS - t1))
  local waited
  waited=$(wait_for_failed "$id" 90)
  echo "[$((SECONDS - t0)) s] a new instance came up after ${up} s; the hire was undone ${waited} s after it was up (status $(status_of "$id"), compensation events $(undone_events "$id"))"
  check "$label: the hire was undone within 20 s of a new instance being up" "$([ "$waited" != never ] && [ "$waited" -le 20 ] && echo ok || echo no)"
  check "$label: it was undone exactly once" "$([ "$(undone_events "$id")" = 1 ] && echo ok || echo no)"
  if [ "$mode" = Temporal ]; then
    docker exec temporal_tp temporal workflow describe --address temporal_tp:7233 --workflow-id "hire-$(echo "$id" | tr -d -)" 2>/dev/null \
      | grep -E "^ *(Status|HistoryLength|TaskQueue|Type)" | sed 's/^ */  workflow: /' || true
  fi
  docker rm -f emp_tp_b >/dev/null
}

if [[ ",$cases," == *",workflow,"* ]]; then
  start_temporal
  kill_case "TEMPORAL" Temporal 5361
  docker rm -f temporal_tp >/dev/null
fi

if [[ ",$cases," == *",saga,"* ]]; then
  kill_case "SAGA" Saga 5362
fi

if [[ ",$cases," == *",server-down,"* ]]; then
  echo "=== TEMPORAL SERVER DOWN: a hire is made while Temporal is not there, then Temporal comes back"
  start_temporal
  start_employee emp_tp_a Temporal 5361
  sleep 5
  docker stop temporal_tp >/dev/null
  t0=$SECONDS
  id=$(hire 5361)
  echo "[0 s] hired $id while Temporal is down: the hire itself succeeded (status $(status_of "$id"))"
  sleep 5
  docker start temporal_tp >/dev/null
  echo "[$((SECONDS - t0)) s] Temporal is back; the reconciler (every 15 s, for hires waiting longer than 10 s) has to start the workflow"
  waited=$(wait_for_failed "$id" 120)
  check "TEMPORAL DOWN: the hire succeeded while Temporal was down" "$([ "$(status_of "$id")" != "" ] && echo ok || echo no)"
  check "TEMPORAL DOWN: the reconciler got the hire undone within 50 s of the hire (30 s deadline counted from the hire, plus a reconciler pass)" "$([ "$waited" != never ] && [ $((SECONDS - t0)) -le 50 ] && echo ok || echo no)"
  check "TEMPORAL DOWN: it was undone exactly once" "$([ "$(undone_events "$id")" = 1 ] && echo ok || echo no)"
  echo "[$((SECONDS - t0)) s] the hire was undone ${waited} s after Temporal returned (status $(status_of "$id"), compensation events $(undone_events "$id"))"
  docker logs emp_tp_a 2>&1 | sed 's/\x1b\[[0-9;]*m//g' | grep -m2 "workflow could not be started" | cut -c1-200 || true
fi

# How late a deadline is acted on when the instance is alive the whole time: four hires 4 s apart, each undone at its own deadline (30 s after it).
precision_case() { # label mode port
  local label="$1" mode="$2" port="$3"
  echo "=== $label: how late after its ${timeout_secs} s deadline is a hire undone by an instance that is running"
  start_employee emp_tp_a "$mode" "$port"
  local ids=() hired=() done_at=() n=4 i
  for i in $(seq 1 $n); do
    ids+=("$(hire "$port")")
    hired+=("$SECONDS")
    done_at+=("")
    [ "$i" -lt "$n" ] && sleep 4
  done
  local limit=$((SECONDS + timeout_secs + 40)) left=$n
  while [ "$left" -gt 0 ] && [ $SECONDS -lt $limit ]; do
    for i in $(seq 0 $((n - 1))); do
      if [ -z "${done_at[$i]}" ] && [ "$(status_of "${ids[$i]}")" = ProvisioningFailed ]; then
        done_at[$i]=$SECONDS
        left=$((left - 1))
      fi
    done
    sleep 0.5
  done
  local report="" max_late=0 late
  for i in $(seq 0 $((n - 1))); do
    if [ -n "${done_at[$i]}" ]; then
      late=$((done_at[$i] - hired[$i] - timeout_secs))
      report="$report ${late}s"
      [ "$late" -gt "$max_late" ] && max_late=$late
    else
      report="$report never"
      max_late=999
    fi
  done
  echo "  lateness of each hire after its deadline (seconds, resolution 1 s):$report"
  # Temporal's timer is the server's; the saga's worker looks every 15 s.
  local limit=20
  [ "$mode" = Temporal ] && limit=4
  check "$label: every hire undone within ${limit} s of its own deadline" "$([ "$max_late" -le "$limit" ] && echo ok || echo no)"
  docker rm -f emp_tp_a >/dev/null
}

if [[ ",$cases," == *",precision,"* ]]; then
  docker rm -f temporal_tp emp_tp_a emp_tp_b >/dev/null 2>&1 || true
  start_temporal
  precision_case "TEMPORAL" Temporal 5361
  docker rm -f temporal_tp >/dev/null
  precision_case "SAGA" Saga 5362
fi

echo
if [ "$failures" -eq 0 ]; then
  echo "ALL CHECKS PASSED"
else
  echo "$failures CHECK(S) FAILED"
  exit 1
fi
