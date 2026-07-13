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
# shellcheck source=lib/check_unity_runnable.bash
source "${ROOT}/train_scripts/lib/check_unity_runnable.bash"

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

RUN_CHECK=1
if [[ "${UNITY_BIN}" == *.exe ]]; then
  RUN_CHECK=0
fi
if [ "${RUN_CHECK}" -eq 1 ]; then
  if ! check_unity_runnable "${UNITY_BIN}"; then
    rc=$?
    if [ "${rc}" -eq 2 ]; then
      unity_glibc_hint
    fi
    if [ "${FOREST_STREAM_SENTIS_REQUIRED:-0}" = "1" ]; then
      exit 1
    fi
    exit 0
  fi
fi

PROJECT_PATH="${ROOT}"
ENV_ONNX="${ONNX_DIR}"
ENV_SENTIS="${SENTIS_DIR}"
if [[ "${UNITY_BIN}" == *.exe ]]; then
  PROJECT_PATH="$(wslpath -w "${ROOT}")"
  if [[ "${ONNX_DIR}" != [A-Za-z]:* ]]; then
    ENV_ONNX="$(wslpath -w "$(cd "${ONNX_DIR}" && pwd)")"
  fi
  if [[ "${SENTIS_DIR}" != [A-Za-z]:* ]]; then
    ENV_SENTIS="$(wslpath -w "$(cd "${SENTIS_DIR}" && pwd)")"
  fi
fi

export FOREST_STREAM_ONNX_DIR="${ENV_ONNX}"
export FOREST_STREAM_SENTIS_DIR="${ENV_SENTIS}"

echo "[export_stream_sentis] Unity=${UNITY_BIN}"
echo "[export_stream_sentis] onnx=${ONNX_DIR} -> sentis=${SENTIS_DIR}"

"${UNITY_BIN}" \
  -batchmode \
  -nographics \
  -projectPath "${PROJECT_PATH}" \
  -executeMethod ForestStreamSentisExporter.ExportFromEnv \
  -logFile - \
  -quit
