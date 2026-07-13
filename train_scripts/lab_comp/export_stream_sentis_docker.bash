#!/usr/bin/env bash
# ONNX → .sentis через Docker (Ubuntu 22.04).
# Personal license НЕ умеет -batchmode/-nographics → нужен DISPLAY с хоста (:1 / AnyDesk).
#
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
IMAGE="${FOREST_SENTIS_DOCKER_IMAGE:-forest_sentis_export:22.04}"
ONNX_DIR="${FOREST_STREAM_ONNX_DIR:?}"
SENTIS_DIR="${FOREST_STREAM_SENTIS_DIR:?}"
UNITY_BIN="${UNITY_EDITOR:?}"
DISPLAY_VAR="${DISPLAY:-:1}"

if ! command -v docker >/dev/null 2>&1; then
  echo "[export_sentis_docker] ERROR: docker не найден" >&2
  exit 1
fi

if ! docker image inspect "${IMAGE}" >/dev/null 2>&1; then
  echo "[export_sentis_docker] образ ${IMAGE} нет — собираю..."
  bash "${ROOT}/train_scripts/lab_comp/build_sentis_docker.bash"
fi

UNITY_BIN="$(readlink -f "${UNITY_BIN}")"
UNITY_ROOT="$(cd "$(dirname "${UNITY_BIN}")/../.." && pwd)"
ONNX_ABS="$(cd "${ONNX_DIR}" && pwd)"
SENTIS_ABS="$(mkdir -p "${SENTIS_DIR}" && cd "${SENTIS_DIR}" && pwd)"
PROJECT_ABS="$(cd "${ROOT}" && pwd)"

HOST_UID="$(id -u)"
HOST_GID="$(id -g)"
HOST_HOME="${HOME}"

mkdir -p \
  "${HOST_HOME}/.cache/unity3d" \
  "${HOST_HOME}/.config/unity3d" \
  "${HOST_HOME}/.local/share/unity3d"

# Разрешить контейнеру рисовать на host X (DISPLAY с VNC/AnyDesk).
if command -v xhost >/dev/null 2>&1; then
  xhost +SI:localuser:"$(id -un)" >/dev/null 2>&1 || xhost +local: >/dev/null 2>&1 || true
fi

XAUTH_HOST="${XAUTHORITY:-${HOST_HOME}/.Xauthority}"
MOUNTS=(
  -v "${UNITY_ROOT}:${UNITY_ROOT}:ro"
  -v "${PROJECT_ABS}:${PROJECT_ABS}"
  -v "${ONNX_ABS}:${ONNX_ABS}"
  -v "${SENTIS_ABS}:${SENTIS_ABS}"
  -v "${HOST_HOME}/.cache/unity3d:${HOST_HOME}/.cache/unity3d"
  -v "${HOST_HOME}/.config/unity3d:${HOST_HOME}/.config/unity3d"
  -v "${HOST_HOME}/.local/share/unity3d:${HOST_HOME}/.local/share/unity3d"
  -v /tmp/.X11-unix:/tmp/.X11-unix:ro
)

[ -d "${HOST_HOME}/.config/unityhub" ] \
  && MOUNTS+=(-v "${HOST_HOME}/.config/unityhub:${HOST_HOME}/.config/unityhub")
[ -f "${XAUTH_HOST}" ] \
  && MOUNTS+=(-v "${XAUTH_HOST}:${XAUTH_HOST}:ro")

ENV_ARGS=(
  -e "HOME=${HOST_HOME}"
  -e "USER=$(id -un)"
  -e "DISPLAY=${DISPLAY_VAR}"
  -e "FOREST_STREAM_ONNX_DIR=${ONNX_ABS}"
  -e "FOREST_STREAM_SENTIS_DIR=${SENTIS_ABS}"
)
[ -f "${XAUTH_HOST}" ] && ENV_ARGS+=(-e "XAUTHORITY=${XAUTH_HOST}")

# Опционально: Pro/serial (если есть).
LICENSE_ARGS=()
if [ -n "${UNITY_SERIAL:-}" ] && [ -n "${UNITY_USERNAME:-}" ] && [ -n "${UNITY_PASSWORD:-}" ]; then
  LICENSE_ARGS+=(-username "${UNITY_USERNAME}" -password "${UNITY_PASSWORD}" -serial "${UNITY_SERIAL}")
fi

echo "[export_sentis_docker] image=${IMAGE} uid=${HOST_UID} DISPLAY=${DISPLAY_VAR}"
echo "[export_sentis_docker] Unity=${UNITY_BIN} (GUI license, без -nographics)"
echo "[export_sentis_docker] onnx=${ONNX_ABS} -> sentis=${SENTIS_ABS}"

# Важно: НЕ -nographics и НЕ -batchmode — иначе Personal требует com.unity.editor.headless.
set +e
docker run --rm \
  --user "${HOST_UID}:${HOST_GID}" \
  --network host \
  "${ENV_ARGS[@]}" \
  "${MOUNTS[@]}" \
  -w "${PROJECT_ABS}" \
  "${IMAGE}" \
  "${UNITY_BIN}" \
  -projectPath "${PROJECT_ABS}" \
  -executeMethod ForestStreamSentisExporter.ExportFromEnv \
  -logFile - \
  -quit \
  "${LICENSE_ARGS[@]}"
rc=$?
set -e

if ls "${SENTIS_ABS}"/*.sentis >/dev/null 2>&1; then
  echo "[export_sentis_docker] OK: $(ls "${SENTIS_ABS}"/*.sentis | wc -l) .sentis"
  exit 0
fi

echo "[export_sentis_docker] Unity exit=${rc}, .sentis нет" >&2
echo "[export_sentis_docker] Проверь: DISPLAY=${DISPLAY_VAR} жив (AnyDesk/VNC), Unity Hub → Personal активирован" >&2
exit 1
