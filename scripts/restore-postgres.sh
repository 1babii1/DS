#!/usr/bin/env bash
# Restores a dump made by backup-postgres.sh into a NEW database on the same running
# postgres container, for verifying a backup is actually restorable - it never touches
# the live "platform" database that every service is connected to, so it's safe to run
# against a shared dev instance without coordinating a maintenance window.
#
# A real disaster-recovery restore (replacing "platform" itself) is a separate,
# deliberately more dangerous operation: stop every service first (they hold open
# connections and would otherwise race the restore), drop/recreate "platform", restore
# into it, then restart services. That path is intentionally not scripted here - see
# docs/adr/0009-postgres-backup-and-restore.md for the documented manual procedure and
# why it stays manual for now.
#
# Usage: scripts/restore-postgres.sh <dump-file> [target-database]
#   target-database defaults to platform_restore_check (dropped and recreated if it
#   already exists - never "platform" itself; the script refuses that name outright).
set -euo pipefail

CONTAINER="${POSTGRES_CONTAINER:-postgres}"
DUMP_FILE="${1:?Usage: restore-postgres.sh <dump-file> [target-database]}"
TARGET_DB="${2:-platform_restore_check}"

if [[ "$TARGET_DB" == "platform" ]]; then
  echo "Refusing to restore into 'platform' directly - that's the live database every service uses. Restore into a scratch database and diff it, or follow the manual DR procedure in the ADR." >&2
  exit 1
fi

if [[ ! -f "$DUMP_FILE" ]]; then
  echo "Dump file not found: $DUMP_FILE" >&2
  exit 1
fi

if ! docker inspect -f '{{.State.Running}}' "$CONTAINER" >/dev/null 2>&1; then
  echo "Container '$CONTAINER' is not running - start it first (docker compose up -d postgres)." >&2
  exit 1
fi

dump_basename="$(basename "$DUMP_FILE")"
dump_path_in_container="/tmp/$dump_basename"

echo "Copying dump into container..."
docker cp "$DUMP_FILE" "$CONTAINER:$dump_path_in_container"

echo "Recreating scratch database '$TARGET_DB'..."
docker exec -u postgres "$CONTAINER" psql -c "DROP DATABASE IF EXISTS $TARGET_DB;"
docker exec -u postgres "$CONTAINER" psql -c "CREATE DATABASE $TARGET_DB;"

echo "Restoring into '$TARGET_DB'..."
docker exec -u postgres "$CONTAINER" pg_restore -d "$TARGET_DB" "$dump_path_in_container"
docker exec -u postgres "$CONTAINER" rm -f "$dump_path_in_container"

echo
echo "Restored. Compare against the live database, e.g.:"
echo "  docker exec -u postgres $CONTAINER psql -d platform -Atc \"SELECT schemaname, relname, n_live_tup FROM pg_stat_user_tables ORDER BY 1,2;\""
echo "  docker exec -u postgres $CONTAINER psql -d $TARGET_DB -Atc \"SELECT schemaname, relname, n_live_tup FROM pg_stat_user_tables ORDER BY 1,2;\""
echo
echo "Drop the scratch database when done: docker exec -u postgres $CONTAINER psql -c \"DROP DATABASE $TARGET_DB;\""
