#!/usr/bin/env bash
# CPU affinity для lab_comp (Ryzen 5 3500 = 6 ядер).
# Train (mlagents + headless Unity): ядра 0-3 — туда стрим не пускаем.
# Стрим (Python onnx + Unity): 4-5. OBS: лучше одно ядро рядом, без драки с train.
#
# Важно: affinity не изолирует RAM/GPU/swap — тяжёлый train всё равно может
# давать редкие hitch на стриме через память/диск. Картинка в приоритете →
# stream nice ниже (важнее), train nice выше (уступает CPU).
#
#   source train_scripts/lab_comp/cpu_affinity.env.bash
#
# Переопределение:
#   FOREST_STREAM_CPUS=4-5 FOREST_OBS_CPUS=5 FOREST_TRAIN_CPUS=0-3

# shellcheck disable=SC2034
FOREST_STREAM_CPUS="${FOREST_STREAM_CPUS:-4-5}"
FOREST_TRAIN_CPUS="${FOREST_TRAIN_CPUS:-0-3}"
# OBS на 5 — меньше вытесняет Unity-стрим с ядра 4; оба всё ещё вне train 0-3.
FOREST_OBS_CPUS="${FOREST_OBS_CPUS:-5}"

# nice: меньше = важнее для планировщика.
FOREST_STREAM_NICE="${FOREST_STREAM_NICE:--15}"
FOREST_OBS_NICE="${FOREST_OBS_NICE:--10}"
FOREST_TRAIN_NICE="${FOREST_TRAIN_NICE:-10}"

forest_taskset() {
  local cpus="$1"
  shift
  if command -v taskset >/dev/null 2>&1 && [ -n "${cpus}" ]; then
    exec taskset -c "${cpus}" "$@"
  fi
  exec "$@"
}

forest_renice_pid() {
  local nice_val="$1"
  local pid="$2"
  if [ -z "${pid}" ] || [ ! -d "/proc/${pid}" ]; then
    return 0
  fi
  renice -n "${nice_val}" -p "${pid}" >/dev/null 2>&1 || true
}
