#!/usr/bin/env bash
# Применить affinity к УЖЕ запущенным train/stream (без рестарта).
#
#   bash train_scripts/lab_comp/pin_running_cpus.bash
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
# shellcheck source=cpu_affinity.env.bash
source "${ROOT}/train_scripts/lab_comp/cpu_affinity.env.bash"

pin_match() {
  local cpus="$1"
  local pattern="$2"
  local label="$3"
  local pids
  pids="$(pgrep -f "${pattern}" 2>/dev/null || true)"
  if [ -z "${pids}" ]; then
    echo "[pin] ${label}: нет процессов"
    return 0
  fi
  local pid
  for pid in ${pids}; do
    if taskset -cp "${cpus}" "${pid}" >/dev/null 2>&1; then
      echo "[pin] ${label} pid=${pid} -> CPU ${cpus}"
    else
      echo "[pin] WARN: не смог pin ${label} pid=${pid}" >&2
    fi
  done
}

echo "[pin] STREAM_CPUS=${FOREST_STREAM_CPUS} TRAIN_CPUS=${FOREST_TRAIN_CPUS} OBS_CPUS=${FOREST_OBS_CPUS}"

pin_match "${FOREST_TRAIN_CPUS}" "mlagents-learn" "mlagents"
pin_match "${FOREST_TRAIN_CPUS}" "stream_forest_survival.*-batchmode" "train-unity"
pin_match "${FOREST_STREAM_CPUS}" "stream_onnx_infer" "stream-python"
pin_match "${FOREST_STREAM_CPUS}" "forestStreamOnly" "stream-unity"
if pgrep -x obs >/dev/null 2>&1; then
  for pid in $(pgrep -x obs); do
    taskset -cp "${FOREST_OBS_CPUS}" "${pid}" >/dev/null 2>&1 \
      && echo "[pin] obs pid=${pid} -> CPU ${FOREST_OBS_CPUS}" || true
  done
fi

echo "[pin] done"
