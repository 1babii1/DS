# Running the platform on Kubernetes

The chart is `deploy/helm/platform`: the seven .NET services (Directory, Auth, Employee, Audit, Rewards, Notification, Search) as
Deployments with Services, HorizontalPodAutoscalers, PodDisruptionBudgets, and a migration Job per service that runs before each
install and upgrade. Postgres, Kafka, the schema registry, Redis and Elasticsearch are **not** in it: they are prerequisites.
Decisions and measurements: [ADR 0040](../adr/0040-kubernetes-helm-chart.md).

## What the cluster must provide first

1. **A Postgres database `platform` with the schemas already created**: run `docker/postgres/init-databases.sql` against it (with
   CloudNativePG, as `postInitSQL`). This is not optional. The migration tool creates its history table before the service's own
   schema exists; on a database without the schemas it lands in `public`, and from the second migration run on it is shadowed
   and the whole history replays and fails (`relation "employees" already exists`).
2. **Secrets that hold connection strings and credentials**, created however the cluster creates secrets. The chart never
   contains a value. Per service: a Secret listed in `envFromSecrets` whose keys are environment variable names
   (`ConnectionStrings__EmployeeServiceDb`, `Kafka__SaslPassword`, ...), and for the migration Job a Secret with the *direct* (not
   pooled) connection string, named in `migrations.connectionSecret`.
3. Reachable Kafka (`Kafka__BootstrapServers` and the topic settings of ADR 0035), schema registry (`SchemaRegistry__Url`), and the rest
   of what each service's `appsettings.Docker.json` points at, set through the `env` map of the service in the values file.

## Render and check without a cluster

    helm lint deploy/helm/platform
    helm template rel deploy/helm/platform | kubeconform -strict -summary

## Try one service on a local kind cluster

    kind create cluster --name platform            # one control plane, two workers
    # images are built locally and loaded into the nodes (kind load docker-image; with a snap-packaged Docker use docker save, docker cp and ctr images import)
    kubectl create namespace platform
    kubectl -n platform create secret generic postgres --from-literal=POSTGRES_PASSWORD=... --from-literal=POSTGRES_DB=platform
    kubectl -n platform create configmap postgres-init --from-file=init-databases.sql=docker/postgres/init-databases.sql
    kubectl -n platform apply -f deploy/helm/kind-postgres.yaml
    kubectl -n platform create secret generic employee-env --from-literal='ConnectionStrings__EmployeeServiceDb=...'
    kubectl -n platform create secret generic employee-migration --from-literal='connection=...'
    helm upgrade --install emp deploy/helm/platform -n platform -f deploy/helm/examples/kind-employee.yaml --wait

To roll out a change, upgrade with any changed value (for example an extra variable in the service's `env` map) and watch
`/health/live` through the Service meanwhile.

## Migrations and rollouts

The migration Job runs **before** the new pods start, so the schema is always at least as new as the code about to run, but the old
pods are still serving while it does. Every migration must therefore work with the previous version of the code: expand, then contract
([zero-downtime-migrations.md](zero-downtime-migrations.md)). A Job that fails stops the upgrade; its pod is removed with it, so
reproduce it with a pod running the same image and arguments to read the error.
