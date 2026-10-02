#!/usr/bin/env bash
# How long an event takes from its outbox row being written to the audit log having stored it (ADR 0030): the whole path,
# publish and consume. Reads audit.entries (OccurredAt is the outbox row's time, ReceivedAt the consumer's), so it measures
# whichever delivery mode the stack is running; run it after some traffic (the k6 hire chain, a few hires from the UI).
#
#   scripts/delivery-latency.sh            # events written in the last 3 minutes
#   scripts/delivery-latency.sh 30         # written in the last 30 minutes
#
# Read-only. Only events from the services the audit log consumes (directory, employee).
set -euo pipefail
minutes="${1:-3}"

sql="SELECT count(*) AS events,
       round((percentile_cont(0.50) WITHIN GROUP (ORDER BY lag))::numeric * 1000) AS p50_ms,
       round((percentile_cont(0.95) WITHIN GROUP (ORDER BY lag))::numeric * 1000) AS p95_ms,
       round((max(lag))::numeric * 1000) AS max_ms
FROM (
  SELECT extract(epoch FROM (\"ReceivedAt\" - \"OccurredAt\")) AS lag
  FROM audit.entries
  WHERE \"OccurredAt\" > now() - interval '${minutes} minutes' AND \"SchemaId\" IS NOT NULL
) t"
echo "events | p50 ms | p95 ms | max ms"
docker exec postgres psql -U postgres -d platform -v ON_ERROR_STOP=1 -At -F ' | ' -c "$sql"
