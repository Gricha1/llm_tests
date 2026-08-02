#!/usr/bin/env bash
# Jack Stage2 на lab_comp: fine-tune от Stage1 + emptyDO / backward penalties.
# Нужен билд с -forestJackStage2.
#
#   INIT_FROM=97 bash train_scripts/lab_comp/train_headless_jack_stage2.bash
#   → results/97_stage2
#   INIT_FROM=97 RUN_ID=97_stage2_b bash ...
#   RUN_ID=97_stage2 bash ... --resume

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

export NUM_ENVS="${NUM_ENVS:-26}"
export TIME_SCALE="${TIME_SCALE:-8}"
export RUN_ID="${RUN_ID:-}"
export INIT_FROM="${INIT_FROM:-}"
export BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"

exec bash "${ROOT}/train_headless_jack_stage2.bash" "$@"
