#!/usr/bin/env bash
# Минимум проекта Unity на сервер для batch onnx→sentis (без Library).
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

# shellcheck source=rsync_ssh.bash
source "${ROOT}/train_scripts/lab_comp/rsync_ssh.bash"
lab_comp_init_ssh

echo "[sync_unity] Assets + ProjectSettings + Packages -> ${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}"
lab_comp_ssh_mkdir "${REMOTE_DIR}"

echo "[sync_unity] 1/3 Assets..."
rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
  --exclude 'Library/' \
  --exclude 'Temp/' \
  --exclude 'Logs/' \
  --exclude 'obj/' \
  --exclude 'Build/' \
  --exclude 'build_versions/' \
  --exclude 'results/' \
  --exclude 'stream_weights/' \
  --exclude '.git/' \
  Assets/ "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/Assets/"

echo "[sync_unity] 2/3 ProjectSettings..."
rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
  ProjectSettings/ "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/ProjectSettings/"

echo "[sync_unity] 3/3 Packages..."
rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
  Packages/ "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/Packages/"

echo "[sync_unity] detect Unity Editor on server..."
"${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" \
  "cd ${REMOTE_DIR} && bash train_scripts/lab_comp/detect_unity_editor.bash"

echo "[sync_unity] done"
