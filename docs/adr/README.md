# Architecture Decision Records

Short records of the significant technical decisions made while building this platform,
in [MADR](https://adr.github.io/madr/)-style format: context, decision, consequences.
Written after the fact, from the actual reasoning captured in commit messages and PR
descriptions as each phase shipped, not backfilled generically.

| ADR | Decision |
|---|---|
| [0001](0001-schema-per-service-shared-database.md) | Schema-per-service in one shared Postgres database |
| [0002](0002-outbox-pattern-for-integration-events.md) | Transactional outbox for cross-service events |
| [0003](0003-choreography-saga-for-hire-employee.md) | Choreography saga for the Hire Employee → provision account flow |
| [0004](0004-no-api-gateway-aggregation.md) | No API Gateway aggregation / BFF, for now |
| [0005](0005-rewards-ledger-design.md) | Rewards ledger design and no cross-service employee validation in v1 |

Not yet written up, though each decision is already live in the codebase and explained
in its own commit messages: gRPC for internal calls, local JWT validation via JWKS,
OpenIddict as the OIDC provider, pgvector + local Ollama for semantic search, and
McpServer as its own read-only service.
