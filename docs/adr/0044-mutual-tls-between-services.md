# 44. Mutual TLS on the internal gRPC call

## Status
Accepted for the one internal call that exists, EmployeeService to DirectoryService (gRPC). Built as an opt-in mechanism and
proven with real handshakes; **not turned on in the default stack** and not run against the live stack. Related:
[0010](0010-kubernetes-migration-target-architecture.md) (a service mesh was the other candidate), [0040](0040-kubernetes-helm-chart.md).

## Context
Inside the compose network services talk in cleartext. A token proves *which user* a request is for; nothing proves *which service*
is calling, and the gRPC port of DirectoryService is reachable by anything that can reach the network. Mutual TLS gives each side
a certificate signed by the platform's own authority: the callee knows the caller is a member of the platform, the caller knows it
is talking to the real callee, and the traffic is encrypted.

Two ways to get it: a service mesh (the sidecar does the TLS and rotates certificates, nothing in the application changes) or the
application doing it itself. The mesh is the better answer on Kubernetes and is a large piece of infrastructure to adopt for one
call; the platform has exactly one internal service-to-service call.

## Decision
- **The application does it, with a small shared piece** (`Shared.Security.MutualTls`), configured under `MutualTls:` (`Enabled`,
  `CertificatePath`, `KeyPath`, `CaPath`, PEM files), **off unless enabled**.
  - Server side: the listener uses HTTPS with the service's certificate and **requires** a client certificate that chains to the
    platform authority.
  - Client side: the handler presents the service's certificate and accepts the server only if its certificate chains to the
    authority **and its name matches the host** (the certificate for DirectoryService carries `directory_service` as a name).
  - **Trust is the platform authority alone.** The machine's trust store is not consulted: a certificate from a public authority is
    not a member of this platform.
- **Applied to the gRPC port only.** The REST port arrives through nginx with a user's token and stays as it is.
- **A script for development certificates** (`scripts/mtls-certs.sh`, openssl) and an override (`docker-compose.mtls.yml`) that
  turns it on for the two services. Nothing in the default `docker compose up` changes.

## What was verified
Real TLS handshakes in `MutualTlsTests`, against a Kestrel server that requires the certificate:

| Client | Result |
|---|---|
| Certificate signed by the platform authority | Request served; the server sees the client's identity (`CN=employee`) |
| Trusts the server, presents no certificate | Refused at the handshake |
| Certificate signed by another authority | Refused |
| Right authority, expired certificate | Refused |
| Good certificate, but trusts only another authority (so it distrusts this server) | The client refuses the server |

Plus: a certificate is judged by the platform authority alone, and **certificates made by the openssl script (not by .NET) work in a
real handshake**, which checks the key encoding and the extensions a deployment is actually given. Mutation-checked: accepting any
client certificate fails the "another authority" and "expired" cases; skipping the server check fails the "distrusts this server" case.

## Consequences
- **Certificates expire and nothing renews them here.** The script issues 30-day certificates; rotating them is a restart with new
  files. A real deployment needs cert-manager, a mesh, or a secret store that issues short-lived certificates, which is the
  argument for a mesh once there is more than one call to protect.
- **Revocation is not checked** (the chain is built with revocation off): a stolen certificate is good until it expires.
- **The authority's key is next to the certificates in the development script,** deliberately; in a real deployment it is the most
  protected secret in the platform.
- A client certificate says "a platform service", not "which one": any certificate from the authority is accepted. Allowing
  only EmployeeService to call DirectoryService would need the server to check the client's name, which it does not.

## What is and is not verified
**Run end to end on the stack's network** (`scripts/mtls-live-drill.sh`: a second DirectoryService and two second EmployeeServices built
from this code, beside the stack's own, with the real gRPC client, its retry and circuit-breaker handlers, and the real HTTP/2 port):

| | Result |
|---|---|
| gRPC port, no client certificate | Refused at the handshake |
| gRPC port, certificate of another authority | Refused |
| gRPC port, certificate of the platform | Reaches the application (it answers 404 for a path it does not serve) |
| Hire through Employee with mutual TLS on both sides | **200**, the employee created; Employee asked Directory over gRPC to validate the assignment |
| Hire through an Employee speaking plain http to the same Directory | **503** `employee.directory.unavailable`, nothing created |

EmployeeService ran on a database of its own and with no Kafka, so its hires did not reach the real stack; the three reference
rows the hire chain needs (location, department, position) were added to the development database, as the k6 hire chain does.

**A thing found on the way, since explained:** `./efbundle` of the EmployeeService image, run as `docker run image ./efbundle ...`, exited 1 and
applied nothing. The image's `ENTRYPOINT` is `dotnet EmployeeService.Web.dll`, so that command started the web host with a stray argument, and
the host's outbox publisher aborted on "SchemaRegistry:Url must be set". It was a wrong invocation, not a defect: with `--entrypoint ./efbundle`
the same image applies all migrations to an empty database and exits 0 (7 tables, checked), and a second run says it is up to date. Compose
overrides `entrypoint` for its migration container and Kubernetes did the same, which is why those worked. The drill still applies the migrations
as SQL, which is fine but no longer needed for this reason.

**The other callers, looked at and left alone.** The gRPC call was "the one internal call that exists" only in the sense of service to
service over gRPC. McpServer also calls EmployeeService, DirectoryService, SearchService, AuditService and RewardsService over REST,
forwarding the user's token (six typed clients in `McpServer/Program.cs`). Mutual TLS on that path is not the same small step:
- The REST port is the one nginx proxies to, in cleartext, for every user request. Requiring a client certificate there would reject
  nginx unless nginx also presented one to every upstream; the gRPC port was separate, which is why it could be done alone.
- So it needs **a second listener per service** (an internal HTTPS port that requires the certificate, used by McpServer) or nginx
  made a mutual-TLS client for all upstreams. Both are changes to every service's startup and to the ingress, and both add six
  certificates to rotate by hand with the script above.
- The client side would be small (the same handler on each typed client). The cost is entirely on the servers and the ingress.
- What McpServer sends is already the user's token, checked by each service; the certificate would add "the caller is a platform
  member", which matters if the network is hostile. In the compose network it is not, and on Kubernetes this is what a mesh does for
  all of it without application code (0010). **Decision: not built.** If the mesh is adopted, it covers these calls, the gRPC call,
  Kafka clients and the database connection alike; doing it per call in the application is the wrong place to grow.

Not verified: NotificationService, the MCP server and the other callers, which are not covered; certificate rotation; revocation;
the compose override file itself (the drill starts containers with the same settings but not through it).
