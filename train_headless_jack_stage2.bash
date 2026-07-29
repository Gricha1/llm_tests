#!/usr/bin/env bash
set -eu
set -o pipefail

# Jack Stage2: fine-tune от Stage1 + штрафы:
#   — пустой DO (нет цели рядом) → −5
#   — ходьба назад в wood / food / water → −0.5 за decision (zombie без штрафа)
#
# INIT_FROM только читается (init_path). Чекпоинты пишутся в новый RUN_ID
# (свободный run_N, если RUN_ID не задан). results/run_97 не перезаписывается.
#
#   INIT_FROM=run_97 bash train_headless_jack_stage2.bash
#   INIT_FROM=run_97 RUN_ID=run_98 bash train_headless_jack_stage2.bash
#   RUN_ID=run_98 bash train_headless_jack_stage2.bash --resume
#
# Lab: INIT_FROM=run_97 bash train_scripts/lab_comp/train_headless_jack_stage2.bash

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
RUN_ID="${RUN_ID:-jack_stage2}"
INIT_FROM="${INIT_FROM:-}"
NUM_ENVS="${NUM_ENVS:-26}"
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
export FOREST_JACK_WOOD_FOOD_ONLY=1
export FOREST_JACK_ONLY_TASKS=1
export FOREST_JACK_STAGE2=1

BASE_CONFIG="custom_configs/Jack_single_agent.yaml"
CONFIG="custom_configs/_autogen_Jack_stage2.yaml"
BUILD_PATH="build_versions/${BUILD%.x86_64}.x86_64"
LAUNCHER="${ROOT}/train_scripts/lab_comp/launch_forest_env.bash"
export FOREST_BUILD_PATH="${ROOT}/${BUILD_PATH}"
export FOREST_TRAIN_MODE="multi"

PY_BIN="python3"
if ! command -v python3 >/dev/null 2>&1; then
  PY_BIN="python"
fi

if [ -f "build_versions/${BUILD%.x86_64}.BUILD_STAMP" ]; then
  echo "[jack_stage2] BUILD_STAMP:"
  sed 's/^/[jack_stage2]   /' "build_versions/${BUILD%.x86_64}.BUILD_STAMP"
else
  echo "[jack_stage2] WARN: нет BUILD_STAMP — билд могли не обновить через sync.bash" >&2
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
best_p = None
best_s = -1
rx = re.compile(r"^" + re.escape(behavior) + r"-(\d+)\.pt$")
for name in os.listdir(d):
    m = rx.match(name)
    if m:
        s = int(m.group(1))
        if s > best_s:
            best_s = s
            best_p = os.path.join(d, name)
if best_p is None:
    print(f"ERROR: в {d} нет checkpoint.pt и ни одного {behavior}-<step>.pt", file=sys.stderr)
    sys.exit(1)
print(os.path.abspath(best_p))
PY
}

write_yaml_with_init_path() {
  local src_yaml="$1"
  local dst_yaml="$2"
  local jack_pt="$3"
  "${PY_BIN}" - <<'PY' "${src_yaml}" "${dst_yaml}" "${jack_pt}" "${ROOT}"
import os, sys
src, dst, jack_pt, root = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
text = open(src, "r", encoding="utf-8").read().splitlines()
jack_r = os.path.relpath(os.path.abspath(jack_pt), root).replace("\\", "/")
out = []
done = False
for line in text:
    s = line.strip()
    if not done and s == "init_path: null" and line.startswith("    "):
        out.append(f"    init_path: {jack_r}")
        done = True
    else:
        out.append(line)
if not done:
    print("ERROR: в yaml не найден init_path: null у JackLowLevelAgent", file=sys.stderr)
    sys.exit(1)
os.makedirs(os.path.dirname(dst) or ".", exist_ok=True)
open(dst, "w", encoding="utf-8").write("\n".join(out) + "\n")
print(f"[jack_stage2] init_path -> {jack_r}")
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
  local d="results/${run}/JackLowLevelAgent"
  [ -d "${d}" ] || return 1
  compgen -G "${d}/*.pt" >/dev/null
}

if [ "${RESUME}" -eq 0 ]; then
  if [ -z "${INIT_FROM}" ]; then
    echo "ERROR: для Stage2 нужен INIT_FROM=<stage1_run_id> (например INIT_FROM=run_97)" >&2
    echo "  или продолжить уже начатый Stage2: RUN_ID=... bash ... --resume" >&2
    exit 1
  fi
  if ! run_has_checkpoints "${INIT_FROM}"; then
    echo "ERROR: INIT_FROM=${INIT_FROM} — нет .pt в results/${INIT_FROM}/JackLowLevelAgent" >&2
    exit 1
  fi
fi

# INIT_FROM только читаем; чекпоинты пишем в другой RUN_ID.
if [ -n "${INIT_FROM}" ] && [ "${RUN_ID}" = "${INIT_FROM}" ]; then
  echo "ERROR: RUN_ID=${RUN_ID} совпадает с INIT_FROM — так затрём веса Stage1." >&2
  echo "  Не задавай RUN_ID (возьмётся свободный run_N) или укажи другой, напр. RUN_ID=run_98" >&2
  exit 1
fi

# Дефолтный id → сразу свободный run_N (не jack_stage2), чтобы явно было «новое обучение».
if [ "${RESUME}" -eq 0 ] && { [ "${RUN_ID}" = "jack_stage2" ] || [ -z "${RUN_ID}" ]; }; then
  RUN_ID="$(pick_free_run_id)"
  echo "[jack_stage2] новый RUN_ID=${RUN_ID} (веса из INIT_FROM=${INIT_FROM} не трогаем)"
fi

if [ -d "results/${RUN_ID}" ] && ! run_has_checkpoints "${RUN_ID}"; then
  echo "[jack_stage2] results/${RUN_ID} пустой или без .pt — сношу и стартую"
  rm -rf "results/${RUN_ID}"
  if [ "${RESUME}" -eq 1 ]; then
    echo "[jack_stage2] WARN: --resume игнорирую (нечего продолжать)."
    RESUME=0
  fi
fi

if [ -d "results/${RUN_ID}" ] && [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 0 ]; then
  echo "ERROR: results/${RUN_ID} уже есть (с чекпоинтами)." >&2
  echo "  --resume  продолжить Stage2" >&2
  echo "  --force   начать заново в этом RUN_ID (INIT_FROM не удаляется)" >&2
  echo "  RUN_ID=run_N  другой id" >&2
  exit 1
fi

if [ "${RESUME}" -eq 0 ] && [ "${FORCE}" -eq 1 ] && [ -d "results/${RUN_ID}" ]; then
  if [ -n "${INIT_FROM}" ] && [ "${RUN_ID}" = "${INIT_FROM}" ]; then
    echo "ERROR: --force на INIT_FROM=${INIT_FROM} запрещён" >&2
    exit 1
  fi
  echo "[jack_stage2] --force: удаляю results/${RUN_ID} (INIT_FROM=${INIT_FROM:-none} цел)"
  rm -rf "results/${RUN_ID}"
fi

if [ "${RESUME}" -eq 1 ]; then
  if ! run_has_checkpoints "${RUN_ID}"; then
    echo "[jack_stage2] WARN: --resume, но нет .pt — нужен INIT_FROM" >&2
    RESUME=0
    if [ -z "${INIT_FROM}" ]; then
      echo "ERROR: нет чекпоинтов Stage2 и не задан INIT_FROM" >&2
      exit 1
    fi
  else
    echo "[jack_stage2] resume: чекпоинты в results/${RUN_ID}"
  fi
fi

if [ "${RESUME}" -eq 0 ]; then
  JACK_PT="$(find_behavior_checkpoint "${INIT_FROM}" "JackLowLevelAgent")"
  write_yaml_with_init_path "${BASE_CONFIG}" "${CONFIG}" "${JACK_PT}"
else
  # Resume: autogen мог пропасть — восстановим без init (веса уже в RUN_ID).
  if [ ! -f "${CONFIG}" ]; then
    cp "${BASE_CONFIG}" "${CONFIG}"
  fi
  JACK_PT=""
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
  -forestJackWoodFoodOnly
  -forestJackOnlyTasks
  -forestJackStage2
  -forestBasePort "${TRAIN_PORT}"
  -forestTrainAllHeadless
  -forestResultsDir "${ROOT}/results/${RUN_ID}"
)

export FOREST_BASE_PORT="${TRAIN_PORT}"
export FOREST_RESULTS_DIR="${ROOT}/results/${RUN_ID}"
mkdir -p "${FOREST_RESULTS_DIR}"

TB_PORT="${TB_PORT:-6006}"
echo "[jack_stage2] TensorBoard RUN_ID=${RUN_ID} port=${TB_PORT}..."
RUN_ID="${RUN_ID}" PORT="${TB_PORT}" bash "${ROOT}/train_scripts/lab_comp/run_tensorboard.bash" --daemon || \
  echo "WARN: TensorBoard не стартовал (обучение продолжается)" >&2
LAB_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
if [ -n "${LAB_IP}" ]; then
  echo "[jack_stage2] TensorBoard: http://${LAB_IP}:${TB_PORT}/"
fi

echo "============================================================"
if [ "${RESUME}" -eq 1 ]; then
  BASE_META="${ROOT}/results/${RUN_ID}/stage2_base.txt"
  if [ -z "${INIT_FROM}" ] && [ -f "${BASE_META}" ]; then
    INIT_FROM="$(tr -d '\r\n' < "${BASE_META}")"
  fi
  echo "[jack_stage2] РЕЖИМ: продолжение (--resume)"
else
  mkdir -p "${ROOT}/results/${RUN_ID}"
  if [ -n "${INIT_FROM}" ]; then
    printf '%s\n' "${INIT_FROM}" > "${ROOT}/results/${RUN_ID}/stage2_base.txt"
  fi
  echo "[jack_stage2] РЕЖИМ: новое обучение (init из Stage1)"
fi
echo "[jack_stage2] БАЗОВЫЕ веса (только чтение):  results/${INIT_FROM:-—}"
if [ -n "${JACK_PT}" ]; then
  echo "[jack_stage2]   checkpoint: ${JACK_PT}"
fi
echo "[jack_stage2] СОХРАНЕНИЕ нового обучения:   results/${RUN_ID}"
if [ -n "${INIT_FROM}" ]; then
  echo "[jack_stage2]   (results/${INIT_FROM} не перезаписывается)"
fi
echo "[jack_stage2] num-envs=${NUM_ENVS} port=${TRAIN_PORT} time-scale=${TIME_SCALE} resume=${RESUME}"
echo "[jack_stage2] Stage2: emptyDO=-5, backWalk=-0.5 (wood/food/water)"
echo "============================================================"

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
