#!/usr/bin/env bash
# Ollama на lab_comp через Docker (Ubuntu 18.04 / glibc 2.27 не тянет нативный бинарник).
#
#   bash train_scripts/lab_comp/run_ollama_docker.bash
#   bash train_scripts/lab_comp/run_ollama_docker.bash --pull qwen2.5:3b
set -euo pipefail

NAME="${OLLAMA_CONTAINER:-forest_ollama}"
PORT="${OLLAMA_PORT:-11434}"
DATA="${OLLAMA_DATA:-$HOME/ollama_data}"
MODEL="${1:-}"
DO_PULL=0

for arg in "$@"; do
  case "$arg" in
    --pull) DO_PULL=1 ;;
    --help|-h)
      echo "Usage: $0 [--pull] [model]"
      exit 0
      ;;
    *)
      if [ "$arg" != "--pull" ]; then MODEL="$arg"; fi
      ;;
  esac
done

MODEL="${MODEL:-${OLLAMA_MODEL:-qwen2.5:3b}}"

if ! command -v docker >/dev/null 2>&1; then
  echo "ERROR: docker не найден" >&2
  exit 1
fi

mkdir -p "${DATA}"

if ! docker ps --format '{{.Names}}' | grep -qx "${NAME}"; then
  if docker ps -a --format '{{.Names}}' | grep -qx "${NAME}"; then
    docker start "${NAME}"
  else
    docker pull ollama/ollama
    docker run -d --name "${NAME}" --restart unless-stopped \
      -p "127.0.0.1:${PORT}:11434" \
      -v "${DATA}:/root/.ollama" \
      ollama/ollama
  fi
fi

echo "[ollama] container=${NAME} http://127.0.0.1:${PORT}"
curl -sf "http://127.0.0.1:${PORT}/api/tags" >/dev/null
echo "[ollama] api ok"

if [ "${DO_PULL}" -eq 1 ] || [ "$#" -gt 0 ]; then
  echo "[ollama] pull ${MODEL}"
  docker exec "${NAME}" ollama pull "${MODEL}"
fi

docker exec "${NAME}" ollama list
