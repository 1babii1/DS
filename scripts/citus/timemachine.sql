-- the org time machine's shape: everything up to a date, in order, for the whole organisation
SELECT count(*), max("OccurredAt") FROM (SELECT "OccurredAt" FROM entries WHERE "OccurredAt" <= timestamptz '2026-09-01+00' ORDER BY "OccurredAt") t;
