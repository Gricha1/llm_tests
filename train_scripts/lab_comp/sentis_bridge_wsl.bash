#!/usr/bin/env bash
# WSL/Windows: onnx→sentis локально, .sentis на lab_comp (обход glibc на Ubuntu 18.04).
#
#   RUN_ID=run_60 wsl bash train_scripts/lab_comp/sentis_bridge_wsl.bash
#
# На сервере stream_inference_watch может работать без Unity Editor — только hot reload.

set -eu
set -o pipefail

ROOT="/mnt/c/Grisha/unity_projects/forest_survival"
cd "${ROOT}"

KEY="${SSH_KEY:-${HOME}/.ssh/lab_comp_key}"
REMOTE="${REMOTE:-reedgern@192.168.194.7}"
REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"
RUN_ID="${RUN_ID:-run_60}"
POLL_SEC="${POLL_SEC:-15}"

RSYNC_SSH="ssh -i ${KEY} -o StrictHostKeyChecking=no"

LOCAL_WEIGHTS="${ROOT}/stream_weights/${RUN_ID}"
LOCAL_ONNX="${LOCAL_WEIGHTS}/onnx"
REMOTE_WEIGHTS="${REMOTE_DIR}/stream_weights/${RUN_ID}"
REMOTE_ONNX="${REMOTE_WEIGHTS}/onnx"

mkdir -p "${LOCAL_ONNX}" "${LOCAL_WEIGHTS}"

# shellcheck source=../lib/resolve_unity_editor.bash
source "${ROOT}/train_scripts/lib/resolve_unity_editor.bash"

if ! UNITY_BIN="$(resolve_unity_editor)"; then
  echo "[sentis_bridge] ERROR: Unity Editor не найден (WSL или Windows Hub)." >&2
  exit 1
fi
export UNITY_EDITOR="${UNITY_BIN}"

echo "[sentis_bridge] RUN_ID=${RUN_ID} poll=${POLL_SEC}s Unity=${UNITY_BIN}"
echo "[sentis_bridge] remote onnx: ${REMOTE}:${REMOTE_ONNX}"

onnx_signature() {
  local sig="" f
  for f in "${LOCAL_ONNX}"/*.onnx; do
    [ -f "${f}" ] || continue
    sig+="$(basename "${f}"):$(stat -c '%s:%Y' "${f}" 2>/dev/null)|"
  done
  echo "${sig}"
}

LAST_SIG=""

while true; do
  rsync -az -e "${RSYNC_SSH}" \
    "${REMOTE}:${REMOTE_ONNX}/" \
    "${LOCAL_ONNX}/" 2>/dev/null || true

  sig="$(onnx_signature)"
  if [ -n "${sig}" ] && [ "${sig}" != "${LAST_SIG}" ]; then
    echo "[sentis_bridge] новые onnx — конвертирую sentis локально..."
    if FOREST_STREAM_ONNX_DIR="${LOCAL_ONNX}" \
       FOREST_STREAM_SENTIS_DIR="${LOCAL_WEIGHTS}" \
       FOREST_STREAM_SENTIS_REQUIRED=1 \
       bash train_scripts/export_stream_sentis.bash; then
      rsync -avz --progress -e "${RSYNC_SSH}" \
        "${LOCAL_WEIGHTS}/"*.sentis \
        "${REMOTE}:${REMOTE_WEIGHTS}/"
      LAST_SIG="${sig}"
      echo "[sentis_bridge] sentis на сервере: ${REMOTE_WEIGHTS}/"
    else
      echo "[sentis_bridge] ERROR: конвертация не удалась" >&2
    fi
  fi

  sleep "${POLL_SEC}"
done
