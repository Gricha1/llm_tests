#!/usr/bin/env bash
set -eu
set -o pipefail

# Jack only — обучение одного агента (JackLowLevelAgent).
# Автоматически выбирает первый свободный results/run_N.
#
# Unity Editor — запустить скрипт, затем нажать Play:
#   bash train_scripts/train_jack.bash
#   TIME_SCALE=1 bash train_scripts/train_jack.bash   # обычная скорость
#   TIME_SCALE=0.5 bash train_scripts/train_jack.bash
#
# Headless-билд (без Play, time_scale=5 по умолчанию):
#   bash train_scripts/train_jack.bash jack_cow.x86_64
#   bash train_scripts/train_jack.bash jack_cow.x86_64 20
#   TIME_SCALE=2 bash train_scripts/train_jack.bash jack_cow.x86_64 20
#
# Продолжить существующий run (Editor или билд):
#   RUN_ID=run_3 bash train_scripts/train_jack.bash --resume
#   RUN_ID=run_3 bash train_scripts/train_jack.bash jack_cow.x86_64 20 --resume

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
cd "${ROOT}"

CONFIG="custom_configs/Jack_single_agent.yaml"
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

echo "[train_jack] config: ${CONFIG}"
echo "[train_jack] run-id: ${RUN_ID}"
echo "[train_jack] port: ${PORT}"
if [ "${RESUME}" -eq 1 ]; then
  echo "[train_jack] mode: resume"
fi

ML_ARGS=(
  "${CONFIG}"
  --run-id "${RUN_ID}"
  --base-port "${PORT}"
)
if [ "${RESUME}" -eq 1 ]; then
  ML_ARGS+=(--resume)
fi

if [ -z "${1:-}" ]; then
  echo "[train_jack] mode: Unity Editor (press Play when mlagents-learn is waiting)"
  NUM_ENVS="${NUM_ENVS:-1}"
  TIME_SCALE="${TIME_SCALE:-2}"
  echo "[train_jack] num-envs: ${NUM_ENVS}"
  echo "[train_jack] time-scale: ${TIME_SCALE}"
  exec mlagents-learn "${ML_ARGS[@]}" \
    --num-envs "${NUM_ENVS}" \
    --time-scale "${TIME_SCALE}"
fi

BUILD="build_versions/$1"
BUILD="${BUILD%.x86_64}.x86_64"
if [ ! -f "${BUILD}" ]; then
  echo "ERROR: ${BUILD} not found" >&2
  exit 1
fi
chmod +x "${BUILD}" 2>/dev/null || true

NUM_ENVS="${2:-20}"
TIME_SCALE="${TIME_SCALE:-5}"
echo "[train_jack] mode: headless build ${BUILD}"
echo "[train_jack] num-envs: ${NUM_ENVS}"
echo "[train_jack] time-scale: ${TIME_SCALE}"
exec mlagents-learn "${ML_ARGS[@]}" \
  --env="${BUILD}" \
  --num-envs "${NUM_ENVS}" \
  --time-scale "${TIME_SCALE}" \
  --no-graphics
