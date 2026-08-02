#!/usr/bin/env bash
# Windows-билд presentation для локального ручного теста (твой ПК).
#   bash train_scripts/build_presentation_windows.bash
set -eu
set -o pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
cd "${ROOT}"

BUILD_NAME="${1:-presentation_manual_win}"
UNITY="${UNITY_EDITOR:-/mnt/c/Program Files/Unity/Hub/Editor/6000.0.26f1/Editor/Unity.exe}"
BUILD_LOG="${BUILD_LOG:-${ROOT}/build_versions/build_${BUILD_NAME}.log}"

if [ ! -f "${UNITY}" ]; then
  echo "ERROR: Unity не найден: ${UNITY}" >&2
  exit 1
fi

PROJECT_WIN="$(wslpath -w "${ROOT}")"
LOG_WIN="$(wslpath -w "${BUILD_LOG}")"
mkdir -p "${ROOT}/build_versions"

echo "[build] Windows presentation -> build_versions/${BUILD_NAME}/"
echo "[build] log: ${BUILD_LOG}"
"${UNITY}" -batchmode -nographics -quit \
  -projectPath "${PROJECT_WIN}" \
  -executeMethod BuildPresentationWindows.BuildFromCommandLine \
  -buildOutputName "${BUILD_NAME}" \
  -logFile "${LOG_WIN}"

EXE="${ROOT}/build_versions/${BUILD_NAME}/${BUILD_NAME}.exe"
if [ ! -f "${EXE}" ]; then
  echo "ERROR: нет ${EXE}" >&2
  tail -40 "${BUILD_LOG}" >&2 || true
  exit 1
fi
echo "[build] OK: ${EXE}"
