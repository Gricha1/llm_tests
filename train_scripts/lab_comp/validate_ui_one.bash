#!/usr/bin/env bash
# Валидация ОДНОГО героя / ОДНОЙ задачи (то, что дергает UI).
#
# Примеры (на lab_comp):
#   bash train_scripts/lab_comp/validate_ui_one.bash BUILD 97_stage2 wood 20 "" jack
#   bash train_scripts/lab_comp/validate_ui_one.bash BUILD lily_1_stage2 flower 20 "" lily
#   bash train_scripts/lab_comp/validate_ui_one.bash BUILD george_1 heat 20 "" george
#
# Аргументы: <BUILD> <RUN_ID> <TASK> [SECONDS] [CONFIG_ignored] [HERO]
# Пишет: results/<RUN_ID>/videos/ui_val_<hero>_<TASK>.mp4
# Веса только читаются (--resume). В yaml только выбранный герой.
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

SELF_PID="$$"
kill_other_validates() {
  local pid cmd
  for pid in $(pgrep -f 'train_scripts/lab_comp/validate_ui_one[.]bash' 2>/dev/null || true); do
    if [ "$pid" != "$SELF_PID" ] && [ "$pid" != "$PPID" ]; then
      echo "[validate_ui] kill old script pid=$pid"
      kill -TERM "$pid" 2>/dev/null || true
    fi
  done
  sleep 1
  for proc in /proc/[0-9]*; do
    pid="${proc#/proc/}"
    [ -d "$proc" ] || continue
    [ "$pid" = "$SELF_PID" ] && continue
    cmd="$(tr '\0' ' ' <"$proc/cmdline" 2>/dev/null || true)"
    case "$cmd" in
      *validate_ui_one.bash*|*forestValidate*|*ui_val_*_frames*)
        kill -KILL "$pid" 2>/dev/null || true
        ;;
    esac
  done
}
kill_other_validates

BUILD="${1:-${BUILD:-stream_forest_survival_2_12_07_2026}}"
BUILD="${BUILD%.x86_64}"
RUN_ID="${2:?нужен RUN_ID}"
TASK="${3:?нужен TASK}"
SECONDS_N="${4:-30}"
HERO="${6:-${VALIDATE_HERO:-jack}}"

CAP_W="${CAP_W:-1920}"
CAP_H="${CAP_H:-1080}"
CAP_FPS="${CAP_FPS:-30}"

TASK="$(echo "${TASK}" | tr '[:upper:]' '[:lower:]')"
HERO="$(echo "${HERO}" | tr '[:upper:]' '[:lower:]')"
case "${HERO}" in
  jack|lily|george|all) ;;
  *) echo "ERROR: HERO=${HERO} — jack|lily|george|all" >&2; exit 1 ;;
esac

case "${HERO}" in
  jack)
    case "${TASK}" in stream|wood|food|water|zombie) ;;
      *) echo "ERROR: Jack TASK=${TASK}" >&2; exit 1 ;;
    esac
    BEHAVIOR="JackLowLevelAgent"
    CONFIG="Jack_single_agent.yaml"
    SOLO_ARGS=(-forestJackOnlyTasks)
    export FOREST_JACK_ONLY_TASKS=1
    ;;
  lily)
    case "${TASK}" in stream|food|water|heat|flower) ;;
      *) echo "ERROR: Lily TASK=${TASK}" >&2; exit 1 ;;
    esac
    BEHAVIOR="LilyLowLevelAgent"
    CONFIG="Lily_single_agent.yaml"
    SOLO_ARGS=(-forestLilyOnlyTasks)
    export FOREST_LILY_ONLY_TASKS=1
    ;;
  george)
    case "${TASK}" in stream|food|water|heat) ;;
      *) echo "ERROR: George TASK=${TASK}" >&2; exit 1 ;;
    esac
    BEHAVIOR="GeorgeLowLevelAgent"
    CONFIG="George_single_agent.yaml"
    SOLO_ARGS=(-forestGeorgeOnlyTasks)
    export FOREST_GEORGE_ONLY_TASKS=1
    ;;
  all)
    case "${TASK}" in stream) ;;
      *) echo "ERROR: HERO=all только TASK=stream" >&2; exit 1 ;;
    esac
    BEHAVIOR="JackLowLevelAgent"
    CONFIG="Jack_Lily_George.yaml"
    SOLO_ARGS=()
    ;;
esac

BUILD_PATH="build_versions/${BUILD}.x86_64"
[ -f "${BUILD_PATH}" ] || { echo "ERROR: нет ${BUILD_PATH}" >&2; exit 1; }
[ -d "results/${RUN_ID}" ] || { echo "ERROR: нет results/${RUN_ID}" >&2; exit 1; }
[ -f "custom_configs/${CONFIG}" ] || { echo "ERROR: нет custom_configs/${CONFIG}" >&2; exit 1; }

# Веса по номеру шага (не mtime): checkpoint.pt после --resume часто битый/меньше.
pick_best_pt() {
  local beh="$1"
  local dir="results/${RUN_ID}/${beh}"
  local best="" best_step=-1 f step
  for f in "${dir}/${beh}-"*.pt; do
    [ -f "${f}" ] || continue
    step="$(basename "${f}")"
    step="${step#${beh}-}"
    step="${step%.pt}"
    if [[ "${step}" =~ ^[0-9]+$ ]] && [ "${step}" -gt "${best_step}" ]; then
      best_step="${step}"
      best="${f}"
    fi
  done
  echo "${best}"
}

if [ "${HERO}" = "all" ]; then
  for beh in JackLowLevelAgent LilyLowLevelAgent GeorgeLowLevelAgent; do
    pt="$(pick_best_pt "${beh}")"
    if [ -z "${pt}" ]; then
      echo "ERROR: HERO=all нужен ${beh}-*.pt в results/${RUN_ID}" >&2
      exit 1
    fi
    echo "[validate_ui] trio weights ${beh}=${pt}"
  done
  HERO_PT="$(pick_best_pt JackLowLevelAgent)"
else
  HERO_PT="$(pick_best_pt "${BEHAVIOR}")"
  if [ -z "${HERO_PT}" ]; then
    echo "ERROR: нет ${BEHAVIOR}-*.pt в results/${RUN_ID}" >&2
    exit 1
  fi
fi

# Валидация в временном run-id: не трогаем training_status/checkpoint исходного RUN_ID.
VAL_RUN_ID="_ui_val_${RUN_ID}_${HERO}"
rm -rf "results/${VAL_RUN_ID}"
mkdir -p "results/${VAL_RUN_ID}/${BEHAVIOR}"
if [ "${HERO}" = "all" ]; then
  for beh in JackLowLevelAgent LilyLowLevelAgent GeorgeLowLevelAgent; do
    pt="$(pick_best_pt "${beh}")"
    mkdir -p "results/${VAL_RUN_ID}/${beh}"
    cp -f "${pt}" "results/${VAL_RUN_ID}/${beh}/checkpoint.pt"
    cp -f "${pt}" "results/${VAL_RUN_ID}/${beh}/$(basename "${pt}")"
  done
else
  cp -f "${HERO_PT}" "results/${VAL_RUN_ID}/${BEHAVIOR}/checkpoint.pt"
  cp -f "${HERO_PT}" "results/${VAL_RUN_ID}/${BEHAVIOR}/$(basename "${HERO_PT}")"
fi
# Минимальный status, чтобы --resume не писал в исходный run.
python3 - "${VAL_RUN_ID}" "${BEHAVIOR}" "${HERO_PT}" <<'PY'
import json, re, sys
from pathlib import Path
val_id, beh, pt = sys.argv[1], sys.argv[2], sys.argv[3]
step = 0
m = re.search(r"-(\d+)\.pt$", pt.replace("\\", "/"))
if m:
    step = int(m.group(1))
root = Path("results") / val_id
status = {
    "metadata": {
        "stats_format_version": "0.3.0",
        "mlagents_version": "1.1.0",
        "torch_version": "2.0.1+cu117",
    },
    beh: {
        "step": step,
        "checkpoints": [{
            "steps": step,
            "file_path": str(root / beh / "checkpoint.pt").replace("\\", "/"),
            "reward": None,
            "creation_time": 0.0,
            "auxillary_file_paths": [],
        }],
        "final_checkpoint": {
            "steps": step,
            "file_path": str(root / beh / "checkpoint.pt").replace("\\", "/"),
            "reward": None,
            "creation_time": 0.0,
            "auxillary_file_paths": [],
        },
    },
}
(root / "run_logs").mkdir(parents=True, exist_ok=True)
(root / "run_logs" / "training_status.json").write_text(
    json.dumps(status, indent=4), encoding="utf-8"
)
print(f"[validate_ui] temp status step={step} run={val_id}")
PY

if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda activate mlagents
fi
command -v mlagents-learn >/dev/null 2>&1 || { echo "ERROR: mlagents-learn не найден" >&2; exit 1; }
command -v ffmpeg >/dev/null 2>&1 || { echo "ERROR: ffmpeg не найден" >&2; exit 1; }

VIDEO_DIR="results/${RUN_ID}/videos"
CAP_DIR="${VIDEO_DIR}/ui_val_${HERO}_${TASK}_frames"
OUT_MP4="${VIDEO_DIR}/ui_val_${HERO}_${TASK}.mp4"
OUT_TMP="${OUT_MP4}.tmp"
OUT_WAV="${VIDEO_DIR}/ui_val_${HERO}_${TASK}.wav"
mkdir -p "${VIDEO_DIR}"
rm -rf "${CAP_DIR}"
mkdir -p "${CAP_DIR}"
rm -f "${OUT_TMP}" "${OUT_WAV}"

PORT=7010
while netstat -tuln 2>/dev/null | grep -q ":${PORT} "; do
  PORT=$((PORT + 1))
done

FRAMES=$((CAP_FPS * SECONDS_N))
echo "[validate_ui] HERO=${HERO} RUN_ID=${RUN_ID} TASK=${TASK} SECONDS=${SECONDS_N}"
echo "[validate_ui] config=${CONFIG} behavior=${BEHAVIOR}"
echo "[validate_ui] weights=${HERO_PT}"
echo "[validate_ui] mlagents_run=${VAL_RUN_ID} (temp, source RUN_ID not written)"
echo "[validate_ui] out=${OUT_MP4}"

ENV_ARGS=(
  -forestValidate
  -forestValidateTask "${TASK}"
  "${SOLO_ARGS[@]}"
  -screen-width "${CAP_W}"
  -screen-height "${CAP_H}"
  --capture-dir "$(pwd)/${CAP_DIR}"
  --capture-camera-a CamA
  --capture-a-source screen
  --capture-every 1
  --capture-width "${CAP_W}"
  --capture-height "${CAP_H}"
  --capture-msaa 1
  --capture-format jpg
  --capture-jpg-quality 75
  --capture-fps "${CAP_FPS}"
  --quit-after-capture-frames "${FRAMES}"
  --quit-after-seconds "$((SECONDS_N + 90))"
  --quit-delay-seconds 0.5
)

ML_CMD=(
  mlagents-learn "custom_configs/${CONFIG}"
  --inference
  --resume
  --env="${BUILD_PATH}"
  --run-id "${VAL_RUN_ID}"
  --base-port "${PORT}"
  --num-envs 1
  --timeout-wait 600
  --env-args "${ENV_ARGS[@]}"
)

export SDL_AUDIODRIVER="${SDL_AUDIODRIVER:-pulse}"
AUDIO_PID=""
VAL_SINK="forest_ui_val"
cleanup_audio() {
  if [ -n "${AUDIO_PID}" ] && kill -0 "${AUDIO_PID}" 2>/dev/null; then
    kill -INT "${AUDIO_PID}" 2>/dev/null || true
    wait "${AUDIO_PID}" 2>/dev/null || true
  fi
  AUDIO_PID=""
}
trap cleanup_audio EXIT

if command -v pactl >/dev/null 2>&1; then
  if ! pactl list short sinks 2>/dev/null | grep -q "${VAL_SINK}"; then
    pactl load-module module-null-sink \
      sink_name="${VAL_SINK}" \
      sink_properties=device.description=ForestUiValidate \
      >/dev/null 2>&1 || true
  fi
  if pactl list short sinks 2>/dev/null | grep -q "${VAL_SINK}"; then
    export PULSE_SINK="${VAL_SINK}"
    export PULSE_SOURCE="${VAL_SINK}.monitor"
    echo "[validate_ui] pulse → ${OUT_WAV}"
    ffmpeg -y -hide_banner -loglevel error \
      -f pulse -i "${VAL_SINK}.monitor" \
      -t "$((SECONDS_N + 40))" \
      -ac 2 -ar 44100 \
      "${OUT_WAV}" &
    AUDIO_PID=$!
  fi
fi

set +e
if command -v xvfb-run >/dev/null 2>&1; then
  xvfb-run -a -s "-screen 0 ${CAP_W}x${CAP_H}x24" \
    env PYTHONUNBUFFERED=1 SDL_AUDIODRIVER=pulse \
      PULSE_SINK="${PULSE_SINK:-}" PULSE_SOURCE="${PULSE_SOURCE:-}" \
    "${ML_CMD[@]}"
  ML_RC=$?
else
  env PYTHONUNBUFFERED=1 SDL_AUDIODRIVER=pulse \
    PULSE_SINK="${PULSE_SINK:-}" PULSE_SOURCE="${PULSE_SOURCE:-}" \
    "${ML_CMD[@]}"
  ML_RC=$?
fi
set -e
cleanup_audio
trap - EXIT

N_FRAMES="$(find "${CAP_DIR}" -type f \( -name 'frame_*.jpg' -o -name 'frame_*.png' \) 2>/dev/null | wc -l | tr -d ' ')"
WAV_BYTES="$(stat -c%s "${OUT_WAV}" 2>/dev/null || echo 0)"
echo "[validate_ui] frames=${N_FRAMES} mlagents_rc=${ML_RC} wav_bytes=${WAV_BYTES}"
if [ "${N_FRAMES}" -le 1 ]; then
  echo "ERROR: кадров почти нет" >&2
  exit 2
fi

if [ "${WAV_BYTES}" -gt 10000 ]; then
  echo "[validate_ui] mux video+audio"
  ffmpeg -y -hide_banner -loglevel error \
    -framerate "${CAP_FPS}" -pattern_type glob -i "${CAP_DIR}/frame_*.jpg" \
    -i "${OUT_WAV}" \
    -c:v libx264 -pix_fmt yuv420p -crf 23 -preset veryfast \
    -c:a aac -b:a 128k -shortest -f mp4 "${OUT_TMP}"
else
  echo "[validate_ui] video only (wav=${WAV_BYTES})"
  ffmpeg -y -hide_banner -loglevel error \
    -framerate "${CAP_FPS}" -pattern_type glob -i "${CAP_DIR}/frame_*.jpg" \
    -c:v libx264 -pix_fmt yuv420p -crf 23 -preset veryfast \
    -f mp4 "${OUT_TMP}"
fi
mv -f "${OUT_TMP}" "${OUT_MP4}"
echo "[validate_ui] OK ${OUT_MP4}"
ls -lh "${OUT_MP4}"
exit 0
