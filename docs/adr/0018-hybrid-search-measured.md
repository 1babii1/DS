# 18. Hybrid search, measured

## Status
Accepted. Builds on [0007](0007-elasticsearch-cross-service-search.md) (keyword search) and
[0015](0015-mcp-tools-read-through-the-service-apis.md) (McpServer's semantic department search).

## Context
Search had two halves that did not meet. `GET /api/search` matched the words typed; McpServer's `search_departments`
matched meaning, but only for departments and only through DirectoryService's own embeddings. Nobody had measured
either, so "which is better, and does combining them help" had no answer.

Building on the semantic side exposed a hole: a department's embedding was made once, from the name it had when it
was created, and nothing could ever update it, because **a department could not be renamed at all**. The domain had
`SetName`, but no command, endpoint or event called it. The plan's premise ("recompute the embedding on rename")
was about a feature that did not exist.

## Decision

**Departments can be renamed, and the rename reaches every index.** `PATCH /api/departments/{id}/name` (edit right,
same name validation as creation) changes the name only (identifier and path stay) and writes `DepartmentRenamed` to
the outbox in the same transaction; a rename to the current name changes and publishes nothing. SearchService
re-indexes the keyword document from the event. DirectoryService consumes its own `directory.events` and, on
`DepartmentRenamed`, deletes the stale embedding, which the existing worker then makes again from the new name. The
consumer never calls the model.

**SearchService gets a semantic side for all five kinds it already indexes**, filled from the events it already
consumes. The consumer records what to embed in `search.document_embeddings` (a hash of the text keeps an existing
vector while the text is unchanged) and a background worker makes the vectors with `nomic-embed-text`, the model
Directory uses. The consumer never waits on the model, so a slow or absent Ollama cannot stall consumption or keyword
search; rows without a vector are simply not semantic candidates yet. A vector made from text that changed while the
model was working is not stored. An employee's vector is made from name and role only, never the email.

**One endpoint, three modes.** `GET /api/search?mode=keyword|semantic|hybrid` (default `hybrid`). Semantic ranks by
cosine distance; hybrid asks both sources at once and fuses the top 50 of each with Reciprocal Rank Fusion (k = 60,
positions only, so a text score and a distance need no normalising). If the semantic side is down, missing or slower
than 3 seconds, hybrid answers from keyword and says so in the response's new `mode` field; `mode=semantic` answers 503.

**Keyword search stops matching on joining words.** See the second run below.

## Measurement
A fixed synthetic organization (60 departments, 12 positions) and 43 labelled queries in three kinds: **Word** (shares a
word with the name), **Meaning** (describes it with no word in common) and **Typo**. Labels were written before any
result was seen. It runs on the real pipeline (events through the consumer into Elasticsearch and pgvector in
containers) with the real model. `SEARCH_EVAL=1 dotnet test backend/SearchService/SearchService.IntegrationTests
--filter "Category=Eval"` reproduces it; it is skipped otherwise. recall@k is the share of the relevant items found in
the top k; MRR is the mean of 1 / rank of the first relevant item.

First run, as shipped:

| mode | recall@1 | recall@3 | recall@5 | MRR |
|---|---|---|---|---|
| keyword | 0.30 | 0.31 | 0.31 | 0.31 |
| semantic | 0.69 | 0.79 | 0.85 | 0.79 |
| hybrid | 0.58 | 0.67 | 0.85 | 0.70 |

Hybrid was worse than semantic alone. The misses showed why: the keyword side returned "Research and Development",
"Learning and Development" and "Compensation and Benefits" for any query containing "and" or "for", because a query
matches on any one of its words. RRF then mixed that noise into the top ranks. That is a defect of keyword search
independent of hybrid (a search for "vendors and buyers" returned every department with an "and" in its name).

Second run, after dropping joining words (and, the, for, to ...) from the keyword query:

| mode | recall@1 | recall@3 | recall@5 | MRR |
|---|---|---|---|---|
| keyword | 0.30 | 0.30 | 0.30 | 0.30 |
| semantic | 0.69 | 0.79 | 0.85 | 0.79 |
| hybrid | 0.69 | 0.79 | 0.85 | 0.79 |

By kind (MRR, second run): Word 1.00 / 1.00 / 1.00 (keyword / semantic / hybrid); Meaning 0.04 / 0.65 / 0.65;
Typo 0.00 / 0.85 / 0.85.

**Reading.** Semantic and hybrid are far better than keyword on anything that is not a shared word (Meaning 0.04 to
0.65, Typo 0 to 0.85) and no worse on shared words (all 1.00). **Hybrid does not beat semantic alone on this set:** it
ties. What hybrid buys is not measured here: an exact word can never be missed because of the model, and it keeps
answering when the model is down. Semantic still misses some descriptions (for example "people who pay our suppliers"
returned Payments, Supply Chain and Payroll before Accounts Payable): the model, not the fusion, is the limit.

## Consequences
- **The second run is post-hoc.** The joining-word fix was made after seeing the first run, on the same queries, so the
  second table is optimistic for hybrid; the fix is justified by the defect itself, and a held-out query set would
  be the honest test of it. 43 queries is a small set: it shows direction, not rates.
- `hybrid` stays the default because it is never worse than semantic here and degrades to keyword; if the measured
  tie held on a larger set, `semantic` with a keyword fallback would be simpler.
- **Two embeddings of a department now exist** (Directory's, used by McpServer's `search_departments`, and
  SearchService's). Both follow a rename. Retiring one, most naturally by pointing `search_departments` at this endpoint,
  is a separate decision this ADR does not take.
- An employee document's subtitle carries the department name as it was when the employee was indexed; a department
  rename does not rewrite it, so an employee's embedding text keeps the old department name until their next event.
- Every hybrid or semantic search now embeds the query: one model call per search, bounded by the 3 second budget.
- DirectoryService gains a Kafka consumer and a `dead_letters` table (migration), SearchService a `document_embeddings`
  table (migration) and a dependency on Ollama. `docker compose` needs the two migration images rebuilt.
- The model is used as shipped (no `search_query:` / `search_document:` prefixes that `nomic-embed-text` documents);
  trying them is an obvious next experiment.
- Per-result authorization is unchanged (0007): any signed-in user sees any result.

## What is and is not verified
Tests, each guard mutation-checked: the rename (name only, event in the same transaction, deleted and unknown are not
found, same name publishes nothing, the cache is dropped); the consumers (rename re-indexes; stale embedding dropped
and re-made; redelivery harmless); staging (vector kept while the text is unchanged, cleared when it changes, no email
in an employee's vector, a vector for text that changed meanwhile is not stored, a failing model leaves rows pending
without failing anything); RRF; the modes (semantic finds what keyword cannot, both-agree outranks one-only, default
mode, types narrow the semantic side, missing vectors, model down, model slow, unknown mode); the joining-word filter.
The measurement above.

Not verified: the full Docker stack end to end with the new migrations applied to a database holding real history; the
frontend against the new `mode` field (it does not send it, so it now gets hybrid); latency of a hybrid search on real
hardware beyond the 3 second bound; a held-out query set.
