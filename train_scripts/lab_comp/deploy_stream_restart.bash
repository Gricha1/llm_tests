#!/usr/bin/env bash
# Собрать (опционально) → залить билд + правки на lab_comp → перезапустить stream-обучение.
#
# Полный цикл (сборка + деплой + resume):
#   bash train_scripts/lab_comp/deploy_stream_restart.bash
#
# Только деплой уже собранного билда:
#   bash train_scripts/lab_comp/deploy_stream_restart.bash --no-build
#
# Только сборка локально:
#   bash train_scripts/lab_comp/deploy_stream_restart.bash --build-only
#
# Переменные:
#   BUILD=stream_forest_survival_1_06_07_2026   (без .x86_64)
#   RUN_ID=jack_stream_05_07_2026_copy
#   NUM_ENVS=1  TIME_SCALE=1  FOREST_PRESENTATION_ONLY=0
#   REMOTE=reedgern@192.168.194.7
#   REMOTE_DIR=~/lab_work_space/forest_survival
#   SYNC_CODE=1   — rsync train_scripts + custom_configs (+ Assets/*.cs если нужно)

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_1_06_07_2026}"
BUILD="${BUILD%.x86_64}"
RUN_ID="${RUN_ID:-jack_stream_05_07_2026_copy}"
NUM_ENVS="${NUM_ENVS:-1}"
TIME_SCALE="${TIME_SCALE:-1}"
FOREST_PRESENTATION_ONLY="${FOREST_PRESENTATION_ONLY:-1}"
REMOTE="${REMOTE:-reedgern@192.168.194.7}"
REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"
SSH_KEY="${SSH_KEY:-${HOME}/.ssh/lab_comp_key}"
SYNC_CODE="${SYNC_CODE:-1}"
RESUME=1

DO_BUILD=1
DO_DEPLOY=1
DO_TRAIN=1

for arg in "$@"; do
  case "${arg}" in
    --no-build) DO_BUILD=0 ;;
    --build-only) DO_DEPLOY=0; DO_TRAIN=0 ;;
    --no-train) DO_TRAIN=0 ;;
    --no-resume) RESUME=0 ;;
    --resume) RESUME=1 ;;
  esac
done

if [ ! -f "${SSH_KEY}" ] && [ -f "/mnt/c/Users/User/.ssh/id_ed25519" ]; then
  mkdir -p "${HOME}/.ssh"
  cp "/mnt/c/Users/User/.ssh/id_ed25519" "${SSH_KEY}"
  chmod 600 "${SSH_KEY}"
fi

RSYNC_SSH="ssh -i ${SSH_KEY} -o StrictHostKeyChecking=no"
SSH_CMD=(ssh -i "${SSH_KEY}" -o StrictHostKeyChecking=no "${REMOTE}")

if [ "${DO_BUILD}" -eq 1 ]; then
  echo "[deploy] === local build: ${BUILD} ==="
  bash train_scripts/build_stream_linux.bash "${BUILD}"
fi

if [ "${DO_BUILD}" -eq 1 ] && [ "${DO_DEPLOY}" -eq 0 ] && [ "${DO_TRAIN}" -eq 0 ]; then
  echo "[deploy] build-only done."
  exit 0
fi

if [ ! -f "build_versions/${BUILD}.x86_64" ]; then
  echo "ERROR: build_versions/${BUILD}.x86_64 не найден" >&2
  exit 1
fi

echo "[deploy] === sync to ${REMOTE}:${REMOTE_DIR} ==="

"${SSH_CMD[@]}" "mkdir -p ${REMOTE_DIR}/build_versions"

if [ "${SYNC_CODE}" -eq 1 ]; then
  echo "[deploy] sync train_scripts + custom_configs..."
  rsync -avz --progress -e "${RSYNC_SSH}" \
    train_scripts/ "${REMOTE}:${REMOTE_DIR}/train_scripts/"
  rsync -avz --progress -e "${RSYNC_SSH}" \
    custom_configs/ "${REMOTE}:${REMOTE_DIR}/custom_configs/"

  echo "[deploy] sync Assets (C# правки для следующей сборки; текущий run = билд)..."
  rsync -avz --progress -e "${RSYNC_SSH}" \
    --exclude 'Library/' --exclude 'Temp/' \
    Assets/ \
    "${REMOTE}:${REMOTE_DIR}/Assets/"
fi

echo "[deploy] sync build ${BUILD}.x86_64 + _Data..."
rsync -avz --progress -e "${RSYNC_SSH}" \
  "build_versions/${BUILD}.x86_64" \
  "${REMOTE}:${REMOTE_DIR}/build_versions/"
rsync -avz --progress -e "${RSYNC_SSH}" \
  "build_versions/${BUILD}_Data/" \
  "${REMOTE}:${REMOTE_DIR}/build_versions/${BUILD}_Data/"

echo "[deploy] verify on server:"
"${SSH_CMD[@]}" "ls -la ${REMOTE_DIR}/build_versions/${BUILD}.x86_64 && du -sh ${REMOTE_DIR}/build_versions/${BUILD}_Data"

if [ "${DO_TRAIN}" -eq 0 ]; then
  echo "[deploy] sync done (--no-train)."
  exit 0
fi

echo "[deploy] === stop old training on server ==="
"${SSH_CMD[@]}" "cd ${REMOTE_DIR} && bash train_scripts/kill_forest_survival.bash" || true

TRAIN_ARGS=(train_scripts/lab_comp/train_stream.bash)
if [ "${RESUME}" -eq 1 ]; then
  TRAIN_ARGS+=(--resume)
fi

REMOTE_TRAIN="cd ${REMOTE_DIR} && export DISPLAY=\${DISPLAY:-:1} BUILD=${BUILD}.x86_64 RUN_ID=${RUN_ID} NUM_ENVS=${NUM_ENVS} TIME_SCALE=${TIME_SCALE} FOREST_PRESENTATION_ONLY=${FOREST_PRESENTATION_ONLY} && bash ${TRAIN_ARGS[*]}"

echo "[deploy] === start stream training ==="
echo "[deploy] BUILD=${BUILD}.x86_64  RUN_ID=${RUN_ID}"
echo "[deploy] ssh -t ${REMOTE} \"${REMOTE_TRAIN}\""

exec ssh -t -i "${SSH_KEY}" -o StrictHostKeyChecking=no "${REMOTE}" "${REMOTE_TRAIN}"
