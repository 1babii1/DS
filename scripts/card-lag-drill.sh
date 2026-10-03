#!/usr/bin/env bash
# How far behind the employee card's balance runs through Kafka, and what a read that waits for its own write costs (ADR 0034).
# Starts a private Kafka (configured like the stack's single broker, without SASL), a schema registry, a Postgres, and the real RewardsService
# and EmployeeService from this code. Grants through Rewards' HTTP API, reads the card from Employee's, and reports the delay between a
# grant being committed and the card's copy being updated, and the time a read that asked for its own write waited. Reaches the stack's AuthService only
# for tokens. Needs the images ds-rewards:lag and ds-employee:mtls (each service's Dockerfile, built from backend/) and the dotnet SDK.
#
#   scripts/card-lag-drill.sh [employees]        # default 200
set -euo pipefail
cd "$(dirname "$0")/.."
employees="${1:-200}"
net=card-lag-net
stack=dsporfolio_default

cleanup() { docker rm -f pg_lag kafka_lag registry_lag rewards_lag employee_lag >/dev/null 2>&1 || true; docker network rm $net >/dev/null 2>&1 || true; }
[ -n "${KEEP:-}" ] || trap cleanup EXIT
cleanup
docker network create $net >/dev/null

docker run -d --name pg_lag --network $net -e POSTGRES_PASSWORD=lagpw -e POSTGRES_DB=platform pgvector/pgvector:pg18 >/dev/null
docker run -d --name kafka_lag --network $net \
  -e KAFKA_NODE_ID=1 -e KAFKA_PROCESS_ROLES=broker,controller \
  -e KAFKA_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093 -e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://kafka_lag:9092 \
  -e KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER -e KAFKA_CONTROLLER_QUORUM_VOTERS=1@kafka_lag:9093 \
  -e KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT -e KAFKA_INTER_BROKER_LISTENER_NAME=PLAINTEXT \
  -e KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 -e CLUSTER_ID=MTIzNDU2Nzg5MGFiY2RlZg== apache/kafka:3.9.0 >/dev/null
docker run -d --name registry_lag --network $net quay.io/apicurio/apicurio-registry:3.0.7 >/dev/null

until docker exec pg_lag pg_isready -U postgres -d platform >/dev/null 2>&1; do sleep 1; done
sleep 2
docker exec pg_lag psql -U postgres -d platform -q -c "CREATE SCHEMA employee; CREATE SCHEMA rewards" >/dev/null
for pair in "EmployeeService:EmployeeService.Infrastructure.Postgres:EmployeeService.Web:employee" "RewardsService:RewardsService.Infrastructure:RewardsService.Web:rewards"; do
  IFS=: read -r dir infra web name <<<"$pair"
  (cd "backend/$dir" && dotnet build "$web" >/dev/null && dotnet ef migrations script --idempotent -p "$infra" -s "$web" -o "$PWD/../../docker/lag-$name.sql" >/dev/null)
  docker cp "docker/lag-$name.sql" pg_lag:/schema.sql
  docker exec pg_lag psql -U postgres -d platform -q -v ON_ERROR_STOP=1 -f /schema.sql >/dev/null
  rm -f "docker/lag-$name.sql"
done

service_env=(-e ASPNETCORE_ENVIRONMENT=Docker -e Outbox__Mode=Polling -e SchemaRegistry__Url=http://registry_lag:8080/apis/ccompat/v7
  -e Kafka__BootstrapServers=kafka_lag:9092 -e Kafka__SaslUsername= -e Kafka__SaslPassword=)
docker run -d --name rewards_lag --network $net -p 5321:5134 -e ASPNETCORE_URLS=http://*:5134 "${service_env[@]}" \
  -e "ConnectionStrings__RewardsServiceDb=Server=pg_lag;Port=5432;Database=platform;User Id=postgres;Password=lagpw;Search Path=rewards,public" \
  -e RateLimiting__Write__PermitLimit=100000 -e Kafka__Topics__0=employee.events.v2 -e Kafka__GroupId=rewards-lag ds-rewards:lag >/dev/null
docker run -d --name employee_lag --network $net -p 5322:5131 -e ASPNETCORE_URLS=http://*:5131 "${service_env[@]}" \
  -e "ConnectionStrings__EmployeeServiceDb=Server=pg_lag;Port=5432;Database=platform;User Id=postgres;Password=lagpw;Search Path=employee,public" \
  -e Kafka__Topics__0=rewards.events.v2 -e Kafka__Topics__1=auth.events.v2 -e Kafka__GroupId=employee-lag \
  -e EmployeeCard__MaxWait=00:00:10 -e Directory__GrpcAddress=http://nowhere:5179 ds-employee:mtls >/dev/null
# Tokens are checked against the stack's AuthService.
docker network connect $stack rewards_lag
docker network connect $stack employee_lag

docker exec pg_lag psql -U postgres -d platform -q -c "
  INSERT INTO employee.employees (\"Id\", \"FullName\", \"Email\", \"DepartmentId\", \"DepartmentName\", \"PositionId\", \"PositionName\", \"Status\", \"HiredAt\", \"CreatedAt\", \"UpdatedAt\")
  SELECT gen_random_uuid(), 'Lag Test ' || g, 'lag' || g || '@portfolio.local', gen_random_uuid(), 'Dept', gen_random_uuid(), 'Role', 'Active', now(), now(), now()
  FROM generate_series(1, $employees) g" >/dev/null
docker exec pg_lag psql -U postgres -d platform -tAc "SELECT string_agg('\"' || \"Id\" || '\"', ',') FROM employee.employees" | cat > load-tests/k6/.card-lag-ids.txt

for _ in $(seq 1 90); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5321/health/live)" = 200 ] \
    && [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5322/health/live)" = 200 ] && break
  sleep 2
done
sleep 15  # topics created, consumers assigned

docker run --rm --network host -v "$PWD/load-tests/k6":/scripts -w /scripts \
  -e REWARDS_BASE_URL=http://localhost:5321 -e EMPLOYEE_BASE_URL=http://localhost:5322 grafana/k6 run --quiet card-lag.js 2>&1 | grep -E "^\s+(grants_ok|waited_ms|consistent_cards|stale_cards|grant_failed)|THRESHOLD" | cat
rm -f load-tests/k6/.card-lag-ids.txt

echo "--- the copy's own measure: when the balance changed at the source, against when this copy took it"
docker exec pg_lag psql -U postgres -d platform -tAc "
  SELECT 'rows=' || count(*) || ' p50=' || round((percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM (\"ProjectedAt\" - \"BalanceChangedAt\")) * 1000))::numeric) || ' ms p95='
         || round((percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM (\"ProjectedAt\" - \"BalanceChangedAt\")) * 1000))::numeric) || ' ms max='
         || round((max(extract(epoch FROM (\"ProjectedAt\" - \"BalanceChangedAt\")) * 1000))::numeric) || ' ms'
  FROM employee.employee_wallets"
