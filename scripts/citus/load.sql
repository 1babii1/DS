-- 3,000,000 events over 20,000 aggregates and the twelve months of 2026; a long tail: aggregate 0 has far more events than 19999.
INSERT INTO entries
SELECT gen_random_uuid(), 'EmployeeService', (ARRAY['EmployeeHired','EmployeeTransferred','EmployeeTerminated','CurrencyGranted'])[1 + (g % 4)],
       'agg-' || (floor(20000 * power(random(), 2)))::int,
       jsonb_build_object('n', g, 'note', repeat('x', 120)),
       timestamptz '2026-01-01 00:00:00+00' + random() * interval '364 days',
       gen_random_uuid()
FROM generate_series(1, 3000000) g;
INSERT INTO recorded_messages SELECT "MessageId", "OccurredAt" FROM entries;
ANALYZE entries;
