# 6. SignalR for the in-app notification center

## Status
Accepted

## Context
The reference project this platform draws comparisons from (NezhnoWebSite) has a
`NotificationLog` that is really an audit trail of attempted sends - no read/unread
state, no in-app feed, no real-time delivery. This platform's own `NotificationService`
started even thinner: a `BackgroundService` that logged every Kafka message it saw and
persisted nothing. Neither is an in-app notification center a user would recognize as one.

Building a real one meant deciding, up front: how does a browser find out a notification
exists *right now* (poll, or push), and which of the platform's existing domain events
actually have a well-defined, single recipient worth notifying at all.

## Decision

**SignalR over polling, from v1.** The event backbone (Kafka + outbox) already tells every
consumer *that* something happened; the missing piece was only the last mile into an open
browser tab. A polling REST client would work, but trades real-time delivery for either
wasted requests (poll too often) or a delay a push doesn't have (poll too rarely) - for a
feature centered on "the user should be told this happened," that trade isn't worth taking
when ASP.NET Core SignalR is a solved, already-used-elsewhere-in-.NET primitive that sits
directly on top of the JWT auth already configured for every other service here.

**JWT-over-querystring for the hub connection.** A browser's WebSocket API cannot set a
custom `Authorization` header on the upgrade handshake itself, so the SignalR JS client
instead appends the access token as an `access_token` query parameter, and
`JwtBearerEvents.OnMessageReceived` reads it into `context.Token` - scoped to requests
under `/hub` only, so REST calls still authenticate the normal way. This is a documented,
standard ASP.NET Core SignalR pattern, not a workaround or a security hole: it is the same
bearer token, over the same TLS-terminated connection, with the same short (<15 minute)
lifetime as everywhere else in this platform.

**One connection group per recipient (`sub` claim).** `NotificationsHub.OnConnectedAsync`
adds every connection to a group named by the caller's own account id, so a push
(`Clients.Group(recipientAccountId.ToString())`) reaches every open tab/device for that
account without the consumer needing to track connection ids itself.

**Persist before pushing, never only push.** `DomainEventsConsumer` writes the
`Notification` row and commits it before calling `IHubContext.Clients.Group(...).SendAsync`.
If the recipient isn't connected right now, the push is simply lost - the row survives
regardless, and the next `GET /api/notifications` shows it. Delivery of the *event* is
at-least-once (Kafka); delivery of the *push* is best-effort on top of a durable record,
never the only copy.

**A local `AccountLookup` projection, not a synchronous call to AuthService.** This service
already consumes `auth.events` for `AccountProvisioned`/`AccountProvisioningFailed`, so it
already sees every `EmployeeId -> AccountId` pair as it happens. Materializing that into a
small table (updated in the same transaction as the `AccountProvisioned` notification) is
the same trade EmployeeService already made denormalizing `DepartmentName` - avoids a
synchronous cross-service call on every notification write, at the cost of the lookup
briefly not existing for an event that outraces the `AccountProvisioned` message it depends
on (see Consequences).

**Only four event types get a v1 notification**, chosen by walking every domain event
already in the system, not only the new ones:

| Event | Recipient | v1? |
|---|---|---|
| `EmployeeHired` | the new hire | No - no account exists yet, superseded by `AccountProvisioned` |
| `EmployeeTransferred` | the employee | **Yes** - unambiguous recipient |
| `EmployeeTerminated` | the employee? | No - notifying a just-locked-out account is questionable, and account-lock-on-terminate isn't implemented |
| `DepartmentCreated/Moved/Deleted` | department members/manager | No - `Departments` has no `ManagerId`/owner field; no way to derive a recipient without a new data model |
| `AccountProvisioned` | the new hire | **Yes** |
| `AccountProvisioningFailed` | the hiring admin | **Yes**, gated on `HiredByAccountId` being present (see the actor-tracking work this depended on) |
| `CurrencyGranted` | the employee | **Yes** |

Broadcast-shaped events were left out deliberately, not overlooked: guessing a targeting
rule for "who is a department's audience" would have been worse than not building it.

## Consequences
- A `Notification` is deduplicated by `SourceMessageId` (unique index), the same role
  `MessageId` plays on every `DeadLetterEntry` in this codebase - Kafka's at-least-once
  redelivery must not create a second row for the same event.
- `CurrencyGranted` (or `EmployeeTransferred`) arriving before the corresponding
  `AccountProvisioned` has been consumed finds no `AccountLookup` yet and is silently
  skipped - not an error, but a real v1 gap: that notification never retroactively appears
  once the lookup exists. Observed live during verification (a welcome bonus granted
  immediately on hire, well before the account existed). Acceptable for v1 because the
  event itself isn't lost (RewardsService/EmployeeService's own record of it is
  unaffected) - only the *notification about* it is; would be revisited if this proves to
  matter in practice (e.g. by re-checking un-notifiable events once a lookup appears).
- The frontend SignalR client, React hook, and feed UI are explicitly out of scope for this
  phase - blocked on the frontend's OIDC/BFF work (issue #13) not yet carrying a bearer
  token on requests at all. This phase is backend-complete and was verified live end-to-end
  with a hand-rolled Python SignalR client instead.
