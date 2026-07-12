#!/usr/bin/env bash
# Минимум проекта Unity на сервер для batch onnx→sentis (без Library).
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

echo "[sync_unity] Assets + ProjectSettings + Packages -> ${REMOTE}:${REMOTE_DIR}"
ssh -i "${KEY}" -o StrictHostKeyChecking=no "${REMOTE}" "mkdir -p ${REMOTE_DIR}"

rsync -avz --progress -e "${RSYNC_SSH}" \
  --exclude 'Library/' \
  --exclude 'Temp/' \
  --exclude 'Logs/' \
  --exclude 'obj/' \
  --exclude 'Build/' \
  --exclude 'build_versions/' \
  --exclude 'results/' \
  --exclude 'stream_weights/' \
  --exclude '.git/' \
  Assets/ "${REMOTE}:${REMOTE_DIR}/Assets/"

rsync -avz --progress -e "${RSYNC_SSH}" \
  ProjectSettings/ "${REMOTE}:${REMOTE_DIR}/ProjectSettings/"

rsync -avz --progress -e "${RSYNC_SSH}" \
  Packages/ "${REMOTE}:${REMOTE_DIR}/Packages/"

echo "[sync_unity] detect Unity Editor on server..."
ssh -i "${KEY}" -o StrictHostKeyChecking=no "${REMOTE}" \
  "cd ${REMOTE_DIR} && bash train_scripts/lab_comp/detect_unity_editor.bash"

echo "[sync_unity] done"
