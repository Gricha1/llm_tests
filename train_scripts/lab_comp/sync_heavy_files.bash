#!/usr/bin/env bash
# Копирует results + музыку на lab_comp (запуск из WSL на Windows).
set -eu

ROOT="/mnt/c/Grisha/unity_projects/forest_survival"
KEY="${HOME}/.ssh/lab_comp_key"
REMOTE="reedgern@192.168.194.7"
DEST="~/lab_work_space/forest_survival"

mkdir -p "${HOME}/.ssh"
if [ ! -f "${KEY}" ]; then
  cp /mnt/c/Users/User/.ssh/id_ed25519 "${KEY}"
  chmod 600 "${KEY}"
fi

RSYNC_SSH="ssh -i ${KEY} -o StrictHostKeyChecking=no"

echo "[sync] results/jack_stream_05_07_2026_copy ..."
rsync -avz --progress -e "${RSYNC_SSH}" \
  "${ROOT}/results/jack_stream_05_07_2026_copy/" \
  "${REMOTE}:${DEST}/results/jack_stream_05_07_2026_copy/"

echo "[sync] music ..."
rsync -avz --progress -e "${RSYNC_SSH}" \
  "${ROOT}/Assets/Resources/Music/CelticElfMusic.mp3" \
  "${REMOTE}:${DEST}/Assets/Resources/Music/"

rsync -avz --progress -e "${RSYNC_SSH}" \
  "${ROOT}/Assets/StreamingAssets/Music/CelticElfMusic.mp3" \
  "${REMOTE}:${DEST}/Assets/StreamingAssets/Music/"

echo "[sync] verify checkpoint md5 on server:"
${RSYNC_SSH%% *} -i "${KEY}" -o StrictHostKeyChecking=no "${REMOTE}" \
  "md5sum ${DEST}/results/jack_stream_05_07_2026_copy/JackLowLevelAgent/checkpoint.pt"

echo "[sync] done"
