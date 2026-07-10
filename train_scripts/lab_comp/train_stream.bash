#!/usr/bin/env bash
# Стрим на lab_comp: Linux-билд С графикой (для OBS) + mlagents-learn.
#
# На сервере (AnyDesk / VNC, DISPLAY=:1):
#   bash ~/apps/stream/start_vnc.sh          # если нет рабочего стола
#   bash train_scripts/lab_comp/train_stream.bash
#
# Продолжить чекпоинт:
#   bash train_scripts/lab_comp/train_stream.bash --resume
#
# Переменные:
#   BUILD=stream_forest_survival_1_06_07_2026.x86_64
#   RUN_ID=jack_stream_05_07_2026_copy
#   NUM_ENVS=1   TIME_SCALE=2   FOREST_SHOW_PARALLEL_ENVS=1  (отладка: видны копии Env)

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_1_06_07_2026.x86_64}"
RUN_ID="${RUN_ID:-jack_stream_05_07_2026_copy}"
NUM_ENVS="${NUM_ENVS:-1}"
TIME_SCALE="${TIME_SCALE:-2}"
export DISPLAY="${DISPLAY:-:1}"

CONFIG="custom_configs/Jack_single_agent.yaml"
BUILD_PATH="build_versions/${BUILD}"

if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda activate mlagents
fi

if ! command -v mlagents-learn >/dev/null 2>&1; then
  echo "ERROR: mlagents-learn не найден. bash train_scripts/lab_comp/setup_mlagents.bash" >&2
  exit 1
fi

if [ ! -f "${BUILD_PATH}" ]; then
  echo "ERROR: ${BUILD_PATH} не найден." >&2
  echo "С Windows: wsl bash train_scripts/lab_comp/sync_stream_build.bash" >&2
  exit 1
fi
chmod +x "${BUILD_PATH}" 2>/dev/null || true

RESUME=0
for arg in "$@"; do
  if [ "${arg}" = "--resume" ]; then
    RESUME=1
  fi
done

if [ "${RESUME}" -eq 1 ]; then
  if [ ! -d "results/${RUN_ID}/JackLowLevelAgent" ]; then
    echo "ERROR: results/${RUN_ID} не найден" >&2
    exit 1
  fi
  echo "[stream] fix training_status.json (если битый после краша)"
  bash train_scripts/lab_comp/fix_training_status.bash "${RUN_ID}" || true
  echo "[stream] promote checkpoint -> checkpoint.pt"
  bash train_scripts/promote_latest_checkpoint.bash "results/${RUN_ID}"
fi

PORT=5005
while netstat -tuln 2>/dev/null | grep -q ":${PORT} "; do
  PORT=$((PORT + 1))
done

ML_ARGS=(
  "${CONFIG}"
  --run-id "${RUN_ID}"
  --base-port "${PORT}"
  --env="${BUILD_PATH}"
  --num-envs "${NUM_ENVS}"
  --time-scale "${TIME_SCALE}"
)
if [ "${RESUME}" -eq 1 ]; then
  ML_ARGS+=(--resume)
fi

echo "[stream] DISPLAY=${DISPLAY}"
echo "[stream] build: ${BUILD_PATH} (with graphics, for OBS)"
echo "[stream] run-id: ${RUN_ID}  num-envs: ${NUM_ENVS}  time-scale: ${TIME_SCALE}"
echo "[stream] OBS: Window Capture на окно Unity"

exec mlagents-learn "${ML_ARGS[@]}"
