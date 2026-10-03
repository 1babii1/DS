#!/usr/bin/env bash
# The Redis failover drill of ADR 0036. Usage: scripts/redis-ha-drill.sh <stage>
#   up      start a master, two replicas and three sentinels
#   kill    run a client through Sentinel for 40 s, kill the master after 10 s, report what the client saw
#   down    remove everything
# `docker exec` output is piped through cat: with the snap Docker CLI it fails silently when stdout is not a pipe.
set -euo pipefail
cd "$(dirname "$0")/.."
compose="docker compose -f docker/redis-ha/docker-compose.yml"
dx() { local c="$1"; shift; docker exec "$c" "$@" 2>&1 | cat; }

case "${1:?stage}" in
  up)
    $compose up -d
    sleep 8
    dx redis_sentinel1 redis-cli -p 26379 sentinel get-master-addr-by-name mymaster
    dx redis_sentinel1 redis-cli -p 26379 sentinel replicas mymaster | grep -E "^(name|flags)$" -A1 | grep -vE "^(name|flags|--)$"
    ;;
  kill)
    cs="redis_sentinel1:26379,redis_sentinel2:26379,redis_sentinel3:26379,serviceName=mymaster,password=drillpass"
    ( sleep 10; echo "killing the master"; docker kill redis_master >/dev/null ) &
    # built on the host (restoring packages inside a fresh container is slow and flaky), run next to the cluster
    dotnet publish tools/redis-failover-probe -c Release -o tools/redis-failover-probe/out >/dev/null
    docker run --rm --network redis-ha_default -v "$PWD/tools/redis-failover-probe/out":/probe -w /probe mcr.microsoft.com/dotnet/sdk:10.0 \
      dotnet redis-failover-probe.dll 40 "$cs" 2>&1 | cat
    wait
    echo "master according to the sentinels now:"
    dx redis_sentinel1 redis-cli -p 26379 sentinel get-master-addr-by-name mymaster
    ;;
  down)
    $compose down -v
    ;;
esac
