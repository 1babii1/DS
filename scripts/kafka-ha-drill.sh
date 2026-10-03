#!/usr/bin/env bash
# The broker-failure drill of ADR 0035. Usage: scripts/kafka-ha-drill.sh <stage>
#   up               start a three-broker cluster and a client
#   kill-leader MODE produce numbered messages, kill the partition leader mid-stream, count what arrived.
#                    MODE is "safe" (acks=all, idempotent producer, min.insync=2) or "weak" (acks=1, no idempotence, min.insync=1)
#   shrink MODE      followers die, writes continue on the lone leader, then the leader dies and a follower takes over:
#                    what was acknowledged in between, and what survived
#   down             remove everything
# `docker exec` output is piped through cat: with the snap Docker CLI it fails silently when stdout is not a pipe.
set -euo pipefail
cd "$(dirname "$0")/.."
compose="docker compose -f docker/kafka-ha/docker-compose.yml"
kbin=/opt/kafka/bin
bs=kafka1:9092,kafka2:9092,kafka3:9092
dx() { local c="$1"; shift; docker exec "$c" "$@" 2>&1 | cat; }
N="${DRILL_MESSAGES:-4000}"
KILL_AFTER="${DRILL_KILL_AFTER:-4}"

leader_of() { # topic -> broker number
  dx kafka_client $kbin/kafka-topics.sh --bootstrap-server $bs --describe --topic "$1" | sed -n 's/.*Leader: \([0-9]*\).*/\1/p' | head -1
}

case "${1:?stage}" in
  up)
    $compose up -d
    until dx kafka_client $kbin/kafka-topics.sh --bootstrap-server $bs --list >/dev/null 2>&1; do sleep 2; done
    echo "cluster is up"
    ;;
  kill-leader)
    mode="${2:?safe|weak}"
    topic="drill-$mode-$(date +%s)"
    if [ "$mode" = safe ]; then
      cfg="min.insync.replicas=2"; props="acks=all enable.idempotence=true"
    else
      cfg="min.insync.replicas=1"; props="acks=1 enable.idempotence=false"
    fi
    dx kafka_client $kbin/kafka-topics.sh --bootstrap-server $bs --create --topic "$topic" --partitions 1 --replication-factor 3 --config "$cfg" | tail -1
    leader="$(leader_of "$topic")"
    echo "mode=$mode topic=$topic leader=kafka$leader messages=$N"
    pargs=""; for p in $props; do pargs="$pargs --producer-property $p"; done
    # numbers 1..N, one every few milliseconds, produced from the client that is never killed
    ( docker exec kafka_client sh -c "i=1; while [ \$i -le $N ]; do echo \"\$i \$(date +%s)\"; i=\$((i+1)); sleep 0.004; done | $kbin/kafka-console-producer.sh --bootstrap-server $bs --topic $topic --producer-property linger.ms=5 $pargs" > /tmp/kafka-drill-producer.log 2>&1 ) &
    producer=$!
    sleep "$KILL_AFTER"
    echo "killing the leader kafka$leader"
    kill_at="$(date +%s)"
    docker kill "kafka$leader" >/dev/null
    wait $producer || true
    for _ in $(seq 1 30); do [ "$(leader_of "$topic")" != "$leader" ] && break; sleep 1; done
    echo "new leader: kafka$(leader_of "$topic")"
    $compose start "kafka$leader" >/dev/null 2>&1 || docker start "kafka$leader" >/dev/null
    sleep 8
    # each message is "number seconds-when-produced": completeness is judged on the numbers, the stall on the times
    dx kafka_client sh -c "$kbin/kafka-console-consumer.sh --bootstrap-server $bs --topic $topic --from-beginning --timeout-ms 15000 2>/dev/null > /tmp/got.txt; cut -d' ' -f1 /tmp/got.txt | sort -n > /tmp/nums.txt; sort -n -u /tmp/nums.txt > /tmp/uniq.txt; seq 1 $N > /tmp/want.txt; echo received=\$(wc -l < /tmp/nums.txt) distinct=\$(wc -l < /tmp/uniq.txt) missing=\$(comm -13 /tmp/uniq.txt /tmp/want.txt | wc -l) duplicates=\$(( \$(wc -l < /tmp/nums.txt) - \$(wc -l < /tmp/uniq.txt) )); echo produced_before_kill=\$(awk -v k=$kill_at '\$2 < k' /tmp/got.txt | wc -l) produced_after_kill=\$(awk -v k=$kill_at '\$2 >= k' /tmp/got.txt | wc -l); echo longest_pause_between_messages_seconds=\$(sort -n /tmp/got.txt | awk 'NR>1 {d=\$2-p; if (d>m) m=d} {p=\$2} END {print m+0}'); echo first_missing: \$(comm -13 /tmp/uniq.txt /tmp/want.txt | head -3 | tr '\n' ' ')"
    ;;
  shrink)
    mode="${2:?safe|weak}"
    topic="drill-shrink-$mode-$(date +%s)"
    if [ "$mode" = safe ]; then cfg="min.insync.replicas=2"; acks=all; else cfg="min.insync.replicas=1"; acks=1; fi
    dx kafka_client $kbin/kafka-topics.sh --bootstrap-server $bs --create --topic "$topic" --partitions 1 --replication-factor 3 --config "$cfg" | tail -1
    leader="$(leader_of "$topic")"
    perf() { dx kafka_client $kbin/kafka-producer-perf-test.sh --topic "$topic" --num-records "$1" --record-size 100 --throughput 500 \
      --producer-props bootstrap.servers=kafka$leader:9092 acks=$acks delivery.timeout.ms=6000 request.timeout.ms=3000 linger.ms=5 enable.idempotence=false | grep -E "records sent" | tail -1; }
    echo "mode=$mode (acks=$acks, $cfg) leader=kafka$leader"
    echo "phase 1, all three brokers up:"; perf 1000
    followers=""; for b in 1 2 3; do [ "$b" != "$leader" ] && followers="$followers kafka$b"; done
    echo "killing the followers:$followers"; docker kill $followers >/dev/null
    sleep 2
    echo "phase 2, the leader alone:"; perf 1000 || true
    echo "killing the leader kafka$leader, starting the followers"
    docker kill "kafka$leader" >/dev/null; docker start $followers >/dev/null
    for _ in $(seq 1 40); do l="$(leader_of "$topic" || true)"; [ -n "$l" ] && [ "$l" != "$leader" ] && break; sleep 1; done
    echo "new leader: kafka$(leader_of "$topic")"
    docker start "kafka$leader" >/dev/null; sleep 8
    echo "records in the topic now:"; dx kafka_client $kbin/kafka-get-offsets.sh --bootstrap-server $bs --topic "$topic" --time -1
    ;;
  down)
    $compose down -v
    ;;
esac
