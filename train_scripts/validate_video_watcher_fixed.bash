#!/usr/bin/env bash
set -eu
set -o pipefail

# Periodically runs inference validation and captures video frames.
#
# Usage (from train_scripts/):
#   bash validate_video_watcher_fixed.bash <env_build_name> <run_id> <config_name>
#
# Notes:
# - Detects checkpoints by scanning results/<run_id>/*/*-<steps>.pt
# - Keeps output videos in results/<run_id>/videos/

cd "$(dirname "$0")"
cd ..

# Python executable (WSL часто имеет только python3).
PY_BIN="python"
if ! command -v python >/dev/null 2>&1; then
  PY_BIN="python3"
fi

# Ctrl+C handling: stop progress loop + mlagents-learn + Unity.
CURRENT_ML_PID=""
CURRENT_PGID=""
CURRENT_PROGRESS_PID=""
cleanup_on_exit() {
  local code=$?

  if [ -n "${CURRENT_PROGRESS_PID}" ]; then
    kill "${CURRENT_PROGRESS_PID}" 2>/dev/null || true
    wait "${CURRENT_PROGRESS_PID}" 2>/dev/null || true
    CURRENT_PROGRESS_PID=""
  fi

  # Kill whole process group if we started one (most reliable).
  if [ -n "${CURRENT_PGID}" ]; then
    kill -INT -- "-${CURRENT_PGID}" 2>/dev/null || true
    sleep 0.3 || true
    kill -TERM -- "-${CURRENT_PGID}" 2>/dev/null || true
    sleep 0.3 || true
    kill -KILL -- "-${CURRENT_PGID}" 2>/dev/null || true
    CURRENT_PGID=""
    CURRENT_ML_PID=""
  elif [ -n "${CURRENT_ML_PID}" ]; then
    pkill -INT -P "${CURRENT_ML_PID}" 2>/dev/null || true
    kill -INT "${CURRENT_ML_PID}" 2>/dev/null || true
    sleep 0.3 || true
    pkill -TERM -P "${CURRENT_ML_PID}" 2>/dev/null || true
    kill -TERM "${CURRENT_ML_PID}" 2>/dev/null || true
    sleep 0.3 || true
    pkill -KILL -P "${CURRENT_ML_PID}" 2>/dev/null || true
    kill -KILL "${CURRENT_ML_PID}" 2>/dev/null || true
    CURRENT_ML_PID=""
  fi

  # Fallback best-effort
  pkill -INT -f mlagents-learn 2>/dev/null || true
  pkill -TERM -f mlagents-learn 2>/dev/null || true
  pkill -INT -f xvfb-run 2>/dev/null || true
  pkill -TERM -f xvfb-run 2>/dev/null || true

  restore_watcher_checkpoint_staging 2>/dev/null || true

  exit "${code}"
}

# ========== ПАРАМЕТРЫ (правь здесь; не через export) ==========
FORCE_XVFB=1

CAPTURE_EVERY=1
CAPTURE_WIDTH=1920
CAPTURE_HEIGHT=1080
CAPTURE_CAM_A=1
CAPTURE_CAM_B=0
CAPTURE_CAM_C=0
# camera = render only CamA (быстрее, но без Overlay HUD)
# screen = финальный кадр экрана (включает ScreenSpaceOverlay HUD/«стикеры»)
CAPTURE_CAM_A_SOURCE="screen"
CAPTURE_MSAA=1
CAPTURE_FPS=30
CAPTURE_SECONDS=40

VIDEO_FPS_MODE=realtime
VIDEO_FPS_FALLBACK=30

QUIT_AFTER_SECONDS=999999
QUIT_AFTER_EPISODES=0
QUIT_DELAY_SECONDS=0.5

MAX_EVAL_SECONDS=1200
FFMPEG_CRF=18
FFMPEG_PRESET=medium
CAPTURE_FORMAT=jpg
CAPTURE_JPG_QUALITY=60

# Папка захвата кадров.
# Пусто ("") = писать кадры прямо в results/<run_id>/videos/step_* (предсказуемо, рядом с весами/видео).
# Если захочешь снова быстрый диск в WSL — укажи путь типа "/tmp/forest_survival_capture".
CAPTURE_TMP_ROOT=""
# Удалять ли кадры после сборки mp4. Имеет смысл только если CAPTURE_TMP_ROOT не пустой.
CLEANUP_TMP_FRAMES=0

# --- Какие чекпоинты кодировать ---
EVAL_ONLY_LATEST=0
EVAL_LAST_N_CHECKPOINTS=0
EVAL_ALL_CHECKPOINTS=0
EVAL_RUN_ONCE=0
SKIP_LATEST_K=0

# Если хочешь прогнать ВООБЩЕ ВСЕ веса заново по порядку (даже если mp4 уже есть) — поставь 1.
REEVAL_ALL_STEPS=0

# --- Ретраи/ремонт проваленных шагов ---
# Если mp4 не создан, но остался capture_started.txt — считаем это "залипло" и ретраим.
# Чтобы не мешать реально идущему шагу, ретраим только если capture_started старше этого порога.
RETRY_STALE_STARTED_SECONDS=900
# ==============================================================

if [ -z "${1:-}" ] || [ -z "${2:-}" ] || [ -z "${3:-}" ]; then
  echo "Usage: bash train_scripts/validate_video_watcher_fixed.bash <env_build_name> <run_id> <config_name> [only_step]"
  exit 1
fi

ENV_BUILD_NAME="$1"
RUN_ID="$2"
CONFIG_NAME="$3"
ONLY_STEP="${4:-}"

BUILD_PATH="build_versions/${ENV_BUILD_NAME}"
RESULTS_DIR="results/${RUN_ID}"
VIDEO_ROOT="${RESULTS_DIR}/videos"

# Inference loads checkpoint.pt under BEHAVIOR_DIR; we stage a chosen step there and restore after each eval (see stage_checkpoint_for_inference_step).
STAGING_ACTIVE=0

# Checkpoint folder under results/<run_id>/: prefer LilyLowLevelAgent if it actually has *.pt,
# otherwise first subdir that has *-<steps>.pt (e.g. Jack runs use JackLowLevelAgent).
watcher_dir_has_training_pts() {
  local d="$1"
  [ -d "${d}" ] || return 1
  ls "${d}"/*-*.pt >/dev/null 2>&1
}

BEHAVIOR_DIR=""
_LILY="${RESULTS_DIR}/LilyLowLevelAgent"
if watcher_dir_has_training_pts "${_LILY}"; then
  BEHAVIOR_DIR="${_LILY}"
else
  for d in "${RESULTS_DIR}"/*; do
    [ -d "${d}" ] || continue
    watcher_dir_has_training_pts "${d}" || continue
    BEHAVIOR_DIR="${d}"
    break
  done
fi
if [ -z "${BEHAVIOR_DIR}" ]; then
  BEHAVIOR_DIR="${_LILY}"
fi
echo "[watcher] behavior dir: ${BEHAVIOR_DIR}"

mkdir -p "${VIDEO_ROOT}"

START_PORT=7005
CHECK_COUNT=1000

pick_free_port() {
  for i in $(seq 0 "${CHECK_COUNT}"); do
    local p=$((START_PORT + i))
    if ! netstat -tuln | grep -q ":$p "; then
      echo "$p"
      return 0
    fi
  done
  return 1
}

python_list_checkpoint_steps() {
  "${PY_BIN}" - <<'PY' "$1"
import glob, os, re, sys
behavior_dir = sys.argv[1]
pattern = os.path.join(behavior_dir, "*-*.pt")
steps = set()
for p in glob.glob(pattern):
    base = os.path.basename(p)
    m = re.search(r"-(\d+)\.pt$", base)
    if m:
        steps.add(int(m.group(1)))
for s in sorted(steps):
    print(s)
PY
}

python_has_any_checkpoint() {
  "${PY_BIN}" - <<'PY' "$1"
import glob, os, sys
behavior_dir = sys.argv[1]
pattern = os.path.join(behavior_dir, "*-*.pt")
print("1" if glob.glob(pattern) else "0")
PY
}

list_steps_to_eval() {
  local raw
  raw="$(python_list_checkpoint_steps "${BEHAVIOR_DIR}")"
  [ -n "${raw}" ] || return 0

  if [ "${SKIP_LATEST_K}" -gt 0 ] 2>/dev/null; then
    local count
    count="$(echo "${raw}" | wc -l | tr -d ' ')"
    if [ "${count}" -le "${SKIP_LATEST_K}" ]; then
      return 0
    fi
    raw="$(echo "${raw}" | head -n "$((count - SKIP_LATEST_K))")"
  fi

  if [ "${EVAL_ALL_CHECKPOINTS}" = "1" ]; then
    echo "${raw}"
    return 0
  fi
  if [ "${EVAL_ONLY_LATEST}" = "1" ]; then
    echo "${raw}" | tail -n 1
    return 0
  fi
  if [ "${EVAL_LAST_N_CHECKPOINTS}" -gt 0 ] 2>/dev/null; then
    echo "${raw}" | tail -n "${EVAL_LAST_N_CHECKPOINTS}"
    return 0
  fi
  echo "${raw}"
}

already_encoded_target() {
  local step="$1"
  if [ "${REEVAL_ALL_STEPS}" = "1" ]; then
    return 1
  fi
  local ok=1
  if [ "${CAPTURE_CAM_A}" = "1" ] && [ ! -f "${VIDEO_ROOT}/step_${step}_camA.mp4" ]; then ok=0; fi
  if [ "${CAPTURE_CAM_B}" = "1" ] && [ ! -f "${VIDEO_ROOT}/step_${step}_camB.mp4" ]; then ok=0; fi
  if [ "${CAPTURE_CAM_C}" = "1" ] && [ ! -f "${VIDEO_ROOT}/step_${step}_CamOnJack.mp4" ]; then ok=0; fi
  if [ "${ok}" = "1" ]; then
    return 0
  fi

  # If capture_started exists but mp4 doesn't, retry only when the marker is stale.
  is_started_stale() {
    local f="$1"
    [ -f "${f}" ] || return 1
    if [ "${RETRY_STALE_STARTED_SECONDS}" -le 0 ] 2>/dev/null; then
      return 0
    fi
    local now_ts started_ts
    now_ts="$(date +%s 2>/dev/null || echo 0)"
    started_ts="$(stat -c '%Y' "${f}" 2>/dev/null || echo 0)"
    [ "$(( now_ts - started_ts ))" -ge "${RETRY_STALE_STARTED_SECONDS}" ]
  }

  if [ "${CAPTURE_CAM_A}" = "1" ] && is_started_stale "${VIDEO_ROOT}/step_${step}_camA/capture_started.txt"; then
    rm -f "${VIDEO_ROOT}/step_${step}_camA/capture_started.txt" 2>/dev/null || true
    return 1
  fi
  if [ "${CAPTURE_CAM_B}" = "1" ] && is_started_stale "${VIDEO_ROOT}/step_${step}_camB/capture_started.txt"; then
    rm -f "${VIDEO_ROOT}/step_${step}_camB/capture_started.txt" 2>/dev/null || true
    return 1
  fi
  if [ "${CAPTURE_CAM_C}" = "1" ] && is_started_stale "${VIDEO_ROOT}/step_${step}_CamOnJack/capture_started.txt"; then
    rm -f "${VIDEO_ROOT}/step_${step}_CamOnJack/capture_started.txt" 2>/dev/null || true
    return 1
  fi

  return 1
}

python_find_checkpoint_pt_for_step() {
  "${PY_BIN}" - <<'PY' "$1" "$2"
import glob, os, sys
behavior_dir = sys.argv[1]
target = int(sys.argv[2])
pattern = os.path.join(behavior_dir, "*-%d.pt" % target)
hits = sorted(glob.glob(pattern))
print(hits[0] if hits else "")
PY
}

python_can_torch_load_checkpoint() {
  "${PY_BIN}" - <<'PY' "$1"
import sys
path = sys.argv[1]
try:
    import torch
    try:
        torch.load(path, map_location="cpu", weights_only=True)
    except TypeError:
        torch.load(path, map_location="cpu")
    print("1")
except Exception:
    print("0")
PY
}

wait_for_stable_file() {
  local f="$1"
  local tries="${2:-20}"
  local sleep_s="${3:-1}"
  local prev="" cur=""
  for _ in $(seq 1 "${tries}"); do
    if [ ! -f "${f}" ]; then
      sleep "${sleep_s}" || true
      continue
    fi
    cur="$( (stat -c '%s' "${f}" 2>/dev/null || stat -f '%z' "${f}" 2>/dev/null || echo "") | tr -d '\r' )"
    if [ -n "${cur}" ] && [ "${cur}" = "${prev}" ] && [ "${cur}" != "0" ]; then
      return 0
    fi
    prev="${cur}"
    sleep "${sleep_s}" || true
  done
  return 1
}

restore_watcher_checkpoint_staging() {
  [ "${STAGING_ACTIVE:-0}" = "1" ] || return 0
  [ -n "${BEHAVIOR_DIR:-}" ] || {
    STAGING_ACTIVE=0
    return 0
  }
  local dst_pt="${BEHAVIOR_DIR}/checkpoint.pt"
  local backup="${BEHAVIOR_DIR}/checkpoint.pt.__watcher_backup"
  local partial="${BEHAVIOR_DIR}/checkpoint.pt.__watcher_writing"

  rm -f "${partial}" 2>/dev/null || true
  rm -f "${dst_pt}" 2>/dev/null || true
  if [ -f "${backup}" ]; then
    mv -f "${backup}" "${dst_pt}"
  fi
  STAGING_ACTIVE=0
}

stage_checkpoint_for_inference_step() {
  local step="$1"
  local src_pt
  src_pt="$(python_find_checkpoint_pt_for_step "${BEHAVIOR_DIR}" "${step}")"
  if [ -z "${src_pt}" ]; then
    echo "[watcher] ERROR: no .pt checkpoint found for step=${step}"
    return 1
  fi
  if ! wait_for_stable_file "${src_pt}" 20 1; then
    echo "[watcher] WARN: checkpoint file not stable yet (maybe being written). step=${step}"
    return 1
  fi

  local dst_pt="${BEHAVIOR_DIR}/checkpoint.pt"
  local backup="${BEHAVIOR_DIR}/checkpoint.pt.__watcher_backup"
  local partial="${BEHAVIOR_DIR}/checkpoint.pt.__watcher_writing"

  rm -f "${partial}" 2>/dev/null || true

  # Interrupted previous run may leave backup behind.
  if [ -f "${backup}" ]; then
    echo "[watcher] WARN: found orphaned ${backup}; restoring as checkpoint.pt before staging step=${step}"
    rm -f "${dst_pt}" 2>/dev/null || true
    mv -f "${backup}" "${dst_pt}"
  fi

  if [ "$(python_can_torch_load_checkpoint "${src_pt}")" != "1" ]; then
    echo "[watcher] WARN: torch.load failed for step=${step} (checkpoint likely incomplete/corrupt). Will retry later."
    return 1
  fi

  if [ -f "${dst_pt}" ]; then
    mv -f "${dst_pt}" "${backup}"
  fi

  cp -f "${src_pt}" "${partial}"
  if [ "$(python_can_torch_load_checkpoint "${partial}")" != "1" ]; then
    rm -f "${partial}" 2>/dev/null || true
    if [ -f "${backup}" ]; then
      mv -f "${backup}" "${dst_pt}"
    fi
    echo "[watcher] WARN: torch.load failed on staged copy step=${step}. Will retry later."
    return 1
  fi
  mv -f "${partial}" "${dst_pt}"

  STAGING_ACTIVE=1
  return 0
}

run_eval_for_step() {
  local step="$1"
  local pot
  pot="$(pick_free_port)"

  local meta_di_a="${VIDEO_ROOT}/step_${step}_camA"
  local meta_di_b="${VIDEO_ROOT}/step_${step}_camB"
  local meta_di_c="${VIDEO_ROOT}/step_${step}_CamOnJack"

  local cap_di_a="${meta_di_a}"
  local cap_di_b="${meta_di_b}"
  local cap_di_c="${meta_di_c}"
  if [ -n "${CAPTURE_TMP_ROOT}" ]; then
    cap_di_a="${CAPTURE_TMP_ROOT}/${RUN_ID}/step_${step}_camA"
    cap_di_b="${CAPTURE_TMP_ROOT}/${RUN_ID}/step_${step}_camB"
    cap_di_c="${CAPTURE_TMP_ROOT}/${RUN_ID}/step_${step}_CamOnJack"
  fi

  if command -v realpath >/dev/null 2>&1; then
    meta_di_a="$(realpath "${meta_di_a}")"
    meta_di_b="$(realpath "${meta_di_b}")"
    meta_di_c="$(realpath "${meta_di_c}")"
    cap_di_a="$(realpath -m "${cap_di_a}" 2>/dev/null || realpath "${cap_di_a}")"
    cap_di_b="$(realpath -m "${cap_di_b}" 2>/dev/null || realpath "${cap_di_b}")"
    cap_di_c="$(realpath -m "${cap_di_c}" 2>/dev/null || realpath "${cap_di_c}")"
  fi

  if [ "${CAPTURE_CAM_A}" = "1" ]; then mkdir -p "${meta_di_a}" "${cap_di_a}"; fi
  if [ "${CAPTURE_CAM_B}" = "1" ]; then mkdir -p "${meta_di_b}" "${cap_di_b}"; fi
  if [ "${CAPTURE_CAM_C}" = "1" ]; then mkdir -p "${meta_di_c}" "${cap_di_c}"; fi

  echo "[watcher] validate target step=${step} on port=${pot}"
  local target_frames
  target_frames="$(( (CAPTURE_FPS * CAPTURE_SECONDS + CAPTURE_EVERY - 1) / CAPTURE_EVERY ))"
  echo "[watcher] target capture frames=${target_frames} (fps=${CAPTURE_FPS} seconds=${CAPTURE_SECONDS} every=${CAPTURE_EVERY})"

  local env_args=()
  if [ "${CAPTURE_CAM_A}" = "1" ]; then
    env_args+=(--capture-dir "${cap_di_a}" --capture-camera-a "CamA" --capture-a-source "${CAPTURE_CAM_A_SOURCE}")
  fi
  if [ "${CAPTURE_CAM_B}" = "1" ]; then
    env_args+=(--capture-dir-b "${cap_di_b}" --capture-camera-b "CamB")
  fi
  if [ "${CAPTURE_CAM_C}" = "1" ]; then
    env_args+=(--capture-dir-c "${cap_di_c}" --capture-camera-c "CamOnJack")
  fi
  env_args+=(
    --capture-every "${CAPTURE_EVERY}"
    --capture-width "${CAPTURE_WIDTH}"
    --capture-height "${CAPTURE_HEIGHT}"
    --capture-msaa "${CAPTURE_MSAA}"
    --capture-format "${CAPTURE_FORMAT}"
    --capture-jpg-quality "${CAPTURE_JPG_QUALITY}"
    --capture-fps "${CAPTURE_FPS}"
    --quit-after-capture-frames "${target_frames}"
    --quit-after-episodes "${QUIT_AFTER_EPISODES}"
    --quit-after-seconds "${QUIT_AFTER_SECONDS}"
    --quit-delay-seconds "${QUIT_DELAY_SECONDS}"
  )

  if ! stage_checkpoint_for_inference_step "${step}"; then
    echo "[watcher] WARN: cannot prepare checkpoint for step=${step}. Will retry later."
    return 0
  fi

  if [ "${CAPTURE_CAM_A}" = "1" ]; then date -Is > "${meta_di_a}/capture_started.txt" 2>/dev/null || true; fi
  if [ "${CAPTURE_CAM_B}" = "1" ]; then date -Is > "${meta_di_b}/capture_started.txt" 2>/dev/null || true; fi
  if [ "${CAPTURE_CAM_C}" = "1" ]; then date -Is > "${meta_di_c}/capture_started.txt" 2>/dev/null || true; fi

  local mlagents_cmd=(mlagents-learn "custom_configs/${CONFIG_NAME}.yaml"
    --inference
    --resume
    --env="${BUILD_PATH}"
    --run-id "${RUN_ID}"
    --base-port "${pot}"
    --num-envs 1
    --timeout-wait 600
    --env-args "${env_args[@]}"
  )

  count_frames_in_dir() {
    local d="$1"
    shopt -s nullglob
    local a=("$d"/frame_*.jpg)
    local b=("$d"/frame_*.png)
    shopt -u nullglob
    echo "$(( ${#a[@]} + ${#b[@]} ))"
  }

  local start_ts
  start_ts="$(date +%s 2>/dev/null || echo 0)"

  # Start mlagents/Unity first so we have PGID available for the progress sub-shell.
  local pgid_file
  pgid_file="${VIDEO_ROOT}/.watcher_pgid_step_${step}.txt"
  rm -f "${pgid_file}" 2>/dev/null || true

  if [ "${FORCE_XVFB}" = "1" ] && command -v xvfb-run >/dev/null 2>&1; then
    echo "[watcher] running inference under xvfb-run (FORCE_XVFB=1)"
    if [ "${MAX_EVAL_SECONDS}" -le 0 ]; then
      setsid env PYTHONUNBUFFERED=1 PYTHONWARNINGS="ignore::FutureWarning" xvfb-run -a "${mlagents_cmd[@]}" &
      CURRENT_ML_PID="$!"
      CURRENT_PGID="$!"
      echo "${CURRENT_PGID}" > "${pgid_file}" 2>/dev/null || true
    else
      setsid env PYTHONUNBUFFERED=1 PYTHONWARNINGS="ignore::FutureWarning" timeout --signal=INT "${MAX_EVAL_SECONDS}" xvfb-run -a "${mlagents_cmd[@]}" &
      CURRENT_ML_PID="$!"
      CURRENT_PGID="$!"
      echo "${CURRENT_PGID}" > "${pgid_file}" 2>/dev/null || true
    fi
  else
    if [ -z "${DISPLAY:-}" ]; then
      echo "[watcher] ERROR: DISPLAY is empty and FORCE_XVFB=0. Cannot render/capture video."
      restore_watcher_checkpoint_staging
      return 1
    fi
    echo "[watcher] running inference with DISPLAY=${DISPLAY} (FORCE_XVFB=0)"
    if [ "${MAX_EVAL_SECONDS}" -le 0 ]; then
      setsid env PYTHONUNBUFFERED=1 PYTHONWARNINGS="ignore::FutureWarning" "${mlagents_cmd[@]}" &
      CURRENT_ML_PID="$!"
      CURRENT_PGID="$!"
      echo "${CURRENT_PGID}" > "${pgid_file}" 2>/dev/null || true
    else
      setsid env PYTHONUNBUFFERED=1 PYTHONWARNINGS="ignore::FutureWarning" timeout --signal=INT "${MAX_EVAL_SECONDS}" "${mlagents_cmd[@]}" &
      CURRENT_ML_PID="$!"
      CURRENT_PGID="$!"
      echo "${CURRENT_PGID}" > "${pgid_file}" 2>/dev/null || true
    fi
  fi

  # Periodic progress so it doesn't look "stuck".
  local progress_pid=""
  (
    while true; do
      sleep 10 || exit 0
      local now_ts elapsed_s ca cb cc
      now_ts="$(date +%s 2>/dev/null || echo 0)"
      elapsed_s=$(( now_ts - start_ts ))
      ca="0"; cb="0"; cc="0"
      if [ "${CAPTURE_CAM_A}" = "1" ]; then ca="$(count_frames_in_dir "${cap_di_a}")"; fi
      if [ "${CAPTURE_CAM_B}" = "1" ]; then cb="$(count_frames_in_dir "${cap_di_b}")"; fi
      if [ "${CAPTURE_CAM_C}" = "1" ]; then cc="$(count_frames_in_dir "${cap_di_c}")"; fi
      echo "[watcher] progress step=${step}: t=${elapsed_s}s frames camA=${ca} camB=${cb} overhead=${cc} / target=${target_frames}"

      if [ "${CAPTURE_CAM_A}" = "1" ] && [ "${ca}" -ge "${target_frames}" ] 2>/dev/null; then
        echo "[watcher] reached target frames camA=${ca}/${target_frames} — stopping eval run."
        local pgid=""
        pgid="$(cat "${pgid_file}" 2>/dev/null || true)"
        if [ -n "${pgid}" ]; then
          kill -INT -- "-${pgid}" 2>/dev/null || true
          sleep 0.3 || true
          kill -TERM -- "-${pgid}" 2>/dev/null || true
          sleep 0.3 || true
          kill -KILL -- "-${pgid}" 2>/dev/null || true
        fi
        exit 0
      fi
    done
  ) &
  progress_pid="$!"
  CURRENT_PROGRESS_PID="${progress_pid}"

  # Wait for the run to finish (either naturally or because progress loop killed the PGID).
  if [ -n "${CURRENT_ML_PID}" ]; then
    wait "${CURRENT_ML_PID}" || true
  fi
  CURRENT_ML_PID=""
  CURRENT_PGID=""

  if [ -n "${progress_pid}" ]; then
    kill "${progress_pid}" 2>/dev/null || true
    wait "${progress_pid}" 2>/dev/null || true
  fi
  CURRENT_PROGRESS_PID=""
  rm -f "${pgid_file}" 2>/dev/null || true

  local fa fb fc
  fa="0"; fb="0"; fc="0"
  if [ "${CAPTURE_CAM_A}" = "1" ]; then fa="$(count_frames_in_dir "${cap_di_a}")"; fi
  if [ "${CAPTURE_CAM_B}" = "1" ]; then fb="$(count_frames_in_dir "${cap_di_b}")"; fi
  if [ "${CAPTURE_CAM_C}" = "1" ]; then fc="$(count_frames_in_dir "${cap_di_c}")"; fi
  echo "[watcher] finished step=${step} frames: camA=${fa} camB=${fb} overhead=${fc}"

  if [ "${CAPTURE_CAM_A}" = "1" ] && [ "${fa}" = "0" ]; then
    rm -f "${meta_di_a}/capture_started.txt" 2>/dev/null || true
    rm -f "${VIDEO_ROOT}/step_${step}_camA.mp4" 2>/dev/null || true
    echo "[watcher] WARN: camA captured 0 frames (inference likely crashed). Will allow retry on next scan."
  fi

  if command -v ffmpeg >/dev/null 2>&1; then
    pick_frame_ext() {
      local cap_dir="$1"
      if [ -f "${cap_dir}/frame_000000.jpg" ]; then
        echo "jpg"
      else
        echo "png"
      fi
    }

    fps_from_meta() {
      local cap_dir="$1"
      local meta="${cap_dir}/capture_meta.txt"
      [ -f "$meta" ] || { echo ""; return; }
      local wall frames
      wall=$(grep -E '^wall_seconds=' "$meta" 2>/dev/null | head -1 | cut -d= -f2- | tr -d '\r')
      frames=$(grep -E '^frame_count=' "$meta" 2>/dev/null | head -1 | cut -d= -f2- | tr -d '\r')
      [ -n "$wall" ] && [ -n "$frames" ] || { echo ""; return; }
      awk -v w="$wall" -v f="$frames" 'BEGIN {
        if (w > 0.05 && f > 0) {
          x = f / w
          if (x > 120) x = 120
          if (x < 1) x = 1
          printf "%.6f", x
        }
      }'
    }

    pick_video_fps() {
      local cap_dir="$1"
      if [ "${VIDEO_FPS_MODE}" = "fixed" ]; then
        echo "${CAPTURE_FPS}"
        return
      fi
      local fps
      fps="$(fps_from_meta "$cap_dir")"
      if [ -n "$fps" ]; then
        echo "$fps"
      else
        echo "${VIDEO_FPS_FALLBACK}"
      fi
    }

    local fps_a fps_b fps_c
    fps_a=""; fps_b=""; fps_c=""
    if [ "${CAPTURE_CAM_A}" = "1" ]; then fps_a="$(pick_video_fps "${cap_di_a}")"; fi
    if [ "${CAPTURE_CAM_B}" = "1" ]; then fps_b="$(pick_video_fps "${cap_di_b}")"; fi
    if [ "${CAPTURE_CAM_C}" = "1" ]; then fps_c="$(pick_video_fps "${cap_di_c}")"; fi
    echo "[watcher] ffmpeg fps camA=${fps_a:-off} camB=${fps_b:-off} overhead=${fps_c:-off}"

    local ext_a ext_b ext_c
    ext_a=""; ext_b=""; ext_c=""
    if [ "${CAPTURE_CAM_A}" = "1" ]; then ext_a="$(pick_frame_ext "${cap_di_a}")"; fi
    if [ "${CAPTURE_CAM_B}" = "1" ]; then ext_b="$(pick_frame_ext "${cap_di_b}")"; fi
    if [ "${CAPTURE_CAM_C}" = "1" ]; then ext_c="$(pick_frame_ext "${cap_di_c}")"; fi
    echo "[watcher] frame ext camA=${ext_a:-off} camB=${ext_b:-off} overhead=${ext_c:-off}"

    if [ "${CAPTURE_CAM_A}" = "1" ] && [ "${fa}" != "0" ]; then
      local out_mp4_a="${VIDEO_ROOT}/step_${step}_camA.mp4"
      ffmpeg -y -framerate "${fps_a}" -i "${cap_di_a}/frame_%06d.${ext_a}" -c:v libx264 -pix_fmt yuv420p -crf "${FFMPEG_CRF}" -preset "${FFMPEG_PRESET}" "${out_mp4_a}" >/dev/null 2>&1 || true
      echo "[watcher] saved ${out_mp4_a}"
      if [ "${CLEANUP_TMP_FRAMES}" = "1" ] && [ -n "${CAPTURE_TMP_ROOT}" ]; then
        rm -rf "${cap_di_a}" 2>/dev/null || true
      fi
    fi
  else
    echo "[watcher] ffmpeg not found"
  fi

  restore_watcher_checkpoint_staging
}

trap cleanup_on_exit INT TERM

echo "[watcher] scanning checkpoints in ${RESULTS_DIR}"

while true; do
  if [ "$(python_has_any_checkpoint "${BEHAVIOR_DIR}")" != "1" ]; then
    echo "[watcher] no *-*.pt in ${BEHAVIOR_DIR} — waiting 20s (not scanning; checkpoints absent or unreadable)."
    sleep 20
    continue
  fi

  # Avoid `while read` under `set -e`: EOF from read can exit 1 and abort the loop early.
  mapfile -t EVAL_STEPS < <(list_steps_to_eval)
  if [ "${#EVAL_STEPS[@]}" -eq 0 ]; then
    echo "[watcher] checkpoint folder has .pt files but step list is empty (filters?) — waiting 20s."
    sleep 20
    continue
  fi

  for step in "${EVAL_STEPS[@]}"; do
    [ -n "${step}" ] || continue
    if [ -n "${ONLY_STEP}" ] && [ "${step}" != "${ONLY_STEP}" ]; then
      continue
    fi
    if ! already_encoded_target "${step}"; then
      run_eval_for_step "${step}"
    else
      echo "[watcher] skip step_${step} (already encoded)"
    fi
  done

  if [ "${EVAL_RUN_ONCE}" = "1" ]; then
    echo "[watcher] EVAL_RUN_ONCE=1 — done, exiting."
    break
  fi

  echo "[watcher] round done — next scan in 20s."
  sleep 20
done

