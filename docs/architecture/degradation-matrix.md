# Degradation matrix: what still works when one thing is down

One component down at a time. Each row says what stops, what keeps working, and **what backs the claim**: a named test, a
drill with a number in an ADR, or "not verified" when nobody has looked. A row that says "not verified" is a gap, not a
promise. The point of the table is to keep the two apart.

How to read "evidence": a test name is in the repository and runs in CI unless marked otherwise; "drill" is a manual run
recorded in the ADR named, reproducible with the script named.

| Down | Stops | Keeps working | Evidence |
|---|---|---|---|
| **Kafka** (the whole broker) | Delivery of events: Audit, Notification, Search indexing, Rewards' welcome bonus, the onboarding saga all wait | Every HTTP write (the event sits in the outbox, committed with the change); reads; logins | `ChaosTests.A_frozen_broker_delays_bonuses_but_loses_and_duplicates_none`. Broker failure on a cluster: drill, ADR 0035 |
| **One Kafka broker of three** | Nothing, after a pause of about a second while a leader is elected | Publishing and consuming | Drill, ADR 0035 (`scripts/kafka-ha-drill.sh`): 4000 of 4000 arrived, none doubled |
| **Two of three Kafka brokers** | Publishing (writes are refused rather than kept on one copy); the outbox accumulates | Everything that does not need a new event | Drill, ADR 0035 (`shrink safe`): 0 acknowledged writes in the phase, none lost |
| **A service's Postgres** | That service's API and its consumers. Other services are untouched (no cross-schema reads) | Everything outside that service | `ChaosTests.A_frozen_database_never_loses_or_duplicates_a_bonus`; health checks report it. Other services' behaviour while one DB is down: not verified as a whole-stack test |
| **Schema registry** | Publishing Avro events: rows stay pending, nothing is sent | HTTP writes; consuming of messages whose schemas are cached | `A_registry_that_is_down_sends_nothing_and_leaves_the_row_pending`; `A_row_written_while_the_registry_was_down_is_encoded_from_its_json_at_publish_time` |
| **Redis** | DirectoryService's shared cache (reads go to Postgres); SignalR delivery between NotificationService instances | All reads and writes; delivery to clients on the same instance | `RedisDownTests` (reads answer from the source, then cost nothing once the breaker opens), `CircuitBreakingDistributedCacheTests`; failover: drill, ADR 0036. SignalR backplane during an outage: not verified |
| **Elasticsearch** | Keyword search (503 `search.index.unavailable`) and indexing of new events (consumer retries, then dead-letters) | Hybrid search answers from the semantic half and says so (`mode: semantic`); everything outside search | `With_the_keyword_index_down_keyword_mode_is_unavailable_and_hybrid_answers_from_the_semantic_side`, `With_both_halves_down_hybrid_is_unavailable`. Indexing while it is down: not verified |
| **Ollama (embedding model)** | New embeddings: rows stay pending; semantic search for new text | Keyword search; hybrid answers from keyword and says so | `A_model_that_is_down_leaves_rows_pending_without_failing_and_the_next_pass_completes_them`, `When_the_model_is_down_hybrid_answers_from_keyword_and_says_so_and_semantic_is_unavailable` |
| **DirectoryService** | Hiring and transferring (503 `employee.directory.unavailable`, nothing written) | Everything else in EmployeeService; reading employees, terminating, the card | `Hire_while_the_directory_cannot_be_used_is_refused_and_writes_nothing` (unavailable and unauthorised), `Transfer_while_the_directory_is_down_is_refused_and_leaves_the_employee_where_they_were` |
| **AuthService** | New logins, token refresh, account provisioning | Services validate tokens locally against cached keys, so existing sessions keep working until their tokens expire; reads and writes by signed-in users. A hire made now has no account within the 2-minute deadline, so the **onboarding saga undoes it**; if the account appears late, it is locked | Saga: `HireSagaCoordinatorTests` (ADR 0032), Auth: `EmployeeEventsConsumerTests` compensation tests. Token validation with Auth down: not verified |
| **RewardsService** | Grants; the welcome bonus. A hire made now does not get its bonus within the deadline, so the **saga undoes it** | Everything that is not rewards | Saga tests as above. Whether the hire should depend on the bonus at all is a decision to revisit (ADR 0032 "Consequences") |
| **NotificationService** | Delivery of notifications (they are created when it returns, from Kafka) | Everything else | Not verified |
| **AuditService** | The audit trail and the time machine fall behind | Everything else; the trail catches up from Kafka | Consumer idempotency: `AuditService.IntegrationTests` (message recorded once); behaviour of the stack while it is down: not verified |
| **Read replica** | Nothing: reads go back to the primary | All reads, a little slower | `ReadRoutingTests.A_replica_that_cannot_be_asked_is_not_waited_for` and the wait/fallback cases; ADR 0025 |
| **Have I Been Pwned API** | The password breach check | Registration and password change (the check is skipped, "fails open") | `An_unreachable_api_fails_open_rather_than_blocking_the_password` |
| **The language-model provider (assistant)** | The assistant | Everything else; repeated failures open a breaker that answers with a fixed message | `Repeated_server_errors_open_the_breaker_and_it_surfaces_as_the_fixed_message`, `Rate_limit_answers_do_not_open_the_breaker_for_everyone` |
| **nginx** (the only ingress) | All access from outside | Service-to-service traffic and event flow inside | Not verified (nothing in the platform runs more than one) |

## What this table shows

- **The write path is the strongest part.** Outbox plus idempotent consumers mean that Kafka, the registry, Redis, Elasticsearch,
  Ollama, Notification, Audit and Search can each be down without a user-visible write failing. Every row of that kind has a test or a drill.
- **The weakest coupling is the onboarding saga's deadline.** Because a hire only completes when both the account (Auth) and the
  bonus (Rewards) arrive within two minutes, an outage of either turns hires made during it into undone hires. That is the
  designed behaviour (ADR 0032) and it makes two otherwise independent services critical to hiring. It is the first thing to
  question if the deadline or the dependency on the bonus is wrong.
- **Gaps** ("not verified" above): the stack as a whole with one database down, Auth down for token validation, indexing while
  Elasticsearch is down, the SignalR backplane, nginx. They are the next things to test, in roughly that order of risk.
