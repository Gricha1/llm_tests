#!/usr/bin/env bash
# ONNX → .sentis для hot reload (отдельный batch Unity, игровой процесс не трогаем).
#
#   FOREST_STREAM_ONNX_DIR=stream_weights/run_56/onnx \
#   FOREST_STREAM_SENTIS_DIR=stream_weights/run_56 \
#   bash train_scripts/export_stream_sentis.bash
#
# Переменные:
#   UNITY_EDITOR=/path/to/Unity   (или Unity в PATH)

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
cd "${ROOT}"

ONNX_DIR="${FOREST_STREAM_ONNX_DIR:-}"
SENTIS_DIR="${FOREST_STREAM_SENTIS_DIR:-}"

if [ -z "${ONNX_DIR}" ] || [ -z "${SENTIS_DIR}" ]; then
  echo "Usage: FOREST_STREAM_ONNX_DIR=... FOREST_STREAM_SENTIS_DIR=... bash train_scripts/export_stream_sentis.bash" >&2
  exit 1
fi

UNITY_BIN="${UNITY_EDITOR:-}"
if [ -z "${UNITY_BIN}" ] && command -v Unity >/dev/null 2>&1; then
  UNITY_BIN="Unity"
fi
if [ -z "${UNITY_BIN}" ] || ! command -v "${UNITY_BIN}" >/dev/null 2>&1; then
  echo "[export_stream_sentis] skip: UNITY_EDITOR не задан (нет конвертации onnx→sentis)" >&2
  exit 0
fi

export FOREST_STREAM_ONNX_DIR="${ONNX_DIR}"
export FOREST_STREAM_SENTIS_DIR="${SENTIS_DIR}"

echo "[export_stream_sentis] onnx=${ONNX_DIR} -> sentis=${SENTIS_DIR}"
"${UNITY_BIN}" \
  -batchmode \
  -nographics \
  -projectPath "${ROOT}" \
  -executeMethod ForestStreamSentisExporter.ExportFromEnv \
  -logFile - \
  -quit
