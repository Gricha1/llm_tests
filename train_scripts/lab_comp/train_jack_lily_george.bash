#!/usr/bin/env bash
# Jack + Lily + George на lab_comp: stream-билд с графикой (OBS / AnyDesk DISPLAY=:1).
#
#   bash train_scripts/lab_comp/train_jack_lily_george.bash
#   bash train_scripts/lab_comp/train_jack_lily_george.bash --resume
#
# Переменные:
#   BUILD=stream_forest_survival_2_12_07_2026
#   RUN_ID=jack_lily_george_1
#   NUM_ENVS=1  TIME_SCALE=2

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
RUN_ID="${RUN_ID:-jack_lily_george_1}"
NUM_ENVS="${NUM_ENVS:-1}"
TIME_SCALE="${TIME_SCALE:-2}"
export DISPLAY="${DISPLAY:-:1}"

CONFIG="custom_configs/Jack_Lily_George.yaml"
BUILD_PATH="build_versions/${BUILD%.x86_64}.x86_64"

if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda activate mlagents
fi

if ! command -v mlagents-learn >/dev/null 2>&1; then
  echo "ERROR: mlagents-learn не найден. bash train_scripts/lab_comp/setup_mlagents.bash" >&2
  exit 1
fi

if [ ! -f "${CONFIG}" ]; then
  echo "ERROR: ${CONFIG} не найден" >&2
  exit 1
fi

if [ ! -f "${BUILD_PATH}" ]; then
  echo "ERROR: ${BUILD_PATH} не найден." >&2
  echo "С Windows: BUILD=${BUILD} wsl bash train_scripts/lab_comp/sync_stream_build.bash" >&2
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
for arg in "$@"; do
  if [ "${arg}" = "--resume" ]; then
    RESUME=1
  fi
done

if [ -n "${RUN_ID:-}" ] && [ "${RESUME}" -eq 0 ] && [ "${RUN_ID}" = "jack_lily_george_1" ]; then
  if [ -d "results/${RUN_ID}" ]; then
    RUN_ID="$(pick_free_run_id)"
  fi
elif [ "${RESUME}" -eq 0 ] && [ -z "${RUN_ID:-}" ]; then
  RUN_ID="$(pick_free_run_id)"
fi

if [ "${RESUME}" -eq 1 ]; then
  for behavior in JackLowLevelAgent LilyLowLevelAgent GeorgeLowLevelAgent; do
    if [ ! -d "results/${RUN_ID}/${behavior}" ]; then
      echo "ERROR: results/${RUN_ID}/${behavior} не найден" >&2
      exit 1
    fi
  done
  bash train_scripts/lab_comp/fix_training_status.bash "${RUN_ID}" || true
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

echo "[jack_lily_george] DISPLAY=${DISPLAY}"
echo "[jack_lily_george] config: ${CONFIG}"
echo "[jack_lily_george] build: ${BUILD_PATH}"
echo "[jack_lily_george] run-id: ${RUN_ID}  num-envs: ${NUM_ENVS}  time-scale: ${TIME_SCALE}"
echo "[jack_lily_george] behaviors: JackLowLevelAgent, LilyLowLevelAgent, GeorgeLowLevelAgent"

exec mlagents-learn "${ML_ARGS[@]}"
