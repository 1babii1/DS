# 58. The drills run every night and fail when what an ADR claimed no longer holds

## Status
Accepted. Related: [0052](0052-directory-call-deadlines-and-fast-rejection.md), [0053](0053-directory-call-policy-at-the-call-level.md),
[0055](0055-one-outbox-publisher-and-configurable-partitions.md), [0056](0056-the-hire-as-a-temporal-workflow.md), [0057](0057-department-rights-through-openfga.md).

## Context
The ADRs from 0035 on rest on measurements: a broker killed, a network made slow, three instances and a SIGKILL, a deadline that passes while nothing runs. Each was run by hand,
once, on one machine, and written down. Nothing run again. The next change to the shared outbox code, the Directory call or the authorization rules could break any of them without a
single test noticing, because the tests prove the logic and the drills prove what the logic does against real brokers, databases and stores. A measurement that is not repeated is an
anecdote with a date on it.

## Decision
**A workflow (`.github/workflows/drills.yml`) builds the services, starts the shared part of the stack, and runs four drills, nightly and on demand** (`workflow_dispatch`, with a
choice of drills). **Each drill now exits non-zero when a property its ADR claimed does not hold**, instead of printing a table to be read:
- `toxiproxy-drill.sh` (0051 to 0053): slow answers inside the limits still succeed; once the breaker is open a hire is refused in milliseconds and none waits past its deadline; a reset
  and an unreachable Directory are refused fast; hires succeed again within 15 s of the network returning.
- `multi-instance-drill.sh` (0054, 0055): with three instances of each service and one of each killed, every confirmed grant has a ledger row and no key was counted twice, every
  wallet equals its ledger, the card's copy equals every wallet in balance and version, the outbox drained with nothing parked, each event went on the bus once (at most 5% over), and the six partitions are spread over all
  three consumers.
- `temporal-drill.sh` (0056): a hire is undone exactly once after the instance carrying it was killed and the deadline passed with nobody alive, in both the saga and the workflow; a hire made while
  Temporal was down is undone by the reconciler within 50 s of the hire; Temporal acts within 4 s of a deadline and the saga within 20.
- `fga-drill.py` (0057): the 15 statements about who may hire where, through a real OpenFGA, including a move and an outage.

The thresholds are looser than the figures in the ADRs (a runner is slower and noisier than a laptop) and aimed at what each ADR concluded, not at its exact numbers.

**Each check was shown to fail** against the code it was written about: the old Directory policy fails the fast-refusal check; the stack's older Rewards image, without the publisher lock,
fails the once-on-the-bus check (x2.33).

## What running it on a runner found
Running on GitHub's machines, against a stack with no state and no developer's `.env`, found things the drills had been quietly depending on:
- **The seeded administrator did not exist.** On a fresh database the seeder creates the account with the password in configuration, and if the creation is refused it does nothing and says
  nothing. The committed development default, `ChangeMe123!`, is refused by the breached-password check (the platform's own rule), so the first sign-in of every drill got a 401. On the laptop the
  account had been created before that check existed. The run now starts the stack with a random strong password and the drills read it from the environment. A seeder that fails silently is a
  defect of its own; it is not fixed here.
- **A script that stops under `set -e` says nothing about where.** Every drill now prints the line it stopped on, and the last lines of the log of each container it started, when it exits non-zero.
- **One drill script was not executable in git**, which only a fresh checkout could show.
- **The compose project name**: the drills name the stack's network and images after the project the repository is developed under, so the run sets `COMPOSE_PROJECT_NAME`.

## Consequences
- **The run takes about 17 minutes** (3.5 for the images, 0.5 to start the stack, then the drills: 3, 2, 4 and 4 minutes; the OpenFGA one is slow because the sign-in is limited to a few requests a minute). It runs once a day.
- **A red run is information, not noise, only if the checks are right.** A threshold that is too tight will fail on a slow runner for no reason; the first nights will say. The drills' own output
  is attached to the run, with the logs of the stack if one fails.
- **It does not cover everything.** The drills that need a Kubernetes cluster (the chart), three Kafka brokers (replication), a Redis Sentinel set (failover), Citus, and the mutual TLS pair
  are not in it; they are heavier, and stay by hand. The browser tests are not in it either.
- **The workflow starts only the part of the stack the drills share** (Postgres, the pooler, Redis, Kafka, the registry, mail, AuthService and DirectoryService), not the other services.

## What is and is not verified
Each drill passed its checks on a laptop and, on GitHub's runners, each one alone and then all four in sequence on one stack (GitHub Actions run 37768802070 on the branch: 17 minutes, all four green). Not verified: that the nightly schedule
fires (it has not yet had a night); the stability of the thresholds over many runs; the drills that are not included.
