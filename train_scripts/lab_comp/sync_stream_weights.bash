#!/usr/bin/env bash
# Фон: results/RUN_ID → stream_weights/RUN_ID (onnx + sentis) для hot reload worker 0.
#   bash train_scripts/lab_comp/sync_stream_weights.bash run_72
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

RUN_ID="${1:?RUN_ID}"
POLL_SEC="${POLL_SEC:-15}"

RUN_DIR="results/${RUN_ID}"
WEIGHTS_DIR="stream_weights/${RUN_ID}"
ONNX_DIR="${WEIGHTS_DIR}/onnx"
BEHAVIORS=(JackLowLevelAgent LilyLowLevelAgent GeorgeLowLevelAgent)

# shellcheck source=../lib/resolve_unity_editor.bash
source "${ROOT}/train_scripts/lib/resolve_unity_editor.bash"

mkdir -p "${ONNX_DIR}" "${WEIGHTS_DIR}"

LAST_SIG=""

wait_for_stable_file() {
  local f="$1"
  local tries="${2:-10}"
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

find_onnx_for_behavior() {
  local beh="$1"
  local step="$2"
  local dir="${RUN_DIR}/${beh}"
  local candidates=() f newest=""

  if [ -n "${step}" ] && [ "${step}" != "checkpoint" ]; then
    candidates+=(
      "${dir}/${beh}-${step}.onnx"
      "${dir}/${beh}.onnx"
      "${RUN_DIR}/${beh}.onnx"
    )
  fi
  candidates+=("${dir}/${beh}.onnx" "${RUN_DIR}/${beh}.onnx")
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

sync_onnx_from_run() {
  local ok=0 beh src dst step
  for beh in "${BEHAVIORS[@]}"; do
    step="$(latest_step_for_behavior "${beh}")"
    if [ "${step}" -le 0 ]; then
      step="checkpoint"
    fi
    src="$(find_onnx_for_behavior "${beh}" "${step}" || true)"
    if [ -z "${src}" ]; then
      continue
    fi
    wait_for_stable_file "${src}" 10 1 || continue
    dst="${ONNX_DIR}/${beh}.onnx"
    cp -f "${src}" "${dst}"
    ok=1
    echo "[sync_weights] onnx ${beh} <- ${src}"
  done
  [ "${ok}" -eq 1 ]
}

convert_onnx_to_sentis() {
  FOREST_STREAM_ONNX_DIR="${ROOT}/${ONNX_DIR}" \
  FOREST_STREAM_SENTIS_DIR="${ROOT}/${WEIGHTS_DIR}" \
  FOREST_STREAM_SENTIS_REQUIRED=1 \
  FOREST_FORCE_SENTIS_GLIBC=1 \
    bash train_scripts/export_stream_sentis.bash
}

echo "[sync_weights] RUN_ID=${RUN_ID} poll=${POLL_SEC}s -> ${WEIGHTS_DIR}"

run_once() {
  local sig
  sig="$(weights_signature)"
  if sync_onnx_from_run; then
    convert_onnx_to_sentis && LAST_SIG="${sig}"
  fi
}

if [ "${ONCE:-0}" = "1" ]; then
  run_once || true
  exit 0
fi

while true; do
  if [ ! -d "${RUN_DIR}/JackLowLevelAgent" ] && [ -z "${LAST_SIG}" ]; then
    echo "[sync_weights] жду results/${RUN_ID}..."
  fi

  sig="$(weights_signature)"
  if [ "${sig}" != "${LAST_SIG}" ]; then
    if sync_onnx_from_run; then
      if convert_onnx_to_sentis; then
        LAST_SIG="${sig}"
        ls -la "${WEIGHTS_DIR}"/*.sentis 2>/dev/null || true
      else
        echo "[sync_weights] WARN: onnx→sentis не удался." >&2
        echo "[sync_weights] Один раз на сервере: bash train_scripts/lab_comp/build_sentis_docker.bash" >&2
        echo "[sync_weights] На сервере нужен Unity Editor (detect_unity_editor.bash)" >&2
      fi
    fi
  fi

  sleep "${POLL_SEC}"
done
