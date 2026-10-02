#!/bin/sh
# Registers one Debezium connector per producing service (ADR 0030). Idempotent: PUT /connectors/<name>/config creates or
# updates. Runs as a one-shot job once Kafka Connect answers.
set -eu
CONNECT="${CONNECT_URL:-http://kafka_connect:8083}"

until curl -sf "$CONNECT/connectors" >/dev/null; do
  echo "waiting for Kafka Connect"
  sleep 3
done

register() {
  service="$1"      # directory | auth | employee | rewards
  topic="$2"        # the Avro topic this service's events go to
  body=$(cat <<JSON
{
  "connector.class": "io.debezium.connector.postgresql.PostgresConnector",
  "tasks.max": "1",
  "database.hostname": "postgres",
  "database.port": "5432",
  "database.user": "postgres",
  "database.password": "${POSTGRES_PASSWORD}",
  "database.dbname": "platform",
  "topic.prefix": "cdc_${service}",
  "plugin.name": "pgoutput",
  "slot.name": "debezium_${service}",
  "publication.name": "dbz_${service}",
  "publication.autocreate.mode": "filtered",
  "table.include.list": "${service}.outbox_messages",
  "tombstones.on.delete": "false",
  "snapshot.mode": "no_data",

  "key.converter": "org.apache.kafka.connect.storage.StringConverter",
  "value.converter": "org.apache.kafka.connect.converters.ByteArrayConverter",

  "transforms": "outbox",
  "transforms.outbox.type": "io.debezium.transforms.outbox.EventRouter",
  "transforms.outbox.table.field.event.id": "Id",
  "transforms.outbox.table.field.event.key": "AggregateId",
  "transforms.outbox.table.field.event.payload": "AvroPayload",
  "transforms.outbox.table.fields.additional.placement": "Id:header:message-id,Type:header:message-type,OccurredAt:header:occurred-at",
  "transforms.outbox.route.by.field": "Type",
  "transforms.outbox.route.topic.regex": "(.*)",
  "transforms.outbox.route.topic.replacement": "${topic}",
  "transforms.outbox.table.expand.json.payload": "false"
}
JSON
)
  echo "registering outbox-${service} -> ${topic}"
  curl -sf -X PUT -H "Content-Type: application/json" -d "$body" "$CONNECT/connectors/outbox-${service}/config" >/dev/null \
    || { echo "registering outbox-${service} failed" >&2; exit 1; }
}

register directory directory.events.v2
register auth auth.events.v2
register employee employee.events.v2
register rewards rewards.events.v2
echo "connectors registered"
