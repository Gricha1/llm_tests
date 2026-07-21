#!/usr/bin/env bash
# Копирует train_scripts + custom_configs на lab_comp (из WSL).
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

# shellcheck source=rsync_ssh.bash
source "${ROOT}/train_scripts/lab_comp/rsync_ssh.bash"
lab_comp_init_ssh

echo "[sync] train_scripts + custom_configs -> ${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}"

echo "[sync] 1/3 train_scripts..."
rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
  train_scripts/ "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/train_scripts/"

echo "[sync] 2/3 custom_configs..."
rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
  custom_configs/ "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/custom_configs/"

echo "[sync] 3/3 root launch scripts..."
rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
  stream_inference_watch.bash \
  train_headless_jack_lily_george.bash \
  tensorboard.sh \
  "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/"

echo "[sync] lab_comp run scripts included in train_scripts/"
echo "[sync] done — на сервере: bash train_scripts/lab_comp/detect_unity_editor.bash"
