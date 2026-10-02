#!/usr/bin/env bash
# Connection-exhaustion drill (ADR 0024): the same N concurrent clients, each running a 20 ms transaction, first straight
# at Postgres and then through PgBouncer. Direct, anything above max_connections is refused ("too many clients already");
# through the pooler the same clients all succeed and the excess waits in the pooler's queue (visible as latency).
#
#   scripts/pool-drill.sh            # 300 clients, 10 seconds
#   scripts/pool-drill.sh 500 20
#
# Runs pgbench inside the postgres container; briefly loads the dev database, nothing is written.
set -euo pipefail
clients="${1:-300}"
seconds="${2:-10}"
pw="${POSTGRES_PASSWORD:-postgres}"

docker exec postgres sh -c "echo 'SELECT pg_sleep(0.02);' > /tmp/sleep.sql"
echo "max_connections: $(docker exec postgres psql -U postgres -At -c 'show max_connections')"
for host in postgres pgbouncer; do
  echo "=== ${clients} clients, ${seconds}s, via ${host}"
  docker exec -e PGPASSWORD="$pw" postgres pgbench -h "$host" -U postgres -d platform -n -c "$clients" -j 4 -T "$seconds" -f /tmp/sleep.sql 2>&1 \
    | grep -E "too many clients|number of failed|latency average|tps =|OpenSSL failure" | sort | uniq -c || true
done
