#!/usr/bin/env bash
# The sharding experiment of ADR 0033. Usage: scripts/citus-drill.sh <stage>
#   up        start a Citus cluster (coordinator + 2 workers) and a plain Postgres of the same size
#   load      create the audit-shaped table on both and load 3,000,000 events
#   bench     run the four workloads on both and print transactions per second and latency
#   scale     add a third worker and rebalance while a read workload keeps running
#   down      remove everything
# `docker exec` is piped through cat (with the snap Docker CLI it fails silently when stdout is not a pipe) and gets files by
# `docker cp`, not by stdin.
set -euo pipefail
cd "$(dirname "$0")/.."
compose="docker compose -f docker/citus/docker-compose.yml"
pgbin=/usr/lib/postgresql/18/bin
dx() { local c="$1"; shift; docker exec -i "$c" "$@" 2>&1 | cat; }
sql() { local c="$1"; shift; docker exec -i "$c" psql -U postgres -v ON_ERROR_STOP=1 -X -q "$@" 2>&1 | cat; }
psqlf() { local c="$1" f="$2"; docker cp "$f" "$c:/tmp/$(basename "$f")"; docker exec "$c" psql -U postgres -v ON_ERROR_STOP=1 -X -q -f "/tmp/$(basename "$f")" 2>&1 | cat; }

case "${1:?stage}" in
  up)
    $compose up -d coordinator worker1 worker2 single
    until [ "$(docker inspect -f '{{.State.Health.Status}}' citus_worker2)" = healthy ]; do sleep 2; done
    sql citus_coordinator -tAc "SELECT citus_set_coordinator_host('citus_coordinator', 5432)"
    sql citus_coordinator -tAc "SELECT citus_add_node('citus_worker1', 5432)"
    sql citus_coordinator -tAc "SELECT citus_add_node('citus_worker2', 5432)"
    sql citus_coordinator -tAc "SELECT nodeid, nodename, shouldhaveshards FROM pg_dist_node ORDER BY nodeid"
    ;;
  load)
    for c in citus_coordinator citus_single; do psqlf "$c" scripts/citus/schema.sql; done
    sql citus_coordinator -c "SELECT citus_set_node_property('citus_coordinator', 5432, 'shouldhaveshards', false)"
    sql citus_coordinator -c "SET citus.shard_count = 32" -c "SELECT create_distributed_table('entries', 'AggregateId')" -c "SELECT create_distributed_table('recorded_messages', 'MessageId', colocate_with => 'none')"
    for c in citus_coordinator citus_single; do
      echo "== load on $c"; ( time psqlf "$c" scripts/citus/load.sql ) 2>&1 | grep real
    done
    ;;
  bench)
    for c in citus_coordinator citus_single; do
      for f in point range timemachine insert insert2; do docker cp "scripts/citus/$f.sql" "$c:/tmp/$f.sql"; done
    done
    run() { # container script clients seconds
      echo "--- $1 $2 (clients $3)"
      dx "$1" $pgbin/pgbench -U postgres -d postgres -n -f "/tmp/$2.sql" -c "$3" -j 4 -T "$4" | grep -E "tps|latency average|failed" || true
    }
    for c in citus_single citus_coordinator; do
      echo "=========== $c"
      run $c point 16 15; run $c range 4 15; run $c timemachine 2 20; run $c insert 16 15; run $c insert2 16 15
    done
    ;;
  scale)
    $compose --profile scale up -d worker3
    until [ "$(docker inspect -f '{{.State.Health.Status}}' citus_worker3)" = healthy ]; do sleep 2; done
    docker cp scripts/citus/point.sql citus_coordinator:/tmp/point.sql
    sql citus_coordinator -tAc "SELECT nodename, count(*) FROM pg_dist_shard_placement p GROUP BY 1 ORDER BY 1"
    sql citus_coordinator -tAc "SELECT citus_add_node('citus_worker3', 5432)"
    ( dx citus_coordinator $pgbin/pgbench -U postgres -d postgres -n -f /tmp/point.sql -c 16 -j 4 -T 60 -P 5 > /tmp/citus-scale-read.log ) &
    reader=$!
    sleep 10
    echo "== rebalance"; ( time sql citus_coordinator -tAc "SELECT citus_rebalance_start(rebalance_strategy := 'by_shard_count', shard_transfer_mode := 'block_writes')" ) 2>&1 | grep -E "real|^[0-9]"
    wait $reader || true
    grep -E "progress|tps|failed|latency average" /tmp/citus-scale-read.log
    sql citus_coordinator -tAc "SELECT nodename, count(*) FROM pg_dist_shard_placement p GROUP BY 1 ORDER BY 1"
    ;;
  down)
    $compose --profile scale down -v
    ;;
esac
