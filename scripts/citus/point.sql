\set a random(0, 19999)
SELECT "EventType", "OccurredAt" FROM entries WHERE "AggregateId" = 'agg-' || :a ORDER BY "OccurredAt" DESC LIMIT 50;
