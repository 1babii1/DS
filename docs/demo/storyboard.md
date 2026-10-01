# Demo video: storyboard and script (3-5 minutes)

A shot list for recording, with the exact commands and what each scene proves. Every scene says what is **backed by an
automated test or a measurement** (safe to claim) and what is **only rehearsed here and must be tried once before
recording** (do not claim until it has been seen working on the recording machine).

No part of this needs a hosted demo: everything runs locally (`scripts/demo.sh`), the language model included.

## Before recording

```bash
scripts/demo.sh up          # backend stack, waits until healthy
scripts/demo.sh history     # a dated fictional org for the /history scene
scripts/demo.sh llm         # the local model (needs a GPU device and about 12 GB of RAM; ~20 s to load)
docker compose --profile obs up -d   # Grafana for the failure scene (http://localhost:3001)
cd frontend && ~/.local/bin/secrets-run ds-portfolio-dev -- npm run dev     # http://localhost:3000
```

Have two browser profiles signed in: an **admin/editor** (the seeded admin named by `SEED_ADMIN_EMAIL`) and a
**self-registered viewer** (register at `/register`). Close other heavy programs: the model and the stack share about
27 GB of RAM. Zoom the browser to 125%.

## Scene 0: what this is (0:00-0:15)

Show the README top and `scripts/demo.sh status` (eight services, Kafka, Elasticsearch, Postgres, all healthy).
Say: a distributed org-management platform built to work through real distributed-systems problems, with a written
reason for every decision (19 ADRs).

## Scene 1: the assistant proposes, a person approves (0:15-1:15)

Open `/assistant` as the editor. Ask something it can only answer by reading: *"How many people work in Payments?"*
(it reads through the same APIs you could, as you). Then ask for a change that needs ids it can find by reading, for
example: *"Move <a person from the Payments list> to Engineering as a developer."*

Show: the reply is plain text; **the card under it is drawn by the server** from a signed plan (names, department,
position, an expiry), not from the model's words; the card says "Nothing has changed". Open `/activity` in a second tab
to show that nothing was recorded yet. Click **Approve and apply**; show the result and the new audit event.

Then ask for a grant ("Give <person> 200 for the release, reason release bonus"): the card says currency needs a recent
re-verification (step-up) before it can be applied.

- Backed by tests: unknown or wrong-kind ids are refused and plans read in names (`AgentResolutionTests`); the preview
  holds no ids and not the token, the model cannot confirm (`AgentEndpointTests`, `AgentToolsTests`); step-up for money
  (ADR 0016); the grant limits and the daily quota enforced by the ledger (`GrantCurrencyTests`).
- Rehearse first: check that the model finds the position id with `list_positions_by_department` (added after the eval, so
  the eval never saw it). If it cannot, pick a person from the same
  department as a developer and move them to a department that already has a developer.

## Scene 2: prompt injection, measured (1:15-2:15)

Show the table in [ADR 0017](../adr/0017-approval-is-informed-and-bounded.md) (a local gpt-oss-20b driven through the
real tools over an organization with hostile text in names): what was refused, what reached a card. Then, with the model
running:

```bash
dotnet run --project backend/McpServer/McpServer.ModelEval -- --runs 3 --task A1
```

Narrate the lines: the injected "assistant bonus 10000" is **refused** by the ceiling, the model retries at exactly
500, and that one reaches a card, **in names, bounded, and only applied if the person clicks**. Say plainly what is and
is not claimed: the model can still be talked into proposing; the protection is that nothing runs silently and the
reach is bounded. Show the deterministic guardrail eval too:

```bash
dotnet test backend/McpServer/McpServer.IntegrationTests --filter "FullyQualifiedName~GuardrailEval" --logger "console;verbosity=detailed"
```

- Backed by: the eval itself, ADR 0017's before/after table, `docs/agent-evals.md`. Small sample (3 runs per task):
  say "direction, not rates".

## Scene 3: refused by rights (2:15-2:50)

Switch to the **viewer** profile. Ask the assistant to hire someone; a card appears (anyone can ask). Click Approve.
Show the refusal: the plan runs as the person who approved it, so EmployeeService's own authorization says no.

- Backed by tests: the confirmation forwards the caller's own token, never a service credential
  (`PlanExecutorTests`, `ResilienceAndWiringTests`).
- Rehearse first: that a self-registered account really is refused by the hire endpoint in your seeded setup, and what
  the card shows when it is.

## Scene 4: the failure lab (2:50-3:50)

Open Grafana's Resilience dashboard (http://localhost:3001). In a terminal:

```bash
scripts/chaos/freeze.sh kafka 30      # pauses the broker without closing connections, then thaws it
```

Show the metrics before, during and after, and that hiring someone during the freeze neither loses nor duplicates their
welcome bonus once Kafka is back. Then the automated version and the load numbers:

```bash
dotnet test backend/ChaosTests
```

and the before/after table from [`docs/benchmarks/baseline.md`](../benchmarks/baseline.md).

- Backed by: `ChaosTests` (a frozen broker delays bonuses but loses and duplicates none; same for a frozen database),
  the k6 results, the SLO rules in `docs/slo.md`.

## Scene 5: search and the time machine (3:50-4:40)

**Search.** In the frontend's search, type a description with no word in common with the name (for example *"people who
pay our suppliers"* after creating an "Accounts Payable" department) and compare with the same words in keyword mode
through the API. Show the measured table from [ADR 0018](../adr/0018-hybrid-search-measured.md) and its honest line:
hybrid tied with semantic, it did not beat it.

**Time machine.** Open `/history` and drag the slider across the seeded year: the Payments department is renamed to
Billing in April, Platform moves under it in June and back in August, Sales disappears in July, people come and go.
Mention that the answer is rebuilt from the audit log by folding events, with no new storage
([ADR 0019](../adr/0019-org-time-machine-from-the-event-log.md)).

- Backed by: `OrgReplayTests` (every date of a scripted history, exactly at an event and just before it),
  `OrgHistorySeedTests` (the seeded story at its key dates), the search evaluation (`SEARCH_EVAL=1`).
- Rehearse first: a semantic query on your live data (the index fills as events arrive).

## Scene 6: close (4:40-5:00)

Back to the README: the two-minute tour, the "Proof" section, the ADR index, and the "Honest status" list of what is not
done. Say what you would do next.

## What not to claim

- That the assistant cannot be manipulated: it can; the claim is bounded, readable and approved.
- That hybrid search beats semantic search: it tied on a small set.
- That the org history reaches back before the audit log started, or that "who led a department" can be answered (there
  is no such concept).
- That the browser pages were verified by an automated browser test: they were checked by lint, types, a production
  build and unit tests of their logic, and by you on camera.
