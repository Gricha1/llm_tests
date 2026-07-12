#!/usr/bin/env bash
# С Windows (WSL): синк скриптов + stream-билда на lab_comp и опционально старт обучения.
#
#   wsl bash train_scripts/lab_comp/deploy_jack_lily_george.bash
#   wsl bash train_scripts/lab_comp/deploy_jack_lily_george.bash --start
#   wsl bash train_scripts/lab_comp/deploy_jack_lily_george.bash --start --resume
#
# Переменные:
#   BUILD=stream_forest_survival_2_12_07_2026
#   RUN_ID=jack_lily_george_1

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
RUN_ID="${RUN_ID:-jack_lily_george_1}"
KEY="${SSH_KEY:-${HOME}/.ssh/lab_comp_key}"
REMOTE="${REMOTE:-reedgern@192.168.194.7}"
REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"

START=0
RESUME=0
for arg in "$@"; do
  case "${arg}" in
    --start) START=1 ;;
    --resume) RESUME=1 ;;
  esac
done

echo "[deploy] BUILD=${BUILD}"
bash train_scripts/lab_comp/sync_scripts.bash
BUILD="${BUILD}" bash train_scripts/lab_comp/sync_stream_build.bash

if [ "${START}" -eq 0 ]; then
  echo "[deploy] синк готов. На lab_comp:"
  echo "  ssh -i ${KEY} ${REMOTE}"
  echo "  cd ${REMOTE_DIR}"
  if [ "${RESUME}" -eq 1 ]; then
    echo "  RUN_ID=${RUN_ID} bash train_scripts/lab_comp/train_jack_lily_george.bash --resume"
  else
    echo "  RUN_ID=${RUN_ID} bash train_scripts/lab_comp/train_jack_lily_george.bash"
  fi
  exit 0
fi

SSH_CMD="cd ${REMOTE_DIR} && RUN_ID=${RUN_ID} BUILD=${BUILD} bash train_scripts/lab_comp/train_jack_lily_george.bash"
if [ "${RESUME}" -eq 1 ]; then
  SSH_CMD="${SSH_CMD} --resume"
fi

echo "[deploy] ssh start training on ${REMOTE}"
ssh -i "${KEY}" -o StrictHostKeyChecking=no "${REMOTE}" "${SSH_CMD}"
