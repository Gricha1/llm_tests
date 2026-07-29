#!/usr/bin/env bash
# Jack Stage1 на lab_comp: 26 envs, без штрафов emptyDO / backward.
#
#   bash train_scripts/lab_comp/train_headless_jack_stage1.bash
#   RUN_ID=run_98 bash train_scripts/lab_comp/train_headless_jack_stage1.bash
#   RUN_ID=run_98 bash train_scripts/lab_comp/train_headless_jack_stage1.bash --resume

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

export NUM_ENVS="${NUM_ENVS:-26}"
export TIME_SCALE="${TIME_SCALE:-8}"
export RUN_ID="${RUN_ID:-}"
export BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"

exec bash "${ROOT}/train_headless_jack_stage1.bash" "$@"
