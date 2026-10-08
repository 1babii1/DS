# 56. The hire's onboarding as a Temporal workflow, beside the saga it was compared with

## Status
Accepted as a second implementation behind a setting; **the saga of [0032](0032-orchestrated-hire-saga.md) stays the default.** Related:
[0003](0003-choreography-saga.md), [0010](0010-kubernetes-migration-target-architecture.md) (which names Temporal as a target),
[0041](0041-feature-flags-and-canary.md), [0054](0054-several-instances-of-one-service.md).

## Context
The saga of 0032 is a small state machine with a row in Postgres, a worker that looks at deadlines, and a compensation request. It is the kind of
thing a durable-execution engine exists to do, and 0010 names Temporal as where this might go. A claim about it is only worth making next to the
thing it would replace, so the same process was built twice, against the same domain code, and run through the same failures.

## What was built
`HireOrchestration:Mode` is `Saga` (default) or `Temporal`, behind one interface (`IHireOrchestrator`: begin in the hire's transaction, start after the
commit, three facts). The Temporal side:
- **`HireWorkflow`** holds a `HireSaga`, **the same pure state machine the saga uses**, feeds it the facts that arrive as signals and the time from
  the workflow clock, and when the saga decides to undo the hire runs one activity. The decisions are therefore identical by construction; what differs
  is everything around them.
- **`HireActivities.CompensateAsync`** calls the coordinator's `ApplyCompensation`, the very method the saga's own steps use: one meaning of "undo a hire".
  Temporal retries it without limit (1 s, doubling, up to 30 s).
- **Signals are sent with the start** (start the workflow if it is not there, and tell it this), so a fact that arrives first, or after a failed start, is
  not lost. A workflow that has finished is left alone.
- **The deadline counts from the hire**, passed in as `HiredAt`, so a workflow started late does not extend it (see below).
- **`TemporalHireReconciler`**: every 15 s, every employee still waiting for an account and older than 10 s gets its workflow started (idempotent: the
  workflow is named after the employee).
- `--profile temporal` runs Temporal's development server in the compose stack. Nothing of Temporal is registered in saga mode.

## Measured
`scripts/temporal-drill.sh`: a real EmployeeService on a private Postgres, a real DirectoryService, Temporal's dev server; the other services are not run, so
no account or bonus ever arrives and every hire ends at its 30 s deadline.

| case | Temporal | Saga |
|---|---|---|
| The instance is killed (SIGKILL) 4 s after the hire; the deadline passes with **no instance running**; a new instance is started | up in 2 s; hire undone **1 s after** it was up; 1 compensation event | up in 2 s; hire undone **1 s after** it was up; 1 compensation event |
| How late after its deadline an **instance that stays up** undoes four hires made 4 s apart | **1, 1, 1, 1 s** (resolution 1 s) | **16, 12, 9, 5 s** |
| A hire is made while the orchestrator's store is unreachable (Temporal down) | the hire succeeds (200), the start fails and is logged, the reconciler starts the workflow; hire undone **31 s** after it was made | not applicable: the state is in the hire's own transaction |

## What it shows
1. **Surviving a killed process does not tell them apart.** The thing one is first tempted to demonstrate ("kill the worker; it continues from where it
   stopped") is done as well by the saga, because its state is in Postgres and its worker looks at every overdue hire when it starts. Both undid the hire
   once, a second after a new instance was up. If that is the argument for Temporal, it does not hold here.
2. **What does differ is precision.** The saga looks every 15 s, so a deadline is acted on up to 15 s late (5 to 16 s in the run, with the processing); Temporal's timer lives on the
   server and fired within a second. For a two-minute onboarding deadline that is irrelevant; for a deadline that mattered it would not be.
3. **Moving the state out of the database opens a gap the saga does not have.** The saga's row is written in the hire's transaction, so a hire without its
   process cannot exist. A workflow has to be started after the commit, and the call can fail: a hire with nothing watching it. The reconciler is what closes
   it, and it is code the saga never needed (60 lines and a poll). The same drill found a mistake of mine in the first version of it: the workflow
   counted its deadline from its own start, so a hire made while Temporal was down was undone 55 s after the hire, not 30. Passing the hire time fixed it
   (31 s), with a test that fails without it.
4. **It is not less code.** Specific to the saga: 134 lines (repository, interface, deadline worker, migration). Specific to Temporal: 334 lines (workflow
   122, orchestrator 94, reconciler 60, registration 38, activity 20). Shared by both, unchanged: the state machine (139) and the compensation (93).
   Temporal removes a table and a polling worker and adds a client, a worker, a reconciler and a server.
5. **The time-skipping test server cannot test everything.** It does not move the clock while a task waits for a worker, which is the very situation "nobody is
   there when the deadline comes"; that case runs on the real local server with short intervals (3 s) instead.

## Decision
Keep the saga as the default and Temporal as an option that is built, tested and drilled, not adopted. The reasons are the ones above: the saga is
smaller to operate (no cluster, no extra store, no extra reconciler), its state is in the database the hire is already in, and it does everything the
drill asked of it except act on time. Temporal would earn its place with a process that has many steps, long waits (days), human tasks or fan-out
that the saga would turn into a swamp of states; a three-fact, two-minute onboarding is not that. The interface means the choice can be revisited
without touching the callers.

## Consequences
- **A second thing to keep in step.** `HireSaga` is shared, so the two cannot disagree on decisions, but a new step (say a laptop request) has to be added to the
  state machine and to both ways of carrying it; the workflow's signals and the saga's coordinator methods are a pair.
- **Switching modes with hires in flight is not handled.** A hire made in saga mode has a saga row and no workflow; if the mode is switched, the first fact that
  arrives for it starts a workflow, and both would undo it at the deadline (the request is idempotent, but it is sent twice). Switch only with nothing in flight.
- **The workflow code is constrained:** it must give the same answer when replayed, so it reads time only from the workflow clock and does effects only in activities.
  The domain object makes that easy here (it takes the time as an argument); it would not for code that reads the clock.
- **Temporal's dev server keeps its state in memory.** The compose profile is for running and showing the workflow, not a deployment.
- **The late-step grace is an hour,** after which the workflow ends; the saga would still hear of a step that arrived a day later. A bonus that lands
  after an hour is not undone in Temporal mode.

## What is and is not verified
Seven workflow tests on Temporal's test server (both steps in time; repeated facts; a missed deadline undoes only the steps that happened; a late step
undone in a second, narrower request; a failed account undone at once; a workflow started late counts from the hire; and, on the real local server with real
seconds, a deadline that passes while no worker is alive is acted on when a worker returns), a test of the shared compensation against Postgres, and the
66 existing EmployeeService tests unchanged (74 in all pass). Mutation-checked: the bonus signal ignored (three fail), the failed-account signal ignored
(one fails), the hire time ignored (one fails). The drill above, once per case, plus earlier runs that found the two problems in 3. Not verified: the full
chain with the real Auth and Rewards on Kafka (the other services were not run), several EmployeeService instances in Temporal mode (each runs a worker and
a reconciler, which is safe by the workflow naming but was not run), Temporal against a persistent store, and any load.
