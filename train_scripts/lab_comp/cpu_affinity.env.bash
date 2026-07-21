#!/usr/bin/env bash
# CPU affinity для lab_comp (Ryzen 5 3500 = 6 ядер).
# Train (mlagents + headless Unity): максимум ядер.
# Стрим (Python onnx + Unity) и OBS: одно общее ядро.
#
#   source train_scripts/lab_comp/cpu_affinity.env.bash
#
# Переопределение:
#   FOREST_STREAM_CPUS=4 FOREST_OBS_CPUS=4 FOREST_TRAIN_CPUS=0-3,5

# shellcheck disable=SC2034
FOREST_STREAM_CPUS="${FOREST_STREAM_CPUS:-5}"
FOREST_TRAIN_CPUS="${FOREST_TRAIN_CPUS:-0-4}"
FOREST_OBS_CPUS="${FOREST_OBS_CPUS:-5}"

forest_taskset() {
  local cpus="$1"
  shift
  if command -v taskset >/dev/null 2>&1 && [ -n "${cpus}" ]; then
    exec taskset -c "${cpus}" "$@"
  fi
  exec "$@"
}
