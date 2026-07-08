#!/usr/bin/env bash
# Сборка Linux stream-билда из WSL без открытия Unity Editor.
#
#   bash train_scripts/build_stream_linux.bash
#   bash train_scripts/build_stream_linux.bash stream_forest_survival_1_06_07_2026
#
# Переменные:
#   UNITY_EDITOR  — путь к Unity.exe (Windows)
#   BUILD_LOG     — лог сборки

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
cd "${ROOT}"

BUILD_NAME="${1:-${BUILD:-stream_forest_survival_1_06_07_2026}}"
BUILD_NAME="${BUILD_NAME%.x86_64}"

UNITY="${UNITY_EDITOR:-/mnt/c/Program Files/Unity/Hub/Editor/6000.0.26f1/Editor/Unity.exe}"
BUILD_LOG="${BUILD_LOG:-${ROOT}/build_versions/build_${BUILD_NAME}.log}"

if [ ! -f "${UNITY}" ]; then
  echo "ERROR: Unity не найден: ${UNITY}" >&2
  echo "Задай UNITY_EDITOR или установи Editor 6000.0.26f1 через Hub." >&2
  exit 1
fi

PROJECT_WIN="$(wslpath -w "${ROOT}")"
LOG_WIN="$(wslpath -w "${BUILD_LOG}")"
mkdir -p "$(dirname "${BUILD_LOG}")"

echo "[build] project: ${ROOT}"
echo "[build] output:  build_versions/${BUILD_NAME}.x86_64"
echo "[build] log:     ${BUILD_LOG}"
echo "[build] Unity batchmode (может занять 5–20 мин)..."
BUILD_BIN="build_versions/${BUILD_NAME}.x86_64"
BUILD_BIN_NOEXT="build_versions/${BUILD_NAME}"
BUILD_DLL="build_versions/${BUILD_NAME}_Data/Managed/Assembly-CSharp.dll"
BUILD_BIN_MTIME_BEFORE=""
BUILD_DLL_MTIME_BEFORE=""
if [ -f "${BUILD_BIN}" ]; then
  BUILD_BIN_MTIME_BEFORE="$(stat -c %Y "${BUILD_BIN}" 2>/dev/null || stat -f %m "${BUILD_BIN}" 2>/dev/null || echo 0)"
fi
if [ -f "${BUILD_DLL}" ]; then
  BUILD_DLL_MTIME_BEFORE="$(stat -c %Y "${BUILD_DLL}" 2>/dev/null || stat -f %m "${BUILD_DLL}" 2>/dev/null || echo 0)"
fi

set +e
"${UNITY}" \
  -batchmode -quit -nographics \
  -projectPath "${PROJECT_WIN}" \
  -executeMethod BuildStreamLinux.BuildFromCommandLine \
  -buildOutputName "${BUILD_NAME}" \
  -logFile "${LOG_WIN}"
UNITY_EXIT=$?
set -e

if [ "${UNITY_EXIT}" -ne 0 ]; then
  echo "ERROR: Unity завершился с кодом ${UNITY_EXIT}. Хвост лога:" >&2
  tail -n 40 "${BUILD_LOG}" >&2 || true
  exit 1
fi

if grep -q "HandleProjectAlreadyOpenInAnotherInstance\|already open in another instance" "${BUILD_LOG}" 2>/dev/null; then
  echo "ERROR: Unity Editor уже открыт — закрой Editor и пересобери." >&2
  exit 1
fi

if ! grep -q "\[BuildStreamLinux\] OK:" "${BUILD_LOG}" 2>/dev/null; then
  echo "ERROR: в логе нет [BuildStreamLinux] OK — сборка не удалась. Хвост:" >&2
  tail -n 40 "${BUILD_LOG}" >&2 || true
  exit 1
fi

# Unity на Windows иногда пишет бинарник без .x86_64 — копируем для Linux.
if [ -f "${BUILD_BIN_NOEXT}" ]; then
  cp -f "${BUILD_BIN_NOEXT}" "${BUILD_BIN}"
  chmod +x "${BUILD_BIN}" 2>/dev/null || true
fi

if [ ! -f "${BUILD_BIN}" ]; then
  echo "ERROR: билд не создан. Хвост лога:" >&2
  tail -n 40 "${BUILD_LOG}" >&2 || true
  if grep -q "Scripts have compiler errors" "${BUILD_LOG}" 2>/dev/null; then
    echo "ERROR: ошибки компиляции в Unity — см. лог выше." >&2
  fi
  exit 1
fi

BUILD_DLL_MTIME_AFTER=""
if [ -f "${BUILD_DLL}" ]; then
  BUILD_DLL_MTIME_AFTER="$(stat -c %Y "${BUILD_DLL}" 2>/dev/null || stat -f %m "${BUILD_DLL}" 2>/dev/null || echo 0)"
fi
if [ -n "${BUILD_DLL_MTIME_BEFORE}" ] && [ -n "${BUILD_DLL_MTIME_AFTER}" ] \
    && [ "${BUILD_DLL_MTIME_BEFORE}" = "${BUILD_DLL_MTIME_AFTER}" ]; then
  echo "ERROR: Assembly-CSharp.dll не обновился — код в билде старый." >&2
  exit 1
fi

# Билд старше исходников — предупреждение
if [ "${BUILD_BIN}" -ot "Assets/JackScript.cs" ] 2>/dev/null; then
  echo "WARN: .x86_64 старее JackScript.cs — возможно сборка не обновилась." >&2
fi

chmod +x "${BUILD_BIN}" 2>/dev/null || true
du -sh "${BUILD_BIN}" "build_versions/${BUILD_NAME}_Data" 2>/dev/null || true
echo "[build] OK: ${BUILD_BIN}"
