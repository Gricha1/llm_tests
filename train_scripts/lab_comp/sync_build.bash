#!/usr/bin/env bash
# Отправляет Linux-билд на lab_comp (запуск из WSL на Windows).
#
#   wsl bash train_scripts/lab_comp/sync_build.bash
#   BUILD=stream_forest_survival_2_12_07_2026 wsl bash train_scripts/lab_comp/sync_build.bash

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
  cp /mnt/c/Users/User/.ssh/id_ed25519 "${KEY}" 2>/dev/null || true
  chmod 600 "${KEY}" 2>/dev/null || true
fi

if [ ! -f "${ROOT}/build_versions/${BUILD}.x86_64" ]; then
  echo "ERROR: ${ROOT}/build_versions/${BUILD}.x86_64 не найден" >&2
  exit 1
fi
if [ ! -d "${ROOT}/build_versions/${BUILD}_Data" ]; then
  echo "ERROR: ${ROOT}/build_versions/${BUILD}_Data не найден" >&2
  exit 1
fi

RSYNC_SSH="ssh -i ${KEY} -o StrictHostKeyChecking=no"

echo "[sync_build] ${BUILD} -> ${REMOTE}:${DEST}"
rsync -avz --progress -e "${RSYNC_SSH}" \
  "${ROOT}/build_versions/${BUILD}.x86_64" \
  "${REMOTE}:${DEST}/"
rsync -avz --progress -e "${RSYNC_SSH}" \
  "${ROOT}/build_versions/${BUILD}_Data/" \
  "${REMOTE}:${DEST}/${BUILD}_Data/"

ssh -i "${KEY}" -o StrictHostKeyChecking=no "${REMOTE}" \
  "chmod +x ${DEST}/${BUILD}.x86_64 && ls -la ${DEST}/${BUILD}.x86_64"

echo "[sync_build] done"
