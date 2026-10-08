# 57. Who may hire into a department: relationship-based authorization with OpenFGA

## Status
Accepted as an option behind a setting; **roles mode, the existing behaviour, stays the default.** Related: [0004](0004-no-api-gateway-aggregation.md),
[0036](0036-redis-failure-and-sentinel.md) (what to do when a dependency of a decision is down), [0042](0042-row-level-security-multi-tenancy.md),
[0056](0056-the-hire-as-a-temporal-workflow.md) (the other option built beside its default).

## Context
The platform's roles are three: viewer, editor, administrator. An editor may hire into any department and move anyone anywhere; there is no way to say
"Anna runs Engineering, so she may hire into Engineering and everything under it, and not into Sales". That is a statement about a relation between a person
and a node of a tree, and the tree changes (departments move). Role checks cannot say it; a table of "who manages what" in EmployeeService could, but the
rule "or any department above it" is a walk up a tree that someone would write by hand, and get wrong once, and not notice.

## Decision
**With `DepartmentAuthorization:Mode=Tree`, a non-administrator may hire into a department, and transfer a person into or out of one, only if the store says they
manage it or a department above it.** The model is three lines (`deploy/fga/model.fga`): a department has a `parent` and `manager`s, and
`can_manage: manager or can_manage from parent`.
- **The store is OpenFGA**, on its own Postgres (the `fga` compose profile), so that who manages what survives a restart. The service finds or creates the store
  and loads the model on first use.
- **EmployeeService is the one place the question is asked and the tree is kept.** It reads DirectoryService's department events (created with a parent,
  moved, deleted; `directory.events.v2`) and writes the parent links; each handling is one atomic write and idempotent. A move reads the parent links the store
  holds and replaces them, instead of trusting the event's "old parent".
- **Managers are made by an administrator** (`PUT/DELETE /api/employees/departments/{id}/managers/{accountId}`, with a fresh step-up, and only for a department Directory
  knows). OpenFGA is the only place that fact lives.
- **An administrator is allowed without the store being asked.** An outage of the store must not lock out the people who would repair it.
- **When the store cannot answer, a non-administrator is refused, with a 503 and not a 403**, so that "you may not" and "we cannot tell right now" are different
  to the caller. Never a guess and never an allow. Reads do not depend on it.
- **The check comes before the directory is asked anything**, so a caller who may not hire into a department cannot tell whether it exists: a department that
  is not there and one they do not manage get the same refusal.
- **The caller's role is read from the token, never from the request.** The hire command is bound from the body, so the administrator flag could have been sent
  by a client; the controller overwrites it, and a test sends it.
- The model has its own declarative tests, run by OpenFGA's CLI in CI (`scripts/check-fga-model.sh`), which also checks that the JSON the service embeds equals the model.

Alternatives: **a managers table in EmployeeService plus a recursive query on a copy of the tree** (no new infrastructure, and the walk up the tree and the
copy's upkeep are ours to get right; the choice is closer than the model's three lines make it look), **asking DirectoryService at each hire** (couples every hire to
another call, and Directory has no notion of managers), **claims in the token** (a manager's departments in the access token: stale until it is refreshed, and a
move of a department would change the claims of everyone above it), and **keeping roles only**.

## Measured
`scripts/fga-drill.sh`: the stack's DirectoryService and Kafka, a real OpenFGA on Postgres, a real EmployeeService in tree mode, two non-administrator
accounts. 17 checks, all passed in the last run.
- **A department made in Directory reaches the store in about two seconds** (2.3 s from creating the tree to the first hire into its deepest department going through), by
  the outbox cycle and the consumer.
- **A move takes effect in about a second** (the manager of the old parent was refused 1.1 s after the move; the manager of the new one was allowed).
- **Boris, who manages Engineering, hires into Engineering, Backend (one level down) and Platform (two), and is refused in Sales (beside), Company (above) and in
  a department that does not exist.** Carla, who manages nothing, is refused; an administrator hires anywhere.
- **The check costs a few milliseconds:** a hire by a manager p50 15 ms, p95 19 ms; by an administrator (no check) p50 12 ms, p95 27 ms (30 hires each, one machine).
- **OpenFGA stopped: a manager's hire is refused with a 503 in 38 ms; an administrator's hire goes through; reading employees works.** Started again, the manager's rights are
  back with nothing replayed, because they were in Postgres.

## Found on the way
Pointing the new consumer at `directory.events.v2`, a topic with weeks of history, it stopped on the first message it could not decode ("End of stream reached", an
Avro message whose bytes do not fit its schema) and **never moved on**. The consumer base class sets such a message aside with its bytes so that it does not stall, but the
dead letters keep the payload as json and the value kept for that case, `avro-undecodable:<base64>`, is not json, so the write failed, the consumer asked for the same message
again, and logged "the database is likely down". The decision to set a message aside was tested; the setting aside itself was not. Fixed in the shared consumer (the
payload is stored as a json string), with a test against the real table that failed first. I did not establish what the undecodable message was: its dead letter was in a
drill database that has since been discarded.

## Consequences
- **Another thing that can be wrong, and another thing to run.** The tree in the store is a copy of Directory's, kept by a consumer. A missed or dead-lettered event leaves it
  wrong, and nothing compares the two. The store's own consistency is not the question; the copy's is.
- **Departments that existed before the consumer have no links until their events are replayed** (the consumer group reads from the start, so history within the topic's
  retention is picked up; older departments are not). There is no backfill beyond that.
- **Deleting a department removes its parent link and its managers, not the links of departments below it.** Directory does not delete a department that has any, so this
  does not arise; if that rule ever changed, orphaned children would keep a parent that is gone.
- **A transfer needs the right over the department left and the department entered.** Over the destination alone, a manager could pull people out of anyone else's department
  into theirs; a test pins it.
- **Switching modes needs the tree to be there first.** In tree mode with an empty store every non-administrator is refused; the managers and the tree have to be in place before
  the switch.
- **The store's query cache** was not on in any run. Checks ask for the higher consistency level, which bypasses it, so a right just given is seen at once; the difference
  was not shown here, because the development server has the cache off.

## What is and is not verified
Thirteen tests on the decision, the tree's upkeep and the handlers against a small in-memory stand-in of the store (a manager's reach down the tree and no further; an
administrator without the store; a store that cannot answer; a move gives and takes rights and replaces what the store holds; events twice; a deleted department; hire and
transfer with the refused hire writing nothing; the forged administrator flag; a department that does not exist), each rule mutation-checked (fail open, the flag taken from
the body, a move that keeps the old parent, a transfer checked at one end, each caught); six on a real OpenFGA v1.22 in a container; the model's own tests in OpenFGA's CLI
(6 tests, 15 checks, 3 list queries), mutation-checked by removing the inheritance; the reader schemas of the three events against the producer's; the drill above; the whole backend suite (17 test assemblies), which passes except one McpServer test that cannot start its host on this machine because editor
language servers use up the 128 inotify instances (the same failure as on a commit without this change, see 0055). Not
verified: more than one EmployeeService instance running the tree consumer (it is idempotent and keyed by department, so safe by construction, not run); the store with its query
cache on; a tree of any size (a handful of departments); termination, and the other places that change who is where; the frontend, which does not show or use managers.
