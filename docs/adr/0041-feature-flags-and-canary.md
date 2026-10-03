# 41. Feature flags with OpenFeature, and a canary track in the chart

## Status
Accepted. Implements the flag half of [0012](0012-reliability-as-a-process.md) and a smaller version of its canary half; builds on
[0040](0040-kubernetes-helm-chart.md).

## Context
0012 separated two questions. A canary answers "is the new code safe". A flag answers "should this user get the new behaviour".
Conflating them makes every rollback a redeploy. The platform had neither: a behaviour could only be turned off by changing code or
configuration and restarting, and a new version was either everywhere or nowhere.

## Decision
### Flags
- **OpenFeature, the vendor-neutral flag API** (the SDK, `OpenFeature` 2.14.1), with a provider of our own that reads flags from
  configuration (`FeatureFlags:<name>:Enabled` and `:Percentage`). Code asks `IFeatureFlags.IsEnabledAsync(flag, user, default)`;
  swapping the provider for a hosted flag service later changes no calling code.
- **A kill switch and a sticky percentage.** `Enabled: false` is off for everyone. With `Enabled: true` and a percentage under 100
  a user is in or out by a stable hash of the flag and the user, so a user keeps their answer, everyone already in stays in as
  the percentage rises, and two flags do not pick the same people. Someone who cannot be identified is placed on the safe (off) side
  of a partial rollout. A flag that is not configured takes the default the code gave it.
- **Changed without a deploy.** Flags are read from a JSON file watched while the service runs; in a cluster it is a ConfigMap
  (`feature-flags`, rendered from `featureFlags:` in the chart's values) mounted into every pod. Polling file watching is used
  because a ConfigMap is updated by swapping a symlink, which an inotify watch does not see.
- **First use: `search-hybrid-default`.** A search with no explicit mode is hybrid (keyword fused with the model's semantic results)
  by default. With the flag off it is keyword only: a switch for the one path that depends on the embedding model, and at a
  percentage a gradual rollout of it. A caller that names a mode is not affected. Unconfigured, the behaviour is what it was.

### Canary
- **A second Deployment of the same service on the `canary` track**, enabled per service in the chart
  (`canary: {enabled, replicas, tag}`), beside the stable one. The Service selects both tracks, so traffic divides in
  proportion to the number of pods: one canary pod beside nine stable ones is about a tenth. The stable Deployment, its HPA and
  rollout are unchanged, and the PodDisruptionBudget covers both.
- **That is as fine as it gets here.** Weighted routing per request, automatic analysis against the SLOs and automatic rollback
  (what 0012 proposed with Argo Rollouts) need a rollout controller or a mesh and are not built. The SLO burn-rate alerts
  ([docs/slo.md](../slo.md)) are what a person watches while a canary runs.

## Consequences
- **A flag is code that is always there.** Each one is a branch to test in both positions and to remove once the rollout is done;
  nothing here tracks that. The first flag has no planned removal because it is an operational switch, not a rollout.
- **The canary's traffic share depends on pod counts,** so it shifts when the HPA scales the stable track and cannot be pinned to
  a percentage.
- **The flag file is read by Search only.** The mount and the variable are in every pod, but only SearchService loads the file.
  Another service takes a flag by calling the same two lines.
- A flag file with a typo is a flag that silently takes its default; there is no validation or audit of who changed what
  (that is what a Git-tracked ConfigMap is for).

## What is and is not verified
Unit tests: a missing flag gives the caller's default; the kill switch overrides the percentage; 100% is on even for an
unidentified caller; a partial rollout puts an unidentified caller on the safe side; at 25% about a quarter of 10,000 users
are in (inside 23 to 27%); a user's answer is stable and everyone in at 20% is in at 40%; two flags at 10% overlap far less
than the same users would; a change to configuration reaches a running provider without a restart; the application interface
returns the right answer for a user inside and outside the line; an edited flag **file** changes the answer in a running
provider; a missing file is not an error. Integration tests: the flag off makes a search with no mode keyword-only, on or absent
makes it hybrid, and an explicit mode is unaffected; the flag is asked about the caller from the token. The chart renders and
validates (37 resources with a flag and a canary set; canary Deployment on its own track with its own image tag).

Not verified: a ConfigMap edit reaching a running pod (the file reload is tested with a plain file; the cluster that could have
shown the symlink case had already been removed); the canary taking traffic in proportion in a real cluster; the cost of the
polling file watcher; nothing was run against the whole platform. Not built: automated canary analysis and rollback.
