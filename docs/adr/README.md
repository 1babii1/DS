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
| [0006](0006-signalr-notification-center.md) | SignalR for the in-app notification center |
| [0007](0007-elasticsearch-cross-service-search.md) | Elasticsearch-backed cross-service search |
| [0008](0008-mcp-server-cross-schema-reporting-layer.md) | McpServer as a read-only cross-schema reporting layer (superseded by 0015) |
| [0009](0009-postgres-backup-and-restore.md) | Postgres backup and restore |
| [0010](0010-kubernetes-migration-target-architecture.md) | Kubernetes migration target architecture (design only, not implemented) |
| [0011](0011-kafka-cluster-and-cdc-target-architecture.md) | Kafka cluster, Debezium CDC, event contracts (design only, not implemented) |
| [0012](0012-reliability-as-a-process.md) | SLOs, canary delivery, chaos testing (design only, not implemented) |
| [0013](0013-cell-based-isolation-target-architecture.md) | Cell-based isolation, Temporal, Citus (design only, not implemented) |
| [0014](0014-multi-region-target-architecture.md) | Multi-region deployment (design only, not implemented) |
| [0015](0015-mcp-tools-read-through-the-service-apis.md) | MCP tools read through the service APIs, as the caller |
| [0016](0016-agent-proposes-user-confirms.md) | The agent proposes, the user confirms |
| [0017](0017-approval-is-informed-and-bounded.md) | Approval is informed and bounded, not a promise about the model |
| [0019](0019-org-time-machine-from-the-event-log.md) | The org on a past date, folded from the event log |
| [0020](0020-one-embedding-per-department.md) | One embedding per department, in SearchService |
| [0021](0021-sign-out-revokes-the-refresh-token.md) | Sign-out clears the BFF's tokens and revokes the refresh token at the issuer |
| [0022](0022-hub-tickets.md) | A short-lived ticket opens the notification hub; the OAuth token never reaches the browser |
| [0023](0023-event-contracts-avro-and-registry.md) | Event contracts: Avro schemas, a registry as the compatibility arbiter, additive-only evolution |
| [0024](0024-connection-pooling.md) | A connection pooler in front of Postgres |
| [0025](0025-read-replica-and-read-your-writes.md) | A read replica, and reading your own writes from it |
| [0026](0026-failover-rpo-rto.md) | Failing over to the standby: what is lost, what it costs to lose nothing |
| [0027](0027-audit-entries-partitioned-by-month.md) | The audit log, partitioned by month |
| [0028](0028-no-kafka-transactions.md) | Kafka transactions are not used: the outbox and idempotent consumers are the exactly-once story |
| [0029](0029-expand-contract-migrations.md) | Schema changes that never break the version running before them |
| [0030](0030-change-data-capture-with-debezium.md) | Change data capture with Debezium instead of the polling publisher |
| [0031](0031-event-sourced-wallets.md) | Wallets are event-sourced |
| [0038](0038-load-shedding.md) | Load shedding: turn requests away quickly instead of answering all of them slowly |
| [0037](0037-idempotency-key-on-hire.md) | Idempotency-Key on the hire |
| [0035](0035-kafka-replication-and-broker-failure.md) | Kafka replication: three copies, two in sync, and what a broker failure costs |
| [0036](0036-redis-failure-and-sentinel.md) | Redis is an accelerator: fail fast, stop asking, and fail over with Sentinel |
| [0032](0032-orchestrated-hire-saga.md) | The hire is an orchestrated saga with a deadline and compensations |
| [0033](0033-sharding-the-audit-log-with-citus.md) | Sharding the audit log: measured with Citus, not adopted |
| [0034](0034-employee-card-read-model.md) | The employee card: a read model with a way to see your own write |

Not yet written up, though each decision is already live in the codebase and explained
in its own commit messages: gRPC for internal calls, local JWT validation via JWKS,
OpenIddict as the OIDC provider, pgvector + local Ollama for semantic search.
