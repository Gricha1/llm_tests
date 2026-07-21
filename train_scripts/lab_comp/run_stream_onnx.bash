#!/usr/bin/env bash
# Стрим OBS: Unity с графикой + Python onnxruntime (отдельно от mlagents-learn).
#
#   RUN_ID=run_72 bash train_scripts/lab_comp/run_stream_onnx.bash
#
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

# shellcheck source=lab_comp_env.bash
if [ -f "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash" ]; then
  # shellcheck disable=SC1091
  source "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash"
fi

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
RUN_ID="${RUN_ID:?задайте RUN_ID=run_XX}"
STREAM_PORT="${STREAM_PORT:-7000}"
TIME_SCALE="${TIME_SCALE:-1}"
# 0 = веса только когда закончился эпизод Jack.
POLL_SEC="${POLL_SEC:-0}"
TARGET_FPS="${TARGET_FPS:-30}"
QUALITY_LEVEL="${QUALITY_LEVEL:-1}"
STREAM_WIDTH="${STREAM_WIDTH:-1920}"
STREAM_HEIGHT="${STREAM_HEIGHT:-1080}"
# Unity иногда долго молчит (хитч / death / тяжёлый ResetTrees) — не рвать стрим.
TIMEOUT_WAIT="${TIMEOUT_WAIT:-300}"
export DISPLAY="${DISPLAY:-:1}"

BUILD_PATH="build_versions/${BUILD%.x86_64}.x86_64"
BUILD_STAMP="build_versions/${BUILD%.x86_64}.BUILD_STAMP"
if [ ! -f "${BUILD_PATH}" ]; then
  echo "ERROR: ${BUILD_PATH} не найден" >&2
  exit 1
fi
chmod +x "${BUILD_PATH}" 2>/dev/null || true

if [ -f "${BUILD_STAMP}" ]; then
  echo "[stream_onnx] BUILD_STAMP:"
  sed 's/^/  /' "${BUILD_STAMP}"
else
  echo "WARN: нет ${BUILD_STAMP} — билд могли не заливать через sync_build (возможна старая версия)" >&2
fi

PY=""
if [ -x "${HOME}/anaconda3/envs/mlagents/bin/python" ]; then
  PY="${HOME}/anaconda3/envs/mlagents/bin/python"
elif [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda activate mlagents
  PY="$(command -v python)"
else
  echo "ERROR: conda env mlagents не найден (${HOME}/anaconda3/envs/mlagents)" >&2
  exit 1
fi

echo "[stream_onnx] python=${PY}"
"${PY}" -c "import mlagents_envs" 2>/dev/null || {
  echo "ERROR: в mlagents нет mlagents_envs — conda activate mlagents" >&2
  exit 1
}

"${PY}" -c "import onnxruntime" 2>/dev/null || {
  echo "[stream_onnx] pip install onnxruntime в mlagents..."
  "${PY}" -m pip install -q "onnxruntime>=1.16"
}

"${PY}" -c "import onnxruntime, mlagents_envs" || {
  echo "ERROR: onnxruntime/mlagents_envs всё ещё недоступны в ${PY}" >&2
  exit 1
}

# Старые стрим-Unity часто остаются после Ctrl+C — убиваем перед стартом.
pkill -9 -f 'forestStreamOnly' 2>/dev/null || true
pkill -9 -f stream_onnx_infer 2>/dev/null || true
sleep 1

while netstat -tuln 2>/dev/null | grep -q ":${STREAM_PORT} "; do
  STREAM_PORT=$((STREAM_PORT + 1))
done

echo "[stream_onnx] RUN_ID=${RUN_ID} DISPLAY=${DISPLAY} port=${STREAM_PORT}"
echo "[stream_onnx] build=${BUILD_PATH} ${STREAM_WIDTH}x${STREAM_HEIGHT} q=${QUALITY_LEVEL}"
echo "[stream_onnx] OBS: захват окна Unity (forest_survival)"

export FOREST_STREAM_EXTERNAL_BRAIN=1
export FOREST_TIME_SCALE="${TIME_SCALE}"
export FOREST_RESULTS_DIR="${ROOT}/results/${RUN_ID}"
# Меньше пауз рендера / VSync — Unity не ждёт дисплей между env.step.
export __GL_SYNC_TO_VBLANK="${__GL_SYNC_TO_VBLANK:-0}"
export vblank_mode="${vblank_mode:-0}"

# Стрим+OBS на одном ядре (по умолчанию 5); train — на 0-4.
# shellcheck source=cpu_affinity.env.bash
source "${ROOT}/train_scripts/lab_comp/cpu_affinity.env.bash"
echo "[stream_onnx] CPU affinity: stream=${FOREST_STREAM_CPUS} train=${FOREST_TRAIN_CPUS} obs=${FOREST_OBS_CPUS}"
renice -n -10 $$ >/dev/null 2>&1 || renice -n -5 $$ >/dev/null 2>&1 || true

# OBS не должен сидеть на ядрах Unity-стрима
if pgrep -x obs >/dev/null 2>&1; then
  for pid in $(pgrep -x obs); do
    taskset -cp "${FOREST_OBS_CPUS}" "${pid}" >/dev/null 2>&1 || true
  done
  echo "[stream_onnx] OBS pinned to CPU ${FOREST_OBS_CPUS}"
fi

STREAM_CMD=(
  "${PY}" "${ROOT}/train_scripts/lab_comp/stream_onnx_infer.py"
  --run-id "${RUN_ID}"
  --env "${ROOT}/${BUILD_PATH}"
  --port "${STREAM_PORT}"
  --results-dir "${ROOT}/results"
  --poll-sec "${POLL_SEC}"
  --time-scale "${TIME_SCALE}"
  --target-fps "${TARGET_FPS}"
  --quality-level "${QUALITY_LEVEL}"
  --width "${STREAM_WIDTH}"
  --height "${STREAM_HEIGHT}"
  --timeout "${TIMEOUT_WAIT}"
)

if command -v taskset >/dev/null 2>&1; then
  exec taskset -c "${FOREST_STREAM_CPUS}" "${STREAM_CMD[@]}"
fi
exec "${STREAM_CMD[@]}"
