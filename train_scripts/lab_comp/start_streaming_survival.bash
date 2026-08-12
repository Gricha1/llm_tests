#!/usr/bin/env bash
# Streaming Survival: Unity без ML-Agents / без onnx inference.
# Не трогает train. Не запускает stream_onnx_infer.
#
#   bash train_scripts/lab_comp/start_streaming_survival.bash
#   BUILD=stream_forest_survival_2_12_07_2026 bash train_scripts/lab_comp/start_streaming_survival.bash
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
BUILD="${BUILD%.x86_64}"
BIN="${ROOT}/build_versions/${BUILD}.x86_64"
LOG_DIR="${ROOT}/results"
LOG="${LOG_DIR}/streaming_survival.log"
PID_FILE="${ROOT}/.streaming_survival.pid"
STAGE_SEC="${STREAMING_SURVIVAL_STAGE_SECONDS:-240}"

mkdir -p "${LOG_DIR}"

if [ ! -x "${BIN}" ] && [ ! -f "${BIN}" ]; then
  echo "ERROR: нет билда ${BIN}" >&2
  exit 1
fi

# стоп старый SS (не train, не onnx presentation если другой)
if [ -f "${PID_FILE}" ]; then
  old="$(cat "${PID_FILE}" || true)"
  if [ -n "${old}" ] && kill -0 "${old}" 2>/dev/null; then
    echo "[ss] stop old pid=${old}"
    kill -TERM "${old}" 2>/dev/null || true
    sleep 1
    kill -KILL "${old}" 2>/dev/null || true
  fi
  rm -f "${PID_FILE}"
fi
pkill -TERM -f 'forestStreamingSurvival' 2>/dev/null || true
sleep 1

export FOREST_STREAMING_SURVIVAL=1
export STREAMING_SURVIVAL_STAGE_SECONDS="${STAGE_SEC}"
export DISPLAY="${DISPLAY:-:1}"

echo "[ss] start BUILD=${BUILD} DISPLAY=${DISPLAY} stage=${STAGE_SEC}s"
nohup "${BIN}" \
  -forestStreamingSurvival \
  -forestStreamOnly \
  -logfile "${LOG}" \
  >"${LOG}.stdout" 2>&1 &
echo $! >"${PID_FILE}"
echo "[ss] pid=$(cat "${PID_FILE}") log=${LOG}"
sleep 3
tail -20 "${LOG}" 2>/dev/null || true
