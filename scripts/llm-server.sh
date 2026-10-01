#!/usr/bin/env bash
# Runs the local language model the assistant talks to: gpt-oss-20b through llama.cpp's server, on the integrated GPU
# via Vulkan (/dev/dri). It is a separate container, not part of docker-compose, because it needs a GPU device and
# ~12 GB of weights that do not belong in the default stack. The frontend reaches it on http://localhost:8090/
# (LLM_BASE_URL in frontend/.env.example); when it is not running the assistant page says so and nothing else is affected.
#
# Usage: scripts/llm-server.sh [start|stop|status]
#
# The weights are downloaded once into .llm-models/ (gitignored). Everything stays on this machine: no API key, no
# external model service.
set -euo pipefail

NAME="${LLM_CONTAINER:-ds_llm}"
PORT="${LLM_PORT:-8090}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODELS="$HERE/.llm-models"
FILE="gpt-oss-20b-mxfp4.gguf"
URL="https://huggingface.co/ggml-org/gpt-oss-20b-GGUF/resolve/main/gpt-oss-20b-MXFP4.gguf"
IMAGE="ghcr.io/ggml-org/llama.cpp:server-vulkan"

start() {
  if [[ ! -e /dev/dri ]]; then
    echo "No /dev/dri: this needs a GPU device (the model is run on the integrated GPU through Vulkan)." >&2
    exit 1
  fi

  mkdir -p "$MODELS"
  if [[ ! -s "$MODELS/$FILE" ]]; then
    echo "Downloading the model weights (about 12 GB, once) into $MODELS ..."
    curl -L -C - -o "$MODELS/$FILE" "$URL"
  fi

  if ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
    # A stale ghcr.io login makes a public pull fail with "denied"; logging out is harmless.
    docker pull "$IMAGE" || { docker logout ghcr.io >/dev/null 2>&1 || true; docker pull "$IMAGE"; }
  fi

  docker rm -f "$NAME" >/dev/null 2>&1 || true

  # The model directory must be one the docker daemon can see, which is why the weights live inside the repository.
  docker run -d --name "$NAME" --device /dev/dri \
    -p "127.0.0.1:${PORT}:8080" \
    -v "$MODELS:/models" \
    "$IMAGE" \
    -m "/models/$FILE" -ngl 99 -c 8192 -fa on --jinja --host 0.0.0.0 --port 8080 >/dev/null

  echo "Loading the model (about 20 seconds) ..."
  for _ in $(seq 1 90); do
    if curl -sf "http://127.0.0.1:${PORT}/health" >/dev/null 2>&1; then
      echo "The model is up on http://localhost:${PORT}/ (container $NAME)."
      return 0
    fi
    sleep 2
  done

  echo "The model did not become ready; see: docker logs $NAME" >&2
  exit 1
}

case "${1:-start}" in
  start) start ;;
  stop) docker rm -f "$NAME" >/dev/null 2>&1 && echo "Stopped $NAME." || echo "$NAME was not running." ;;
  status)
    if curl -sf "http://127.0.0.1:${PORT}/health" >/dev/null 2>&1; then
      echo "The model is up on http://localhost:${PORT}/."
    else
      echo "The model is not running (scripts/llm-server.sh start)."
      exit 1
    fi
    ;;
  *) echo "Usage: $0 [start|stop|status]" >&2; exit 2 ;;
esac
