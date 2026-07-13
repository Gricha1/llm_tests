#!/usr/bin/env bash
set -eu
set -o pipefail

# Стрим для OBS: Unity запускается ОДИН РАЗ, веса подтягиваются через StreamWeightsHotReload (.sentis).
# Headless-обучение параллельно: тот же RUN_ID.
#
#   RUN_ID=run_56 bash stream_inference_watch.bash
#
# Переменные:
#   BUILD=stream_forest_survival_2_12_07_2026
#   RUN_ID=jack_lily_george_1
#   TIME_SCALE=1
#   POLL_SEC=15
#   UNITY_EDITOR=   (опционально: batch onnx→sentis)

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
cd "${ROOT}"

# shellcheck source=train_scripts/lab_comp/lab_comp_env.bash
if [ -f "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash" ]; then
  source "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash"
fi

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
RUN_ID="${RUN_ID:-jack_lily_george_1}"
TIME_SCALE="${TIME_SCALE:-1}"
POLL_SEC="${POLL_SEC:-15}"
export DISPLAY="${DISPLAY:-:1}"

BUILD_PATH="build_versions/${BUILD%.x86_64}.x86_64"
RUN_DIR="results/${RUN_ID}"
WEIGHTS_DIR="stream_weights/${RUN_ID}"
ONNX_DIR="${WEIGHTS_DIR}/onnx"

BEHAVIORS=(JackLowLevelAgent LilyLowLevelAgent GeorgeLowLevelAgent)

# shellcheck source=train_scripts/lib/resolve_unity_editor.bash
source "${ROOT}/train_scripts/lib/resolve_unity_editor.bash"

if ! UNITY_EDITOR="$(resolve_unity_editor)"; then
  echo "[stream] WARN: Unity Editor не найден — onnx→sentis не будет работать." >&2
  echo "[stream] Установи Editor или задай:" >&2
  echo "  export UNITY_EDITOR=\"\$HOME/Unity/Hub/Editor/6000.0.26f1/Editor/Unity\"" >&2
else
  export UNITY_EDITOR
  echo "[stream] Unity Editor: ${UNITY_EDITOR}"
fi

if [ ! -f "${BUILD_PATH}" ]; then
  echo "ERROR: ${BUILD_PATH} не найден" >&2
  exit 1
fi
chmod +x "${BUILD_PATH}" 2>/dev/null || true

mkdir -p "${ONNX_DIR}" "${WEIGHTS_DIR}"

UNITY_PID=""
LAST_WEIGHTS_SIG=""

stop_unity() {
  if [ -n "${UNITY_PID}" ] && kill -0 "${UNITY_PID}" 2>/dev/null; then
    echo "[stream] stop Unity pid=${UNITY_PID}"
    kill -INT "${UNITY_PID}" 2>/dev/null || true
    wait "${UNITY_PID}" 2>/dev/null || true
  fi
  UNITY_PID=""
}

cleanup() {
  stop_unity
}
trap cleanup EXIT INT TERM

wait_for_stable_file() {
  local f="$1"
  local tries="${2:-15}"
  local delay="${3:-1}"
  local i prev=""
  for ((i = 0; i < tries; i++)); do
    [ -f "${f}" ] || return 1
    local sz
    sz="$(stat -c %s "${f}" 2>/dev/null || echo 0)"
    if [ -n "${prev}" ] && [ "${prev}" = "${sz}" ] && [ "${sz}" -gt 0 ]; then
      return 0
    fi
    prev="${sz}"
    sleep "${delay}"
  done
  return 1
}

latest_step_in_run() {
  local max_step=0
  local beh dir step f base
  for beh in "${BEHAVIORS[@]}"; do
    step="$(latest_step_for_behavior "${beh}")"
    if [ "${step}" -gt "${max_step}" ]; then
      max_step="${step}"
    fi
  done
  echo "${max_step}"
}

latest_step_for_behavior() {
  local beh="$1"
  local max_step=0
  local dir="${RUN_DIR}/${beh}"
  local f base step
  [ -d "${dir}" ] || { echo 0; return; }
  for f in "${dir}/${beh}-"*.pt; do
    [ -f "${f}" ] || continue
    base="$(basename "${f}")"
    step="${base#${beh}-}"
    step="${step%.pt}"
    if [[ "${step}" =~ ^[0-9]+$ ]] && [ "${step}" -gt "${max_step}" ]; then
      max_step="${step}"
    fi
  done
  echo "${max_step}"
}

weights_signature() {
  local sig="" beh step src
  for beh in "${BEHAVIORS[@]}"; do
    step="$(latest_step_for_behavior "${beh}")"
    if [ "${step}" -le 0 ]; then
      sig+="${beh}:none|"
      continue
    fi
    src="$(find_onnx_for_behavior "${beh}" "${step}" 2>/dev/null || true)"
    if [ -z "${src}" ]; then
      sig+="${beh}:no-onnx@${step}|"
      continue
    fi
    sig+="${beh}:$(stat -c '%s:%Y' "${src}" 2>/dev/null || echo "${src}")|"
  done
  echo "${sig}"
}

find_onnx_for_behavior() {
  local beh="$1"
  local step="$2"
  local dir="${RUN_DIR}/${beh}"
  local candidates=()

  if [ -n "${step}" ] && [ "${step}" != "checkpoint" ]; then
    candidates+=(
      "${dir}/${beh}-${step}.onnx"
      "${dir}/${beh}.onnx"
      "${RUN_DIR}/${beh}.onnx"
    )
  fi

  candidates+=(
    "${dir}/${beh}.onnx"
    "${RUN_DIR}/${beh}.onnx"
  )

  local f newest=""
  for f in "${dir}/${beh}-"*.onnx; do
    [ -f "${f}" ] || continue
    newest="${f}"
  done
  [ -n "${newest}" ] && candidates+=("${newest}")

  local c
  for c in "${candidates[@]}"; do
    [ -f "${c}" ] && { echo "${c}"; return 0; }
  done
  return 1
}

sync_onnx_from_run() {
  local ok=0
  local beh src dst step
  for beh in "${BEHAVIORS[@]}"; do
    step="$(latest_step_for_behavior "${beh}")"
    if [ "${step}" -le 0 ]; then
      step="checkpoint"
    fi
    src="$(find_onnx_for_behavior "${beh}" "${step}" || true)"
    if [ -z "${src}" ]; then
      echo "[stream] WARN: нет onnx для ${beh} step=${step} (ждём чекпоинт)"
      continue
    fi
    wait_for_stable_file "${src}" 10 1 || continue
    dst="${ONNX_DIR}/${beh}.onnx"
    cp -f "${src}" "${dst}"
    ok=1
    echo "[stream] sync onnx ${beh} <- ${src}"
  done
  [ "${ok}" -eq 1 ]
}

convert_onnx_to_sentis() {
  FOREST_STREAM_ONNX_DIR="${ROOT}/${ONNX_DIR}" \
  FOREST_STREAM_SENTIS_DIR="${ROOT}/${WEIGHTS_DIR}" \
  FOREST_STREAM_SENTIS_REQUIRED=1 \
    bash train_scripts/export_stream_sentis.bash
}

start_unity_once() {
  if [ -n "${UNITY_PID}" ] && kill -0 "${UNITY_PID}" 2>/dev/null; then
    return 0
  fi

  echo "[stream] start Unity (once) weights=${WEIGHTS_DIR} DISPLAY=${DISPLAY} time-scale=${TIME_SCALE}"
  export FOREST_TIME_SCALE="${TIME_SCALE}"
  "${BUILD_PATH}" \
    -forestStreamOnly \
    -forestStreamWeightsDir "${ROOT}/${WEIGHTS_DIR}" &
  UNITY_PID=$!
  sleep 3
}

echo "[stream] watch RUN_ID=${RUN_ID} poll=${POLL_SEC}s build=${BUILD_PATH}"
echo "[stream] Unity hot-reload из ${WEIGHTS_DIR}/*.sentis (без mlagents-learn, без рестарта)"

while true; do
  if [ ! -d "${RUN_DIR}/JackLowLevelAgent" ] && [ -z "${LAST_WEIGHTS_SIG}" ]; then
    echo "[stream] жду первый чекпоинт от headless-обучения (RUN_ID=${RUN_ID})..."
  fi

  sig="$(weights_signature)"
  if [ "${sig}" != "${LAST_WEIGHTS_SIG}" ]; then
    echo "[stream] новые веса — sync onnx + sentis (Unity не перезапускаем)"
    if sync_onnx_from_run; then
      if convert_onnx_to_sentis; then
        LAST_WEIGHTS_SIG="${sig}"
        ls -la "${WEIGHTS_DIR}"/*.sentis 2>/dev/null || echo "[stream] WARN: .sentis пока нет"
      else
        echo "[stream] ERROR: onnx→sentis не удался." >&2
        echo "[stream] Если GLIBC_2.28 — Ubuntu 18.04: с WSL запусти sentis_bridge_wsl.bash" >&2
      fi
    fi
  fi

  start_unity_once

  if [ -n "${UNITY_PID}" ] && ! kill -0 "${UNITY_PID}" 2>/dev/null; then
    echo "[stream] Unity упал — перезапуск через 5с"
    UNITY_PID=""
    sleep 5
  fi

  sleep "${POLL_SEC}"
done
