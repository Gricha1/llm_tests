#!/usr/bin/env bash
# Общие переменные lab_comp (source из run_train / stream_inference_watch).
set -eu

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
ENV_FILE="${ROOT}/.lab_comp_env"

if [ -f "${ENV_FILE}" ]; then
  # shellcheck source=/dev/null
  source "${ENV_FILE}"
fi

# shellcheck source=../lib/resolve_unity_editor.bash
source "${ROOT}/train_scripts/lib/resolve_unity_editor.bash"

export BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
export BUILD="${BUILD%.x86_64}"
export RUN_ID="${RUN_ID:-run_60}"
export DISPLAY="${DISPLAY:-:1}"

if [ -z "${UNITY_EDITOR:-}" ]; then
  UNITY_EDITOR="$(resolve_unity_editor 2>/dev/null || true)"
  export UNITY_EDITOR
fi

export OMP_NUM_THREADS="${OMP_NUM_THREADS:-4}"
export MKL_NUM_THREADS="${MKL_NUM_THREADS:-4}"
