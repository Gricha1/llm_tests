#!/usr/bin/env bash
# ONNX → .sentis для hot reload (отдельный batch Unity, игровой процесс не трогаем).
#
#   FOREST_STREAM_ONNX_DIR=stream_weights/run_56/onnx \
#   FOREST_STREAM_SENTIS_DIR=stream_weights/run_56 \
#   bash train_scripts/export_stream_sentis.bash
#
# Переменные:
#   UNITY_EDITOR=/path/to/Unity   (Linux: ~/Unity/Hub/Editor/6000.0.26f1/Editor/Unity)

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
cd "${ROOT}"

# shellcheck source=lib/resolve_unity_editor.bash
source "${ROOT}/train_scripts/lib/resolve_unity_editor.bash"

ONNX_DIR="${FOREST_STREAM_ONNX_DIR:-}"
SENTIS_DIR="${FOREST_STREAM_SENTIS_DIR:-}"

if [ -z "${ONNX_DIR}" ] || [ -z "${SENTIS_DIR}" ]; then
  echo "Usage: FOREST_STREAM_ONNX_DIR=... FOREST_STREAM_SENTIS_DIR=... bash train_scripts/export_stream_sentis.bash" >&2
  exit 1
fi

UNITY_BIN="$(resolve_unity_editor || true)"
if [ -z "${UNITY_BIN}" ]; then
  echo "[export_stream_sentis] ERROR: Unity Editor не найден." >&2
  echo "  Задай UNITY_EDITOR, например:" >&2
  echo "  export UNITY_EDITOR=\"\$HOME/Unity/Hub/Editor/6000.0.26f1/Editor/Unity\"" >&2
  echo "  Или установи Editor через: bash train_scripts/lab_comp/install_unity_gui.bash" >&2
  if [ "${FOREST_STREAM_SENTIS_REQUIRED:-0}" = "1" ]; then
    exit 1
  fi
  exit 0
fi

export FOREST_STREAM_ONNX_DIR="${ONNX_DIR}"
export FOREST_STREAM_SENTIS_DIR="${SENTIS_DIR}"

echo "[export_stream_sentis] Unity=${UNITY_BIN}"
echo "[export_stream_sentis] onnx=${ONNX_DIR} -> sentis=${SENTIS_DIR}"

"${UNITY_BIN}" \
  -batchmode \
  -nographics \
  -projectPath "${ROOT}" \
  -executeMethod ForestStreamSentisExporter.ExportFromEnv \
  -logFile - \
  -quit
