# 26. Failing over to the standby: what is lost, what it costs to lose nothing

## Status
Accepted. Builds on [0025](0025-read-replica-and-read-your-writes.md) (the standby). Promotion is **manual** here; automatic
failover (Patroni or similar) is a separate, larger step and is not claimed.

## Context
A standby is only worth having if it can take over, and "can take over" has two numbers: how much acknowledged work is gone
when it does (RPO) and how long writes are refused (RTO). Asynchronous replication, which the standby uses by default, makes
no promise about the first: the primary tells the client "committed" without waiting for the standby.

## Decision
**Measure it, with the primary killed without warning, and choose the replication mode by what the numbers cost.**
`scripts/failover-drill.sh` starts its own primary and standby (same image and set-up as compose; the development stack is
never touched), runs 8 writers each doing single-row autocommit inserts so that every acknowledgement is one `INSERT 0 1`,
kills the primary with `SIGKILL`, promotes the standby with `pg_promote()`, and compares what clients were told with what
exists on the promoted server. `PARTITION_MS` cuts the standby off from the primary that long before the kill, modelling the
failure that matters: the primary loses its standby and then dies.

| Replication | Standby cut off before the kill | Acknowledged | **Lost (RPO)** | Rate before kill | Kill to first write (RTO) |
|---|---|---|---|---|---|
| async | no | 5 580 | **0** | ~1 850 /s | 0.85 s |
| async | **1 s** | 9 250 | **3 764** | ~2 160 /s | 0.85 s |
| sync | no | 4 422 | **0** | ~1 470 /s | 0.83 s |
| sync | **1 s** | 4 405 | **0** | ~1 030 /s | 0.83 s |

Reading it:
- **Async loses nothing only while the standby keeps up.** With a clean kill it lost nothing; with the standby cut off for one
  second it lost **3 764 of 9 250 acknowledged inserts**: everything the primary acknowledged while it could not reach its
  standby. The loss is bounded by the window of contact lost, not by anything the database promises.
- **Sync lost nothing in either case**, at a price: about 20% lower write rate with a healthy standby (1 470 against 1 850 per
  second), and with the standby cut off **writes stop** (4 405 acknowledged against 9 250: the primary refused to acknowledge
  what the standby could not confirm). That is the consistency/availability choice made concrete (PACELC: during the
  partition it chooses C over A, and otherwise latency over consistency).
- **RTO is the same whichever mode**, 0.85 s, of which 0.15 s is `pg_promote`. This is the *mechanical* part: kill, promote,
  first accepted write. It does not include noticing that the primary is dead or deciding to promote, which in a manual
  procedure is minutes and is what automation would remove.

**The default stays asynchronous** for the standby, and synchronous replication is a deliberate switch
(`synchronous_standby_names = '*'`), appropriate for the data where losing an acknowledged write is unacceptable (the Rewards
ledger is the candidate) and where refusing writes during a partition is preferable to accepting ones that might vanish. A
single standby cannot be both a synchronous standby and optional: if it dies, a synchronous primary stops. Real use needs two
standbys and `ANY 1 (...)`, which this set-up does not have.

## Consequences
- **Promotion is a one-way door.** The old primary is not a standby afterwards; bringing it back means a rebuild from the new
  primary (`pg_rewind` or a fresh base backup). Nothing here automates that, and the services' connection strings still point
  at the old host: after a failover they must be repointed (or a pooler/DNS name moved).
- **The write rate in the table is this machine's, for 8 tiny transactions**, not a benchmark of the platform; what transfers is
  the *direction and shape* (async loses what was in flight, sync loses nothing and costs throughput and availability).
- **No split-brain protection.** If the old primary were still alive and writing when the standby was promoted, two servers
  would accept writes. The drill kills the primary first; a real procedure has to make sure of that (fencing).

## What is and is not verified
Run for real (four scenarios, one run each, so no variance estimate; the 1 s partitions are the only source of loss, which is why
they are in the table). Not verified: automatic failover; the services recovering against a promoted server (they were not in
the drill); a standby that is behind on *replay* rather than on *receipt* (`recovery_min_apply_delay`; what promotion does with
received-but-unreplayed WAL in that case, and how long it takes, I did not test); more than one
standby; fencing.
