#!/usr/bin/env bash
set -eu
set -o pipefail

# «Headless» = обучение без стрима; stream — stream_inference_watch.bash (Unity один раз, hot reload .sentis).
#
#   bash train_headless_jack_lily_george.bash
#   RUN_ID=run_54 bash train_headless_jack_lily_george.bash --resume
#   RUN_ID=run_54 bash train_headless_jack_lily_george.bash --force

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
RUN_ID="${RUN_ID:-jack_lily_george_1}"
NUM_ENVS="${NUM_ENVS:-11}"
TIME_SCALE="${TIME_SCALE:-8}"
TORCH_DEVICE="${TORCH_DEVICE:-cuda}"
TRAIN_PORT="${TRAIN_PORT:-5005}"
export DISPLAY="${DISPLAY:-:1}"
# Несколько Unity-процессов → больше ядер CPU (каждый ~1–2 ядра). Следи за RAM (~2–3 ГБ на процесс).
export OMP_NUM_THREADS="${OMP_NUM_THREADS:-4}"
export MKL_NUM_THREADS="${MKL_NUM_THREADS:-4}"

CONFIG="custom_configs/Jack_Lily_George.yaml"
BUILD_PATH="build_versions/${BUILD%.x86_64}.x86_64"

if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda activate mlagents
fi

if ! command -v mlagents-learn >/dev/null 2>&1; then
  echo "ERROR: mlagents-learn не найден" >&2
  exit 1
fi

if [ ! -f "${CONFIG}" ] || [ ! -f "${BUILD_PATH}" ]; then
  echo "ERROR: нужны ${CONFIG} и ${BUILD_PATH}" >&2
  exit 1
fi
chmod +x "${BUILD_PATH}" 2>/dev/null || true

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
  echo "[headless] --force: удаляю results/${RUN_ID}"
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
  --env="${BUILD_PATH}"
  --num-envs "${NUM_ENVS}"
  --time-scale "${TIME_SCALE}"
  --torch-device "${TORCH_DEVICE}"
  --env-args -forestSingleEnvByPort -forestBasePort "${TRAIN_PORT}"
)
[ "${RESUME}" -eq 1 ] && ML_ARGS+=(--resume)

echo "[headless] DISPLAY=${DISPLAY}  run-id=${RUN_ID}  num-envs=${NUM_ENVS}  port=${TRAIN_PORT}  time-scale=${TIME_SCALE}  torch=${TORCH_DEVICE}"
echo "[headless] параллельно на стрим: RUN_ID=${RUN_ID} bash stream_inference_watch.bash"
exec mlagents-learn "${ML_ARGS[@]}"
