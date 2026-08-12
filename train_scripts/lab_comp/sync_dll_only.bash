#!/usr/bin/env bash
set -eu
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
# shellcheck source=rsync_ssh.bash
source "$ROOT/train_scripts/lab_comp/rsync_ssh.bash"
lab_comp_init_ssh
BUILD=stream_forest_survival_2_12_07_2026
DEST="${REMOTE_DIR}/build_versions"
rsync -avz -e "${LAB_COMP_RSYNC_SSH}" \
  "build_versions/${BUILD}.x86_64" \
  "${LAB_COMP_RSYNC_REMOTE}:${DEST}/"
rsync -avz -e "${LAB_COMP_RSYNC_SSH}" \
  "build_versions/${BUILD}_Data/Managed/Assembly-CSharp.dll" \
  "${LAB_COMP_RSYNC_REMOTE}:${DEST}/${BUILD}_Data/Managed/"
rsync -avz -e "${LAB_COMP_RSYNC_SSH}" \
  train_scripts/lab_comp/start_streaming_survival.bash \
  stream_bot/tests/live_full_episode.py \
  "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/train_scripts/lab_comp/" 2>/dev/null || true
rsync -avz -e "${LAB_COMP_RSYNC_SSH}" \
  stream_bot/tests/live_full_episode.py \
  "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/stream_bot/tests/"
echo DLL_SYNC_OK
"${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" \
  "ls -la ${DEST}/${BUILD}_Data/Managed/Assembly-CSharp.dll ${DEST}/${BUILD}.x86_64"
