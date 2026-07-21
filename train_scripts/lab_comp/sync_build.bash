#!/usr/bin/env bash
# Отправляет Linux-билд на lab_comp (запуск из WSL на Windows).
# Перед заливкой СНОСИТ старый билд на сервере — чтобы не крутился полустарый _Data.
#
#   wsl bash train_scripts/lab_comp/sync_build.bash
#   BUILD=stream_forest_survival_2_12_07_2026 wsl bash train_scripts/lab_comp/sync_build.bash

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
BUILD="${BUILD%.x86_64}"
DEST="${REMOTE_DIR:-~/lab_work_space/forest_survival}/build_versions"
LOCAL_BIN="${ROOT}/build_versions/${BUILD}.x86_64"
LOCAL_DATA="${ROOT}/build_versions/${BUILD}_Data"
LOCAL_DLL="${LOCAL_DATA}/Managed/Assembly-CSharp.dll"
STAMP_NAME="${BUILD}.BUILD_STAMP"

# shellcheck source=rsync_ssh.bash
source "${ROOT}/train_scripts/lab_comp/rsync_ssh.bash"
lab_comp_init_ssh

if [ ! -f "${LOCAL_BIN}" ]; then
  echo "ERROR: ${LOCAL_BIN} не найден" >&2
  exit 1
fi
if [ ! -d "${LOCAL_DATA}" ]; then
  echo "ERROR: ${LOCAL_DATA} не найден" >&2
  exit 1
fi
if [ ! -f "${LOCAL_DLL}" ]; then
  echo "ERROR: ${LOCAL_DLL} не найден — билд битый" >&2
  exit 1
fi

# Если C# новее DLL — это старый локальный билд, на сервер его лучше не слать.
newest_src="$(find "${ROOT}/Assets" -name '*.cs' -printf '%T@\n' 2>/dev/null | sort -n | tail -1 || true)"
dll_mtime="$(stat -c '%Y' "${LOCAL_DLL}" 2>/dev/null || stat -f '%m' "${LOCAL_DLL}")"
if [ -n "${newest_src}" ]; then
  # newest_src — epoch with decimals from -printf %T@
  newest_int="${newest_src%%.*}"
  if [ "${newest_int}" -gt "${dll_mtime}" ]; then
    echo "ERROR: локальный билд УСТАРЕЛ — в Assets есть .cs новее Assembly-CSharp.dll." >&2
    echo "  DLL: $(date -d "@${dll_mtime}" '+%Y-%m-%d %H:%M:%S' 2>/dev/null || date -r "${dll_mtime}" '+%Y-%m-%d %H:%M:%S')" >&2
    echo "  Сначала: bash train_scripts/build_stream_linux.bash ${BUILD}" >&2
    if [ "${FORCE_STALE_BUILD:-0}" != "1" ]; then
      exit 1
    fi
    echo "WARN: FORCE_STALE_BUILD=1 — всё равно заливаю старый билд" >&2
  fi
fi

dll_size="$(stat -c '%s' "${LOCAL_DLL}" 2>/dev/null || stat -f '%z' "${LOCAL_DLL}")"
bin_mtime="$(stat -c '%Y' "${LOCAL_BIN}" 2>/dev/null || stat -f '%m' "${LOCAL_BIN}")"
stamp_path="${ROOT}/build_versions/${STAMP_NAME}"
{
  echo "build=${BUILD}"
  echo "built_utc=$(date -u -d "@${bin_mtime}" '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u -r "${bin_mtime}" '+%Y-%m-%dT%H:%M:%SZ')"
  echo "synced_utc=$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
  echo "dll_bytes=${dll_size}"
  echo "dll_mtime_unix=${dll_mtime}"
} > "${stamp_path}"

echo "[sync_build] ${BUILD} -> ${LAB_COMP_RSYNC_REMOTE}:${DEST}"
echo "[sync_build] stamp: $(tr '\n' ' ' < "${stamp_path}")"
lab_comp_ssh_mkdir "${DEST}"

echo "[sync_build] 0/3 удаляю СТАРЫЙ билд на сервере..."
"${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" \
  "rm -rf ${DEST}/${BUILD}.x86_64 ${DEST}/${BUILD}_Data ${DEST}/${STAMP_NAME} && echo removed_old_ok"

echo "[sync_build] 1/3 ${BUILD}.x86_64 ..."
rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
  "${LOCAL_BIN}" \
  "${LAB_COMP_RSYNC_REMOTE}:${DEST}/"

echo "[sync_build] 2/3 ${BUILD}_Data/ (--delete, может занять несколько минут)..."
rsync -avz --delete --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
  "${LOCAL_DATA}/" \
  "${LAB_COMP_RSYNC_REMOTE}:${DEST}/${BUILD}_Data/"

echo "[sync_build] 3/3 ${STAMP_NAME} ..."
rsync -avz -e "${LAB_COMP_RSYNC_SSH}" \
  "${stamp_path}" \
  "${LAB_COMP_RSYNC_REMOTE}:${DEST}/"

"${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" \
  "chmod +x ${DEST}/${BUILD}.x86_64 && ls -la ${DEST}/${BUILD}.x86_64 ${DEST}/${STAMP_NAME} && cat ${DEST}/${STAMP_NAME}"

echo "[sync_build] done — перезапусти train/stream, иначе процессы держат старый билд в памяти"
