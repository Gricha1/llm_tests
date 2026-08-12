#!/usr/bin/env bash
set -eu
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "${ROOT}"
# shellcheck source=rsync_ssh.bash
source "${ROOT}/train_scripts/lab_comp/rsync_ssh.bash"
REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"
# Windows может прокинуть пустой REMOTE_DIR — и :- не спасёт
case "${REMOTE_DIR}" in
  ""|"/"|"/home/"*) REMOTE_DIR="~/lab_work_space/forest_survival" ;;
esac
lab_comp_init_ssh
case "${REMOTE_DIR}" in
  ""|"/"|"/home/grisha"*) REMOTE_DIR="~/lab_work_space/forest_survival" ;;
esac
echo "[sync_bot] → ${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/stream_bot"
# tilde раскрывает удалённый shell
"${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" "mkdir -p ${REMOTE_DIR}/stream_bot ${REMOTE_DIR}/train_scripts/lab_comp"
rsync -avz -e "${LAB_COMP_RSYNC_SSH}" \
  --exclude '.venv/' \
  --exclude '__pycache__/' \
  --exclude '*.sqlite3' \
  --exclude '.env' \
  stream_bot/ "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/stream_bot/"
rsync -avz -e "${LAB_COMP_RSYNC_SSH}" \
  train_scripts/lab_comp/train_lab_ui.py \
  train_scripts/lab_comp/llm_bot_manager.py \
  "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/train_scripts/lab_comp/"
echo "[sync_bot] done"
