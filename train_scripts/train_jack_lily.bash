#!/usr/bin/env bash
set -eu
set -o pipefail

# Jack + Lily: новое совместное обучение (без --resume).
# Автоматически выбирает первый свободный results/run_N.
#
# Unity Editor — нажать Play после старта (time_scale=2, в 2 раза быстрее):
#   bash train_scripts/train_jack_lily.bash
#   TIME_SCALE=1 bash train_scripts/train_jack_lily.bash   # обычная скорость
#   TIME_SCALE=0.5 bash train_scripts/train_jack_lily.bash   # ещё медленнее
#
# Headless-билд (без Play, time_scale=5 как в yaml):
#   bash train_scripts/train_jack_lily.bash jack_lily_train_together_1
#   bash train_scripts/train_jack_lily.bash jack_lily_train_together_1 20
#   TIME_SCALE=2 bash train_scripts/train_jack_lily.bash jack_lily_train_together_1 20

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
cd "${ROOT}"

CONFIG="custom_configs/Jack_Lily.yaml"
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

RUN_ID="$(pick_free_run_id)"
PORT="$(pick_free_port)"

echo "[train_jack_lily] config: ${CONFIG}"
echo "[train_jack_lily] new run-id: ${RUN_ID}"
echo "[train_jack_lily] port: ${PORT}"

if [ -z "${1:-}" ]; then
  echo "[train_jack_lily] mode: Unity Editor (press Play when prompted)"
  NUM_ENVS="${NUM_ENVS:-1}"
  TIME_SCALE="${TIME_SCALE:-2}"
  echo "[train_jack_lily] num-envs: ${NUM_ENVS}"
  echo "[train_jack_lily] time-scale: ${TIME_SCALE}"
  exec mlagents-learn "${CONFIG}" \
    --run-id "${RUN_ID}" \
    --base-port "${PORT}" \
    --num-envs "${NUM_ENVS}" \
    --time-scale "${TIME_SCALE}"
fi

BUILD="build_versions/$1"
if [ ! -d "${BUILD}" ]; then
  echo "ERROR: ${BUILD} not found" >&2
  exit 1
fi

NUM_ENVS="${2:-20}"
TIME_SCALE="${TIME_SCALE:-5}"
echo "[train_jack_lily] mode: headless build ${BUILD}"
echo "[train_jack_lily] num-envs: ${NUM_ENVS}"
echo "[train_jack_lily] time-scale: ${TIME_SCALE}"
exec mlagents-learn "${CONFIG}" \
  --run-id "${RUN_ID}" \
  --env="${BUILD}" \
  --base-port "${PORT}" \
  --num-envs "${NUM_ENVS}" \
  --time-scale "${TIME_SCALE}" \
  --no-graphics
