#!/usr/bin/env bash
# Стрим OBS: Unity + onnxruntime (отдельно от mlagents-learn).
#
#   RUN_ID=run_75 bash train_scripts/lab_comp/run_stream_onnx.bash
#
# По умолчанию — супервизор: #restart_stream пишет .stream_restart_request,
# этот скрипт убивает ТОЛЬКО стрим (не train) и поднимает заново.
# Стоп без рестарта: Ctrl+C  или  touch .stream_stop_request
# Без супервизора: FOREST_STREAM_SUPERVISE=0 bash ...
#
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

FLAG="${ROOT}/.stream_restart_request"
STREAM_RESTART_POLL_SEC="${STREAM_RESTART_POLL_SEC:-1}"
COOLDOWN_SEC="${STREAM_RESTART_COOLDOWN_SEC:-5}"
# stream_onnx_infer выходит с 75 при флаге #restart_stream
RESTART_EXIT=75

# --- супервизор (внешний цикл) ---
if [ "${FOREST_STREAM_SUPERVISE:-1}" = "1" ] && [ "${FOREST_STREAM_INNER:-0}" != "1" ]; then
  # shellcheck source=lab_comp_env.bash
  if [ -f "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash" ]; then
    # shellcheck disable=SC1091
    source "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash"
  fi

  STOP_FLAG="${ROOT}/.stream_stop_request"
  child=""
  stopping=0

  kill_stream_only() {
    echo "[stream_onnx] kill STREAM only (не train)..."
    pkill -9 -f 'stream_onnx_infer\.py' 2>/dev/null || true
    pkill -9 -f 'forestStreamOnly' 2>/dev/null || true
    sleep 1
  }

  spawn_watch_pid=""
  start_spawn_watch() {
    # FOREST_SPAWN_WATCH=0 — выкл. По умолчанию: детект пустых trees/sheep → ResetSpawners.
    if [ "${FOREST_SPAWN_WATCH:-1}" != "1" ]; then
      return 0
    fi
    if [ -n "${spawn_watch_pid}" ] && kill -0 "${spawn_watch_pid}" 2>/dev/null; then
      return 0
    fi
    mkdir -p "${ROOT}/results/${RUN_ID:-_}"
    echo "[stream_onnx] spawn_repair watch RUN_ID=${RUN_ID:-?}…"
    set +e
    (
      source ~/anaconda3/etc/profile.d/conda.sh 2>/dev/null || true
      conda activate mlagents 2>/dev/null || true
      # Рестарт стрима только при FOREST_SPAWN_WATCH_RESTART=1 (иначе цикл рестартов на старом DLL).
      extra=()
      if [ "${FOREST_SPAWN_WATCH_RESTART:-0}" != "1" ]; then
        extra+=(--no-restart)
      fi
      exec python3 -u "${ROOT}/train_scripts/lab_comp/watch_stream_spawn_repair.py" \
        --run-id "${RUN_ID:-}" \
        --interval "${FOREST_SPAWN_WATCH_INTERVAL:-20}" \
        --bad-streak "${FOREST_SPAWN_WATCH_STREAK:-3}" \
        "${extra[@]}"
    ) >> "${ROOT}/results/${RUN_ID:-_}/spawn_repair_watch.log" 2>&1 &
    spawn_watch_pid=$!
    set -e
    echo "[stream_onnx] spawn_repair pid=${spawn_watch_pid} log=results/${RUN_ID:-_}/spawn_repair_watch.log"
  }
  stop_spawn_watch() {
    if [ -n "${spawn_watch_pid}" ]; then
      kill -TERM "${spawn_watch_pid}" 2>/dev/null || true
      wait "${spawn_watch_pid}" 2>/dev/null || true
      spawn_watch_pid=""
    fi
    pkill -f 'watch_stream_spawn_repair\.py' 2>/dev/null || true
  }

  stop_supervisor() {
    stopping=1
    echo "[stream_onnx] STOP (Ctrl+C / SIGTERM) — супервизор не перезапускает"
    rm -f "${FLAG}" "${STOP_FLAG}"
    stop_spawn_watch
    kill_stream_only
    if [ -n "${child}" ]; then
      kill -TERM "${child}" 2>/dev/null || true
      wait "${child}" 2>/dev/null || true
    fi
    exit 0
  }
  trap stop_supervisor INT TERM

  echo "[stream_onnx] SUPERVISE=1 flag=${FLAG} RUN_ID=${RUN_ID:-?}"
  echo "[stream_onnx] стоп: Ctrl+C  или  touch ${STOP_FLAG}"
  rm -f "${FLAG}" "${STOP_FLAG}"

  while true; do
    if [ -f "${STOP_FLAG}" ]; then
      echo "[stream_onnx] найден ${STOP_FLAG} — выход"
      rm -f "${STOP_FLAG}"
      stop_spawn_watch
      exit 0
    fi

    echo "[stream_onnx] старт inner..."
    set +e
    # Не пробрасываем POLL_SEC в inner: иначе onnx получит 1с reload вместо 0.
    FOREST_STREAM_INNER=1 env -u POLL_SEC \
      bash "${ROOT}/train_scripts/lab_comp/run_stream_onnx.bash" "$@" &
    child=$!
    set -e
    start_spawn_watch

    restarted=0
    while kill -0 "${child}" 2>/dev/null; do
      if [ -f "${STOP_FLAG}" ]; then
        echo "[stream_onnx] ${STOP_FLAG} — останавливаем"
        rm -f "${STOP_FLAG}"
        stop_spawn_watch
        kill_stream_only
        wait "${child}" 2>/dev/null || true
        exit 0
      fi
      if [ -f "${FLAG}" ]; then
        echo "[stream_onnx] флаг ${FLAG} — #restart_stream"
        cat "${FLAG}" 2>/dev/null | sed 's/^/[stream_onnx]   /' || true
        rm -f "${FLAG}"
        kill_stream_only
        wait "${child}" 2>/dev/null || true
        restarted=1
        break
      fi
      sleep "${STREAM_RESTART_POLL_SEC}"
    done

    if [ "${stopping}" -ne 0 ]; then
      exit 0
    fi

    if [ -f "${STOP_FLAG}" ]; then
      echo "[stream_onnx] ${STOP_FLAG} после child — супервизор выходит (не рестарт)"
      rm -f "${STOP_FLAG}" "${FLAG}"
      kill_stream_only
      exit 0
    fi

    if [ "${restarted}" -eq 0 ]; then
      set +e
      wait "${child}"
      code=$?
      set -e
      if [ -f "${STOP_FLAG}" ]; then
        echo "[stream_onnx] ${STOP_FLAG} — супервизор выходит (не рестарт)"
        rm -f "${STOP_FLAG}" "${FLAG}"
        kill_stream_only
        exit 0
      fi
      # 130=SIGINT, 143=SIGTERM, 137=SIGKILL, 0=нормальный выход — не рестартим
      if [ "${code}" -eq "${RESTART_EXIT}" ]; then
        echo "[stream_onnx] python exit ${RESTART_EXIT} (#restart_stream)"
        restarted=1
      elif [ "${code}" -eq 130 ] || [ "${code}" -eq 143 ] || [ "${code}" -eq 137 ] || [ "${code}" -eq 9 ] || [ "${code}" -eq 0 ]; then
        echo "[stream_onnx] остановлен code=${code} — супервизор выходит (не рестарт)"
        kill_stream_only
        exit 0
      else
        echo "[stream_onnx] стрим упал code=${code} — рестарт через ${COOLDOWN_SEC}с"
      fi
    fi

    # Иначе после падения старый python держит :7000 → UnityWorkerInUseException.
    kill_stream_only
    rm -f "${FLAG}"
    sleep "${COOLDOWN_SEC}"
  done
fi

# --- один запуск (INNER) ---
# shellcheck source=lab_comp_env.bash
if [ -f "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash" ]; then
  # shellcheck disable=SC1091
  source "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash"
fi

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
RUN_ID="${RUN_ID:?задайте RUN_ID=run_XX}"
STREAM_PORT="${STREAM_PORT:-7000}"
TIME_SCALE="${TIME_SCALE:-1}"
# Только явный ONNX_POLL_SEC / POLL_SEC_ONNX (не общий POLL_SEC — его ставит супервизор).
POLL_SEC_ONNX="${ONNX_POLL_SEC:-${POLL_SEC_ONNX:-0}}"
TARGET_FPS="${TARGET_FPS:-30}"
QUALITY_LEVEL="${QUALITY_LEVEL:-1}"
STREAM_WIDTH="${STREAM_WIDTH:-1920}"
STREAM_HEIGHT="${STREAM_HEIGHT:-1080}"
TIMEOUT_WAIT="${TIMEOUT_WAIT:-300}"
export DISPLAY="${DISPLAY:-:1}"
# Из tmux часто нет XAUTHORITY → Unity не создаёт окно, OBS чёрный.
if [ -z "${XAUTHORITY:-}" ] || [ ! -f "${XAUTHORITY}" ]; then
  if [ -f "/run/user/$(id -u)/gdm/Xauthority" ]; then
    export XAUTHORITY="/run/user/$(id -u)/gdm/Xauthority"
  elif [ -f "${HOME}/.Xauthority" ]; then
    export XAUTHORITY="${HOME}/.Xauthority"
  else
    unset XAUTHORITY || true
  fi
fi
echo "[stream_onnx] DISPLAY=${DISPLAY} XAUTHORITY=${XAUTHORITY:-none}"

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
  echo "WARN: нет ${BUILD_STAMP} — билд могли не заливать через sync.bash (возможна старая версия)" >&2
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

# Stale Unity + чужие stream_onnx (не этот bash/parent).
pkill -9 -f 'forestStreamOnly' 2>/dev/null || true
my_pid=$$
while read -r pid; do
  [ -z "${pid}" ] && continue
  [ "${pid}" = "${my_pid}" ] && continue
  # не убивать предков (супервизор / tmux)
  if [ "${pid}" -eq "${PPID}" ] 2>/dev/null; then
    continue
  fi
  kill -9 "${pid}" 2>/dev/null || true
done < <(pgrep -f 'stream_onnx_infer\.py' 2>/dev/null || true)
sleep 2

port_in_use() {
  local p="$1"
  if command -v ss >/dev/null 2>&1; then
    ss -tuln 2>/dev/null | grep -qE "[:.]${p}[[:space:]]"
    return $?
  fi
  netstat -tuln 2>/dev/null | grep -qE "[:.]${p}[[:space:]]"
}

# Ждём освобождения порта, иначе ML-Agents падает с UnityWorkerInUseException.
for _ in $(seq 1 20); do
  if ! port_in_use "${STREAM_PORT}"; then
    break
  fi
  echo "[stream_onnx] порт ${STREAM_PORT} занят — жду..."
  # добиваем слушателя на этом порту (только stream python)
  if command -v fuser >/dev/null 2>&1; then
    fuser -k "${STREAM_PORT}/tcp" 2>/dev/null || true
  fi
  sleep 1
done
if port_in_use "${STREAM_PORT}"; then
  echo "[stream_onnx] WARN: ${STREAM_PORT} всё ещё занят, ищем свободный"
  while port_in_use "${STREAM_PORT}"; do
    STREAM_PORT=$((STREAM_PORT + 1))
  done
fi

echo "[stream_onnx] RUN_ID=${RUN_ID} DISPLAY=${DISPLAY} port=${STREAM_PORT}"
echo "[stream_onnx] build=${BUILD_PATH} ${STREAM_WIDTH}x${STREAM_HEIGHT} q=${QUALITY_LEVEL}"
echo "[stream_onnx] OBS: захват окна Unity (forest_survival)"

export FOREST_STREAM_EXTERNAL_BRAIN=1
export FOREST_TIME_SCALE="${TIME_SCALE}"
export FOREST_RESULTS_DIR="${ROOT}/results/${RUN_ID}"
export FOREST_STREAM_RESTART_FLAG="${FLAG}"
export __GL_SYNC_TO_VBLANK="${__GL_SYNC_TO_VBLANK:-0}"
export vblank_mode="${vblank_mode:-0}"
# Звук стрима → отдельный null-sink (не S/PDIF ~300мс). OBS берёт forest_stream.monitor.
export PULSE_SINK="${PULSE_SINK:-forest_stream}"
export PULSE_LATENCY_MSEC="${PULSE_LATENCY_MSEC:-80}"
bash "${ROOT}/train_scripts/lab_comp/mute_train_pulse_audio.bash" >/tmp/forest_mute_audio.log 2>&1 || true

# shellcheck source=cpu_affinity.env.bash
source "${ROOT}/train_scripts/lab_comp/cpu_affinity.env.bash"
echo "[stream_onnx] CPU affinity: stream=${FOREST_STREAM_CPUS} train=${FOREST_TRAIN_CPUS} obs=${FOREST_OBS_CPUS}"
echo "[stream_onnx] audio: PULSE_SINK=${PULSE_SINK} PULSE_LATENCY_MSEC=${PULSE_LATENCY_MSEC}"
renice -n "${FOREST_STREAM_NICE}" $$ >/dev/null 2>&1 || renice -n -10 $$ >/dev/null 2>&1 || true

if pgrep -x obs >/dev/null 2>&1; then
  for pid in $(pgrep -x obs); do
    taskset -cp "${FOREST_OBS_CPUS}" "${pid}" >/dev/null 2>&1 || true
    forest_renice_pid "${FOREST_OBS_NICE}" "${pid}"
  done
  echo "[stream_onnx] OBS pinned to CPU ${FOREST_OBS_CPUS} nice=${FOREST_OBS_NICE}"
fi

STREAM_CMD=(
  "${PY}" "${ROOT}/train_scripts/lab_comp/stream_onnx_infer.py"
  --run-id "${RUN_ID}"
  --env "${ROOT}/${BUILD_PATH}"
  --port "${STREAM_PORT}"
  --results-dir "${ROOT}/results"
  --poll-sec "${POLL_SEC_ONNX}"
  --time-scale "${TIME_SCALE}"
  --target-fps "${TARGET_FPS}"
  --quality-level "${QUALITY_LEVEL}"
  --width "${STREAM_WIDTH}"
  --height "${STREAM_HEIGHT}"
  --timeout "${TIMEOUT_WAIT}"
)

if command -v taskset >/dev/null 2>&1; then
  # После старта Unity: только mute явного train + unmute стрима (не глушим FMOD «по умолчанию»).
  (
    sleep 12
    bash "${ROOT}/train_scripts/lab_comp/mute_train_pulse_audio.bash" >/tmp/forest_mute_audio.log 2>&1 || true
    sleep 8
    bash "${ROOT}/train_scripts/lab_comp/mute_train_pulse_audio.bash" >>/tmp/forest_mute_audio.log 2>&1 || true
  ) &
  disown
  exec taskset -c "${FOREST_STREAM_CPUS}" "${STREAM_CMD[@]}"
fi
(
  sleep 12
  bash "${ROOT}/train_scripts/lab_comp/mute_train_pulse_audio.bash" >/tmp/forest_mute_audio.log 2>&1 || true
  sleep 8
  bash "${ROOT}/train_scripts/lab_comp/mute_train_pulse_audio.bash" >>/tmp/forest_mute_audio.log 2>&1 || true
) &
disown
exec "${STREAM_CMD[@]}"
