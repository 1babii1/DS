# 40. Kubernetes: a hand-written Helm chart, tried on a real cluster

## Status
Accepted. Updates [0010](0010-kubernetes-migration-target-architecture.md): that ADR proposed generating the Kubernetes manifests
from an Aspire AppHost and listed the infrastructure choices (CloudNativePG, PgBouncer, Valkey). It was design only. The chart
described here is the first of it that exists, and it departs from 0010 on one point (below).

## Context
0010 set a trigger for adopting Kubernetes ("a service needs a second replica for real") and a target shape. The reliability
work since (a read replica, PgBouncer, load shedding, idempotent writes, outbox delivery that survives a broker) was done so that a
second replica of anything would be safe. What was missing was evidence that the services behave on a scheduler: probes that
mean something, a rollout that drops no request, a node drain that does not take everything down.

## Decision
- **A hand-written Helm chart (`deploy/helm/platform`), not one generated from an Aspire AppHost.** There is no AppHost in the
  repository, and writing one only to generate the chart would add a second description of the topology (the first being
  docker-compose) for the sake of a tool; the chart is short enough to read. If an AppHost appears for other reasons, generating
  the chart from it is still possible; this removes none of that.
- **One template, seven services:** a `defaults` block and one entry per service (image, port, ingress prefixes, migration image).
  A service can be left out per release.
- **Probes mean three different things.** Startup and liveness use `/health/live` (the process answers; it checks no
  dependency, because restarting a service does not bring a database back). Readiness uses `/health/ready`. Rollouts are
  `maxUnavailable: 0, maxSurge: 1` with a 5 s `preStop` sleep so traffic stops before the process is told to.
- **HPA (CPU 70%, 2 to 6) and PodDisruptionBudget (`minAvailable: 1`)** per service; a topology spread over nodes as a
  preference. The HPA is not given a fixed replica count, which an upgrade would otherwise reset.
- **Migrations are a Helm hook Job** (pre-install, pre-upgrade) running the `efbundle` of each service's image, with the
  connection string from a Secret. Secrets are never in the chart; the chart takes the names of Secrets that already exist.
- **Containers run as the image's unprivileged user (1654)**, with all capabilities dropped, no privilege escalation, and an
  `emptyDir` for `/tmp`.

## What was found by running it
Rendered 35 resources validate against the Kubernetes schemas (`kubeconform -strict`). Then EmployeeService on a three-node
kind cluster, with a Postgres of its own:

1. **Kafka was part of readiness, and that would have been an outage.** `/health/ready` included a Kafka check that reported
   unhealthy. Under Kubernetes a failed readiness probe removes the pod from the Service, so a broker going away would have taken
   every instance of six services out of rotation, although no HTTP request of theirs needs Kafka (writes go to the outbox). In
   docker-compose the same check only labelled a container, so it had never mattered. The pods stayed 0/1 ready until this was
   changed. The check now reports **degraded**, which still answers 200 and still appears in the report, and it gives up in 2 s
   (it took 3, the probe's own timeout). Two tests: the check's status and time, and the ready endpoint answering 200 with Kafka down.
   This also makes true what the degradation matrix says about Kafka.
2. **The migrations depended on schemas that docker-compose creates for them.** On a database without them, the first run put the
   history table in `public`; the second run, with the schema now present, read an empty history from it, started from the first
   migration and failed. Compose never showed it because its Postgres runs `init-databases.sql` first. The fix is a prerequisite,
   not code: create the schemas first (the runbook says so; the kind Postgres mounts the same script).
3. **No request was lost during a rolling update.** 1,984 requests to `/health/live` through the Service while the Deployment was
   replaced (a migration Job ran first and succeeded): 0 failed.
4. **The budget held a drain.** With both replicas on the only schedulable node, draining it evicted one and was refused eight
   times for the other ("would violate the pod's disruption budget") until the timeout; a replica stayed up. A first drain of one
   of two nodes moved the pods without a gap.
5. **Non-root worked** on the existing image (user 1654, `/tmp` as an `emptyDir`).

## Consequences
- The chart does not install the infrastructure. Running the whole platform needs Postgres (with the schemas), Kafka, the schema
  registry and the other prerequisites provided and their addresses set through each service's `env` map.
- Because readiness no longer fails on Kafka, nothing signals "this instance cannot deliver events" to the scheduler; the
  degraded status is in the report and the outbox backlog is the real signal (not alerted on).
- The image still ships `curl`, which only docker-compose needs.

## What is and is not verified
Run: lint, schema validation, EmployeeService alone on kind (install with the migration hook, a second install with it, three
rollouts, the zero-failure poll, the node drain with and without the budget), and the Kafka-health tests.

**The HPA, run later** on a kind cluster with a metrics server (with `--kubelet-insecure-tls`, as kind needs): six load pods hitting
`/health/live` took the two EmployeeService pods to 312% of their 100m CPU request and the autoscaler went from 2 to 4 replicas
(its maximum) within 90 s. After the load was removed it stayed at 4 for the 300 s stabilisation window and went back to 2 at
about 6 minutes: up fast, down slowly, as configured. The small CPU request is why a handful of curl loops was enough; it is a
guess, not a measurement of the service.

Not verified: the other six
services (they share the template and nothing else was run); the whole platform in a cluster; the ingress; a real Postgres
operator and its failover; the migration Job against a database that already holds the history of a docker-compose installation;
resource requests and limits (they are guesses).
