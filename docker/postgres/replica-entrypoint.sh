#!/bin/sh
# A streaming standby of the `postgres` service (ADR 0025). On first start it takes a base backup of the primary, which
# also writes the standby configuration (-R); afterwards it just starts and follows. REPLICA_APPLY_DELAY (e.g. 5s) holds
# back replay on purpose, to make replication lag visible and testable.
set -eu
PGDATA="${PGDATA:-/var/lib/postgresql/18/docker}"
export PGPASSWORD="${POSTGRES_PASSWORD:-postgres}"

until pg_isready -h postgres -U postgres >/dev/null 2>&1; do
  echo "waiting for the primary"
  sleep 1
done

if [ ! -s "$PGDATA/PG_VERSION" ]; then
  echo "taking a base backup of the primary"
  mkdir -p "$PGDATA"
  chown postgres:postgres "$PGDATA"
  chmod 700 "$PGDATA"
  gosu postgres pg_basebackup -h postgres -U postgres -D "$PGDATA" -R -X stream
fi

exec docker-entrypoint.sh postgres -c hot_standby=on -c recovery_min_apply_delay="${REPLICA_APPLY_DELAY:-0}"
