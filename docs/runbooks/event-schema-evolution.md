# Changing an event: the runbook

For [ADR 0023](../adr/0023-event-contracts-avro-and-registry.md). Events are Avro; the schema of each is a `.avsc` file
next to the record that publishes it; the registry and CI refuse a schema that an earlier event could not be read by.
**The one rule: only add, never remove or change.** Everything below is that rule applied.

## Where things are

| What | Where |
|---|---|
| A producer's event schema | `backend/<Service>/…/IntegrationEvents/Schemas/<Event>.avsc` (the operator-redrive event: `backend/Shared/IntegrationEvents/Schemas/`) |
| A consumer's reader schema | `backend/<Service>/…/Consumers/Schemas/<Event>.avsc`, declaring exactly the fields of its own record |
| The producer's C# record | next to its schema; a test fails if the two differ |
| The check | `scripts/check_schemas.py`, run by CI as "Event schema compatibility" |

## Run the check yourself

```bash
docker run -d --rm --name reg-check -p 18082:8080 quay.io/apicurio/apicurio-registry:3.0.7
python3 scripts/check_schemas.py --registry http://localhost:18082/apis/ccompat/v7 --base origin/main
docker rm -f reg-check
```

It needs a fresh registry each time (it registers everything on `--base`, then your change). The running stack's
registry (`:8081`) is not for this: it keeps what it has seen.

## Recipes

### Add a field
1. Add it to the producer's `.avsc` as **optional with a default**: `{"name":"Grade","type":["null","int"],"default":null}`.
   A field without a default is refused (a consumer that has not been redeployed, and every old event, lack it).
2. Add the property to the C# record as nullable (`int? Grade`). The schema/record test checks both.
3. Consumers need no change. One that wants the field adds it to its own reader schema **and** its own record, in the same
   commit; CI checks the reader against the producer's latest.

### Stop using a field (remove)
Never delete it. In its schema give it a default if it has none (so new events may omit it), add `"doc": "Deprecated: …"`,
and stop filling it in code. Old readers keep seeing it (as the default), new ones ignore it. The check refuses a removed
field with this advice.

### Rename a field
A rename is "add the new one, deprecate the old one". Add `new_name` (optional, default null), deprecate `old_name` as
above, fill both for as long as any consumer still reads the old one, then stop filling the old one. Consumers move to
the new name in their own time.

### Change a type
Widen it with a union rather than replacing it: `["int","string"]`. Both are accepted for the transition; the registry
refuses a plain replacement.

### A new event type
A new `.avsc` (namespace is the producer's: `ds.directory`, `ds.employee`, `ds.auth`, `ds.rewards`, `ds.ops`) with the
file name equal to the record name, the C# record, and the event added to the producer's `…EventTypes`. Consumers that
want it add a reader schema. Nothing else is registered by hand: the service registers its schemas at startup.

### Something that truly cannot be expressed as an addition
Make a **new event type** (`EmployeeHiredV2`) and publish both for a while; do not edit the old one. If you find
yourself here, write an ADR first: it means the old event meant something that has stopped being true.

## When something goes wrong

| Symptom | Meaning | Do |
|---|---|---|
| CI says a schema is incompatible | The change would stop earlier events being read | Make it additive (above) |
| CI says a field was removed | The "never remove" rule | Deprecate it instead |
| `outbox_pending_messages` grows, services log "Schema registry not ready" | The registry is down; hires and grants still commit, publishing waits | Bring the registry back (`docker compose up -d schema_registry`); the backlog drains by itself |
| A consumer logs "Could not decode a message … will try again" | It cannot reach the registry; it is waiting on that message, nothing is lost | Same |
| Rows in `dead_letters` of a consumer, error mentions a schema id | The registry does not know the id the message carries (a rebuilt or different registry) | The messages were set aside with their bytes; they cannot be decoded without the schema that wrote them |
| Look at what is waiting to be published | | `scripts/outbox.sh <directory\|auth\|employee\|rewards>`; `parked` for the stuck ones |

## Do not
- Do not point a service at an empty registry that replaces a lost one: every message already in Kafka names a schema id
  that registry would give to something else. The registry's database (`registry` on the shared Postgres) is part of the
  data to back up.
- Do not register schemas by hand in the running registry. The services do it, with the compatibility rule set on each
  subject; a hand-registered schema skips the review CI gives.
