#!/usr/bin/env bash
# Backs up the platform Postgres database (every service's schema lives in this one
# instance - see docs/adr/0001) to a timestamped custom-format dump, via the running
# docker-compose "postgres" container - no separate psql/pg_dump install required on
# the host, and no database credentials pass through this script's own arguments
# (pg_dump runs inside the container as the postgres OS user via docker exec, the same
# trust-auth path the container's own healthcheck uses).
#
# Usage: scripts/backup-postgres.sh [output-dir]
#   output-dir defaults to ./backups (created if missing, gitignored).
#
# This is Wave 0 of the reliability roadmap's own gap: a database with zero backups
# holds every service's data, including Rewards (money) and Auth (identity). A backup
# nobody has restored from is a hypothesis, not a backup - see restore-postgres.sh and
# docs/adr/0009-postgres-backup-and-restore.md for the restore path this pairs with.
set -euo pipefail

CONTAINER="${POSTGRES_CONTAINER:-postgres}"
DATABASE="${POSTGRES_DATABASE:-platform}"
OUTPUT_DIR="${1:-./backups}"

if ! docker inspect -f '{{.State.Running}}' "$CONTAINER" >/dev/null 2>&1; then
  echo "Container '$CONTAINER' is not running - start it first (docker compose up -d postgres)." >&2
  exit 1
fi

mkdir -p "$OUTPUT_DIR"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
dump_path_in_container="/tmp/platform_${timestamp}.dump"
dump_path_on_host="${OUTPUT_DIR}/platform_${timestamp}.dump"

echo "Backing up '$DATABASE' from container '$CONTAINER'..."
docker exec -u postgres "$CONTAINER" pg_dump -Fc -d "$DATABASE" -f "$dump_path_in_container"
docker cp "$CONTAINER:$dump_path_in_container" "$dump_path_on_host"
docker exec -u postgres "$CONTAINER" rm -f "$dump_path_in_container"

size="$(du -h "$dump_path_on_host" | cut -f1)"
echo "Backup written to $dump_path_on_host ($size)."
