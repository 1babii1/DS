# 21. Sign-out clears the BFF's tokens and revokes the refresh token at the issuer

## Status
Accepted. Resolves issue #103 in its cheaper form. Touches token handling only by enabling one more endpoint for one
client. (The choice of OpenIddict as the issuer is not yet written up as an ADR; see the index.)

## Context
The Next.js BFF keeps each person's OAuth tokens server-side (`web_auth.accounts`). Sign-out cleared them there, which
stops the BFF using them, but the refresh token (30 days) stayed valid at OpenIddict: anyone who had copied it out of
that table could keep minting access tokens for a month after the person had signed out.

## Decision
**Clear first, then revoke.** Sign-out nulls the stored tokens in one statement that also returns the refresh token it
cleared, then the BFF calls the issuer's `connect/revocation` (RFC 7009) as the confidential `portfolio-web` client with
its own credentials, from the server. The clearing is the guarantee; revocation is best effort with a 3 second timeout,
and a failure is reported to nobody and does not fail the sign-out. With nothing stored there is nothing to revoke, so
signing out twice is harmless.

**The endpoint exists for one client.** `connect/revocation` is enabled on the server, but only `portfolio-web` holds the
permission; the browser-facing public client does not, and a request with a wrong secret revokes nothing.

**The access token is not revoked.** Resource services validate it locally against the issuer's published keys, so
nothing a revocation could change reaches them. It stays valid until it expires, **15 minutes**. Shortening that, or
introspection at every call, are the alternatives, and both cost a round trip per request for a window this small.

## Consequences
- A refresh token copied from the BFF's database is dead from the moment of sign-out, if the issuer was reachable then.
  If it was not, the token lives until it expires or is revoked another way; nothing retries.
- For up to 15 minutes after sign-out a copied **access** token still works at every service.
- This does not end the person's session at AuthService itself (its own cookie); that is a separate matter from the
  tokens the BFF holds.

## What is and is not verified
Tests: a revoked refresh token cannot be exchanged (`invalid_grant`); revoking twice is accepted; a wrong client secret is
refused (401) and the token keeps working; the public client is refused; the BFF helper sends the token, the hint and the
client credentials to the issuer's revocation endpoint and reports, rather than throws, on refusal or an unreachable
issuer (mutation-checked). The sign-out SQL was run, rolled back, against the stack's database.

Not verified: the whole sign-out through the running frontend and a real issuer; the 3 second timeout path.
