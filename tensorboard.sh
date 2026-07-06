#!/bin/sh
# TensorBoard for ML-Agents logs under results/.
#
# Usage:
#   sh tensorboard.sh              # all runs (slow on /mnt/c, may not load everything)
#   sh tensorboard.sh run_8        # one run — use this if run_8 is missing in the UI
#   sh tensorboard.sh run_8 run_10 # several runs

set -eu
cd "$(dirname "$0")"

PORT="${PORT:-6006}"

if [ "$#" -eq 0 ]; then
  LOGDIR="results"
  echo "[tensorboard] all runs: ${LOGDIR}"
  echo "[tensorboard] tip: sh tensorboard.sh run_8"
else
  LOGDIR=""
  for run_id in "$@"; do
    run_id="${run_id#results/}"
    dir="results/${run_id}"
    if [ ! -d "${dir}" ]; then
      echo "ERROR: ${dir} not found" >&2
      exit 1
    fi
    if ! find "${dir}" -name 'events.out.tfevents.*' -print -quit 2>/dev/null | grep -q .; then
      echo "WARN: ${dir} has no events.out.tfevents.* (empty or failed training)" >&2
    fi
    if [ -n "${LOGDIR}" ]; then
      LOGDIR="${LOGDIR},${run_id}:${dir}"
    else
      LOGDIR="${run_id}:${dir}"
    fi
  done
  echo "[tensorboard] runs: ${LOGDIR}"
fi

exec tensorboard --logdir "${LOGDIR}" --bind_all --port "${PORT}"
