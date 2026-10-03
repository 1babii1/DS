#!/usr/bin/env bash
# The edge rate-limit drill of ADR 0039: the real nginx config in front of stub services, hammered by a client.
set -euo pipefail
cd "$(dirname "$0")/.."
compose="docker compose -f docker/nginx/drill/docker-compose.yml"
$compose up -d >/dev/null 2>&1
sleep 2
count() { # method path n -> "status count" lines
  docker run --rm --network edge-drill_default curlimages/curl:latest sh -c \
    "for i in \$(seq 1 $3); do curl -s -o /dev/null -w '%{http_code}\n' -X $1 http://nginx$2; done" 2>&1 | sort | uniq -c | tr '\n' ' '
  echo
}
echo "100 GET /api/employees:        $(count GET /api/employees 100)"
echo "100 POST /api/employees:       $(count POST /api/employees 100)"
echo "100 POST /api/rewards (its own budget):  $(count POST /api/rewards 100)"
echo "20 POST /auth/login:           $(count POST /auth/login 20)"
echo "20 GET /auth/login (not a POST): $(count GET /auth/login 20)"
$compose down -v >/dev/null 2>&1
