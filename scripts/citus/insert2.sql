-- the real shape: the entry and its recorded message id in one transaction (two tables, usually two different shards)
\set a random(0, 19999)
BEGIN;
INSERT INTO recorded_messages VALUES (gen_random_uuid(), now());
INSERT INTO entries VALUES (gen_random_uuid(), 'EmployeeService', 'EmployeeHired', 'agg-' || :a, '{"n":1}', timestamptz '2026-06-15+00' + random() * interval '10 days', gen_random_uuid());
END;
