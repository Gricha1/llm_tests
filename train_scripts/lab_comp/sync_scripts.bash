#!/usr/bin/env bash
# Копирует train_scripts + custom_configs на lab_comp (из WSL).
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

KEY="${SSH_KEY:-${HOME}/.ssh/lab_comp_key}"
REMOTE="${REMOTE:-reedgern@192.168.194.7}"
REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"

mkdir -p "${HOME}/.ssh"
if [ ! -f "${KEY}" ]; then
  cp /mnt/c/Users/User/.ssh/id_ed25519 "${KEY}" 2>/dev/null || true
  chmod 600 "${KEY}" 2>/dev/null || true
fi

RSYNC_SSH="ssh -i ${KEY} -o StrictHostKeyChecking=no"

echo "[sync] train_scripts + custom_configs -> ${REMOTE}:${REMOTE_DIR}"
rsync -avz --progress -e "${RSYNC_SSH}" \
  train_scripts/ "${REMOTE}:${REMOTE_DIR}/train_scripts/"
rsync -avz --progress -e "${RSYNC_SSH}" \
  custom_configs/ "${REMOTE}:${REMOTE_DIR}/custom_configs/"
rsync -avz --progress -e "${RSYNC_SSH}" \
  stream_inference_watch.bash \
  train_headless_jack_lily_george.bash \
  "${REMOTE}:${REMOTE_DIR}/"

echo "[sync] done"
