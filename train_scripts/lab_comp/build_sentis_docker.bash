#!/usr/bin/env bash
# Собрать Docker-образ для onnx→sentis на lab_comp (один раз).
#   bash train_scripts/lab_comp/build_sentis_docker.bash
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
IMAGE="${FOREST_SENTIS_DOCKER_IMAGE:-forest_sentis_export:22.04}"
DOCKERFILE="${ROOT}/train_scripts/lab_comp/Dockerfile.sentis_export"

if ! command -v docker >/dev/null 2>&1; then
  echo "ERROR: docker не найден" >&2
  exit 1
fi

echo "[build_sentis_docker] building ${IMAGE} ..."
docker build -t "${IMAGE}" -f "${DOCKERFILE}" "${ROOT}/train_scripts/lab_comp"
echo "[build_sentis_docker] ready: ${IMAGE}"
echo "Дальше export сам подхватит Docker при GLIBC-ошибке."
