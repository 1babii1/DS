#!/usr/bin/env bash
# Loads a small, fictional, dated organization history into AuditService's log so the org-chart time machine
# (/history in the frontend, GET /api/audit/org-chart) has something to show on a stack whose real events all
# happened minutes ago. Development only; nothing calls it automatically.
#
# Usage: scripts/seed-org-history.sh          load the history (safe to repeat)
#        scripts/seed-org-history.sh --remove take exactly those rows out again
#
# It writes only to audit.entries, through the running docker-compose "postgres" container as the postgres OS user
# (the same trust-auth path scripts/backup-postgres.sh uses), so no credentials pass through this script's arguments.
# The events are deliberately NOT published to Kafka: on the real topics they would also reach every other consumer,
# creating login accounts, welcome bonuses and search documents for people who do not exist.
set -euo pipefail

CONTAINER="${POSTGRES_CONTAINER:-postgres}"
DATABASE="${POSTGRES_DATABASE:-platform}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! docker inspect -f '{{.State.Running}}' "$CONTAINER" >/dev/null 2>&1; then
  echo "Container '$CONTAINER' is not running - start it first (docker compose up -d postgres)." >&2
  exit 1
fi

mode=()
[[ "${1:-}" == "--remove" ]] && mode=(--remove)

python3 "$HERE/org-history/emit-sql.py" "${mode[@]}" \
  | docker exec -i "$CONTAINER" psql -U postgres -d "$DATABASE" -v ON_ERROR_STOP=1 -q

if [[ "${1:-}" == "--remove" ]]; then
  echo "Removed the seeded org history from audit.entries."
else
  echo "Loaded the seeded org history into audit.entries (28 events, January-August 2026)."
fi
