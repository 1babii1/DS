#!/usr/bin/env bash
# Department rights through OpenFGA on the running stack (ADR 0057). Starts OpenFGA (on its own Postgres, as in the "fga" compose profile) and an
# EmployeeService in tree mode on a private Postgres, beside the stack's own, on its network. Departments are made and moved in the stack's
# DirectoryService; its events reach the new EmployeeService, which keeps OpenFGA's tree in step. scripts/fga-drill.py then asks the real
# endpoints who may hire where, while the tree changes and while OpenFGA is stopped and started again.
#
# Leaves a few departments, positions, a location and two editor accounts behind in the development database, like the other drills.
# Needs the image ds-employee:fga (build backend/EmployeeService/Dockerfile from backend/), or EMP_IMAGE.
#
#   scripts/fga-drill.sh
set -euo pipefail
cd "$(dirname "$0")/.."
net=dsporfolio_default
emp_image="${EMP_IMAGE:-ds-employee:fga}"
fga_image=openfga/openfga:v1.22.0

cleanup() { docker rm -f emp_fga fga_drill fga_pg_drill pg_fga_drill >/dev/null 2>&1 || true; }
[ -n "${KEEP:-}" ] || trap cleanup EXIT
cleanup

docker run -d --name fga_pg_drill --network $net -e POSTGRES_USER=openfga -e POSTGRES_PASSWORD=fgapw -e POSTGRES_DB=openfga postgres:16 >/dev/null
docker run -d --name pg_fga_drill --network $net -e POSTGRES_PASSWORD=emppw -e POSTGRES_DB=platform pgvector/pgvector:pg18 >/dev/null
until docker exec fga_pg_drill pg_isready -U openfga -d openfga >/dev/null 2>&1; do sleep 1; done
until docker exec pg_fga_drill pg_isready -U postgres -d platform >/dev/null 2>&1; do sleep 1; done
sleep 2

uri="postgres://openfga:fgapw@fga_pg_drill:5432/openfga?sslmode=disable"
docker run --rm --network $net -e OPENFGA_DATASTORE_ENGINE=postgres -e OPENFGA_DATASTORE_URI="$uri" $fga_image migrate >/dev/null 2>&1
docker run -d --name fga_drill --network $net -p 18090:8080 -e OPENFGA_DATASTORE_ENGINE=postgres -e OPENFGA_DATASTORE_URI="$uri" $fga_image run >/dev/null
for _ in $(seq 1 60); do curl -s -o /dev/null http://localhost:18090/healthz && break; sleep 1; done

docker exec pg_fga_drill psql -U postgres -d platform -q -c "CREATE SCHEMA employee" >/dev/null
# The migration bundle has to be named as the entrypoint (ADR 0044).
docker run --rm --network $net --entrypoint ./efbundle "$emp_image" \
  --connection "Server=pg_fga_drill;Port=5432;Database=platform;User Id=postgres;Password=emppw;Search Path=employee,public" >/dev/null

docker run -d --name emp_fga --network $net -p 5361:5131 \
  -e ASPNETCORE_ENVIRONMENT=Docker -e Outbox__Mode=Polling -e SchemaRegistry__Url=http://schema_registry:8080/apis/ccompat/v7 \
  -e Kafka__SaslUsername=kafka -e Kafka__SaslPassword=changeme-kafka -e Kafka__Topics__0=auth.events.v2 -e Kafka__GroupId=emp-fga-drill \
  -e "ConnectionStrings__EmployeeServiceDb=Server=pg_fga_drill;Port=5432;Database=platform;User Id=postgres;Password=emppw;Search Path=employee,public" \
  -e RateLimiting__Write__PermitLimit=1000000 -e ASPNETCORE_URLS=http://*:5131 -e Directory__GrpcAddress=http://directory_service:5179 \
  -e DepartmentAuthorization__Mode=Tree -e DepartmentAuthorization__OpenFgaUrl=http://fga_drill:8080 \
  "$emp_image" >/dev/null
for _ in $(seq 1 60); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5361/health/live)" = 200 ] && break; sleep 2; done
sleep 10 # the consumer joins its group

python3 scripts/fga-drill.py
