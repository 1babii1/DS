import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { Client } from "pg";

const databaseUrl = process.env.DATABASE_URL;

if (!databaseUrl) {
  throw new Error("DATABASE_URL must be supplied by the migration process.");
}

const scriptDirectory = new URL(".", import.meta.url);
const migrationPath = fileURLToPath(
  new URL("../database/migrations/001_web_auth.sql", scriptDirectory),
);
const migration = await readFile(migrationPath, "utf8");
const client = new Client({ connectionString: databaseUrl });

try {
  await client.connect();
  await client.query("BEGIN");
  await client.query(migration);
  await client.query("COMMIT");
} catch (error) {
  await client.query("ROLLBACK").catch(() => undefined);
  throw error;
} finally {
  await client.end();
}
