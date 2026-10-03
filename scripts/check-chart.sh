#!/usr/bin/env bash
# Lints the Helm chart and validates what it renders against the Kubernetes schemas, in the shapes people actually use it:
# the defaults, with a feature flag and a canary, and a service switched off. Needs helm and kubeconform on the PATH.
# Runs in CI (.github/workflows/ci.yml) and locally.
set -euo pipefail
cd "$(dirname "$0")/.."
chart=deploy/helm/platform

helm lint "$chart"

check() { # label, then the extra helm arguments
  local label="$1"
  shift
  local rendered
  rendered="$(mktemp --suffix=.yaml)"
  helm template rel "$chart" "$@" > "$rendered"
  local count
  count="$(grep -c '^kind:' "$rendered")"
  echo "== $label: $count resources"
  # A validator that is handed nothing it recognises reports success on zero resources; require that it looked at what was rendered.
  kubeconform -strict -summary "$rendered" | tee /dev/stderr | grep -q "Summary: $count resources found .*Invalid: 0, Errors: 0"
  rm -f "$rendered"
}

check "defaults"
check "flag and canary" \
  --set featureFlags.search-hybrid-default.enabled=false \
  --set featureFlags.search-hybrid-default.percentage=30 \
  --set services.employee.canary.enabled=true \
  --set services.employee.canary.replicas=1 \
  --set services.employee.canary.tag=v2
check "one service off, no autoscaler" --set enabled.search=false --set defaults.hpa.enabled=false
check "ingress on" --set ingress.enabled=true --set ingress.host=platform.example.test
check "the kind example" -f deploy/helm/examples/kind-employee.yaml
