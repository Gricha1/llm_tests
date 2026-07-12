#!/usr/bin/env bash
set -eu
set -o pipefail

# Jack + Lily + George: три политики одновременно.
# По умолчанию — билд stream_forest_survival_2_12_07_2026.
#
#   bash train_scripts/train_jack_lily_george.bash
#   bash train_scripts/train_jack_lily_george.bash stream_forest_survival_2_12_07_2026 1
#   RUN_ID=run_5 bash train_scripts/train_jack_lily_george.bash --resume
#
# Unity Editor (без билда):
#   USE_EDITOR=1 bash train_scripts/train_jack_lily_george.bash

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
cd "${ROOT}"

DEFAULT_BUILD="stream_forest_survival_2_12_07_2026"
CONFIG="custom_configs/Jack_Lily_George.yaml"
if [ ! -f "${CONFIG}" ]; then
  echo "ERROR: ${CONFIG} not found" >&2
  exit 1
fi

pick_free_run_id() {
  local i
  for ((i = 1; i <= 1000000; i++)); do
    local folder="results/run_${i}"
    if [ ! -d "${folder}" ]; then
      echo "run_${i}"
      return 0
    fi
  done
  echo "ERROR: no free results/run_N found" >&2
  exit 1
}

pick_free_port() {
  local start_port=5005
  local i
  for ((i = 0; i <= 1000; i++)); do
    local port=$((start_port + i))
    if ! netstat -tuln 2>/dev/null | grep -q ":${port} "; then
      echo "${port}"
      return 0
    fi
  done
  echo "ERROR: no free port found" >&2
  exit 1
}

RESUME=0
ARGS=()
for arg in "$@"; do
  if [ "${arg}" = "--resume" ]; then
    RESUME=1
  else
    ARGS+=("${arg}")
  fi
done
set -- "${ARGS[@]}"

if [ -n "${RUN_ID:-}" ]; then
  :
elif [ "${RESUME}" -eq 1 ]; then
  echo "ERROR: для --resume укажите RUN_ID=run_N" >&2
  exit 1
else
  RUN_ID="$(pick_free_run_id)"
fi

PORT="$(pick_free_port)"

echo "[train_jack_lily_george] config: ${CONFIG}"
echo "[train_jack_lily_george] run-id: ${RUN_ID}"
echo "[train_jack_lily_george] port: ${PORT}"
if [ "${RESUME}" -eq 1 ]; then
  echo "[train_jack_lily_george] mode: resume"
fi

ML_ARGS=(
  "${CONFIG}"
  --run-id "${RUN_ID}"
  --base-port "${PORT}"
)
if [ "${RESUME}" -eq 1 ]; then
  ML_ARGS+=(--resume)
fi

if [ "${USE_EDITOR:-0}" = "1" ] && [ -z "${1:-}" ]; then
  echo "[train_jack_lily_george] mode: Unity Editor (press Play)"
  NUM_ENVS="${NUM_ENVS:-1}"
  TIME_SCALE="${TIME_SCALE:-2}"
  exec mlagents-learn "${ML_ARGS[@]}" \
    --num-envs "${NUM_ENVS}" \
    --time-scale "${TIME_SCALE}"
fi

if [ -z "${1:-}" ]; then
  set -- "${DEFAULT_BUILD}" "${NUM_ENVS:-1}"
fi

BUILD_NAME="${1%.x86_64}"
BUILD="build_versions/${BUILD_NAME}.x86_64"
if [ ! -f "${BUILD}" ]; then
  BUILD="build_versions/${BUILD_NAME}"
fi
if [ ! -f "${BUILD}" ] && [ ! -d "${BUILD}" ]; then
  echo "ERROR: build not found: build_versions/${BUILD_NAME}" >&2
  exit 1
fi
chmod +x "${BUILD}" 2>/dev/null || true

NUM_ENVS="${2:-1}"
TIME_SCALE="${TIME_SCALE:-5}"
echo "[train_jack_lily_george] mode: build ${BUILD}"
echo "[train_jack_lily_george] num-envs: ${NUM_ENVS}  time-scale: ${TIME_SCALE}"
exec mlagents-learn "${ML_ARGS[@]}" \
  --env="${BUILD}" \
  --num-envs "${NUM_ENVS}" \
  --time-scale "${TIME_SCALE}" \
  --no-graphics
