#!/usr/bin/env bash
set -eu
set -o pipefail

# Jack+Lily+George: 21 Unity headless (num-envs=21) + стрим отдельно (run_stream_onnx.bash).
# workers 0–9  = PresentationFull (все трое вместе)
# workers 10–13 = Jack wood/food/water/zombie (по 1)
# workers 14–17 = Lily food/water/heat/flower (по 1)
# workers 18–20 = George food/water/heat (по 1)
#
#   RUN_ID=run_72 bash train_headless_jack_lily_george.bash --resume
#   RUN_ID=run_72 bash train_scripts/lab_comp/run_stream_onnx.bash
#
# Без PresentationFull (только узкие, 11 envs):
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
export OMP_NUM_THREADS="${OMP_NUM_THREADS:-2}"
export MKL_NUM_THREADS="${MKL_NUM_THREADS:-2}"

# Train на ядрах 0-3; стрим занимает 4,5 (см. cpu_affinity.env.bash).
# shellcheck source=train_scripts/lab_comp/cpu_affinity.env.bash
source "${ROOT}/train_scripts/lab_comp/cpu_affinity.env.bash"
export FOREST_TRAIN_CPUS
export FOREST_STREAM_CPUS

# Стрим вынесен в run_stream_onnx.bash — train всегда без окон.
export FOREST_TRAIN_ALL_HEADLESS=1

if [ "${TRAIN_MODE}" = "multi" ]; then
  NUM_ENVS="${NUM_ENVS:-11}"
else
  NUM_ENVS="${NUM_ENVS:-21}"
fi

CONFIG="custom_configs/Jack_Lily_George.yaml"
BUILD_PATH="build_versions/${BUILD%.x86_64}.x86_64"
LAUNCHER="${ROOT}/train_scripts/lab_comp/launch_forest_env.bash"
export FOREST_BUILD_PATH="${ROOT}/${BUILD_PATH}"
export FOREST_TRAIN_MODE="${TRAIN_MODE}"

if [ -f "build_versions/${BUILD%.x86_64}.BUILD_STAMP" ]; then
  echo "[train] BUILD_STAMP:"
  sed 's/^/[train]   /' "build_versions/${BUILD%.x86_64}.BUILD_STAMP"
else
  echo "[train] WARN: нет BUILD_STAMP — билд могли не обновить через sync.bash" >&2
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

# Есть ли реальные .pt для продолжения (пустая results/run_N — не считается).
run_has_checkpoints() {
  local run="$1"
  local beh d
  for beh in JackLowLevelAgent LilyLowLevelAgent GeorgeLowLevelAgent; do
    d="results/${run}/${beh}"
    [ -d "${d}" ] || return 1
    # хотя бы один .pt (checkpoint.pt или Name-*.pt)
    if ! compgen -G "${d}/*.pt" >/dev/null; then
      return 1
    fi
  done
  return 0
}

# Пустая/битая папка run не должна блокировать новый старт.
if [ -d "results/${RUN_ID}" ] && ! run_has_checkpoints "${RUN_ID}"; then
  echo "[train] results/${RUN_ID} пустой или без .pt — сношу и стартую с нуля"
  rm -rf "results/${RUN_ID}"
  if [ "${RESUME}" -eq 1 ]; then
    echo "[train] WARN: --resume игнорирую (нечего продолжать). Для нового обучения флаг не нужен."
    RESUME=0
  fi
fi

if [ -d "results/${RUN_ID}" ] && [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 0 ]; then
  if [ "${RUN_ID}" = "jack_lily_george_1" ]; then
    RUN_ID="$(pick_free_run_id)"
  else
    echo "ERROR: results/${RUN_ID} уже есть (с чекпоинтами)." >&2
    echo "  --resume  продолжить обучение" >&2
    echo "  --force   начать заново (удалит прогресс run)" >&2
    echo "  RUN_ID=run_N  другой свободный id" >&2
    echo "Новый run без флагов:  RUN_ID=run_80 bash train_scripts/lab_comp/run_train.bash" >&2
    exit 1
  fi
fi

if [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 1 ] && [ -d "results/${RUN_ID}" ]; then
  echo "[train] --force: удаляю results/${RUN_ID}"
  rm -rf "results/${RUN_ID}"
fi

if [ "${RESUME}" -eq 1 ]; then
  if ! run_has_checkpoints "${RUN_ID}"; then
    echo "[train] WARN: --resume, но results/${RUN_ID}/*/ нет .pt — стартую с нуля" >&2
    RESUME=0
  else
    echo "[train] resume: найдены чекпоинты в results/${RUN_ID}"
  fi
fi

if [ "${RESUME}" -eq 0 ]; then
  echo "[train] новый прогон: RUN_ID=${RUN_ID} (без --resume)"
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

# Важно: --resume/--force ДО --env-args, иначе mlagents съест их как аргументы Unity.
[ "${RESUME}" -eq 1 ] && ML_ARGS+=(--resume)
# Даже пустой results/run_N (наш mkdir под Unity/jsonl) mlagents считает «старыми данными»
# и падает без --force. Для нового прогона всегда --force.
if [ "${RESUME}" -eq 0 ]; then
  ML_ARGS+=(--force)
elif [ "${FORCE}" -eq 1 ]; then
  ML_ARGS+=(--force)
fi

if [ "${TRAIN_MODE}" = "multi" ]; then
  ML_ARGS+=(--env-args -forestSingleEnvByPort -forestBasePort "${TRAIN_PORT}" -forestTrainAllHeadless -forestResultsDir "${ROOT}/results/${RUN_ID}")
else
  ML_ARGS+=(
    --env-args
    -forestSingleEnvByPort
    -forestPresentationWorker0
    -forestBasePort "${TRAIN_PORT}"
    -forestTrainAllHeadless
    -forestResultsDir "${ROOT}/results/${RUN_ID}"
  )
fi

export FOREST_BASE_PORT="${TRAIN_PORT}"
export FOREST_RESULTS_DIR="${ROOT}/results/${RUN_ID}"
mkdir -p "${FOREST_RESULTS_DIR}"

# TensorBoard сразу с train — с ПК: http://<lab_comp>:6006 или open_tensorboard.bash (туннель).
TB_PORT="${TB_PORT:-6006}"
echo "[train] TensorBoard RUN_ID=${RUN_ID} port=${TB_PORT}..."
RUN_ID="${RUN_ID}" PORT="${TB_PORT}" bash "${ROOT}/train_scripts/lab_comp/run_tensorboard.bash" --daemon || \
  echo "WARN: TensorBoard не стартовал (обучение продолжается)" >&2
LAB_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
if [ -n "${LAB_IP}" ]; then
  echo "[train] TensorBoard: http://${LAB_IP}:${TB_PORT}/  (если порт открыт в сети)"
fi
echo "[train] С ПК (туннель): RUN_ID=${RUN_ID} bash train_scripts/lab_comp/open_tensorboard.bash → http://127.0.0.1:${TB_PORT}"

echo "[train] mode=${TRAIN_MODE} DISPLAY=${DISPLAY} run-id=${RUN_ID} num-envs=${NUM_ENVS} port=${TRAIN_PORT} time-scale=${TIME_SCALE} resume=${RESUME}"
echo "[train] CPU affinity: train=${FOREST_TRAIN_CPUS} (stream reserved=${FOREST_STREAM_CPUS})"
if [ "${TRAIN_MODE}" = "presentation" ]; then
  echo "[train] 21 headless: w0-9=PresentationFull×10, w10-13=Jack×4, w14-17=Lily×4, w18-20=George×3"
  echo "[train] Стрим отдельно: RUN_ID=${RUN_ID} bash train_scripts/lab_comp/run_stream_onnx.bash"
fi
if command -v taskset >/dev/null 2>&1; then
  exec taskset -c "${FOREST_TRAIN_CPUS}" mlagents-learn "${ML_ARGS[@]}"
fi
exec mlagents-learn "${ML_ARGS[@]}"
