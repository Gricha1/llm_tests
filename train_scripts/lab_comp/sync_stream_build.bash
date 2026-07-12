#!/usr/bin/env bash
# Копирует stream-билд на lab_comp (запуск из WSL).
set -eu
set -o pipefail

ROOT="/mnt/c/Grisha/unity_projects/forest_survival"
KEY="${HOME}/.ssh/lab_comp_key"
REMOTE="reedgern@192.168.194.7"
DEST="~/lab_work_space/forest_survival/build_versions"
BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
BUILD="${BUILD%.x86_64}"

mkdir -p "${HOME}/.ssh"
if [ ! -f "${KEY}" ]; then
  cp /mnt/c/Users/User/.ssh/id_ed25519 "${KEY}"
  chmod 600 "${KEY}"
fi

RSYNC_SSH="ssh -i ${KEY} -o StrictHostKeyChecking=no"

echo "[sync] ${BUILD} -> ${REMOTE}:${DEST}"
rsync -avz --progress -e "${RSYNC_SSH}" \
  "${ROOT}/build_versions/${BUILD}.x86_64" \
  "${REMOTE}:${DEST}/"
rsync -avz --progress -e "${RSYNC_SSH}" \
  "${ROOT}/build_versions/${BUILD}_Data/" \
  "${REMOTE}:${DEST}/${BUILD}_Data/"

echo "[sync] verify on server:"
${RSYNC_SSH%% *} -i "${KEY}" -o StrictHostKeyChecking=no "${REMOTE}" \
  "ls -la ${DEST}/${BUILD}.x86_64 && du -sh ${DEST}/${BUILD}_Data"

echo "[sync] done"
