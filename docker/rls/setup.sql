-- Two tenants' employees in one table, a role the application connects as, and a policy.
CREATE TABLE employees (id serial PRIMARY KEY, tenant uuid NOT NULL, name text NOT NULL);
INSERT INTO employees (tenant, name) VALUES
  ('aaaaaaaa-0000-0000-0000-000000000000', 'a-1'), ('aaaaaaaa-0000-0000-0000-000000000000', 'a-2'), ('aaaaaaaa-0000-0000-0000-000000000000', 'a-3'),
  ('bbbbbbbb-0000-0000-0000-000000000000', 'b-1'), ('bbbbbbbb-0000-0000-0000-000000000000', 'b-2');

CREATE ROLE app LOGIN PASSWORD 'apppw' NOSUPERUSER NOBYPASSRLS;
GRANT SELECT, INSERT, UPDATE, DELETE ON employees TO app;
GRANT USAGE ON SEQUENCE employees_id_seq TO app;

ALTER TABLE employees ENABLE ROW LEVEL SECURITY;
ALTER TABLE employees FORCE ROW LEVEL SECURITY;
-- A missing or empty setting matches nothing: the policy fails closed.
CREATE POLICY tenant_isolation ON employees
  USING (tenant = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
