#!/usr/bin/env bash
# Клонировать проект на lab_comp из GitHub.
# Запуск НА СЕРВЕРЕ (или: ssh lab_comp 'bash -s' < train_scripts/lab_comp/clone_from_git.bash)
#
# Если ветка forest_survival ещё не запушена с Windows:
#   git push main forest_survival
#   git push main forest_survival:forest_survival

set -eu
set -o pipefail

REPO_URL="${REPO_URL:-https://github.com/Gricha1/llm_tests.git}"
BRANCH="${BRANCH:-forest_survival}"
TARGET="${TARGET:-${HOME}/lab_work_space/forest_survival}"

if [ -d "${TARGET}/.git" ]; then
  echo "[clone] уже git repo: ${TARGET}"
  cd "${TARGET}"
  git fetch origin
  git checkout "${BRANCH}"
  git pull origin "${BRANCH}"
else
  rm -rf "${TARGET}"
  mkdir -p "$(dirname "${TARGET}")"
  git clone --branch "${BRANCH}" --depth 1 "${REPO_URL}" "${TARGET}" || {
    echo "ERROR: clone failed. Возможно ветка ${BRANCH} не запушена на GitHub."
    echo "На Windows (WSL): cd forest_survival && git push main forest_survival:forest_survival"
    exit 1
  }
fi

echo "[clone] готово: ${TARGET}"
ls "${TARGET}" | head -20
