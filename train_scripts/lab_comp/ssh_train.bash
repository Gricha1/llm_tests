#!/usr/bin/env bash
# Запуск обучения на lab_comp с локальной машины (WSL / Git Bash).
#
# Примеры:
#   bash train_scripts/lab_comp/ssh_train.bash
#   RUN_ID=jack_stream_05_07_2026_copy bash train_scripts/lab_comp/ssh_train.bash --resume
#   NUM_ENVS=10 TIME_SCALE=3 bash train_scripts/lab_comp/ssh_train.bash --resume

set -eu
set -o pipefail

REMOTE="${REMOTE:-lab_comp}"
REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"

NUM_ENVS="${NUM_ENVS:-20}"
TIME_SCALE="${TIME_SCALE:-5}"
BUILD="${BUILD:-jack_cow.x86_64}"

ARGS=()
for arg in "$@"; do
  ARGS+=("${arg}")
done

REMOTE_CMD="cd ${REMOTE_DIR} && export NUM_ENVS=${NUM_ENVS} TIME_SCALE=${TIME_SCALE} BUILD=${BUILD}"
if [ -n "${RUN_ID:-}" ]; then
  REMOTE_CMD="${REMOTE_CMD} RUN_ID=${RUN_ID}"
fi
REMOTE_CMD="${REMOTE_CMD} && bash train_scripts/lab_comp/train_headless.bash"
if [ "${#ARGS[@]}" -gt 0 ]; then
  REMOTE_CMD="${REMOTE_CMD} ${ARGS[*]}"
fi

echo "[ssh_train] ${REMOTE}: ${REMOTE_CMD}"
exec ssh -t "${REMOTE}" "${REMOTE_CMD}"
