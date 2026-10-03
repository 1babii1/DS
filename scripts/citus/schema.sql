-- The audit log's shape (ADR 0027) as the sharding experiment needs it. The key has to contain the distribution column on a
-- Citus table, so it is (AggregateId, Id, OccurredAt) here; RecordedMessages stays its own table keyed by the message id.
DROP TABLE IF EXISTS entries CASCADE;
DROP TABLE IF EXISTS recorded_messages CASCADE;
CREATE TABLE entries (
  "Id" uuid NOT NULL,
  "SourceService" varchar(50) NOT NULL,
  "EventType" varchar(100) NOT NULL,
  "AggregateId" varchar(200) NOT NULL,
  "Payload" jsonb NOT NULL,
  "OccurredAt" timestamptz NOT NULL,
  "MessageId" uuid NOT NULL,
  PRIMARY KEY ("AggregateId", "Id", "OccurredAt")
) PARTITION BY RANGE ("OccurredAt");
CREATE TABLE recorded_messages ("MessageId" uuid PRIMARY KEY, "RecordedAt" timestamptz NOT NULL);
DO $$
BEGIN
  FOR m IN 0..11 LOOP
    EXECUTE format('CREATE TABLE entries_p%s PARTITION OF entries FOR VALUES FROM (%L) TO (%L)',
      m, (timestamptz '2026-01-01 00:00:00+00' + make_interval(months => m)), (timestamptz '2026-01-01 00:00:00+00' + make_interval(months => m + 1)));
  END LOOP;
END $$;
CREATE INDEX ON entries ("OccurredAt");
CREATE INDEX ON entries ("MessageId");
