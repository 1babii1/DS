SELECT "EventType", count(*) FROM entries WHERE "OccurredAt" >= timestamptz '2026-06-01+00' AND "OccurredAt" < timestamptz '2026-06-08+00' GROUP BY 1;
