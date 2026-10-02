#!/usr/bin/env bash
# Failover drill (ADR 0026): how much acknowledged work is lost, and how long writes are unavailable, when the primary is
# killed and its standby is promoted by hand. Run once with asynchronous replication and once with synchronous, to see
# what the guarantee costs.
#
#   scripts/failover-drill.sh async
#   scripts/failover-drill.sh sync
#   PARTITION_MS=1000 scripts/failover-drill.sh async   # the standby loses contact 1 s before the primary dies
#
# Self-contained: starts its own primary and standby on a private Docker network (same image, same replication set-up as
# docker-compose.yml) and removes them at the end. It never touches the development stack.
#
# Method: WRITERS sessions each run a long list of single-row INSERTs (autocommit), so every statement is one transaction and
# psql prints one "INSERT 0 1" per acknowledgement. After DURATION seconds the primary is killed with SIGKILL (no clean
# shutdown), the standby is promoted, and the rows that exist on it are compared with what clients were told was committed.
#   RPO  = acknowledged inserts missing from the promoted server (data lost)
#   RTO  = from the kill to the first write the promoted server accepted (writes unavailable)
set -euo pipefail

mode="${1:-async}"
[[ "$mode" == "async" || "$mode" == "sync" ]] || { echo "usage: $0 async|sync" >&2; exit 2; }
WRITERS="${WRITERS:-8}"
INSERTS="${INSERTS:-40000}"
DURATION="${DURATION:-3}"
PARTITION_MS="${PARTITION_MS:-0}"
IMAGE="${IMAGE:-pgvector/pgvector:pg18}"
NET="failover_drill"
PRIMARY="drill_primary"
STANDBY="drill_standby"
PORT_P=15441
PORT_S=15442
export PGPASSWORD=drill
work="$(mktemp -d)"

cleanup() { docker rm -f "$PRIMARY" "$STANDBY" >/dev/null 2>&1 || true; docker network rm "$NET" >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT
cleanup
mkdir -p "$work"
trap cleanup EXIT

now() { date +%s.%N; }
psqlp() { psql -h localhost -p "$PORT_P" -U postgres -d drill -X -q "$@"; }
psqls() { psql -h localhost -p "$PORT_S" -U postgres -d drill -X -q "$@"; }

docker network create "$NET" >/dev/null

# The primary: the image's default rules plus the replication line the standby needs (the file compose mounts).
hba="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/docker/postgres/pg_hba.conf"
docker run -d --name "$PRIMARY" --network "$NET" -p "$PORT_P:5432" -e POSTGRES_PASSWORD=drill -e POSTGRES_DB=drill \
  -v "$hba:/etc/postgresql/pg_hba.conf:ro" "$IMAGE" postgres -c hba_file=/etc/postgresql/pg_hba.conf >/dev/null
until docker exec "$PRIMARY" pg_isready -U postgres -d drill >/dev/null 2>&1; do sleep 1; done
sleep 2

# The standby: a base backup of the primary, then streaming (what replica-entrypoint.sh does).
# The standby sits on the default bridge (so its published port keeps working) and on the replication network (which is
# what PARTITION_MS cuts, to model the primary losing contact with its standby just before it dies).
docker create --name "$STANDBY" --network bridge -p "$PORT_S:5432" -e POSTGRES_PASSWORD=drill --entrypoint /bin/sh "$IMAGE" -c '
  export PGPASSWORD=drill
  mkdir -p "$PGDATA" && chown postgres:postgres "$PGDATA" && chmod 700 "$PGDATA"
  gosu postgres pg_basebackup -h drill_primary -U postgres -D "$PGDATA" -R -X stream
  exec docker-entrypoint.sh postgres -c hot_standby=on' >/dev/null
docker network connect "$NET" "$STANDBY"
docker start "$STANDBY" >/dev/null
until psqls -c "select 1" >/dev/null 2>&1; do sleep 1; done

psqlp -c "create table events (writer int not null, n int not null, primary key (writer, n))"
if [[ "$mode" == "sync" ]]; then
  psqlp -c "alter system set synchronous_standby_names = '*'" -c "select pg_reload_conf()" >/dev/null
fi
until [[ "$(psqlp -At -c "select count(*) from pg_stat_replication where state = 'streaming' and sync_state = '$([[ $mode == sync ]] && echo sync || echo async)'")" == "1" ]]; do sleep 1; done
echo "mode: $mode; $(psqlp -At -c "select sync_state from pg_stat_replication") replication; ${WRITERS} writers; standby cut off ${PARTITION_MS} ms before the kill"

# One list of single-statement transactions per writer.
for w in $(seq 1 "$WRITERS"); do
  seq 1 "$INSERTS" | sed "s/^\(.*\)\$/insert into events values ($w, \1);/" >"$work/w$w.sql"
done

start="$(now)"
for w in $(seq 1 "$WRITERS"); do
  # No -q: the command tag "INSERT 0 1" printed per statement is the acknowledgement being counted.
  psql -h localhost -p "$PORT_P" -U postgres -d drill -X -f "$work/w$w.sql" >"$work/acks$w" 2>/dev/null &
done
sleep "$DURATION"
if [[ "$PARTITION_MS" -gt 0 ]]; then
  docker network disconnect "$NET" "$STANDBY"
  sleep "$(awk -v ms="$PARTITION_MS" 'BEGIN { printf "%.3f", ms / 1000 }')"
fi

kill_at="$(now)"
docker kill "$PRIMARY" >/dev/null
promote_start="$(now)"
psqls -At -c "select pg_promote(wait => true, wait_seconds => 60)" >/dev/null
promoted_at="$(now)"
until psqls -c "insert into events values (0, 0)" >/dev/null 2>&1; do sleep 0.05; done
first_write_at="$(now)"
wait 2>/dev/null || true

acked=0
lost=0
for w in $(seq 1 "$WRITERS"); do
  a="$(grep -c '^INSERT 0 1' "$work/acks$w" || true)"
  present="$(psqls -At -c "select count(*) from events where writer = $w and n <= $a")"
  acked=$((acked + a))
  lost=$((lost + a - present))
done

rate="$(awk -v a="$acked" -v s="$start" -v k="$kill_at" 'BEGIN { printf "%.0f", a / (k - s) }')"
rto="$(awk -v k="$kill_at" -v f="$first_write_at" 'BEGIN { printf "%.2f", f - k }')"
promote="$(awk -v p="$promote_start" -v d="$promoted_at" 'BEGIN { printf "%.2f", d - p }')"
echo "acknowledged inserts:        $acked   (about $rate per second before the kill)"
echo "acknowledged and lost (RPO): $lost"
echo "kill -> first write accepted (RTO): ${rto}s   (of which pg_promote: ${promote}s)"
