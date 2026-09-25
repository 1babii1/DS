# 15. MCP tools read through the service APIs, as the caller

## Status
Accepted. Supersedes [0008](0008-mcp-server-cross-schema-reporting-layer.md).

## Context
ADR 0008 let McpServer read `directory.*` and `employee.*` directly with one superuser Postgres
connection, on the argument that a read-only reporting layer crossing the schema boundary is the
point rather than a leak. That was a reasonable call for read-only tools, and it held up for a while:
read access was equivalent, because the Employee and Directory GET endpoints are `[Authorize]` only,
exactly like `/mcp`.

It stopped being enough for two reasons, one already real and one coming:

- **A divergence that already existed.** `GET /api/employees` is paged on purpose (its handler
  records that the unpaged list used to hand out every employee's email in one call). The MCP tool
  `list_employees_by_department` returned every employee of a department with no cap. A rule one
  service had deliberately introduced did not apply to the tool sitting next to it.
- **Write tools are next.** An agent that can change data must act with the caller's authority and go
  through each service's validation, authorization, idempotency and audit. Having reads on one path
  (raw SQL as a superuser) and writes on another would make the reads the weak link and put a
  database credential that can read everything inside the process the model talks to.

This is not a live privilege escalation: for reads the two paths granted the same access.

## Decision
Every MCP tool calls the owning service's REST API and forwards the caller's own bearer token
(`BearerForwardingHandler`). McpServer has no database connection, no connection string and no service
credential of its own. The precedent is EmployeeService's gRPC client, which already forwards the
caller's token to DirectoryService. One shared token can be forwarded as-is because the `roles` scope
carries every service's audience.

The handler fails closed: with no token on the incoming request the call is refused before any I/O,
never sent anonymously or under another identity. Only a fixed, category-level message of a failed call
reaches the model; a downstream response body is never read on failure.

The subtree query moved into DirectoryService (`GET /api/departments/{id}/subtree`, bounded to 500 nodes
and flagged when cut). The existing `department/{id}` endpoint cannot serve it - its handler never
populates `Children` - and having each caller rebuild the hierarchy is what put this logic in the wrong
place to begin with.

## Consequences
- Tool behavior changed where the API's rules are stricter, on purpose: employee listing is paged (the
  API's own cap of 200 per page) and returns `hasNext`; root departments are paged with `hasMore`; a
  subtree over 500 nodes is cut and flagged; an inactive department has no browsable tree, and neither
  do departments hanging under one: deleting a department changes only its own path and leaves its
  children active, so the subtree follows active parents instead of matching the path (a path match
  kept listing them - found by the first run of SubtreeTests). Whether deleting a department should
  also handle its children is a Directory domain question this does not settle.
- Forwarding the caller's token forwards every audience it carries. Token exchange to a narrow-audience
  token would be stricter and is deferred until write tools exist.
- DirectoryService's search limiter is 30/min per source IP, and every user now arrives from McpServer's
  IP: one shared bucket for all MCP users. The same shape as the shared-NAT case in
  `docs/benchmarks/baseline.md`; a per-user budget is the honest fix and is not done.
- Retry and the circuit breaker act on network failures, timeouts and 5xx, and deliberately not on 429,
  so one heavy user cannot open the breaker for everyone. Stated explicitly because the default Polly
  options do not react to a 5xx response at all (measured: 12 requests that each got a 503 made exactly
  12 calls, no retries, breaker never opened).
- The Ollama and HIBP clients added earlier use that default shape, so their retry and breaker only act on
  exceptions, not on 5xx responses. Known gap, not yet changed (see the learnings inbox).
- Verified in-process: token forwarding and isolation between 40 concurrent callers through the real
  dependency injection setup, the real retry and breaker policy, failure mapping, bounds, and shapes.
  Not verified: a live call through nginx with a real token; the subtree SQL and its integration test
  (written, not run - the container runtime was unavailable); MCP transport behavior beyond the tool
  methods.
