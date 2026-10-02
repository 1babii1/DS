# 22. A short-lived ticket opens the notification hub; the OAuth token never reaches the browser

## Status
Accepted. Resolves issue #101. Related: [0006](0006-signalr-notification-center.md) (the hub and its groups).

## Context
NotificationService pushes to `/hub/notifications` over SignalR. A browser WebSocket cannot set an `Authorization`
header, so the hub accepted the OAuth access token in the `access_token` query parameter. The Next.js frontend keeps
OAuth tokens server-side (the BFF), so using that hub from the page would mean handing the page its access token, and a
token in a URL ends up in logs and traces. The live push therefore had no client.

## Decision
**A ticket, issued by NotificationService itself.** `POST /api/notifications/hub-ticket` (authenticated with the normal
bearer token, called by the BFF server to server) returns `{ ticket, expiresAt }` for the caller's own account. The
BFF returns only the ticket to the page, and SignalR's `accessTokenFactory` sends it as `access_token`.

- **What a ticket says:** subject (account), audience `notifications-hub`, purpose `hub-handshake`, issuer
  `notification-service`, issued-at and expiry, **at most 60 seconds**. The lifetime is checked on the ticket, so a
  correctly signed ticket that claims longer is refused. HMAC-SHA256 with a key only this service holds
  (`HubTickets:SigningKeyBase64`: required in Production, an ephemeral key elsewhere with a warning).
- **Where it works:** the hub is bound to its own authentication scheme, which reads the query parameter and accepts
  only tickets. The REST endpoints use the JWT bearer scheme and never read a query string. So a ticket opens the hub
  and nothing else, and an OAuth token in the query no longer opens the hub.
- **Why not issue it from AuthService:** a ticket for one service's hub is that service's concern. Issuing it there
  would need a shared key or a key-distribution path between two services and a new contract for a 60-second value;
  validating it where it is issued needs neither. No new service, no cross-schema read.
- **Why not a cookie handshake** (the issue's second option): it needs the hub to accept a session cookie from a
  different origin's browser, with CORS, SameSite and revocation rules to define, to avoid a 60-second token.

## Consequences
- **Logout does not reach into a connected hub or an issued ticket.** A ticket is stateless, so it cannot be revoked
  inside its minute, and a connection already open stays open until the client closes it (the frontend closes it on
  sign-out). The window is the ticket lifetime. Making tickets single-use would need state shared across instances, and
  SignalR's negotiate and WebSocket requests may present the same ticket.
- **Several instances need the same key.** Without it a ticket issued by one instance fails on another. The backplane
  already shares groups across instances; the key is the one new thing to configure.
- **The ticket is in a query string while it lives.** Telemetry redacts query values (a test in `Shared.UnitTests` pins it); the hosting request log, which would print it, is kept off by the platform's
  Serilog configuration (a test checks the setting in each environment); Serilog's own request log writes the path only.
  nginx's access log masks it.

## What is and is not verified
Tests: the ticket service (valid, one account and not another, expired at the second, wrong audience, purpose or issuer
even when signed by the key, a signed ticket claiming more than a minute, from the future, wrong key, changed payload,
malformed input) and the pipeline (a valid ticket passes the hub's negotiate, none or a bad one is 401, a JWT-shaped
value is 401, a ticket is 401 on the REST endpoints in the header and in the query, issuing needs a signed-in caller,
a ticket is issued for the caller's own account). The scheme binding, the signature check and the lifetime bound were
mutation-checked.

Not verified: a real SignalR client connecting end to end and receiving a push; a normal bearer token on REST in the
test host (the existing controller tests call the controller directly); multi-instance behaviour.
