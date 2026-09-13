import "server-only";

import { Pool, type PoolClient } from "pg";
import { authConfiguration } from "./config";

export const authDatabase = new Pool({
  connectionString: authConfiguration.databaseUrl,
  options: "-c search_path=web_auth",
});

export async function withTransaction<T>(
  operation: (client: PoolClient) => Promise<T>,
): Promise<T> {
  const client = await authDatabase.connect();

  try {
    await client.query("BEGIN");
    const result = await operation(client);
    await client.query("COMMIT");
    return result;
  } catch (error) {
    await client.query("ROLLBACK").catch(() => undefined);
    throw error;
  } finally {
    client.release();
  }
}
