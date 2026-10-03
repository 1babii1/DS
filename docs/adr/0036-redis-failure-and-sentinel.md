# 36. Redis is an accelerator: fail fast, stop asking, and fail over with Sentinel

## Status
Accepted. Related: [0004](0004-no-api-gateway-aggregation.md); [0035](0035-kafka-replication-and-broker-failure.md) (the same
question for the bus).

## Context
Redis does two jobs here: DirectoryService's cache (HybridCache with Redis as the shared second level) and the SignalR backplane
that lets NotificationService run on more than one instance. Neither is a source of truth: the cache can always be rebuilt from
Postgres, and the backplane only relays. So "what happens when Redis is gone" should be "things get a little slower", not "the
service is down". The code already half-said so (cache writes after a commit are best-effort), but nothing tested the read path.

A test that points the cache at a port nothing listens on showed the real behaviour. **The read does not fail, and it does not
return quickly either: every cached read took about 6 seconds**, the client's connect timeout, because with the defaults every
call retries the connection and waits for it. A dead Redis was a service outage in everything but name.

## Decision
- **Fail fast** (`RedisConnectionString.Resilient`): `abortConnect=false` (start and run without a connection, reconnect in the
  background), 500 ms connect and operation timeouts, two connect retries. Anything the connection string already sets wins.
  Applied to both the cache and the SignalR backplane. Reads with Redis down: about 6 s becomes about 1 s while the service finds out.
- **Then stop asking** (`CircuitBreakingDistributedCache`): a breaker around the distributed cache. Three failures in a row and
  the cache is skipped outright for 15 s (a miss on read, a no-op on write), then one trial call decides whether it is back.
  After the first few reads, **a read with Redis down costs 0 ms** extra (measured: 20 reads, max 0 ms).
- **Failover with Sentinel** for the day Redis is up but its master dies: a reference setup in `docker/redis-ha/` (master, two
  replicas, three sentinels, quorum 2) and `scripts/redis-ha-drill.sh`, with a probe (`tools/redis-failover-probe`) that connects
  the way the services do. The services take a Sentinel connection string (`host:26379,...,serviceName=mymaster,password=...`)
  with no code change; the main stack keeps its single Redis.

## What was measured
Killing the master while a client incremented a counter every 20 ms through Sentinel (two runs, identical): **4 writes failed, the
client saw a pause of about 3.6 s between successes** (sentinels need three seconds to declare the master down, then elect and
promote), and **0 acknowledged writes were lost**, with the new master being a former replica.
The load was light, so replication was always caught up; Redis replication is asynchronous, and under load a failover can lose
the last writes the old master acknowledged. For a cache and a relay that is acceptable; for anything else it would not be.

**A failure found on the way:** configured with host names, a killed master's name stops resolving at all, and every sentinel
went into TILT mode ("Failed to resolve hostname") instead of failing over; nothing was promoted and the client failed for the
remaining half minute. With fixed addresses the same kill failed over in seconds. The compose file therefore uses fixed addresses
and says why.

## Consequences
- **Invalidation can be lost while the breaker is open.** Evictions are dropped along with everything else, so an entry cached
  before an outage can survive the change that should have removed it, until its own expiry (5 minutes in memory, 30 in Redis).
  The best-effort cache already accepted this; the breaker only stops paying for it on every request.
- **A flapping Redis** (up and down every few seconds) is hidden by the breaker: the log line when it opens and closes is the
  evidence. There is no metric yet.
- **The SignalR backplane gets the fast-fail settings but no breaker.** With Redis down, messages reach clients connected to the
  same instance and not those on others. Not tested here.
- Each Redis outage costs the first few requests about half a second each while the breaker learns; a lower bound for that needs
  a shorter timeout than 500 ms, which risks false failures on a loaded but healthy Redis.

## What is and is not verified
Real Redis client, Redis unreachable: reads answer from the source in every case; with the breaker, after the first three failures
every read costs nothing; without it, a later read still takes about a second (a test records that, so the breaker is not left in
place after it stops being needed). The breaker alone with a controllable clock: passes through when healthy, turns failures into
misses, opens after three in a row, a success resets the count, one trial after the open period (success closes it, failure
re-opens it), cancellation is not a failure. The connection-string rules. Sentinel failover live, twice, as above.
Mutation-checked: the breaker never opening fails both its own tests and the integration test.

Not verified: the services running against Sentinel in compose (the connection string is standard, nothing ran it); the SignalR
backplane during an outage or a failover; a failover under write load (the loss window); a Redis that is slow rather than down.
