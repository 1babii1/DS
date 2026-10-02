#!/usr/bin/env bash
# Shows a producing service's outbox: what is pending, what is parked, what went out last, with the readable JSON of each
# event and whether its Avro bytes were staged at write time (ADR 0023). Read-only; goes through the postgres container
# like backup-postgres.sh, so it needs no credentials of its own.
#
#   scripts/outbox.sh employee            # pending, parked and the last 5 sent
#   scripts/outbox.sh rewards parked      # only parked ones, with the error
#   scripts/outbox.sh directory 20        # the last 20 rows
set -euo pipefail

service="${1:-}"
mode="${2:-summary}"
case "$service" in directory | auth | employee | rewards) ;; *)
  echo "usage: $0 <directory|auth|employee|rewards> [summary|parked|<count>]" >&2
  exit 2
  ;;
esac

psqlc() { docker exec -i postgres psql -U postgres -d platform -v ON_ERROR_STOP=1 "$@"; }
table="${service}.outbox_messages"

cols='"Id", "Type", "AggregateId", "OccurredAt", "ProcessedAt" IS NOT NULL AS sent, "AttemptCount" AS tries,
      ("AvroPayload" IS NOT NULL) AS avro_staged, left("Payload"::text, 90) AS json'

if [[ "$mode" == "parked" ]]; then
  psqlc -c "SELECT ${cols}, \"ParkedAt\", left(\"LastError\", 80) AS last_error FROM ${table} WHERE \"ParkedAt\" IS NOT NULL ORDER BY \"ParkedAt\""
elif [[ "$mode" =~ ^[0-9]+$ ]]; then
  psqlc -c "SELECT ${cols} FROM ${table} ORDER BY \"OccurredAt\" DESC LIMIT ${mode}"
else
  psqlc -c "SELECT count(*) FILTER (WHERE \"ProcessedAt\" IS NULL AND \"ParkedAt\" IS NULL) AS pending,
                   count(*) FILTER (WHERE \"ParkedAt\" IS NOT NULL) AS parked,
                   count(*) FILTER (WHERE \"ProcessedAt\" IS NOT NULL) AS sent,
                   count(*) FILTER (WHERE \"ProcessedAt\" IS NULL AND \"AvroPayload\" IS NULL) AS to_encode_from_json
            FROM ${table}"
  psqlc -c "SELECT ${cols} FROM ${table} WHERE \"ProcessedAt\" IS NULL OR \"ParkedAt\" IS NOT NULL ORDER BY \"OccurredAt\" LIMIT 20"
  psqlc -c "SELECT ${cols} FROM ${table} WHERE \"ProcessedAt\" IS NOT NULL ORDER BY \"OccurredAt\" DESC LIMIT 5"
fi
