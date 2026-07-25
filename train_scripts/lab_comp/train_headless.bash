#!/usr/bin/env bash
# Jack-only на lab_comp: 30 сред, wood:food:water:zombie ≈ 2:1:2:2.
# Обёртка над train_headless_jack.bash (нужен свежий Linux-билд с -forestJackOnlyTasks).
#
#   RUN_ID=run_80 bash train_scripts/lab_comp/train_headless.bash
#   RUN_ID=run_80 bash train_scripts/lab_comp/train_headless.bash --resume
#
# Переменные: BUILD, NUM_ENVS (по умолчанию 30), TIME_SCALE, RUN_ID

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

export NUM_ENVS="${NUM_ENVS:-30}"
export TIME_SCALE="${TIME_SCALE:-8}"
export RUN_ID="${RUN_ID:-}"
export BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"

exec bash "${ROOT}/train_headless_jack.bash" "$@"
