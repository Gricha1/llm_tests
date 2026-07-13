#!/usr/bin/env bash
set -eu
set -o pipefail

# Jack+Lily+George: 12 Unity-процессов (num-envs=12).
# worker 0 = presentation для OBS, workers 1–11 = headless train.
#
#   bash train_headless_jack_lily_george.bash
#   RUN_ID=run_60 bash train_headless_jack_lily_george.bash --resume
#
# Без presentation (11 headless):
#   TRAIN_MODE=multi bash train_headless_jack_lily_george.bash

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
RUN_ID="${RUN_ID:-jack_lily_george_1}"
TRAIN_MODE="${TRAIN_MODE:-presentation}"
TIME_SCALE="${TIME_SCALE:-8}"
PRESENTATION_TIME_SCALE="${PRESENTATION_TIME_SCALE:-1}"
export FOREST_PRESENTATION_TIME_SCALE="${PRESENTATION_TIME_SCALE}"
TORCH_DEVICE="${TORCH_DEVICE:-cuda}"
TRAIN_PORT="${TRAIN_PORT:-5005}"
export DISPLAY="${DISPLAY:-:1}"
export OMP_NUM_THREADS="${OMP_NUM_THREADS:-4}"
export MKL_NUM_THREADS="${MKL_NUM_THREADS:-4}"

if [ "${TRAIN_MODE}" = "multi" ]; then
  NUM_ENVS="${NUM_ENVS:-11}"
else
  NUM_ENVS="${NUM_ENVS:-12}"
fi

CONFIG="custom_configs/Jack_Lily_George.yaml"
BUILD_PATH="build_versions/${BUILD%.x86_64}.x86_64"
LAUNCHER="${ROOT}/train_scripts/lab_comp/launch_forest_env.bash"
export FOREST_BUILD_PATH="${ROOT}/${BUILD_PATH}"
export FOREST_TRAIN_MODE="${TRAIN_MODE}"

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

if [ -d "results/${RUN_ID}" ] && [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 0 ]; then
  if [ "${RUN_ID}" = "jack_lily_george_1" ]; then
    RUN_ID="$(pick_free_run_id)"
  else
    echo "ERROR: results/${RUN_ID} уже есть." >&2
    echo "  --resume  продолжить" >&2
    echo "  --force   начать заново (удалит прогресс run)" >&2
    echo "  RUN_ID=run_N  другой run" >&2
    exit 1
  fi
fi

if [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 1 ] && [ -d "results/${RUN_ID}" ]; then
  echo "[train] --force: удаляю results/${RUN_ID}"
  rm -rf "results/${RUN_ID}"
fi

if [ "${RESUME}" -eq 1 ]; then
  for behavior in JackLowLevelAgent LilyLowLevelAgent GeorgeLowLevelAgent; do
    [ -d "results/${RUN_ID}/${behavior}" ] || {
      echo "ERROR: results/${RUN_ID}/${behavior} не найден" >&2
      exit 1
    }
  done
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
)

if [ "${TRAIN_MODE}" = "multi" ]; then
  ML_ARGS+=(--env-args -forestSingleEnvByPort -forestBasePort "${TRAIN_PORT}")
else
  ML_ARGS+=(--env-args -forestSingleEnvByPort -forestPresentationWorker0 -forestBasePort "${TRAIN_PORT}")
fi

export FOREST_BASE_PORT="${TRAIN_PORT}"

[ "${RESUME}" -eq 1 ] && ML_ARGS+=(--resume)

echo "[train] mode=${TRAIN_MODE} DISPLAY=${DISPLAY} run-id=${RUN_ID} num-envs=${NUM_ENVS} port=${TRAIN_PORT} time-scale=${TIME_SCALE} presentation-scale=${PRESENTATION_TIME_SCALE}"
if [ "${TRAIN_MODE}" = "presentation" ]; then
  echo "[train] worker0 = 1 окно с дисплеем (OBS), workers 1–11 = -batchmode -nographics без окна"
  echo "[train] OBS: Захват окна → forest_survival (единственное окно)"
fi
exec mlagents-learn "${ML_ARGS[@]}"
