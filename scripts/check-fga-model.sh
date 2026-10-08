#!/usr/bin/env bash
# Checks the authorization model of ADR 0057 two ways: its own declarative tests (who may manage which department, in a small organisation) run by
# OpenFGA's CLI, and that the JSON the service embeds and loads into the store (EmployeeService.Web/Authorization/fga-model.json) says exactly what model.fga says.
#
#   scripts/check-fga-model.sh
set -euo pipefail
cd "$(dirname "$0")/.."
cli=openfga/cli:latest

docker run --rm -v "$PWD/deploy/fga":/m "$cli" model test --tests /m/tests.fga.yaml

want=$(docker run --rm -v "$PWD/deploy/fga":/m "$cli" model transform --file /m/model.fga | python3 -c 'import json,sys; print(json.dumps(json.load(sys.stdin), sort_keys=True))')
json=backend/EmployeeService/EmployeeService.Web/Authorization/fga-model.json
have=$(python3 -c 'import json,sys; print(json.dumps(json.load(open(sys.argv[1])), sort_keys=True))' "$json")
if [ "$want" != "$have" ]; then
  echo "$json does not match deploy/fga/model.fga; regenerate it:" >&2
  echo "  docker run --rm -v \"\$PWD/deploy/fga\":/m $cli model transform --file /m/model.fga | python3 -m json.tool --sort-keys > $json" >&2
  exit 1
fi
echo "the embedded model matches model.fga"
