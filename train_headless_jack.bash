#!/usr/bin/env bash
set -eu
set -o pipefail

# Jack only: 30 Unity headless (num-envs=30), задачи wood:food:water:zombie ≈ 2:1:2:2
# (на 30 слотах: 9 wood, 4 food, 9 water, 8 zombie).
#
#   RUN_ID=run_80 bash train_headless_jack.bash
#   RUN_ID=run_80 bash train_headless_jack.bash --resume
#
# Стрим отдельно (если нужен):
#   RUN_ID=run_80 bash train_scripts/lab_comp/run_stream_onnx.bash

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
RUN_ID="${RUN_ID:-jack_only_1}"
NUM_ENVS="${NUM_ENVS:-30}"
TIME_SCALE="${TIME_SCALE:-8}"
TORCH_DEVICE="${TORCH_DEVICE:-cuda}"
TRAIN_PORT="${TRAIN_PORT:-5005}"
export DISPLAY="${DISPLAY:-:1}"
export OMP_NUM_THREADS="${OMP_NUM_THREADS:-2}"
export MKL_NUM_THREADS="${MKL_NUM_THREADS:-2}"

# shellcheck source=train_scripts/lab_comp/cpu_affinity.env.bash
source "${ROOT}/train_scripts/lab_comp/cpu_affinity.env.bash"
export FOREST_TRAIN_CPUS
export FOREST_STREAM_CPUS

export FOREST_TRAIN_ALL_HEADLESS=1
export FOREST_JACK_ONLY_TASKS=1

CONFIG="custom_configs/Jack_single_agent.yaml"
BUILD_PATH="build_versions/${BUILD%.x86_64}.x86_64"
LAUNCHER="${ROOT}/train_scripts/lab_comp/launch_forest_env.bash"
export FOREST_BUILD_PATH="${ROOT}/${BUILD_PATH}"
export FOREST_TRAIN_MODE="multi"

if [ -f "build_versions/${BUILD%.x86_64}.BUILD_STAMP" ]; then
  echo "[train_jack] BUILD_STAMP:"
  sed 's/^/[train_jack]   /' "build_versions/${BUILD%.x86_64}.BUILD_STAMP"
else
  echo "[train_jack] WARN: нет BUILD_STAMP — билд могли не обновить через sync.bash" >&2
fi

if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda activate mlagents
fi

if ! command -v mlagents-learn >/dev/null 2>&1; then
  echo "ERROR: mlagents-learn не найден" >&2
  exit 1
fi

if [ ! -f "${CONFIG}" ] || [ ! -f "${BUILD_PATH}" ] || [ ! -f "${LAUNCHER}" ]; then
  echo "ERROR: нужны ${CONFIG}, ${BUILD_PATH} и ${LAUNCHER}" >&2
  exit 1
fi
chmod +x "${BUILD_PATH}" "${LAUNCHER}" 2>/dev/null || true

pick_free_run_id() {
  local i
  for ((i = 1; i <= 1000000; i++)); do
    if [ ! -d "results/run_${i}" ]; then
      echo "run_${i}"
      return 0
    fi
  done
  echo "ERROR: no free results/run_N" >&2
  exit 1
}

RESUME=0
FORCE=0
for arg in "$@"; do
  [ "${arg}" = "--resume" ] && RESUME=1
  [ "${arg}" = "--force" ] && FORCE=1
done

run_has_checkpoints() {
  local run="$1"
  local d="results/${run}/JackLowLevelAgent"
  [ -d "${d}" ] || return 1
  compgen -G "${d}/*.pt" >/dev/null
}

if [ -d "results/${RUN_ID}" ] && ! run_has_checkpoints "${RUN_ID}"; then
  echo "[train_jack] results/${RUN_ID} пустой или без .pt — сношу и стартую с нуля"
  rm -rf "results/${RUN_ID}"
  if [ "${RESUME}" -eq 1 ]; then
    echo "[train_jack] WARN: --resume игнорирую (нечего продолжать)."
    RESUME=0
  fi
fi

if [ -d "results/${RUN_ID}" ] && [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 0 ]; then
  if [ "${RUN_ID}" = "jack_only_1" ]; then
    RUN_ID="$(pick_free_run_id)"
  else
    echo "ERROR: results/${RUN_ID} уже есть (с чекпоинтами)." >&2
    echo "  --resume  продолжить обучение" >&2
    echo "  --force   начать заново" >&2
    echo "  RUN_ID=run_N  другой id" >&2
    exit 1
  fi
fi

if [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 1 ] && [ -d "results/${RUN_ID}" ]; then
  echo "[train_jack] --force: удаляю results/${RUN_ID}"
  rm -rf "results/${RUN_ID}"
fi

if [ "${RESUME}" -eq 1 ]; then
  if ! run_has_checkpoints "${RUN_ID}"; then
    echo "[train_jack] WARN: --resume, но нет .pt — стартую с нуля" >&2
    RESUME=0
  else
    echo "[train_jack] resume: найдены чекпоинты в results/${RUN_ID}"
  fi
fi

if [ "${RESUME}" -eq 0 ]; then
  echo "[train_jack] новый прогон: RUN_ID=${RUN_ID}"
fi

while netstat -tuln 2>/dev/null | grep -q ":${TRAIN_PORT} "; do
  TRAIN_PORT=$((TRAIN_PORT + 1))
done

ML_ARGS=(
  "${CONFIG}"
  --run-id "${RUN_ID}"
  --base-port "${TRAIN_PORT}"
  --env="${LAUNCHER}"
  --num-envs "${NUM_ENVS}"
  --time-scale "${TIME_SCALE}"
  --torch-device "${TORCH_DEVICE}"
  --timeout-wait "${TIMEOUT_WAIT:-300}"
)

[ "${RESUME}" -eq 1 ] && ML_ARGS+=(--resume)
if [ "${RESUME}" -eq 0 ]; then
  ML_ARGS+=(--force)
elif [ "${FORCE}" -eq 1 ]; then
  ML_ARGS+=(--force)
fi

ML_ARGS+=(
  --env-args
  -forestSingleEnvByPort
  -forestJackOnlyTasks
  -forestBasePort "${TRAIN_PORT}"
  -forestTrainAllHeadless
  -forestResultsDir "${ROOT}/results/${RUN_ID}"
)

export FOREST_BASE_PORT="${TRAIN_PORT}"
export FOREST_RESULTS_DIR="${ROOT}/results/${RUN_ID}"
mkdir -p "${FOREST_RESULTS_DIR}"

TB_PORT="${TB_PORT:-6006}"
echo "[train_jack] TensorBoard RUN_ID=${RUN_ID} port=${TB_PORT}..."
RUN_ID="${RUN_ID}" PORT="${TB_PORT}" bash "${ROOT}/train_scripts/lab_comp/run_tensorboard.bash" --daemon || \
  echo "WARN: TensorBoard не стартовал (обучение продолжается)" >&2
LAB_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
if [ -n "${LAB_IP}" ]; then
  echo "[train_jack] TensorBoard: http://${LAB_IP}:${TB_PORT}/"
fi

echo "[train_jack] run-id=${RUN_ID} num-envs=${NUM_ENVS} port=${TRAIN_PORT} time-scale=${TIME_SCALE} resume=${RESUME}"
echo "[train_jack] задачи: wood×9 food×4 water×9 zombie×8 (2:1:2:2)"
echo "[train_jack] CPU affinity: train=${FOREST_TRAIN_CPUS}"

# Pulse: глушить Unity train (по -forestTrainAllHeadless), стрим не трогать.
MUTE_SCRIPT="${ROOT}/train_scripts/lab_comp/mute_train_pulse_audio.bash"
if [ -f "${MUTE_SCRIPT}" ]; then
  bash "${MUTE_SCRIPT}" >/tmp/forest_mute_audio.log 2>&1 || true
  (
    while true; do
      sleep 8
      bash "${MUTE_SCRIPT}" >>/tmp/forest_mute_audio.log 2>&1 || true
    done
  ) &
  disown 2>/dev/null || true
fi

if command -v taskset >/dev/null 2>&1; then
  exec taskset -c "${FOREST_TRAIN_CPUS}" mlagents-learn "${ML_ARGS[@]}"
fi
exec mlagents-learn "${ML_ARGS[@]}"
