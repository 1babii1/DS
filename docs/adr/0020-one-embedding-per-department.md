# 20. One embedding per department, in SearchService

## Status
Accepted. Retires the department embeddings of [0008](0008-mcp-server-cross-schema-reporting-layer.md)'s era and settles the
open decision at the end of [0018](0018-hybrid-search-measured.md). Builds on [0015](0015-mcp-tools-read-through-the-service-apis.md)
(tools read through the service APIs).

## Context
A department had two embeddings, made from the same text by the same model and kept current by the same event: one in
DirectoryService (`department_embeddings`, a worker, a Kafka consumer, its own Ollama client and rate limit), read only
by McpServer's `search_departments`; and one in SearchService, which also answers keyword and hybrid searches over
five entity kinds. ADR 0018 named it as an open decision and left it. Two copies means two places a rename has to be
right, two model clients to keep healthy, and a tool that answered with a weaker search than the one the product used.

## Decision
**SearchService is the only place a department is embedded.** `search_departments` now asks it
(`GET /api/search?types=department`) as the caller, and gets the default hybrid mode: keyword and meaning together,
falling back to keyword when the model is slow. DirectoryService loses `GET /api/departments/search`, its embedding
table, worker, consumer, model client and rate limit, and the `dead_letters` table that existed only for that consumer
(one generated migration drops both tables). McpServer gains a dependency on SearchService, through the same
caller-token client every other tool uses.

Alternatives considered: keep both and document it (cheap, but the divergence is the problem); move search into
DirectoryService (it would then need every kind's data, which is what ADR 0007 put in SearchService on purpose).

## Consequences
- **The tool's answer changed shape slightly.** `score` is SearchService's relative rank, not a 0-1 similarity, so the
  tool description says it only orders one answer; a page is at most 20 results, not 50.
- **A new runtime dependency:** `search_departments` fails (with the fixed tool message) when SearchService is down,
  where before it needed DirectoryService and Ollama. Searching by name still works with the model down, because hybrid
  degrades to keyword.
- **The old endpoint is gone.** Nothing in the repository called it besides the tool; an outside caller would break.
- **The migration is destructive** (drops stored vectors). Its `Down` recreates the tables empty; the vectors were
  derived data and SearchService holds the same ones.
- Directory keeps the `pgvector` package and the extension because earlier migrations (still part of the history)
  create and reference them; removing those is a separate squash.

## What is and is not verified
Tests: the tool asks SearchService for departments only, with the caller's token, an escaped query and a clamped limit,
and maps what comes back; failures keep the fixed messages; DirectoryService's whole suite passes without the embedding
code and with the drop migration applied to a real database by the test fixture.

Not verified: `search_departments` end to end against a running SearchService and a real model; the quality of the
tool's answers compared with the old semantic-only search (the labelled queries of ADR 0018 measured SearchService, not
the tool).
