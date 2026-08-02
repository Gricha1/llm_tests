#!/usr/bin/env bash
# Jack Stage1→2 на lab_comp.
#   RUN_ID_STAGE1=97 bash train_scripts/lab_comp/train_headless_jack_stage1_then_2.bash
set -eu
set -o pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"
export NUM_ENVS="${NUM_ENVS:-26}"
export TIME_SCALE="${TIME_SCALE:-8}"
export BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
exec bash "${ROOT}/train_headless_jack_stage1_then_2.bash" "$@"
