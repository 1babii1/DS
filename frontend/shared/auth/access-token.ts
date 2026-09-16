import "server-only";

import { authDatabase, withTransaction } from "./database";
import { authConfiguration } from "./config";

type AccountRow = {
  access_token: string | null;
  refresh_token: string | null;
  expires_at: number | null;
};

const providerId = "openiddict";
const tokenRefreshSkewSeconds = 60;

export class AuthenticationRequiredError extends Error {
  constructor() {
    super("A valid sign-in is required.");
  }
}

export async function getAccessToken(userId: string): Promise<string> {
  return withTransaction(async (client) => {
    const result = await client.query<AccountRow>(
      `SELECT access_token, refresh_token, expires_at
       FROM accounts
       WHERE "userId" = $1 AND provider = $2
       FOR UPDATE`,
      [userId, providerId],
    );
    const account = result.rows[0];

    if (!account?.access_token) {
      throw new AuthenticationRequiredError();
    }

    const now = Math.floor(Date.now() / 1000);
    if (account.expires_at && account.expires_at > now + tokenRefreshSkewSeconds) {
      return account.access_token;
    }

    if (!account.refresh_token) {
      throw new AuthenticationRequiredError();
    }

    const response = await fetch(new URL("/connect/token", authConfiguration.issuer), {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        grant_type: "refresh_token",
        client_id: authConfiguration.clientId,
        client_secret: authConfiguration.clientSecret,
        refresh_token: account.refresh_token,
      }),
      cache: "no-store",
    });

    if (!response.ok) {
      throw new AuthenticationRequiredError();
    }

    const refreshed = (await response.json()) as {
      access_token?: string;
      expires_in?: number;
      refresh_token?: string;
    };
    if (!refreshed.access_token || !refreshed.expires_in) {
      throw new AuthenticationRequiredError();
    }

    await client.query(
      `UPDATE accounts
       SET access_token = $1,
           expires_at = $2,
           refresh_token = $3
       WHERE "userId" = $4 AND provider = $5`,
      [
        refreshed.access_token,
        now + refreshed.expires_in,
        refreshed.refresh_token ?? account.refresh_token,
        userId,
        providerId,
      ],
    );

    return refreshed.access_token;
  });
}

export async function revokeCurrentProviderAccount(userId: string): Promise<void> {
  await authDatabase.query(
    `UPDATE accounts
     SET access_token = NULL, refresh_token = NULL, expires_at = NULL
     WHERE "userId" = $1 AND provider = $2`,
    [userId, providerId],
  );
}
