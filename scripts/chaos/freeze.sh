#!/usr/bin/env bash
# Freezes one container of the running stack for N seconds, then thaws it, printing the
# reliability metrics before, during and after - to be watched next to the "Resilience"
# dashboard in Grafana (http://localhost:3001, `docker compose --profile obs up -d`).
#
# `docker pause` stops the processes but leaves TCP connections open: nothing errors at once,
# callers wait for their own timeouts. That is the hung-dependency failure, not a clean refusal.
#
# Usage: scripts/chaos/freeze.sh <container> [seconds]      e.g.  kafka 30   |   postgres 45
#
# The container is thawed on exit, Ctrl-C included, so a script that dies never leaves the
# stack frozen. What the automated version asserts (no bonus lost or doubled) is in
# backend/ChaosTests; this is the same fault made visible.
set -euo pipefail

CONTAINER="${1:?Usage: freeze.sh <container> [seconds]}"
SECONDS_FROZEN="${2:-30}"
PROM="${PROMETHEUS_URL:-http://localhost:9090}"

if ! docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null | grep -q true; then
  echo "Container '$CONTAINER' is not running." >&2
  exit 1
fi

thaw() {
  if [[ "$(docker inspect -f '{{.State.Paused}}' "$CONTAINER" 2>/dev/null)" == "true" ]]; then
    docker unpause "$CONTAINER" >/dev/null
    echo "[$(date +%T)] thawed $CONTAINER"
  fi
}
trap thaw EXIT

# One instant query; prints "n/a" when Prometheus is unreachable or the metric does not exist
# (the outbox metrics exist only in services built from a version that has them).
metric() {
  local value
  value="$(curl -sf --max-time 3 --get "$PROM/api/v1/query" --data-urlencode "query=$1" 2>/dev/null \
    | python3 -c 'import sys,json; r=json.load(sys.stdin)["data"]["result"]; print(r[0]["value"][1] if r else "n/a")' 2>/dev/null || true)"
  echo "${value:-n/a}"
}

snapshot() {
  printf '[%s] %-7s parked=%s pending=%s oldest_pending_s=%s dead_letters=%s\n' \
    "$(date +%T)" "$1" \
    "$(metric 'sum(outbox_parked_messages)')" \
    "$(metric 'sum(outbox_pending_messages)')" \
    "$(metric 'max(outbox_oldest_pending_age_seconds)')" \
    "$(metric 'sum(dead_letters)')"
}

snapshot before
echo "[$(date +%T)] freezing $CONTAINER for ${SECONDS_FROZEN}s"
docker pause "$CONTAINER" >/dev/null

end=$((SECONDS + SECONDS_FROZEN))
while (( SECONDS < end )); do
  sleep 10
  snapshot during
done

thaw
echo "waiting 60s for the system to catch up (metrics refresh every 30s)"
sleep 60
snapshot after
