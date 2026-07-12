#!/usr/bin/env bash
# Синхронизация проекта на lab_comp (запуск из WSL).
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"
SSH_KEY="${SSH_KEY:-${HOME}/.ssh/lab_comp_key}"

# Ключ Windows → WSL (chmod 600), если ещё не скопирован
if [ ! -f "${SSH_KEY}" ] && [ -f "/mnt/c/Users/User/.ssh/id_ed25519" ]; then
  mkdir -p "${HOME}/.ssh"
  cp "/mnt/c/Users/User/.ssh/id_ed25519" "${SSH_KEY}"
  chmod 600 "${SSH_KEY}"
fi

REMOTE="reedgern@192.168.194.7"
RSYNC_SSH="ssh -i ${SSH_KEY} -o StrictHostKeyChecking=no"

RSYNC_EXCLUDES=(
  --exclude '.git/'
  --exclude 'Library/'
  --exclude 'Temp/'
  --exclude 'Logs/'
  --exclude 'obj/'
  --exclude 'Build/'
  --exclude 'Builds/'
  --exclude '.vs/'
  --exclude 'node_modules/'
  --exclude '__pycache__/'
  --exclude '*.pyc'
)

echo "[sync] ${ROOT} -> ${REMOTE}:${REMOTE_DIR}"

${RSYNC_SSH%% *} -i "${SSH_KEY}" -o StrictHostKeyChecking=no "${REMOTE}" "mkdir -p ${REMOTE_DIR}"

rsync -avz --progress -e "${RSYNC_SSH}" "${RSYNC_EXCLUDES[@]}" \
  "${ROOT}/" \
  "${REMOTE}:${REMOTE_DIR}/"

echo "[sync] готово. На сервере:"
echo "  ssh -i ${SSH_KEY} ${REMOTE}"
echo "  cd ${REMOTE_DIR}"
echo "  bash train_scripts/lab_comp/train_jack_lily_george.bash"
echo ""
echo "Или с Windows одной командой:"
echo "  wsl bash train_scripts/lab_comp/deploy_jack_lily_george.bash --start"
