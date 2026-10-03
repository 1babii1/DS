#!/usr/bin/env bash
# The row-level-security drill of ADR 0042: what the policy does, what bypasses it, and how a tenant setting leaks through a
# transaction-mode pooler. Everything runs in throwaway containers.
set -euo pipefail
cd "$(dirname "$0")/.."
compose="docker compose -f docker/rls/docker-compose.yml"
A=aaaaaaaa-0000-0000-0000-000000000000
B=bbbbbbbb-0000-0000-0000-000000000000
net=rls-drill_default
pg=rls-drill-postgres-1

$compose down -v >/dev/null 2>&1
trap '$compose down -v >/dev/null 2>&1' EXIT
$compose up -d postgres >/dev/null 2>&1
until [ "$(docker inspect -f '{{.State.Health.Status}}' $pg)" = healthy ]; do sleep 1; done
docker cp docker/rls/setup.sql $pg:/setup.sql
docker exec $pg psql -U postgres -d platform -q -v ON_ERROR_STOP=1 -f /setup.sql 2>&1 | cat
$compose up -d pgbouncer >/dev/null 2>&1
sleep 3

# host user password sql: one client connection, one command; the answer on one line without the command tags
q() {
  docker run --rm --network $net -e PGPASSWORD="$3" postgres:16 psql -h "$1" -U "$2" -d platform -tAc "$4" 2>&1 \
    | { grep -vE '^(SET|BEGIN|COMMIT|INSERT 0 1)$' || true; } | tr '\n' ' '
}

echo "--- straight at Postgres"
echo "superuser, no tenant given:                       $(q postgres postgres drillpw 'select count(*) from employees')rows of 5 (a superuser is not subject to the policy)"
echo "app role, no tenant given:                        $(q postgres app apppw 'select count(*) from employees')rows (fails closed)"
echo "app role, tenant A, SELECT with no WHERE:         $(q postgres app apppw "set app.tenant_id='$A'; select count(*) from employees")rows of 5"
echo "app role, tenant B, SELECT with no WHERE:         $(q postgres app apppw "set app.tenant_id='$B'; select count(*) from employees")rows of 5"
echo "app role, tenant A, inserting a row for B:        $(q postgres app apppw "set app.tenant_id='$A'; insert into employees (tenant, name) values ('$B','sneaky')")"

echo "--- through PgBouncer (transaction mode, ONE server connection shared by every client)"
echo "session-level setting:"
q pgbouncer app apppw "set app.tenant_id='$A'" >/dev/null
echo "  client 1 sets the tenant for its session (A)"
echo "  client 2 never gave a tenant, counts employees:   $(q pgbouncer app apppw 'select count(*) from employees')rows   <- tenant A's, set by client 1 earlier"
$compose restart pgbouncer >/dev/null 2>&1
sleep 3
echo "transaction-local setting (after restarting the pooler):"
echo "  client 3 sets it inside a transaction (B), counts: $(q pgbouncer app apppw "begin; set local app.tenant_id='$B'; select count(*) from employees; commit")rows"
echo "  client 4 never gave a tenant, counts employees:   $(q pgbouncer app apppw 'select count(*) from employees')rows"

$compose down -v >/dev/null 2>&1
