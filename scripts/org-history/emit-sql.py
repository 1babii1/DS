#!/usr/bin/env python3
"""Turns scripts/org-history/events.jsonl into SQL for audit.entries.

  emit-sql.py          INSERT the dated history (idempotent: a message id already present is left alone)
  emit-sql.py --remove DELETE exactly those rows again

The rows are written straight into AuditService's own table, not published to Kafka: the same events on the real
topics would also reach every other consumer (a hire would create a login account, a welcome bonus, a search
document) for people who do not exist. Nothing here touches any other service's data.
"""
import json
import sys
from pathlib import Path

SOURCE_BY_TOPIC = {"directory.events": "directory", "employee.events": "employee"}


def literal(text: str) -> str:
    return "'" + text.replace("'", "''") + "'"


def load():
    path = Path(__file__).with_name("events.jsonl")
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def insert_sql(events):
    rows = []
    for e in events:
        rows.append(
            "  ("
            + ", ".join(
                [
                    f"gen_random_uuid()",
                    literal(e["id"]) + "::uuid",
                    literal(SOURCE_BY_TOPIC[e["topic"]]),
                    literal(e["type"]),
                    literal(e["key"]),
                    literal(json.dumps(e["payload"], ensure_ascii=False, separators=(",", ":"))) + "::jsonb",
                    literal(e["at"]) + "::timestamptz",
                    "now()",
                ]
            )
            + ")"
        )
    values = ",\n".join(rows)
    ids = ", ".join("(" + literal(e["id"]) + "::uuid)" for e in events)
    # entries is partitioned by event time and cannot carry a unique index on the message id, so "recorded once" lives in
    # audit.recorded_messages (ADR 0027): entries are written only for ids not recorded yet, then the ids are recorded.
    # Loading twice therefore adds nothing the second time.
    return (
        "BEGIN;\n"
        'INSERT INTO audit.entries ("Id", "MessageId", "SourceService", "EventType", "AggregateId", "Payload", "OccurredAt", "ReceivedAt")\n'
        'SELECT * FROM (VALUES\n' + values + '\n) AS v("Id", "MessageId", "SourceService", "EventType", "AggregateId", "Payload", "OccurredAt", "ReceivedAt")\n'
        'WHERE NOT EXISTS (SELECT 1 FROM audit.recorded_messages r WHERE r."MessageId" = v."MessageId");\n'
        'INSERT INTO audit.recorded_messages ("MessageId", "RecordedAt") SELECT id, now() FROM (VALUES ' + ids + ') AS t(id)\n'
        'ON CONFLICT ("MessageId") DO NOTHING;\n'
        "COMMIT;\n"
    )


def remove_sql(events):
    ids = ", ".join(literal(e["id"]) + "::uuid" for e in events)
    return (
        "BEGIN;\n"
        f'DELETE FROM audit.entries WHERE "MessageId" IN ({ids});\n'
        f'DELETE FROM audit.recorded_messages WHERE "MessageId" IN ({ids});\n'
        "COMMIT;\n"
    )


if __name__ == "__main__":
    loaded = load()
    sys.stdout.write(remove_sql(loaded) if "--remove" in sys.argv[1:] else insert_sql(loaded))
