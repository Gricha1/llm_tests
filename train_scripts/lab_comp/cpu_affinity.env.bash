#!/usr/bin/env bash
# CPU affinity для lab_comp (Ryzen 5 3500 = 6 ядер).
# Стрим (Python+Unity+по желанию OBS): последние ядра.
# Train (mlagents + headless Unity): остальные.
#
#   source train_scripts/lab_comp/cpu_affinity.env.bash
#
# Переопределение:
#   FOREST_STREAM_CPUS=5 FOREST_TRAIN_CPUS=0-4

# shellcheck disable=SC2034
# Стрим Unity+Python — выделенные ядра. OBS лучше НЕ сюда: иначе x264 ест кадры у Unity.
FOREST_STREAM_CPUS="${FOREST_STREAM_CPUS:-4,5}"
FOREST_TRAIN_CPUS="${FOREST_TRAIN_CPUS:-0-3}"
# OBS encode на ядрах train — захват идёт через X, CPU encode не должен делить 4,5 с Unity.
FOREST_OBS_CPUS="${FOREST_OBS_CPUS:-0-3}"

forest_taskset() {
  local cpus="$1"
  shift
  if command -v taskset >/dev/null 2>&1 && [ -n "${cpus}" ]; then
    exec taskset -c "${cpus}" "$@"
  fi
  exec "$@"
}
