# 25. A read replica, and reading your own writes from it

## Status
Accepted. Related: [0001](0001-schema-per-service-shared-database.md), [0024](0024-connection-pooling.md).

## Context
Reads outnumber writes and do not need the primary. A streaming replica takes them off it. The cost is that a replica is
behind, by milliseconds normally and by seconds when it is busy, and a person who has just changed something and then
looks at it must not see the old state. "Eventually consistent" is not an acceptable answer to "I just saved this".

## Decision
**A streaming standby of the one Postgres** (`--profile ha`, service `postgres_replica`): a base backup of the primary,
then WAL streamed over the replication protocol (the primary's `pg_hba` gains one line for it). `REPLICA_APPLY_DELAY`
holds replay back on purpose, so lag can be made visible and tested.

**Read-your-writes by position, not by time.** Every successful write answers with `X-Consistency-Token`, the primary's WAL
position (`pg_current_wal_lsn()`) taken after the handler committed, stamped just before the response headers go out. A read
that presents a token goes to the replica only if `pg_last_wal_replay_lsn()` has reached it; otherwise it polls for up to
500 ms and then reads the **primary**. A read with no token has no write of its own to protect and goes to the replica. A
replica that cannot be asked is treated as behind. So the person who wrote always sees their write, whatever the lag; the
cost of a lagging replica is latency for them (at most the wait) and load on the primary, not wrong answers.

**Who carries the token.** The BFF: it keeps the newest token it has seen per browser in an `httpOnly`, `Path=/api/backend`,
60-second cookie and presents it on the browser's next request. The page needs no code. Tokens are compared as numbers (text
order puts `9/0` after `10/0`) and anything that is not exactly `HEX/HEX` is ignored on both ends, since the value comes from
a client.

**Scope:** DirectoryService's Dapper read handlers, which all open their connection through one factory; with no replica
configured (`ConnectionStrings:DirectoryServiceReadDb` empty, the default) everything is the primary, as before. The
EF-based reads, and the other services, are not moved: SearchService's data is already event-fed with its own lag, and the
others have no read load that would justify it.

## Measured
`load-tests/k6/read-your-writes.js` against a replica held 3 seconds behind: each of 20 iterations creates a location and
looks for it twice.

| Read | Found it |
|---|---|
| presenting the write's token | **20 of 20** (each waited the 500 ms and read the primary) |
| presenting nothing | **0 of 20**: the replica really was behind |

So the lag is real, a token-less reader sees stale data, and a reader with a token never does.

## Consequences
- **A token-less read can be stale.** That is the definition of the replica, and acceptable for reads that follow no write of
  the reader's. A person whose cookie expired (60 s) or who switches browsers loses the guarantee for changes older than
  that, which is longer than any replication lag this setup should have; a lag that long is itself an incident.
- **A lagging replica turns into primary load.** Every token-carrying read after a write waits up to 500 ms, then reads the
  primary. Under sustained lag the replica helps less and less. There is no metric for how often the fallback happens: a
  dashboard needs `pg_stat_replication.replay_lag` and a counter of fallbacks, neither is wired.
- **The replica is behind the pooler**: it connects directly, not through PgBouncer (ADR 0024). A second pooler or PgCat is
  the way to give it the same protection.
- **Only DirectoryService stamps tokens**, so only its writes set the cookie. A write to another service would not protect a
  following Directory read, although the position is cluster-wide and would work if that service stamped it too.
- **No failover.** If the primary is lost nothing promotes the replica (roadmap).

## What is and is not verified
Unit tests: the routing decision (no token, caught up, one position behind, catches up inside the wait, the wait is bounded,
an unreachable replica) and the position parsing (numbers not text, malformed input) mutation-checked: off-by-one on the
comparison, no deadline, waiting for an unreachable replica, asking with no token. The BFF's token helpers (numeric
comparison, malformed values ignored, the cookie's attributes) with node tests. **Run live:** the replica streaming
(`pg_stat_replication`: `streaming`, replay lag 3.000 s with the delay on), Directory reading from it, the drill above.

Not verified: the BFF route in a browser (its logic is the helpers above; the route itself needs a signed-in session); a
replica that disappears mid-request (the code falls back, no test kills one); the real cost of the 500 ms wait on the
person's experience; behaviour with several Directory instances (the token is a position, so it should hold, nothing ran
two).
