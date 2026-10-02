# Architecture map

Three views of the same system, from outside in: who uses it (C4 level 1), what runs and how it talks (level 2), how
the services relate as domains (DDD context map), and what actually happens when someone is hired (event storming).
Everything here was read from the code and configuration, not from memory; where something is a decision rather than a
fact, the ADR is linked.

## 1. System context (C4 level 1)

```mermaid
flowchart LR
    person["Person<br/>signs in with a password, passkey or Google"]
    admin["Admin / editor<br/>maintains the organization"]
    assistant["AI assistant user<br/>asks in plain language"]

    subgraph system["dsPortfolio"]
        platform["Org-management platform<br/>departments · people · rewards · notifications · search"]
    end

    llm[("Local model<br/>gpt-oss-20b via llama.cpp<br/>no external API")]
    embed[("Local embeddings<br/>Ollama · nomic-embed-text")]
    google["Google<br/>optional sign-in"]
    mail["Email sender<br/>confirmation, step-up codes"]

    person --> platform
    admin --> platform
    assistant --> platform
    platform -- "proposes changes, reads as the caller" --> llm
    platform --> embed
    platform --> google
    platform --> mail
```

## 2. Containers (C4 level 2)

nginx is the only entry. No service publishes its HTTP port to the host. Each service owns one Postgres schema and
reads nothing from another ([ADR 0001](../adr/0001-schema-per-service-shared-database.md)); services learn about each other's changes
only through events, or through a service's own API.

```mermaid
flowchart TB
    browser["Browser"] --> next["Next.js frontend + BFF<br/>holds OAuth tokens server-side"]
    next --> nginx["nginx — the only way in"]
    browser -. "WebSocket, 60 s ticket" .-> nginx

    subgraph services["Eight services — each owns one Postgres schema, none reads another's"]
        direction LR
        dir["Directory<br/>departments · positions · locations"]
        emp["Employee<br/>hire · transfer · terminate"]
        auth["Auth<br/>OpenIddict OIDC"]
        rew["Rewards<br/>currency ledger"]
        not["Notification<br/>feed + SignalR hub"]
        sea["Search<br/>keyword · semantic · hybrid"]
        aud["Audit<br/>event log · time machine"]
        mcp["McpServer<br/>tools + agent write path"]
    end

    nginx --> services
    emp -. "gRPC" .-> dir
    mcp -. "REST, as the caller" .-> services

    services <-- "outbox out, idempotent consumers in<br/>(table below)" --> kafka(["Kafka"])
    services --> pg[("Postgres<br/>schema per service")]
    sea --> es[("Elasticsearch")]
    sea --> ollama[("Ollama<br/>embeddings")]
    not --> redis[("Redis<br/>SignalR backplane")]
    dir --> redis
    mcp --> llm[("Local model<br/>llama.cpp")]
```

**Topics: who writes, who reads** (from each service's configuration):

| Topic | Written by | Read by |
|---|---|---|
| `directory.events` | DirectoryService | SearchService, AuditService |
| `employee.events` | EmployeeService | AuthService, RewardsService, NotificationService, SearchService, AuditService |
| `auth.events` | AuthService | EmployeeService, RewardsService, NotificationService, SearchService |
| `rewards.events` | RewardsService | NotificationService, SearchService |

Every write to a topic goes through a transactional outbox ([ADR 0002](../adr/0002-outbox-pattern-for-integration-events.md));
every read is an idempotent consumer with bounded retries and a dead-letter table.

## 3. Key components of one container: the hire path

The most interesting path crosses four services. Only the pieces involved are drawn.

```mermaid
flowchart LR
    subgraph EmployeeService
        hire["Hire handler"] --> eout[("employee schema<br/>employee + outbox row<br/>one transaction")]
        ecb["AuthEventsConsumer<br/>completes or fails provisioning"]
    end
    subgraph AuthService
        acons["EmployeeEventsConsumer<br/>creates the account"] --> aout[("auth schema<br/>account + outbox row")]
    end
    subgraph RewardsService
        rcons["WelcomeBonusConsumer<br/>learns employee to account,<br/>grants the bonus once"] --> rled[("rewards schema<br/>ledger + outbox row")]
    end
    subgraph NotificationService
        ncons["DomainEventsConsumer<br/>stores and pushes"] --> hub["NotificationsHub"]
    end

    eout -- "EmployeeHired" --> acons
    aout -- "AccountProvisioned / Failed" --> ecb
    eout -- "EmployeeHired" --> rcons
    aout -- "AccountProvisioned" --> rcons
    rled -- "CurrencyGranted" --> ncons
    aout --> ncons
```

## 4. Context map (DDD)

Each service is a bounded context with its own model of the word "employee".

| Context | Owns | Relationship to others |
|---|---|---|
| **Directory** | departments (tree), positions, locations | **Upstream.** Publishes the department and position facts; EmployeeService calls it over gRPC to check a department and position exist (customer-supplier). |
| **Employee** | the employee record and its lifecycle | Downstream of Directory (validates against it), **partner** of Auth in the hire saga: neither can finish without the other, so the saga is the contract ([ADR 0003](../adr/0003-choreography-saga-for-hire-employee.md)). |
| **Auth** | accounts, sessions, tokens | Consumes `EmployeeHired`; publishes `AccountProvisioned` / `Failed`. |
| **Rewards** | the currency ledger | Downstream of Employee and Auth; needs the employee-to-account mapping and builds its own from `AccountProvisioned`. |
| **Notification, Search, Audit** | feed, index, event log | **Conformist, open-ended consumers:** they take whatever the producers publish and never ask for changes. |
| **McpServer** | the assistant's tools and plan signing | **Anticorruption layer:** a model's idea of a department or position is never trusted; ids are resolved by the owning service ([ADR 0016](../adr/0016-agent-proposes-user-confirms.md), [0017](../adr/0017-approval-is-informed-and-bounded.md)). |

The one pattern that holds everywhere: **a consumer keeps its own local copy of the event shape** instead of sharing
a contract library, so a producer can add a field without a coordinated release, and a breaking change fails a contract
test instead of production. The weakness is that nothing yet *versions* those contracts or checks them across services;
that is the next item on the foundation roadmap.

## 5. Event storming: hiring someone

Read left to right as time; orange are events, blue are commands, the policy lines say "whenever X happens, do Y".

```mermaid
flowchart LR
    c1["Command<br/>Hire employee<br/>(editor)"]:::cmd --> e1["EmployeeHired"]:::evt
    e1 --> p1{{"Policy: when hired,<br/>provision a login"}}:::pol --> c2["Command<br/>Create account"]:::cmd
    c2 --> e2["AccountProvisioned"]:::evt
    c2 --> e2f["AccountProvisioningFailed"]:::evt
    e2 --> p2{{"Policy: when provisioned,<br/>mark the employee active"}}:::pol
    e2f --> p3{{"Policy: when failed,<br/>mark the employee failed"}}:::pol
    e1 --> p4{{"Policy: when hired and the<br/>account is known, grant once"}}:::pol --> c3["Command<br/>Grant welcome bonus"]:::cmd
    c3 --> e3["CurrencyGranted"]:::evt
    e2 --> p5{{"Policy: tell the person"}}:::pol --> n1["Notification stored + pushed"]:::evt
    e3 --> p5
    e1 --> p6{{"Policy: index and record"}}:::pol --> n2["Search document + audit entry"]:::evt

    classDef evt fill:#f59e0b,stroke:#92400e,color:#111
    classDef cmd fill:#60a5fa,stroke:#1e3a8a,color:#111
    classDef pol fill:#e5e7eb,stroke:#6b7280,color:#111
```

**What the storm shows, and what it does not.**
- The failure path (`AccountProvisioningFailed`) is a first-class event with its own reaction, not an exception.
- The welcome bonus is triggered from two events, because Rewards needs both the employee and the account it belongs
  to; it grants **once** per employee, which a concurrency test checks.
- The storm assumes nothing times out. There is no compensation that removes the employee if provisioning fails, only
  a marked state: [ADR 0003](../adr/0003-choreography-saga-for-hire-employee.md) records why a two-step saga stays
  choreographed, and says it would be reconsidered if a third participant joined.
