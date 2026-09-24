# 8. McpServer as a read-only cross-schema reporting layer

## Status
Superseded by [0015](0015-mcp-tools-read-through-the-service-apis.md) - McpServer no longer
reads other services' schemas. Kept as the record of why it did, and of what changed its mind.

## Context
AI assistants need to answer questions about the org structure - semantic search over
departments, the department tree, employee lookup by department - in natural language,
through the [Model Context Protocol](https://modelcontextprotocol.io), not through a
REST API a human client calls. That is a different consumer and a different protocol
from anything DirectoryService or EmployeeService already expose, and the answers
routinely need data that spans both of them (an employee's department name, a
department's employee count) in a single tool call.

The two conventional options were: (a) add MCP tool endpoints to DirectoryService and/or
EmployeeService directly, or (b) a new service. Adding MCP to an existing domain service
would mix a reporting/AI-assistant concern into a service whose own job is owning and
validating its domain's writes; the semantic-search tool also needs `pgvector` and an
Ollama embedding client that neither existing service has any other reason to depend on.
Per `.pi/laws/signals.md` (Architecture/complexity) and the system-design decision tree,
this is a genuine "does communication need to span services it doesn't own" situation,
not a default.

## Decision
McpServer is a separate ASP.NET Core service (`ModelContextProtocol.AspNetCore`,
`WithHttpTransport`) that reads `directory.*` and `employee.*` schemas directly through
Dapper (`NpgsqlDataSourceBuilder` + `UseVector()`), the one deliberate exception to the
schema-per-service boundary ADR-0001 already names. It owns no data itself: no schema of
its own, no writes, no domain logic to protect - every tool in `Tools/DirectoryTools.cs`
(`SearchDepartments`, `GetDepartmentTree`, `GetEmployee`, `ListEmployeesByDepartment`) is
a query. Crossing the schema boundary is the point, not a leak: a reporting/read layer has
no invariant of its own to protect by staying inside one schema, and re-deriving the same
joins through two services' HTTP APIs instead of one SQL query would not make the read any
safer, only slower and harder to reason about.

Authenticated the same way as every other service (`AddPlatformJwtAuthentication`, same
JWKS) rather than left open - these tools return names and emails that the owning
services gate behind their own authorization; an unauthenticated MCP endpoint would have
been the shortest path to that data in the whole platform, not a shortcut.

## Consequences
- New failure/operational obligations MetaServer did not have as part of another service:
  its own health check (`NpgsqlDataSourceHealthCheck`), its own deployment/observability
  identity (`"mcp-server"`), its own dependency on Ollama being reachable for the
  embedding-backed search tool.
- Because it owns no data, there is no migration or rollback story specific to it beyond
  the schemas it reads - retiring or replacing it has no data-loss risk of its own.
- The exception to schema-per-service is now traced to a written decision instead of a
  one-line mention in another ADR; if a second cross-schema reporting need appears, this
  is the ADR to extend or point to, not a new ad-hoc exception.
- Should DirectoryService's or EmployeeService's schema shape change in a way that breaks
  McpServer's Dapper queries, nothing enforces that at compile time (no shared contract) -
  a schema change there needs to grep for `McpServer/Tools` deliberately, the same
  discipline schema-per-service already asks of the two services' own migrations.
