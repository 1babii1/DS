#!/usr/bin/env bash
# Mutual TLS between EmployeeService and DirectoryService on the running stack (ADR 0044). Starts a second Directory and a second
# Employee beside the stack's own, on its network and against its database, with the gRPC call between them needing a client
# certificate, and shows what the call does with and without one. Leaves a few hires/departments behind in the development database,
# like the k6 hire chain does. Needs the images ds-directory:mtls and ds-employee:mtls (build them from backend/, each service's Dockerfile).
#
#   scripts/mtls-live-drill.sh
set -euo pipefail
cd "$(dirname "$0")/.."
net=dsporfolio_default
certs="$PWD/docker/certs"
rogue="$PWD/docker/certs-rogue"
curl_image=curlimages/curl:latest
dir_image="${DIR_IMAGE:-ds-directory:mtls}"
emp_image="${EMP_IMAGE:-ds-employee:mtls}"

cleanup() { docker rm -f dir_mtls emp_mtls emp_plain pg_drill >/dev/null 2>&1 || true; }
[ -n "${KEEP:-}" ] || trap cleanup EXIT
cleanup

rm -rf "$certs" "$rogue"
DIRECTORY_HOST=dir_mtls bash scripts/mtls-certs.sh "$certs" >/dev/null
bash scripts/mtls-certs.sh "$rogue" >/dev/null

common_env=(
  -e ASPNETCORE_ENVIRONMENT=Docker -e Outbox__Mode=Polling
  -e SchemaRegistry__Url=http://schema_registry:8080/apis/ccompat/v7
  -e Kafka__SaslUsername=kafka -e Kafka__SaslPassword=changeme-kafka
)

# EmployeeService gets a database of its own for the drill (the stack's employee schema may be older than this build), and no Kafka, so the
# hires it makes stay out of the real stack. DirectoryService is the stack's database as it is: three rows of reference data are added.
docker run -d --name pg_drill --network $net -e POSTGRES_PASSWORD=drillpw -e POSTGRES_DB=platform pgvector/pgvector:pg18 >/dev/null
until docker exec pg_drill pg_isready -U postgres -d platform >/dev/null 2>&1; do sleep 1; done
sleep 2
# The schema from the migrations as SQL (docker run ./efbundle starts the service's own background services in this image and can fail
# before it migrates anything on a database with no registry configured; see ADR 0044). Needs the dotnet SDK on the host.
(cd backend/EmployeeService && dotnet ef migrations script --idempotent -p EmployeeService.Infrastructure.Postgres -s EmployeeService.Web \
  -o "$PWD/../../docker/employee-schema.sql" >/dev/null 2>&1)
docker exec pg_drill psql -U postgres -d platform -q -c "CREATE SCHEMA employee" >/dev/null
docker cp docker/employee-schema.sql pg_drill:/schema.sql
docker exec pg_drill psql -U postgres -d platform -q -v ON_ERROR_STOP=1 -f /schema.sql >/dev/null

docker run -d --name dir_mtls --network $net -p 5301:5129 -v "$certs":/certs:ro "${common_env[@]}" \
  -e "ConnectionStrings__DirectoryServiceDb=Server=pgbouncer;Port=5432;Database=platform;User Id=postgres;Password=postgres;Search Path=directory,public" \
  -e "ConnectionStrings__Redis=redis:6379,password=changeme-redis" -e Kafka__Topics__0=employee.events.v2 -e Kafka__GroupId=directory-mtls-drill \
  -e MutualTls__Enabled=true -e MutualTls__CertificatePath=/certs/directory_service.pem -e MutualTls__KeyPath=/certs/directory_service.key \
  -e MutualTls__CaPath=/certs/ca.pem "$dir_image" >/dev/null

employee() { # name port address [mtls]
  local extra=()
  if [ "${4:-}" = mtls ]; then
    extra=(-e MutualTls__Enabled=true -e MutualTls__CertificatePath=/certs/employee_service.pem -e MutualTls__KeyPath=/certs/employee_service.key -e MutualTls__CaPath=/certs/ca.pem)
  fi
  docker run -d --name "$1" --network $net -p "$2":5131 -v "$certs":/certs:ro "${common_env[@]}" \
    -e "ConnectionStrings__EmployeeServiceDb=Server=pg_drill;Port=5432;Database=platform;User Id=postgres;Password=drillpw;Search Path=employee,public" \
    -e ASPNETCORE_URLS=http://*:5131 -e Kafka__BootstrapServers=no-such-broker:9092 -e Kafka__Topics__0=auth.events.v2 -e Kafka__GroupId="$1-drill" \
    -e "Directory__GrpcAddress=$3" "${extra[@]}" "$emp_image" >/dev/null
}
employee emp_mtls 5311 https://dir_mtls:5179 mtls
employee emp_plain 5312 http://dir_mtls:5179

for _ in $(seq 1 60); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5301/health/live)" = 200 ] \
    && [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5311/health/live)" = 200 ] \
    && [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5312/health/live)" = 200 ] && break
  sleep 2
done

# Straight at DirectoryService's gRPC port, as a client would: what the handshake does with each certificate.
call() { # label, then curl arguments
  local label="$1"
  shift
  local out
  out="$(docker run --rm --user 0 --network $net -v "$certs":/certs:ro -v "$rogue":/rogue:ro $curl_image -s -o /dev/null --http2 -w '%{http_code}' --max-time 8 "$@" https://dir_mtls:5179/ 2>&1 || true)"
  echo "$label: ${out:-no answer (handshake refused)}"
}
echo "--- the gRPC port of DirectoryService, handshake only"
call "no client certificate           " --cacert /certs/ca.pem
call "certificate of another authority" --cacert /certs/ca.pem --cert /rogue/employee_service.pem --key /rogue/employee_service.key
call "certificate of the platform     " --cacert /certs/ca.pem --cert /certs/employee_service.pem --key /certs/employee_service.key

echo "--- a hire through EmployeeService that must ask DirectoryService over gRPC"
hire() { # label port
  echo "== $1"
  docker run --rm --network host -v "$PWD/load-tests/k6":/scripts -w /scripts \
    -e DIRECTORY_BASE_URL=http://localhost:5301 -e EMPLOYEE_BASE_URL=http://localhost:"$2" grafana/k6 run --quiet mtls-hire.js 2>&1 | grep -E "HIRE status" | cut -c1-260
}
hire "with mutual TLS on both sides" 5311
hire "Employee speaking plain http to a Directory that wants certificates" 5312
