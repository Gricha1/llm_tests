#!/usr/bin/env bash
# Lab wrapper: совместное дообучение Jack+Lily+George.
#   INIT_FROM_JACK=97_stage2 INIT_FROM_LILY=lily_1_stage2 INIT_FROM_GEORGE=george_1_stage2 \
#   RUN_ID=jlg_1 bash train_scripts/lab_comp/train_headless_jack_lily_george_finetune.bash
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
export BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
export NUM_ENVS="${NUM_ENVS:-21}"
export TIME_SCALE="${TIME_SCALE:-8}"
exec bash "${ROOT}/train_headless_jack_lily_george_finetune.bash" "$@"
