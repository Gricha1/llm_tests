#!/usr/bin/env bash
# ONNX → .sentis БЕЗ Docker-лицензии: Unity Editor хоста + динамический linker Ubuntu 22.04.
# Personal license с Hub/AnyDesk (DISPLAY) подхватывается с хоста.
#
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
ONNX_DIR="${FOREST_STREAM_ONNX_DIR:?}"
SENTIS_DIR="${FOREST_STREAM_SENTIS_DIR:?}"
UNITY_BIN="${UNITY_EDITOR:?}"
DISPLAY_VAR="${DISPLAY:-:1}"
GLIBC_DIR="${FOREST_GLIBC22_DIR:-${ROOT}/.cache/glibc22}"

UNITY_BIN="$(readlink -f "${UNITY_BIN}")"
ONNX_ABS="$(cd "${ONNX_DIR}" && pwd)"
SENTIS_ABS="$(mkdir -p "${SENTIS_DIR}" && cd "${SENTIS_DIR}" && pwd)"
PROJECT_ABS="$(cd "${ROOT}" && pwd)"

ensure_glibc22() {
  if [ -x "${GLIBC_DIR}/ld-linux-x86-64.so.2" ] && [ -e "${GLIBC_DIR}/libc.so.6" ]; then
    return 0
  fi
  if ! command -v docker >/dev/null 2>&1; then
    echo "[export_sentis_glibc] ERROR: docker нужен один раз, чтобы скачать glibc 22.04" >&2
    exit 1
  fi
  echo "[export_sentis_glibc] качаю linker+libs Ubuntu 22.04 в ${GLIBC_DIR}..."
  mkdir -p "${GLIBC_DIR}"
  docker run --rm -v "${GLIBC_DIR}:/out" ubuntu:22.04 bash -lc '
    set -e
    apt-get update -qq
    apt-get install -y -qq libc6 >/dev/null
    cp -a /lib64/ld-linux-x86-64.so.2 /out/ 2>/dev/null || cp -a /lib/x86_64-linux-gnu/ld-linux-x86-64.so.2 /out/
    cp -a /lib/x86_64-linux-gnu/libc.so.6 /out/
    cp -a /lib/x86_64-linux-gnu/libm.so.6 /out/
    cp -a /lib/x86_64-linux-gnu/libdl.so.2 /out/ 2>/dev/null || true
    cp -a /lib/x86_64-linux-gnu/librt.so.1 /out/ 2>/dev/null || true
    cp -a /lib/x86_64-linux-gnu/libpthread.so.0 /out/ 2>/dev/null || true
    # подтянуть остальные libs которые тянет Unity при старте
    cp -a /lib/x86_64-linux-gnu/*.so* /out/ 2>/dev/null || true
  '
  chmod +x "${GLIBC_DIR}/ld-linux-x86-64.so.2"
}

ensure_glibc22

export DISPLAY="${DISPLAY_VAR}"
export FOREST_STREAM_ONNX_DIR="${ONNX_ABS}"
export FOREST_STREAM_SENTIS_DIR="${SENTIS_ABS}"
export HOME="${HOME}"

echo "[export_sentis_glibc] DISPLAY=${DISPLAY} Unity=${UNITY_BIN}"
echo "[export_sentis_glibc] onnx=${ONNX_ABS} -> sentis=${SENTIS_ABS}"
echo "[export_sentis_glibc] glibc wrapper=${GLIBC_DIR}"

# Personal: без -batchmode/-nographics (иначе headless entitlement).
set +e
"${GLIBC_DIR}/ld-linux-x86-64.so.2" --library-path "${GLIBC_DIR}" \
  "${UNITY_BIN}" \
  -projectPath "${PROJECT_ABS}" \
  -executeMethod ForestStreamSentisExporter.ExportFromEnv \
  -logFile - \
  -quit
rc=$?
set -e

if ls "${SENTIS_ABS}"/*.sentis >/dev/null 2>&1; then
  echo "[export_sentis_glibc] OK: $(ls "${SENTIS_ABS}"/*.sentis | wc -l) .sentis"
  exit 0
fi

echo "[export_sentis_glibc] exit=${rc}, .sentis нет" >&2
echo "[export_sentis_glibc] Нужен AnyDesk (DISPLAY) + Unity Hub Personal активирован на этом юзере." >&2
exit 1
