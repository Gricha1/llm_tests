#!/usr/bin/env bash
# Общий launcher: HERO=lily|george  STAGE=1|2
# Не вызывать напрямую без HERO/STAGE — см. train_headless_lily_stage*.bash
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
cd "${ROOT}"

HERO="${HERO:?нужен HERO=lily|george}"
STAGE="${STAGE:?нужен STAGE=1|2}"
HERO_LC="$(echo "${HERO}" | tr '[:upper:]' '[:lower:]')"

case "${HERO_LC}" in
  lily)
    BEHAVIOR="LilyLowLevelAgent"
    BASE_CONFIG="custom_configs/Lily_single_agent.yaml"
    ONLY_FLAG="-forestLilyOnlyTasks"
    STAGE2_FLAG="-forestLilyStage2"
    ONLY_ENV="FOREST_LILY_ONLY_TASKS"
    STAGE2_ENV="FOREST_LILY_STAGE2"
    TAG="lily"
    ;;
  george)
    BEHAVIOR="GeorgeLowLevelAgent"
    BASE_CONFIG="custom_configs/George_single_agent.yaml"
    ONLY_FLAG="-forestGeorgeOnlyTasks"
    STAGE2_FLAG="-forestGeorgeStage2"
    ONLY_ENV="FOREST_GEORGE_ONLY_TASKS"
    STAGE2_ENV="FOREST_GEORGE_STAGE2"
    TAG="george"
    ;;
  *)
    echo "ERROR: HERO=lily|george, got HERO=${HERO}" >&2
    exit 1
    ;;
esac

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
NUM_ENVS="${NUM_ENVS:-24}"
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
case "${HERO_LC}" in
  lily) export FOREST_LILY_ONLY_TASKS=1 ;;
  george) export FOREST_GEORGE_ONLY_TASKS=1 ;;
esac

INIT_FROM="${INIT_FROM:-}"
if [ "${STAGE}" = "2" ]; then
  case "${HERO_LC}" in
    lily) export FOREST_LILY_STAGE2=1 ;;
    george) export FOREST_GEORGE_STAGE2=1 ;;
  esac
  DEFAULT_RUN_ID="${TAG}_stage2"
  CONFIG="custom_configs/_autogen_${TAG}_stage2.yaml"
else
  case "${HERO_LC}" in
    lily) export FOREST_LILY_STAGE2=0 ;;
    george) export FOREST_GEORGE_STAGE2=0 ;;
  esac
  DEFAULT_RUN_ID="${TAG}_stage1"
  CONFIG="${BASE_CONFIG}"
fi

RUN_ID="${RUN_ID:-${DEFAULT_RUN_ID}}"
BUILD_PATH="build_versions/${BUILD%.x86_64}.x86_64"
LAUNCHER="${ROOT}/train_scripts/lab_comp/launch_forest_env.bash"
export FOREST_BUILD_PATH="${ROOT}/${BUILD_PATH}"
export FOREST_TRAIN_MODE="multi"

PY_BIN="python3"
if ! command -v python3 >/dev/null 2>&1; then
  PY_BIN="python"
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

if [ ! -f "${BASE_CONFIG}" ] || [ ! -f "${BUILD_PATH}" ] || [ ! -f "${LAUNCHER}" ]; then
  echo "ERROR: нужны ${BASE_CONFIG}, ${BUILD_PATH} и ${LAUNCHER}" >&2
  exit 1
fi
chmod +x "${BUILD_PATH}" "${LAUNCHER}" 2>/dev/null || true

find_behavior_checkpoint() {
  local run_id="$1"
  local behavior_name="$2"
  "${PY_BIN}" - <<'PY' "${ROOT}" "${run_id}" "${behavior_name}"
import os, re, sys
root, run_id, behavior = sys.argv[1], sys.argv[2], sys.argv[3]
d = os.path.join(root, "results", run_id, behavior)
if not os.path.isdir(d):
    print(f"ERROR: нет папки: {d}", file=sys.stderr)
    sys.exit(1)
ck = os.path.join(d, "checkpoint.pt")
if os.path.isfile(ck):
    print(os.path.abspath(ck))
    sys.exit(0)
best_p, best_s = None, -1
rx = re.compile(r"^" + re.escape(behavior) + r"-(\d+)\.pt$")
for name in os.listdir(d):
    m = rx.match(name)
    if m:
        s = int(m.group(1))
        if s > best_s:
            best_s, best_p = s, os.path.join(d, name)
if best_p is None:
    print(f"ERROR: в {d} нет checkpoint.pt / {behavior}-<step>.pt", file=sys.stderr)
    sys.exit(1)
print(os.path.abspath(best_p))
PY
}

write_yaml_with_init_path() {
  local src_yaml="$1"
  local dst_yaml="$2"
  local pt="$3"
  "${PY_BIN}" - <<'PY' "${src_yaml}" "${dst_yaml}" "${pt}" "${ROOT}"
import os, sys
src, dst, pt, root = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
lines = open(src, "r", encoding="utf-8").read().splitlines()
rel = os.path.relpath(os.path.abspath(pt), root).replace("\\", "/")
out, done = [], False
for line in lines:
    if not done and line.strip() == "init_path: null" and line.startswith("    "):
        out.append(f"    init_path: {rel}")
        done = True
    else:
        out.append(line)
if not done:
    print("ERROR: init_path: null не найден", file=sys.stderr)
    sys.exit(1)
open(dst, "w", encoding="utf-8").write("\n".join(out) + "\n")
print(f"[{os.environ.get('TAG','hero')}] init_path -> {rel}")
PY
}

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
  local d="results/${run}/${BEHAVIOR}"
  [ -d "${d}" ] || return 1
  compgen -G "${d}/*.pt" >/dev/null
}

export TAG

if [ "${STAGE}" = "2" ] && [ "${RESUME}" -eq 0 ]; then
  if [ -z "${INIT_FROM}" ]; then
    echo "ERROR: Stage2 нужен INIT_FROM=<stage1_run_id>" >&2
    exit 1
  fi
  if ! run_has_checkpoints "${INIT_FROM}"; then
    echo "ERROR: INIT_FROM=${INIT_FROM} — нет .pt в results/${INIT_FROM}/${BEHAVIOR}" >&2
    exit 1
  fi
  if [ "${RUN_ID}" = "${INIT_FROM}" ]; then
    echo "ERROR: RUN_ID совпадает с INIT_FROM — затрём Stage1" >&2
    exit 1
  fi
fi

if [ "${RESUME}" -eq 0 ] && { [ "${RUN_ID}" = "${DEFAULT_RUN_ID}" ] || [ -z "${RUN_ID}" ]; }; then
  RUN_ID="$(pick_free_run_id)"
fi

if [ -d "results/${RUN_ID}" ] && ! run_has_checkpoints "${RUN_ID}"; then
  rm -rf "results/${RUN_ID}"
  RESUME=0
fi

if [ -d "results/${RUN_ID}" ] && [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 0 ]; then
  echo "ERROR: results/${RUN_ID} уже есть. --resume / --force / другой RUN_ID" >&2
  exit 1
fi

if [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 1 ] && [ -d "results/${RUN_ID}" ]; then
  if [ -n "${INIT_FROM}" ] && [ "${RUN_ID}" = "${INIT_FROM}" ]; then
    echo "ERROR: --force на INIT_FROM запрещён" >&2
    exit 1
  fi
  rm -rf "results/${RUN_ID}"
fi

if [ "${RESUME}" -eq 1 ] && ! run_has_checkpoints "${RUN_ID}"; then
  echo "WARN: --resume без .pt — стартую с нуля" >&2
  RESUME=0
fi

PT=""
if [ "${STAGE}" = "2" ] && [ "${RESUME}" -eq 0 ]; then
  PT="$(find_behavior_checkpoint "${INIT_FROM}" "${BEHAVIOR}")"
  write_yaml_with_init_path "${BASE_CONFIG}" "${CONFIG}" "${PT}"
elif [ "${STAGE}" = "2" ] && [ ! -f "${CONFIG}" ]; then
  cp "${BASE_CONFIG}" "${CONFIG}"
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
[ "${RESUME}" -eq 0 ] && ML_ARGS+=(--force)

ML_ARGS+=(
  --env-args
  -forestSingleEnvByPort
  "${ONLY_FLAG}"
)
if [ "${STAGE}" = "2" ]; then
  ML_ARGS+=("${STAGE2_FLAG}")
fi
ML_ARGS+=(
  -forestBasePort "${TRAIN_PORT}"
  -forestTrainAllHeadless
  -forestResultsDir "${ROOT}/results/${RUN_ID}"
)

export FOREST_BASE_PORT="${TRAIN_PORT}"
export FOREST_RESULTS_DIR="${ROOT}/results/${RUN_ID}"
mkdir -p "${FOREST_RESULTS_DIR}"

TB_PORT="${TB_PORT:-6006}"
RUN_ID="${RUN_ID}" PORT="${TB_PORT}" bash "${ROOT}/train_scripts/lab_comp/run_tensorboard.bash" --daemon || true

echo "============================================================"
echo "[${TAG}_stage${STAGE}] HERO=${HERO_LC} STAGE=${STAGE}"
if [ "${STAGE}" = "2" ]; then
  echo "[${TAG}_stage${STAGE}] БАЗОВЫЕ веса (чтение): results/${INIT_FROM:-—}"
  [ -n "${PT}" ] && echo "[${TAG}_stage${STAGE}]   checkpoint: ${PT}"
  echo "[${TAG}_stage${STAGE}] СОХРАНЕНИЕ:              results/${RUN_ID}"
  [ -n "${INIT_FROM}" ] && printf '%s\n' "${INIT_FROM}" > "${FOREST_RESULTS_DIR}/stage2_base.txt"
else
  echo "[${TAG}_stage${STAGE}] СОХРАНЕНИЕ Stage1:       results/${RUN_ID}"
  echo "[${TAG}_stage${STAGE}] штрафы emptyDO/back: выкл"
fi
if [ "${STAGE}" = "2" ]; then
  echo "[${TAG}_stage${STAGE}] штрафы: emptyDO=-5, backWalk=-0.5"
fi
echo "[${TAG}_stage${STAGE}] num-envs=${NUM_ENVS} port=${TRAIN_PORT} resume=${RESUME}"
echo "============================================================"

# Для pipeline: записать id последнего запуска
printf '%s\n' "${RUN_ID}" > "${ROOT}/results/.last_${TAG}_stage${STAGE}_run_id"

MUTE_SCRIPT="${ROOT}/train_scripts/lab_comp/mute_train_pulse_audio.bash"
if [ -f "${MUTE_SCRIPT}" ]; then
  bash "${MUTE_SCRIPT}" >/tmp/forest_mute_audio.log 2>&1 || true
fi

if command -v taskset >/dev/null 2>&1; then
  exec taskset -c "${FOREST_TRAIN_CPUS}" mlagents-learn "${ML_ARGS[@]}"
fi
exec mlagents-learn "${ML_ARGS[@]}"
