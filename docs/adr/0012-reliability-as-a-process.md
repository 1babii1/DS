# 12. Reliability as a process: SLOs, canary delivery, chaos testing (not yet implemented)

## Status
Proposed - design only. Nothing here is live: there are no SLOs defined, no canary
delivery pipeline, and no chaos testing today. Wave 3 of the reliability-roadmap planning
artifact. Unlike Waves 1-2, this one needs no new stateful infrastructure to start - it
needs a Kubernetes control plane for the delivery half (Argo CD/Rollouts), but the
measurement half (SLOs, burn-rate alerting) can start on the current OTel/Prometheus stack
today, and is the cheapest wave in the whole plan to begin.

## Context
Observability signals are already collected correctly (OTel collector, Tempo, Loki,
Prometheus, Grafana) - what's missing is turning them into decisions. There is no defined
answer today to "is the platform reliable enough right now," no automated gate on a
deploy, and no practice of deliberately breaking something to find out what actually
happens versus what's assumed to happen.

## Trigger
- The first incident that users notice before monitoring does - a concrete sign that
  today's dashboards answer "is something wrong" too late to matter.
- The first deploy that gets rolled back by hand after already causing user-visible
  errors, which a canary gate would have caught before it fully rolled out.

## Decision

**SLOs on user journeys, not per-service uptime.** Four candidate journeys already exist
end-to-end in this platform: sign-in, hire, currency grant, search. An SLO here is a
success-rate + latency target measured on the request path a user actually takes, not on
whether a container process is alive.

**Error budget drives priority, not a dashboard color.** A defined SLO (e.g. 99.9% over 30
days, roughly 43 minutes of budget a month) gives an explicit answer to "can we ship this
feature or should we stabilize first" - the budget being spent is the signal, not a
judgment call made from a gut feeling about how things have felt lately.

**Burn-rate alerts, not static thresholds.** A fast burn (the budget would be exhausted in
hours at the current error rate) pages someone now; a slow burn opens a ticket. Both read
from the same SLI the SLO is defined on, so there's exactly one definition of "bad" per
journey, not one per alert rule.

**Argo CD (GitOps) + Argo Rollouts for canary delivery, once Kubernetes exists (Wave 1).**
A canary step's analysis queries the same SLIs the SLOs are built on and rolls back
automatically if they regress - this is why SLOs have to exist before canary delivery is
useful, not the other way around. Argo is a CNCF graduated project, which matters given
the platform's own bias (see Wave 1/2's license reasoning) toward openly-governed
infrastructure over single-vendor tooling.

**OpenFeature for flags, separating "deployed" from "enabled."** A canary answers "is the
new code safe"; a feature flag answers "should this user see the new behavior" -
conflating them means every rollback is a redeploy instead of a flag flip.

**Chaos testing starts on staging, with a written steady-state hypothesis per experiment**
(e.g. "SLOs hold with the Postgres primary killed and CloudNativePG's failover completing
within its reported ~5-10s") - not a vague "let's see what breaks." LitmusChaos or Chaos
Mesh, whichever has the lighter Kubernetes footprint once Wave 1 exists to run either on.
Game days (scheduled, announced chaos runs with the whole team watching) come after
individual experiments are trusted enough to not need constant babysitting.

## What can start now, independent of every other wave
- Defining the four SLOs and instrumenting the SLIs they're built on - pure measurement,
  no new infrastructure, works against the current OTel/Prometheus stack.
- Burn-rate alert rules on those SLIs.
- A written incident/postquartem template that references the SLO/error-budget language,
  so the first real incident already has the vocabulary instead of inventing it live.

## Consequences
- SLOs that are set before anyone has looked at real traffic patterns will likely need
  revising once real data exists - that's expected, not a sign the first attempt was wrong.
- Canary analysis and chaos testing both depend on Wave 1 (Kubernetes) for where they
  actually run; the measurement half of this wave does not, and should not wait for it.
- Nothing here is implemented yet - no SLO is currently defined, no burn-rate alert exists,
  no canary pipeline exists, no chaos experiment has been run.
