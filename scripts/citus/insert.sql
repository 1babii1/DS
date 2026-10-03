\set a random(0, 19999)
BEGIN;
INSERT INTO entries VALUES (gen_random_uuid(), 'EmployeeService', 'EmployeeHired', 'agg-' || :a, '{"n":1}', timestamptz '2026-06-15+00' + random() * interval '10 days', gen_random_uuid());
END;
