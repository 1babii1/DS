#!/usr/bin/env bash
# Several instances of the same service at once, with some of them killed (ADR 0054). Three RewardsService and three EmployeeService instances share
# one Postgres schema each and one Kafka broker (private to the drill); grants are spread over the Rewards instances, one Rewards and one Employee
# instance are killed with SIGKILL mid-stream and started again, and afterwards the books are checked: nothing confirmed is missing, no key was
# counted twice, the card's copy of every wallet agrees with the wallet, and how many times each event went on the bus.
#
# Tokens come from the stack's AuthService. Needs the images dsporfolio-rewards_service and dsporfolio-employee_service (or REW_IMAGE, EMP_IMAGE).
#
#   PARTITIONS=1 scripts/multi-instance-drill.sh      # topics with this many partitions (created up front)
#   DURATION=60 EMPLOYEES=300 PACE=0.4 KILL=1 ...      # KILL=0 runs without killing anything
set -euo pipefail
cd "$(dirname "$0")/.."
partitions="${PARTITIONS:-1}"
employees="${EMPLOYEES:-300}"
duration="${DURATION:-60}"
kill_them="${KILL:-1}"
net=mi-net
stack=dsporfolio_default
rew_image="${REW_IMAGE:-dsporfolio-rewards_service:latest}"
emp_image="${EMP_IMAGE:-dsporfolio-employee_service:latest}"
names=(rewards_a rewards_b rewards_c employee_a employee_b employee_c)

cleanup() { docker rm -f pg_mi kafka_mi registry_mi "${names[@]}" >/dev/null 2>&1 || true; docker network rm $net >/dev/null 2>&1 || true; rm -f load-tests/k6/.multi-ids.txt load-tests/k6/.multi-k6.log; }
[ -n "${KEEP:-}" ] || trap cleanup EXIT
cleanup
docker network create $net >/dev/null

docker run -d --name pg_mi --network $net -e POSTGRES_PASSWORD=mipw -e POSTGRES_DB=platform pgvector/pgvector:pg18 >/dev/null
docker run -d --name kafka_mi --network $net \
  -e KAFKA_NODE_ID=1 -e KAFKA_PROCESS_ROLES=broker,controller \
  -e KAFKA_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093 -e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://kafka_mi:9092 \
  -e KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER -e KAFKA_CONTROLLER_QUORUM_VOTERS=1@kafka_mi:9093 \
  -e KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT -e KAFKA_INTER_BROKER_LISTENER_NAME=PLAINTEXT \
  -e KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 -e CLUSTER_ID=MTIzNDU2Nzg5MGFiY2RlZg== apache/kafka:3.9.0 >/dev/null
docker run -d --name registry_mi --network $net quay.io/apicurio/apicurio-registry:3.0.7 >/dev/null

until docker exec pg_mi pg_isready -U postgres -d platform >/dev/null 2>&1; do sleep 1; done
sleep 2
docker exec pg_mi psql -U postgres -d platform -q -c "CREATE SCHEMA employee; CREATE SCHEMA rewards" >/dev/null
# The migration bundle has to be named as the entrypoint (ADR 0044).
for pair in "$emp_image|employee" "$rew_image|rewards"; do
  image="${pair%|*}"
  schema="${pair#*|}"
  docker run --rm --network $net --entrypoint ./efbundle "$image" \
    --connection "Server=pg_mi;Port=5432;Database=platform;User Id=postgres;Password=mipw;Search Path=$schema,public" >/dev/null
done

until docker exec kafka_mi /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --list >/dev/null 2>&1; do sleep 2; done
for topic in employee.events.v2 rewards.events.v2 auth.events.v2; do
  docker exec kafka_mi /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --create --topic "$topic" --partitions "$partitions" --replication-factor 1 >/dev/null
done

common=(-e ASPNETCORE_ENVIRONMENT=Docker -e Outbox__Mode=Polling -e SchemaRegistry__Url=http://registry_mi:8080/apis/ccompat/v7
  -e Kafka__BootstrapServers=kafka_mi:9092 -e Kafka__SaslUsername= -e Kafka__SaslPassword=)
start_rewards() { # name port
  docker run -d --name "$1" --network $net -p "$2":5134 -e ASPNETCORE_URLS=http://*:5134 "${common[@]}" \
    -e "ConnectionStrings__RewardsServiceDb=Server=pg_mi;Port=5432;Database=platform;User Id=postgres;Password=mipw;Search Path=rewards,public" \
    -e RateLimiting__Write__PermitLimit=1000000 -e Kafka__Topics__0=employee.events.v2 -e Kafka__GroupId=rewards-mi "$rew_image" >/dev/null
  docker network connect $stack "$1"
}
start_employee() { # name port
  docker run -d --name "$1" --network $net -p "$2":5131 -e ASPNETCORE_URLS=http://*:5131 "${common[@]}" \
    -e "ConnectionStrings__EmployeeServiceDb=Server=pg_mi;Port=5432;Database=platform;User Id=postgres;Password=mipw;Search Path=employee,public" \
    -e Kafka__Topics__0=rewards.events.v2 -e Kafka__Topics__1=auth.events.v2 -e Kafka__GroupId=employee-mi \
    -e Directory__GrpcAddress=http://nowhere:5179 "$emp_image" >/dev/null
  docker network connect $stack "$1"
}
start_rewards rewards_a 5341; start_rewards rewards_b 5342; start_rewards rewards_c 5343
start_employee employee_a 5351; start_employee employee_b 5352; start_employee employee_c 5353

for port in 5341 5342 5343 5351 5352 5353; do
  for _ in $(seq 1 90); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:$port/health/live)" = 200 ] && break; sleep 2; done
done

docker exec pg_mi psql -U postgres -d platform -q -c "
  INSERT INTO employee.employees (\"Id\", \"FullName\", \"Email\", \"DepartmentId\", \"DepartmentName\", \"PositionId\", \"PositionName\", \"Status\", \"HiredAt\", \"CreatedAt\", \"UpdatedAt\")
  SELECT gen_random_uuid(), 'Multi ' || g, 'multi' || g || '@portfolio.local', gen_random_uuid(), 'Dept', gen_random_uuid(), 'Role', 'Active', now(), now(), now()
  FROM generate_series(1, $employees) g" >/dev/null
docker exec pg_mi psql -U postgres -d platform -tAc "SELECT string_agg('\"' || \"Id\" || '\"', ',') FROM employee.employees" | cat >load-tests/k6/.multi-ids.txt
sleep 15 # topics known, consumers assigned

echo "== instances up; $employees employees; topics with $partitions partition(s); load for ${duration}s"
k6out=load-tests/k6/.multi-k6.log

# The killing runs in the background while k6 runs in front: k6 started in the background cannot write to the standard streams.
(
  if [ "$kill_them" = 1 ]; then
    sleep $((duration / 3 + 8))
    echo "== kill -9 rewards_b and employee_c" >&2
    docker kill -s KILL rewards_b employee_c >/dev/null
    sleep $((duration / 4))
    echo "== starting them again" >&2
    docker start rewards_b employee_c >/dev/null
  fi
) &
killer=$!
docker run --rm --network host -v "$PWD/load-tests/k6":/scripts -w /scripts -e DURATION="${duration}s" -e VUS=4 -e PACE="${PACE:-0.4}" \
  grafana/k6 run --quiet multi-grants.js 2>&1 | cat >"$k6out" || true
wait $killer || true

q() { docker exec pg_mi psql -U postgres -d platform -tAc "$1"; }
echo "== draining (outbox of Rewards empty, the card's copy caught up)"
backlog_at_end=$(q "SELECT count(*) FROM rewards.outbox_messages WHERE \"ProcessedAt\" IS NULL")
drain_started=$SECONDS
for _ in $(seq 1 600); do
  pending=$(q "SELECT count(*) FROM rewards.outbox_messages WHERE \"ProcessedAt\" IS NULL")
  behind=$(q "SELECT count(*) FROM rewards.wallets w LEFT JOIN employee.employee_wallets e ON e.\"EmployeeId\" = w.\"EmployeeId\" WHERE e.\"Balance\" IS DISTINCT FROM w.\"Balance\"")
  [ "$pending" = 0 ] && [ "$behind" = 0 ] && break
  sleep 2
done

drained_in=$((SECONDS - drain_started))
confirmed=$(grep -c "GRANT mi-" "$k6out" || true)
unknown=$(grep -c "UNKNOWN mi-" "$k6out" || true)
retried=0 # (the retried-elsewhere counter is in the k6 summary, which the background run does not print)
rows=$(q "SELECT count(*) FROM rewards.transactions WHERE \"Reason\" = 'multi-instance drill'")
keys=$(q "SELECT count(*) FROM rewards.idempotency_records")
total=$(q "SELECT coalesce(sum(\"Amount\"),0) FROM rewards.transactions WHERE \"Reason\" = 'multi-instance drill'")
walletsum=$(q "SELECT coalesce(sum(\"Balance\"),0) FROM rewards.wallets")
cardsum=$(q "SELECT coalesce(sum(\"Balance\"),0) FROM employee.employee_wallets")
badwallets=$(q "SELECT count(*) FROM rewards.wallets w JOIN (SELECT \"EmployeeId\", sum(\"Amount\") s FROM rewards.transactions GROUP BY 1) t USING (\"EmployeeId\") WHERE w.\"Balance\" <> t.s")
badcards=$(q "SELECT count(*) FROM rewards.wallets w LEFT JOIN employee.employee_wallets e ON e.\"EmployeeId\" = w.\"EmployeeId\" WHERE e.\"Balance\" IS DISTINCT FROM w.\"Balance\"")
badversions=$(q "SELECT count(*) FROM (SELECT \"StreamId\", max(\"Version\") v FROM rewards.wallet_events GROUP BY 1) r LEFT JOIN employee.employee_wallets e ON e.\"EmployeeId\" = r.\"StreamId\" WHERE e.\"WalletVersion\" IS DISTINCT FROM r.v")
outbox=$(q "SELECT count(*) FROM rewards.outbox_messages")
unprocessed=$(q "SELECT count(*) FROM rewards.outbox_messages WHERE \"ProcessedAt\" IS NULL")
parked=$(q "SELECT count(*) FROM rewards.outbox_messages WHERE \"ParkedAt\" IS NOT NULL")
onbus=$(docker exec kafka_mi /opt/kafka/bin/kafka-get-offsets.sh --bootstrap-server localhost:9092 --topic rewards.events.v2 | awk -F: '{s+=$3} END{print s+0}')

echo
echo "| check | result |"
echo "|---|---|"
echo "| grants the service confirmed (200) | $confirmed |"
echo "| grants whose fate is unknown (every try failed) | $unknown |"
echo "| ledger rows | $rows (expected between $confirmed and $((confirmed + unknown))) |"
echo "| idempotency keys recorded | $keys (must equal the ledger rows: no key counted twice) |"
echo "| sum of the ledger | $total (rows x 10 = $((rows * 10))) |"
echo "| sum of wallet balances (Rewards) / of the card's copies (Employee) | $walletsum / $cardsum |"
echo "| wallets whose balance differs from their ledger | $badwallets |"
echo "| wallets whose card copy differs in balance / in version | $badcards / $badversions |"
echo "| outbox rows / unprocessed / parked | $outbox / $unprocessed / $parked |"
echo "| rows still unpublished when the load ended / seconds to publish them | $backlog_at_end / $drained_in |"
echo "| messages on rewards.events.v2 for $outbox outbox rows | $onbus (x$(python3 -c "print(round($onbus/max($outbox,1),2))") per row) |"
echo
echo "== who consumes rewards.events.v2 (group employee-mi)"
docker exec kafka_mi /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group employee-mi 2>/dev/null \
  | awk 'NR>1 && $1 != "" && $2 == "rewards.events.v2" {print "partition " $3 "  member " substr($7,1,40) "  lag " $6}' | sort | head -12
