#!/usr/bin/env bash
# Stream OBS + hot reload .sentis — lab_comp (терминал B).
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

# shellcheck source=lab_comp_env.bash
source "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash"

if [ -z "${UNITY_EDITOR:-}" ] || [ ! -f "${UNITY_EDITOR}" ]; then
  echo "[run_stream] WARN: UNITY_EDITOR не задан — onnx→sentis не будет работать." >&2
  echo "[run_stream] Запусти: bash train_scripts/lab_comp/detect_unity_editor.bash" >&2
  echo "[run_stream] Или установи Editor: bash train_scripts/lab_comp/install_unity_gui.bash" >&2
else
  echo "[run_stream] UNITY_EDITOR=${UNITY_EDITOR}"
fi

POLL_SEC="${POLL_SEC:-10}"
TIME_SCALE="${TIME_SCALE:-1}"

echo "[run_stream] BUILD=${BUILD} RUN_ID=${RUN_ID} DISPLAY=${DISPLAY} POLL_SEC=${POLL_SEC}"
exec env BUILD="${BUILD}" RUN_ID="${RUN_ID}" DISPLAY="${DISPLAY}" \
  UNITY_EDITOR="${UNITY_EDITOR:-}" POLL_SEC="${POLL_SEC}" TIME_SCALE="${TIME_SCALE}" \
  bash stream_inference_watch.bash
