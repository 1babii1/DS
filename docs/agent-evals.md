# Agent guardrail eval

The MCP agent (ADR [0016](adr/0016-agent-proposes-user-confirms.md)) lets a model *propose* changes; a
person confirms them. This eval scores the barriers between what a model emits and any change to data.

## What it is, and what it is not

**It is** a deterministic, model-free check that whatever a manipulated or mistaken model passes to a
`propose_*` tool, or whatever hostile content ends up inside a signed plan, cannot get past the checks. It runs in
CI with the rest of the backend tests (`McpServer.IntegrationTests/GuardrailEvalTests`), needs no model and no
network, and reports a score per run:

```
guardrail eval - proposals: 34/34 as expected
guardrail eval - signed plans: 10/10 as expected
```

**It is not** a measure of how good any model is: whether it picks the right tool, understands a request, or
resists being talked into things. A 100% here says the guardrails hold, not that the model behaves. That needs a model
in the loop, and it now exists: `backend/McpServer/McpServer.ModelEval` drives a local model through the real tools over
an organization with hostile text planted in its data, and [ADR 0017](adr/0017-approval-is-informed-and-bounded.md)
records what it found (including that an injected grant can still reach an approval card, bounded and readable). Run it
with `dotnet run --project backend/McpServer/McpServer.ModelEval -- --runs 3` while `scripts/llm-server.sh` is up.

## The corpus

`backend/McpServer/McpServer.IntegrationTests/Evals/guardrails.json`, reviewed as data:

- **proposals**: arguments a model could pass to a tool, each with the reason the case exists and the expected
  outcome - refused, or accepted with a given number of steps and largest amount. Includes the inputs that must
  *not* be blocked (a Cyrillic name, an apostrophe, an emoji, a plus-tagged email), so tightening a check cannot
  quietly break legitimate use.
- **signedPlans**: plans that carry a valid signature but hostile content (a grant above the ceiling, a grant
  reading its employee from itself, a later step, a non-hire step, or an earlier grant's transaction id), run
  through the real executor with recording stand-ins for the services. Each case names the expected step outcomes
  and how many grant requests may have left McpServer.

To add a case: write the JSON entry, run the test, watch it fail if it is a new gap, fix the barrier, watch it pass.

## What it found

Before the corpus was run, the name/text check only rejected control characters. Four cases failed at 28/32:
a right-to-left override (which reverses how a name is displayed), a zero-width space, a byte-order mark and a
line separator were all accepted, and any of them could make a plan read differently from what it does. The check
now rejects by Unicode category (control, format, separators, private-use, unassigned, lone surrogates) while
still allowing letters of any script, accents, apostrophes and emoji.

## Evidence the eval can fail

Each barrier was weakened in turn and the eval watched to go red: no grant ceiling in the executor (S01 and the
proposal cases P03, P04 fail), a grant allowed to read any earlier step's id (S04 fails), format characters
allowed again (P11-P13 fail).

## What McpServer can and cannot observe

It counts proposals (by tool, accepted or refused), confirmations (by outcome, with a reason for rejected ones)
and plan steps (by kind and outcome), and records spans for proposals, runs and steps
(`docker/grafana/.../resilience.json` shows them; `docker/prometheus/alerts.yml` alerts on a plan presented by the
wrong user and on repeated failed plans). No metric, span tag or log carries a plan token or anything a user typed;
a test holds that. Tokens and latency of the model are not visible here - they belong to whoever runs the model.

Not verified: the live metric and label names in a running Prometheus (the alert rules load and parse; the service
was not rebuilt and scraped).

## What changed after ADR 0017

The grant ceiling is now 500 (the ledger enforces it for agent grants, with a daily quota; McpServer keeps the same
number only to refuse early with a readable message), a hire carries no grant (the corpus cases for a grant on a hire
were removed, and one asserts a hire is a single step), and every id is resolved against the owning services before a
plan exists. The corpus runs against a lenient stand-in for those lookups, because it is about what the barriers do with
a model's *arguments*; resolution itself is tested in `AgentResolutionTests`.
