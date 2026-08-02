#!/usr/bin/env bash
# Один синк на lab_comp (из WSL): всегда скрипты + уже собранный Linux-билд.
# Сборку Unity этот скрипт НЕ делает — билд сам в Editor (или build_stream_linux.bash).
#
#   wsl bash train_scripts/lab_comp/sync.bash
#   BUILD=stream_forest_survival_2_12_07_2026 wsl bash train_scripts/lab_comp/sync.bash
#
# FORCE_FULL_SYNC=1 — снести весь _Data перед заливкой билда.
# FORCE_STALE_BUILD=1 — залить билд даже если .cs новее DLL.

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
BUILD="${BUILD%.x86_64}"

for arg in "$@"; do
  case "${arg}" in
    --no-build)
      echo "WARN: --no-build у sync.bash игнорируется — билд на сервер всё равно льём." >&2
      echo "  (не билдить Unity = просто не вызывай build_stream_linux / deploy без --no-build)" >&2
      ;;
    --help|-h)
      echo "Usage: bash train_scripts/lab_comp/sync.bash"
      echo "  Заливает scripts + build_versions/\${BUILD} (билд уже должен быть собран)."
      exit 0
      ;;
  esac
done

# shellcheck source=rsync_ssh.bash
source "${ROOT}/train_scripts/lab_comp/rsync_ssh.bash"
lab_comp_init_ssh

DEST="${REMOTE_DIR}/build_versions"
LOCAL_BIN="${ROOT}/build_versions/${BUILD}.x86_64"
LOCAL_DATA="${ROOT}/build_versions/${BUILD}_Data"
LOCAL_DLL="${LOCAL_DATA}/Managed/Assembly-CSharp.dll"
STAMP_NAME="${BUILD}.BUILD_STAMP"

sync_scripts() {
  echo "[sync] scripts → ${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}"

  echo "[sync] train_scripts..."
  rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
    train_scripts/ "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/train_scripts/"

  echo "[sync] custom_configs..."
  rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
    custom_configs/ "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/custom_configs/"

  echo "[sync] root launch scripts..."
  rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
    stream_inference_watch.bash \
    train_headless_jack.bash \
    train_headless_jack_wood_food.bash \
    train_headless_jack_lily_george.bash \
    train_headless_jack_lily_george_finetune.bash \
    train_headless_hero_stage.bash \
    train_headless_jack_stage1.bash \
    train_headless_jack_stage2.bash \
    train_headless_jack_stage1_then_2.bash \
    train_headless_lily_stage1.bash \
    train_headless_lily_stage2.bash \
    train_headless_lily_stage1_then_2.bash \
    train_headless_george_stage1.bash \
    train_headless_george_stage2.bash \
    train_headless_george_stage1_then_2.bash \
    tensorboard.sh \
    "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/"

  echo "[sync] stream_bot..."
  lab_comp_ssh_mkdir "${REMOTE_DIR}/stream_bot"
  rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
    --exclude '.venv/' \
    --exclude '__pycache__/' \
    --exclude '*.sqlite3' \
    --exclude '.env' \
    stream_bot/ "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/stream_bot/"

  "${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" \
    "chmod +x ${REMOTE_DIR}/train_scripts/lab_comp/*.bash ${REMOTE_DIR}/train_headless_*.bash ${REMOTE_DIR}/stream_inference_watch.bash 2>/dev/null; true"
}

sync_build() {
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

  newest_src="$(find "${ROOT}/Assets" -name '*.cs' -printf '%T@\n' 2>/dev/null | sort -n | tail -1 || true)"
  dll_mtime="$(stat -c '%Y' "${LOCAL_DLL}" 2>/dev/null || stat -f '%m' "${LOCAL_DLL}")"
  if [ -n "${newest_src}" ]; then
    newest_int="${newest_src%%.*}"
    if [ "${newest_int}" -gt "${dll_mtime}" ]; then
      echo "ERROR: локальный билд УСТАРЕЛ — в Assets есть .cs новее Assembly-CSharp.dll." >&2
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

  echo "[sync] build ${BUILD} → ${LAB_COMP_RSYNC_REMOTE}:${DEST}"
  echo "[sync] stamp: $(tr '\n' ' ' < "${stamp_path}")"
  lab_comp_ssh_mkdir "${DEST}"

  if [ "${FORCE_FULL_SYNC:-0}" = "1" ]; then
    echo "[sync] FORCE_FULL_SYNC: сношу .x86_64 + весь _Data..."
    "${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" \
      "rm -rf ${DEST}/${BUILD}.x86_64 ${DEST}/${BUILD}_Data ${DEST}/${STAMP_NAME} && echo removed_old_ok"
  else
    echo "[sync] сношу .x86_64 + Managed DLL (+ stamp); Music/_Data не трогаю"
    "${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" \
      "rm -f ${DEST}/${BUILD}.x86_64 ${DEST}/${STAMP_NAME} ${DEST}/${BUILD}_Data/Managed/Assembly-CSharp.dll ${DEST}/${BUILD}_Data/Managed/Assembly-CSharp-firstpass.dll && echo removed_bin_ok"
  fi

  echo "[sync] ${BUILD}.x86_64 ..."
  rsync -avz --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
    "${LOCAL_BIN}" \
    "${LAB_COMP_RSYNC_REMOTE}:${DEST}/"

  echo "[sync] ${BUILD}_Data/ ..."
  rsync -avz --delete --progress --info=name2,progress2 -e "${LAB_COMP_RSYNC_SSH}" \
    "${LOCAL_DATA}/" \
    "${LAB_COMP_RSYNC_REMOTE}:${DEST}/${BUILD}_Data/"

  echo "[sync] ${STAMP_NAME} ..."
  rsync -avz -e "${LAB_COMP_RSYNC_SSH}" \
    "${stamp_path}" \
    "${LAB_COMP_RSYNC_REMOTE}:${DEST}/"

  "${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" \
    "chmod +x ${DEST}/${BUILD}.x86_64 && ls -la ${DEST}/${BUILD}.x86_64 ${DEST}/${STAMP_NAME} && cat ${DEST}/${STAMP_NAME}"
}

echo "=== sync lab_comp (scripts + build ${BUILD}) ==="
sync_scripts
sync_build
echo "[sync] done — перезапусти train/stream, иначе держит старый билд в памяти"