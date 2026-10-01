#!/usr/bin/env bash
# One entry point for running the whole project locally, in the order a first-time reader needs it.
# Nothing here handles a secret: values come from your own .env / vault, and this script only checks that the
# pieces are up and tells you what to do next.
#
# Usage: scripts/demo.sh up       start the backend stack (docker compose) and wait until it is healthy
#        scripts/demo.sh history  load the dated demo organization into the audit log (for /history)
#        scripts/demo.sh llm      start the local language model for the assistant (needs a GPU device)
#        scripts/demo.sh status   what is running
#        scripts/demo.sh down     stop the stack (data volumes are kept)
#
# The frontend is not in compose: it keeps OAuth tokens server-side and needs the project vault, so it is started from
# frontend/ with the vault runner (see the message printed by "up").
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$HERE"

need() {
  command -v "$1" >/dev/null 2>&1 || { echo "Missing: $1" >&2; exit 1; }
}

wait_healthy() {
  local name="$1" tries="${2:-90}"
  for _ in $(seq 1 "$tries"); do
    local state
    state="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$name" 2>/dev/null || echo missing)"
    [[ "$state" == "healthy" || "$state" == "running" ]] && return 0
    sleep 2
  done
  echo "  $name did not become healthy (docker logs $name)" >&2
  return 1
}

up() {
  need docker
  if [[ ! -f .env ]]; then
    echo "No .env yet: copy .env.example to .env (or use your vault) and set the values it names, then run this again." >&2
    exit 1
  fi

  docker compose up -d

  echo "Waiting for the services ..."
  local failed=0
  for service in postgres kafka elasticsearch directory_service employee_service auth_service audit_service \
                 rewards_service notification_service search_service mcp_server nginx; do
    wait_healthy "$service" || failed=1
  done
  [[ "$failed" -eq 0 ]] || exit 1

  cat <<'EOF'

The backend is up. Next:
  1. Frontend:  cd frontend && ~/.local/bin/secrets-run ds-portfolio-dev -- npm run dev     (http://localhost:3000)
  2. Sign in with the admin named by SEED_ADMIN_EMAIL in .env.example (its password is the one in your vault/.env).
  3. History:   scripts/demo.sh history     (then open /history)
  4. Assistant: scripts/demo.sh llm         (then open /assistant; needs a GPU device and ~12 GB of RAM)
EOF
}

case "${1:-up}" in
  up) up ;;
  history) need docker; "$HERE/scripts/seed-org-history.sh" ;;
  llm) "$HERE/scripts/llm-server.sh" start ;;
  status) need docker; docker compose ps --format 'table {{.Name}}\t{{.Status}}' ;;
  down) need docker; docker compose down ;;
  *) echo "Usage: $0 [up|history|llm|status|down]" >&2; exit 2 ;;
esac
