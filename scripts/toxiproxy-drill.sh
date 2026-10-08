#!/usr/bin/env bash
# What a degraded network between EmployeeService and DirectoryService does to a hire (ADR 0051). Toxiproxy sits on the gRPC call; a steady
# stream of hires (k6) runs through it while the call is made worse step by step, then restored. For each step the table says how many hires
# succeeded, how long they took (p50/p95/p99), how many failed at once (the circuit breaker, a refused connection) and how many failed after
# waiting (a timeout, the retries spent on one call).
#
# Starts its own EmployeeService (private Postgres, no Kafka) and a second DirectoryService on the stack's network and database, beside the
# stack's own; leaves a few departments/positions/locations behind in the development database, like the k6 hire chain does. Tokens come from the
# stack's AuthService. Needs the images dsporfolio-employee_service and dsporfolio-directory_service (docker compose build) or EMP_IMAGE/DIR_IMAGE.
#
#   scripts/toxiproxy-drill.sh [seconds per step, default 30]
#   STEPS=baseline,lat6 scripts/toxiproxy-drill.sh   # only these steps: baseline, lat500, lat2, lat6, reset, down, recovered
#   OUTAGE_SECS=90 scripts/toxiproxy-drill.sh   # the "unreachable" step lasts longer (the gRPC channel's reconnect back-off grows with it)
set -euo pipefail

# When a drill stops for any reason, say where and show what the services it started last said: a failure on a CI runner is otherwise a bare exit code.
set -E
trap 'echo "the drill stopped at line $LINENO: $BASH_COMMAND" >&2' ERR
show_logs() {
  for c in toxiproxy dir_tox emp_tox pg_tox; do
    docker inspect "$c" >/dev/null 2>&1 || continue
    echo "--- $c ($(docker inspect -f '{{.State.Status}}' "$c"))" >&2
    docker logs --tail 25 "$c" 2>&1 | sed 's/\x1b\[[0-9;]*m//g' | cut -c1-220 >&2
  done
}
cd "$(dirname "$0")/.."
secs="${1:-30}"
outage="${OUTAGE_SECS:-$secs}"
net=dsporfolio_default
emp_image="${EMP_IMAGE:-dsporfolio-employee_service:latest}"
dir_image="${DIR_IMAGE:-dsporfolio-directory_service:latest}"
tox_image=ghcr.io/shopify/toxiproxy:2.12.0
api=http://localhost:18474

cleanup() { docker rm -f toxiproxy dir_tox emp_tox pg_tox >/dev/null 2>&1 || true; }
[ -n "${KEEP:-}" ] || trap 'status=$?; [ "$status" = 0 ] || show_logs; cleanup' EXIT
cleanup

docker run -d --name pg_tox --network $net -e POSTGRES_PASSWORD=toxpw -e POSTGRES_DB=platform pgvector/pgvector:pg18 >/dev/null
until docker exec pg_tox pg_isready -U postgres -d platform >/dev/null 2>&1; do sleep 1; done
sleep 2
docker exec pg_tox psql -U postgres -d platform -q -c "CREATE SCHEMA employee" >/dev/null
# The image's entrypoint is the web host: the migration bundle has to be named as the entrypoint (ADR 0044).
docker run --rm --network $net --entrypoint ./efbundle "$emp_image" \
  --connection "Server=pg_tox;Port=5432;Database=platform;User Id=postgres;Password=toxpw;Search Path=employee,public" >/dev/null

common_env=(-e ASPNETCORE_ENVIRONMENT=Docker -e Outbox__Mode=Polling -e SchemaRegistry__Url=http://schema_registry:8080/apis/ccompat/v7
  -e Kafka__SaslUsername=kafka -e Kafka__SaslPassword=changeme-kafka)

docker run -d --name dir_tox --network $net -p 5301:5129 "${common_env[@]}" \
  -e "ConnectionStrings__DirectoryServiceDb=Server=pgbouncer;Port=5432;Database=platform;User Id=postgres;Password=postgres;Search Path=directory,public" \
  -e "ConnectionStrings__Redis=redis:6379,password=changeme-redis" -e Kafka__Topics__0=employee.events.v2 -e Kafka__GroupId=directory-tox-drill "$dir_image" >/dev/null

docker run -d --name toxiproxy --network $net -p 18474:8474 "$tox_image" >/dev/null
until curl -s -o /dev/null "$api/version"; do sleep 1; done
curl -s -X POST "$api/proxies" -d '{"name":"directory_grpc","listen":"0.0.0.0:5179","upstream":"dir_tox:5179"}' >/dev/null

docker run -d --name emp_tox --network $net -p 5311:5131 "${common_env[@]}" \
  -e "ConnectionStrings__EmployeeServiceDb=Server=pg_tox;Port=5432;Database=platform;User Id=postgres;Password=toxpw;Search Path=employee,public" \
  -e RateLimiting__Write__PermitLimit=1000000 -e ASPNETCORE_URLS=http://*:5131 -e Kafka__BootstrapServers=no-such-broker:9092 -e Kafka__Topics__0=auth.events.v2 -e Kafka__GroupId=emp-tox-drill \
  -e Directory__GrpcAddress=http://toxiproxy:5179 ${DEBUG_GRPC:+-e Logging__LogLevel__Grpc=Debug -e Serilog__MinimumLevel__Override__Grpc=Debug} "$emp_image" >/dev/null

for _ in $(seq 1 60); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5301/health/live)" = 200 ] \
    && [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5311/health/live)" = 200 ] && break
  sleep 2
done

toxic() { curl -s -X POST "$api/proxies/directory_grpc/toxics" -d "$1" >/dev/null; }
clear_toxics() {
  for t in $(curl -s "$api/proxies/directory_grpc/toxics" | python3 -c 'import json,sys; print(" ".join(t["name"] for t in json.load(sys.stdin)))'); do
    curl -s -X DELETE "$api/proxies/directory_grpc/toxics/$t" >/dev/null
  done
  curl -s -X POST "$api/proxies/directory_grpc" -d '{"enabled":true}' >/dev/null
}

results=$(mktemp)
k6() { # extra docker -e arguments, then k6 reads the rest from the environment
  docker run --rm --network host -v "$PWD/load-tests/k6":/scripts -w /scripts \
    -e DIRECTORY_BASE_URL=http://localhost:5301 -e EMPLOYEE_BASE_URL=http://localhost:5311 "$@" grafana/k6 run --quiet toxiproxy-hire.js 2>&1
}
# One login and one set of reference data for all the steps (the sign-in has its own limit of five a minute).
prep_out=$(k6 -e MODE=prepare; echo "[k6 exited with $?]")
prep=$(echo "$prep_out" | grep -o 'PREPARED {.*}' | sed 's/^PREPARED //' | sed 's/\\"/"/g;s/}.*$/}/' || true)
if [ -z "$prep" ]; then
  echo "the sign-in and the reference data could not be prepared; k6 said:" >&2
  echo "$prep_out" | tail -15 | cut -c1-300 >&2
  exit 1
fi
token=$(python3 -c 'import json,sys; print(json.loads(sys.argv[1])["token"])' "$prep")
dept=$(python3 -c 'import json,sys; print(json.loads(sys.argv[1])["departmentId"])' "$prep")
pos=$(python3 -c 'import json,sys; print(json.loads(sys.argv[1])["positionId"])' "$prep")
events=$(mktemp)
phase() { # label [seconds]
  local length="${2:-$secs}"
  echo ">> $1 (${length}s)" >&2
  local since
  since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  local out
  out=$(mktemp)
  k6 -e PHASE="$1" -e DURATION="${length}s" -e TOKEN="$token" -e DEPARTMENT_ID="$dept" -e POSITION_ID="$pos" | tee /dev/stderr >"$out"
  # The line is the result plus when, from the start of the step, the first hire succeeded (empty if none did).
  first=$(grep -o 'FIRST_OK [0-9]*' "$out" | awk '{print $2}' | sort -n | head -1 || true)
  grep -E "PHASE_RESULT" "$out" | sed 's/^.*PHASE_RESULT //' | python3 -c 'import json,sys; r=json.loads(sys.stdin.read()); r["firstOkMs"]=int(sys.argv[1]) if sys.argv[1] else None; print(json.dumps(r))' "${first:-}" >>"$results"
  rm -f "$out"
  # What the resilience pipeline itself logged during the step (Polly writes one line per event).
  docker logs --since "$since" emp_tox 2>&1 | sed 's/\x1b\[[0-9;]*m//g' | grep "Resilience event occurred" \
    | sed -E "s/.*EventName: '([A-Za-z]+)'.*/\1/" | sort | uniq -c | awk -v p="$1" '{printf "%s\t%s\t%s\n", p, $2, $1}' >>"$events" || true
}

want() { [ -z "${STEPS:-}" ] || [[ ",$STEPS," == *",$1,"* ]]; }
want baseline && phase "baseline"
if want lat500; then toxic '{"name":"lat","type":"latency","stream":"downstream","attributes":{"latency":500,"jitter":100}}'; phase "latency 500 ms"; clear_toxics; fi
if want lat2; then toxic '{"name":"lat","type":"latency","stream":"downstream","attributes":{"latency":2000,"jitter":200}}'; phase "latency 2 s"; clear_toxics; fi
if want lat6; then toxic '{"name":"lat","type":"latency","stream":"downstream","attributes":{"latency":6000,"jitter":0}}'; phase "latency 6 s (past the 5 s timeout)"; clear_toxics; fi
if want reset; then toxic '{"name":"rst","type":"reset_peer","stream":"downstream","attributes":{"timeout":0}}'; phase "connection reset"; clear_toxics; fi
if want down; then curl -s -X POST "$api/proxies/directory_grpc" -d '{"enabled":false}' >/dev/null; phase "directory unreachable" "$outage"; clear_toxics; fi
want recovered && phase "recovered"

python3 - "$results" "$events" <<'PY'
import json, sys, collections
rows = [json.loads(l) for l in open(sys.argv[1]) if l.strip()]
ev = collections.defaultdict(dict)
for l in open(sys.argv[2]):
    p, name, n = l.rstrip("\n").split("\t")
    ev[p][name] = int(n)
def e(p, name): return ev[p].get(name, 0)
print("\n| step | hires ok | failed | statuses | p50 ms | p95 ms | p99 ms | max ms | first ok at | retries | timeouts | breaker opened / half-open / closed |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|")
for r in rows:
    p = r["phase"]
    st = ", ".join(f"{k}: {v}" for k, v in r["statuses"].items())
    print(f"| {p} | {r['ok']} | {r['failed']} (fast {r['fastFail']}, slow {r['slowFail']}) | {st} | {r['p50']} | {r['p95']} | {r['p99']} | {r['max']} | {(str(round(r['firstOkMs']/1000,1))+' s') if r.get('firstOkMs') is not None else 'none'} | {e(p,'OnRetry')} | {e(p,'OnTimeout')} | {e(p,'OnCircuitOpened')} / {e(p,'OnCircuitHalfOpened')} / {e(p,'OnCircuitClosed')} |")

# What the numbers have to be for the drill to count as passed (ADR 0058): generous, since a runner is slower and noisier than a laptop, and
# aimed at what each ADR claimed, not at the exact figures.
def row(prefix): return next((r for r in rows if r["phase"].startswith(prefix)), None)
checks = []
def need(label, ok): checks.append((label, bool(ok)))
r = row("baseline")
if r: need("baseline: no hire failed, and many succeeded", r["failed"] == 0 and r["ok"] > 100)
for prefix in ("latency 500", "latency 2 s"):
    r = row(prefix)
    if r: need(f"{prefix}: a slow answer inside the limits still succeeds", r["failed"] == 0 and r["ok"] > 0)
r = row("latency 6 s")
if r: need("latency 6 s: hires are refused fast once the breaker is open (p50 <= 200 ms) and none waits past the 8 s deadline", r["ok"] == 0 and r["p50"] <= 200 and r["max"] <= 12000)
r = row("connection reset")
if r: need("connection reset: refused fast (p50 <= 200 ms)", r["p50"] <= 200)
r = row("directory unreachable")
if r: need("directory unreachable: refused fast, and mostly at once", r["p50"] <= 200 and r["fastFail"] >= 0.9 * max(r["failed"], 1))
r = row("recovered")
if r: need("recovered: hires succeed again within 15 s of the network returning", r["ok"] > 0 and r.get("firstOkMs") is not None and r["firstOkMs"] <= 15000)
print()
for label, ok in checks:
    print(("PASS  " if ok else "FAIL  ") + label)
sys.exit(0 if all(ok for _, ok in checks) else 1)
PY
