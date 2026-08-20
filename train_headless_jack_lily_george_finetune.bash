#!/usr/bin/env bash
# Совместное дообучение Jack+Lily+George от трёх разных баз весов.
# Базы (INIT_FROM_*) только читаются; чекпоинты пишутся в новый RUN_ID.
# Среды (21): 0–9 PresentationFull×10; 10–13 Jack; 14–17 Lily; 18–20 George (по 1 задаче).
#
#   INIT_FROM_JACK=97_stage2 \
#   INIT_FROM_LILY=lily_1_stage2 \
#   INIT_FROM_GEORGE=george_1_stage2 \
#   RUN_ID=jlg_finetune_1 \
#   bash train_headless_jack_lily_george_finetune.bash
#
# Lab:
#   bash train_scripts/lab_comp/train_headless_jack_lily_george_finetune.bash
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
RUN_ID="${RUN_ID:-}"
INIT_FROM_JACK="${INIT_FROM_JACK:-}"
INIT_FROM_LILY="${INIT_FROM_LILY:-}"
INIT_FROM_GEORGE="${INIT_FROM_GEORGE:-}"
TRAIN_MODE="${TRAIN_MODE:-presentation}"
TIME_SCALE="${TIME_SCALE:-8}"
PRESENTATION_TIME_SCALE="${PRESENTATION_TIME_SCALE:-1}"
export FOREST_PRESENTATION_TIME_SCALE="${PRESENTATION_TIME_SCALE}"
TORCH_DEVICE="${TORCH_DEVICE:-cuda}"
TRAIN_PORT="${TRAIN_PORT:-5005}"
export DISPLAY="${DISPLAY:-:1}"
export OMP_NUM_THREADS="${OMP_NUM_THREADS:-2}"
export MKL_NUM_THREADS="${MKL_NUM_THREADS:-2}"
export FOREST_TRAIN_ALL_HEADLESS=1

# shellcheck source=train_scripts/lab_comp/cpu_affinity.env.bash
source "${ROOT}/train_scripts/lab_comp/cpu_affinity.env.bash"
export FOREST_TRAIN_CPUS
export FOREST_STREAM_CPUS

if [ "${TRAIN_MODE}" = "multi" ]; then
  NUM_ENVS="${NUM_ENVS:-11}"
else
  NUM_ENVS="${NUM_ENVS:-21}"
fi

BASE_CONFIG="custom_configs/Jack_Lily_George.yaml"
CONFIG="custom_configs/_autogen_Jack_Lily_George_finetune.yaml"
BUILD_PATH="build_versions/${BUILD%.x86_64}.x86_64"
LAUNCHER="${ROOT}/train_scripts/lab_comp/launch_forest_env.bash"
export FOREST_BUILD_PATH="${ROOT}/${BUILD_PATH}"
export FOREST_TRAIN_MODE="${TRAIN_MODE}"

if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda activate mlagents
fi

command -v mlagents-learn >/dev/null 2>&1 || { echo "ERROR: mlagents-learn не найден" >&2; exit 1; }
[ -f "${BASE_CONFIG}" ] || { echo "ERROR: нет ${BASE_CONFIG}" >&2; exit 1; }
[ -f "${BUILD_PATH}" ] || { echo "ERROR: нет ${BUILD_PATH}" >&2; exit 1; }
[ -f "${LAUNCHER}" ] || { echo "ERROR: нет ${LAUNCHER}" >&2; exit 1; }
chmod +x "${BUILD_PATH}" "${LAUNCHER}" 2>/dev/null || true

RESUME=0
FORCE=0
for arg in "$@"; do
  [ "${arg}" = "--resume" ] && RESUME=1
  [ "${arg}" = "--force" ] && FORCE=1
done

if [ -z "${RUN_ID}" ]; then
  echo "ERROR: нужен RUN_ID — новая папка results/<RUN_ID> (базы не перезаписываем)" >&2
  exit 1
fi

find_behavior_checkpoint() {
  local run="$1" beh="$2"
  local d="results/${run}/${beh}"
  local best=""
  [ -d "${d}" ] || return 1
  # Берём максимальный шаг Name-<step>.pt, не checkpoint.pt:
  # после сбоя resume checkpoint.pt может быть базой с меньшим step.
  best="$(ls -1 "${d}"/${beh}-*.pt 2>/dev/null | sort -V | tail -1 || true)"
  if [ -n "${best}" ]; then
    echo "${best}"
    return 0
  fi
  if [ -f "${d}/checkpoint.pt" ]; then
    echo "${d}/checkpoint.pt"
    return 0
  fi
  return 1
}

run_has_any_pt() {
  local run="$1" beh d
  for beh in JackLowLevelAgent LilyLowLevelAgent GeorgeLowLevelAgent; do
    d="results/${run}/${beh}"
    [ -d "${d}" ] || continue
    if compgen -G "${d}/*.pt" >/dev/null; then
      return 0
    fi
  done
  return 1
}

# Resume продолжает results/RUN_ID/*.pt. Базы INIT_FROM_* нужны только для нового прогона.
# Важно: mlagents --resume всё равно читает init_path из configuration.yaml и
# может заново загрузить базы вместо текущих весов RUN_ID. Переписываем init_path.
if [ "${RESUME}" -eq 1 ] && run_has_any_pt "${RUN_ID}"; then
  echo "[jlg_finetune] --resume: беру чекпоинты results/${RUN_ID} (базы INIT_FROM_* не нужны)"
  JACK_PT="$(find_behavior_checkpoint "${RUN_ID}" "JackLowLevelAgent" || true)"
  LILY_PT="$(find_behavior_checkpoint "${RUN_ID}" "LilyLowLevelAgent" || true)"
  GEORGE_PT="$(find_behavior_checkpoint "${RUN_ID}" "GeorgeLowLevelAgent" || true)"
  if [ -z "${JACK_PT}" ] || [ -z "${LILY_PT}" ] || [ -z "${GEORGE_PT}" ]; then
    echo "ERROR: --resume, но нет .pt у всех трёх в results/${RUN_ID}" >&2
    exit 1
  fi
  python3 - <<'PY' "${BASE_CONFIG}" "${CONFIG}" "${JACK_PT}" "${LILY_PT}" "${GEORGE_PT}"
import os, sys
src, dst, jack_pt, lily_pt, george_pt = sys.argv[1:6]
root = os.path.abspath(os.path.join(os.path.dirname(dst), ".."))

def rel(p):
    p = os.path.abspath(p)
    try:
        return os.path.relpath(p, root).replace("\\", "/")
    except ValueError:
        return p.replace("\\", "/")

jack_r, lily_r, george_r = rel(jack_pt), rel(lily_pt), rel(george_pt)
behavior = None
done = {"jack": False, "lily": False, "george": False}
out = []
for line in open(src, encoding="utf-8"):
    s = line.strip()
    if s.startswith("JackLowLevelAgent:"):
        behavior = "jack"
    elif s.startswith("LilyLowLevelAgent:"):
        behavior = "lily"
    elif s.startswith("GeorgeLowLevelAgent:"):
        behavior = "george"
    if s.startswith("init_path:") and line.startswith("    ") and behavior in done and not done[behavior]:
        path = {"jack": jack_r, "lily": lily_r, "george": george_r}[behavior]
        line = f"    init_path: {path}\n"
        done[behavior] = True
    if s.startswith("max_steps:") and line.startswith("    ") and behavior in done:
        try:
            cur = int(s.split(":", 1)[1].strip())
        except ValueError:
            cur = 0
        if cur < 50000000:
            line = "    max_steps: 50000000\n"
    if s.startswith("keep_checkpoints:") and line.startswith("    ") and behavior in done:
        line = "    keep_checkpoints: 20\n"
    out.append(line if line.endswith("\n") else line + "\n")
if not all(done.values()):
    missing = [k for k, v in done.items() if not v]
    raise SystemExit(f"ERROR: init_path не найден для {missing}")
open(dst, "w", encoding="utf-8").writelines(out)
print(f"[jlg_finetune] resume yaml {dst}")
print(f"[jlg_finetune]   Jack   <- {jack_r}")
print(f"[jlg_finetune]   Lily   <- {lily_r}")
print(f"[jlg_finetune]   George <- {george_r}")
PY
  CONFIG="${CONFIG}"
elif [ "${RESUME}" -eq 1 ] && [ -d "results/${RUN_ID}" ] && ! run_has_any_pt "${RUN_ID}"; then
  echo "[jlg_finetune] WARN: --resume, но в RUN_ID нет .pt — стартую с init_path баз"
  RESUME=0
fi

if [ "${RESUME}" -eq 0 ]; then
  if [ -z "${INIT_FROM_JACK}" ] || [ -z "${INIT_FROM_LILY}" ] || [ -z "${INIT_FROM_GEORGE}" ]; then
    echo "ERROR: нужны INIT_FROM_JACK, INIT_FROM_LILY, INIT_FROM_GEORGE (папки results/...)" >&2
    exit 1
  fi
  for src in "${INIT_FROM_JACK}" "${INIT_FROM_LILY}" "${INIT_FROM_GEORGE}"; do
    if [ "${RUN_ID}" = "${src}" ]; then
      echo "ERROR: RUN_ID=${RUN_ID} совпадает с базой ${src} — так затрём веса" >&2
      exit 1
    fi
  done
  for pair in \
    "JackLowLevelAgent:${INIT_FROM_JACK}" \
    "LilyLowLevelAgent:${INIT_FROM_LILY}" \
    "GeorgeLowLevelAgent:${INIT_FROM_GEORGE}"
  do
    beh="${pair%%:*}"
    run="${pair#*:}"
    if ! find_behavior_checkpoint "${run}" "${beh}" >/dev/null; then
      echo "ERROR: нет .pt в results/${run}/${beh}" >&2
      exit 1
    fi
  done

  JACK_PT="$(find_behavior_checkpoint "${INIT_FROM_JACK}" "JackLowLevelAgent")"
  LILY_PT="$(find_behavior_checkpoint "${INIT_FROM_LILY}" "LilyLowLevelAgent")"
  GEORGE_PT="$(find_behavior_checkpoint "${INIT_FROM_GEORGE}" "GeorgeLowLevelAgent")"

  python3 - <<'PY' "${BASE_CONFIG}" "${CONFIG}" "${JACK_PT}" "${LILY_PT}" "${GEORGE_PT}"
import os, sys
src, dst, jack_pt, lily_pt, george_pt = sys.argv[1:6]
root = os.path.abspath(os.path.join(os.path.dirname(dst), ".."))

def rel(p):
    p = os.path.abspath(p)
    try:
        return os.path.relpath(p, root).replace("\\", "/")
    except ValueError:
        return p.replace("\\", "/")

jack_r, lily_r, george_r = rel(jack_pt), rel(lily_pt), rel(george_pt)
behavior = None
done = {"jack": False, "lily": False, "george": False}
out = []
for line in open(src, encoding="utf-8"):
    s = line.strip()
    if s.startswith("JackLowLevelAgent:"):
        behavior = "jack"
    elif s.startswith("LilyLowLevelAgent:"):
        behavior = "lily"
    elif s.startswith("GeorgeLowLevelAgent:"):
        behavior = "george"
    if s.startswith("init_path:") and line.startswith("    ") and behavior in done and not done[behavior]:
        path = {"jack": jack_r, "lily": lily_r, "george": george_r}[behavior]
        line = f"    init_path: {path}\n"
        done[behavior] = True
    if s.startswith("keep_checkpoints:") and line.startswith("    ") and behavior in done:
        line = "    keep_checkpoints: 20\n"
    if s.startswith("max_steps:") and line.startswith("    ") and behavior in done:
        line = "    max_steps: 50000000\n"
    out.append(line if line.endswith("\n") else line + "\n")
if not all(done.values()):
    missing = [k for k, v in done.items() if not v]
    raise SystemExit(f"ERROR: init_path: null не найден для {missing}")
open(dst, "w", encoding="utf-8").writelines(out)
print(f"[jlg_finetune] wrote {dst}")
print(f"[jlg_finetune]   Jack   <- {jack_r}")
print(f"[jlg_finetune]   Lily   <- {lily_r}")
print(f"[jlg_finetune]   George <- {george_r}")
PY
fi

if [ -d "results/${RUN_ID}" ] && run_has_any_pt "${RUN_ID}" && [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 0 ]; then
  echo "ERROR: results/${RUN_ID} уже есть с .pt" >&2
  echo "  --resume  продолжить в этой папке" >&2
  echo "  --force   снести RUN_ID и стартовать заново (базы INIT_FROM_* не трогаем)" >&2
  echo "  RUN_ID=другой_id" >&2
  exit 1
fi

if [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 1 ] && [ -d "results/${RUN_ID}" ]; then
  echo "[jlg_finetune] --force: удаляю results/${RUN_ID} (базы целы)"
  rm -rf "results/${RUN_ID}"
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

if [ "${RESUME}" -eq 1 ]; then
  ML_ARGS+=(--resume)
else
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
{
  echo "jack=${INIT_FROM_JACK}"
  echo "lily=${INIT_FROM_LILY}"
  echo "george=${INIT_FROM_GEORGE}"
} > "${FOREST_RESULTS_DIR}/finetune_bases.txt"

TB_PORT="${TB_PORT:-6006}"
PORT="${TB_PORT}" bash "${ROOT}/train_scripts/lab_comp/run_tensorboard.bash" --daemon --all || true

echo "============================================================"
echo "[jlg_finetune] БАЗЫ (только чтение):"
echo "  Jack   results/${INIT_FROM_JACK}"
echo "  Lily   results/${INIT_FROM_LILY}"
echo "  George results/${INIT_FROM_GEORGE}"
echo "[jlg_finetune] СОХРАНЕНИЕ: results/${RUN_ID}"
echo "[jlg_finetune] mode=${TRAIN_MODE} envs=${NUM_ENVS} port=${TRAIN_PORT} resume=${RESUME}"
echo "[jlg_finetune] Стрим: RUN_ID=${RUN_ID} bash train_scripts/lab_comp/run_stream_onnx.bash"
echo "============================================================"

if command -v taskset >/dev/null 2>&1; then
  # Train уступает CPU стриму (отрицательный nice стриму нужен root — хотя бы поднимем nice train).
  renice -n "${FOREST_TRAIN_NICE:-10}" $$ >/dev/null 2>&1 || true
  exec taskset -c "${FOREST_TRAIN_CPUS}" mlagents-learn "${ML_ARGS[@]}"
fi
renice -n "${FOREST_TRAIN_NICE:-10}" $$ >/dev/null 2>&1 || true
exec mlagents-learn "${ML_ARGS[@]}"
