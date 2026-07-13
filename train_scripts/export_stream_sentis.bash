#!/usr/bin/env bash
# ONNX → .sentis для hot reload (отдельный batch Unity, игровой процесс не трогаем).
#
#   FOREST_STREAM_ONNX_DIR=stream_weights/run_56/onnx \
#   FOREST_STREAM_SENTIS_DIR=stream_weights/run_56 \
#   bash train_scripts/export_stream_sentis.bash
#
# На Ubuntu 18.04 host: при GLIBC auto Docker (build_sentis_docker.bash один раз).

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
  echo "  export UNITY_EDITOR=\"\$HOME/Unity/Hub/Editor/6000.0.26f1/Editor/Unity\"" >&2
  if [ "${FOREST_STREAM_SENTIS_REQUIRED:-0}" = "1" ]; then
    exit 1
  fi
  exit 0
fi

export UNITY_EDITOR="${UNITY_BIN}"

# Только если явно попросили (Ubuntu 18.04 / Personal): host Unity через glibc22.
if [ "${FOREST_FORCE_SENTIS_GLIBC:-0}" = "1" ]; then
  bash "${ROOT}/train_scripts/lab_comp/export_stream_sentis_glibc.bash"
  exit $?
fi

USE_DOCKER=0

RUN_CHECK=1
if [[ "${UNITY_BIN}" == *.exe ]]; then
  RUN_CHECK=0
fi
if [ "${RUN_CHECK}" -eq 1 ]; then
  # Важно: после `if ! cmd` $? будет 0 — брать rc отдельно.
  set +e
  check_unity_runnable "${UNITY_BIN}"
  rc=$?
  set -e
  if [ "${rc}" -ne 0 ]; then
    if [ "${rc}" -eq 2 ]; then
      # Сначала host+glibc (лицензия Hub), Docker Personal часто пустой.
      if bash "${ROOT}/train_scripts/lab_comp/export_stream_sentis_glibc.bash"; then
        exit 0
      fi
      if command -v docker >/dev/null 2>&1; then
        echo "[export_stream_sentis] glibc-wrap fail → Docker (license может не сработать)" >&2
        USE_DOCKER=1
      else
        unity_glibc_hint
        if [ "${FOREST_STREAM_SENTIS_REQUIRED:-0}" = "1" ]; then
          exit 1
        fi
        exit 0
      fi
    else
      if [ "${FOREST_STREAM_SENTIS_REQUIRED:-0}" = "1" ]; then
        exit 1
      fi
      exit 0
    fi
  fi
fi

if [ "${USE_DOCKER}" -eq 1 ] || [ "${FOREST_FORCE_SENTIS_DOCKER:-0}" = "1" ]; then
  exec bash "${ROOT}/train_scripts/lab_comp/export_stream_sentis_docker.bash"
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
