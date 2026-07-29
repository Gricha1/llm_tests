#!/usr/bin/env bash
# Jack wood+food+water+zombie на lab_comp: 26 сред, 12 wood + 4 water + 4 food + 6 zombie.
# Обёртка над train_headless_jack_wood_food.bash (нужен билд с -forestJackWoodFoodOnly).
#
#   RUN_ID=run_90 bash train_scripts/lab_comp/train_headless_jack_wood_food.bash
#   RUN_ID=run_90 bash train_scripts/lab_comp/train_headless_jack_wood_food.bash --resume
#
# Полный jack (4 задачи): train_scripts/lab_comp/train_headless.bash

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

export NUM_ENVS="${NUM_ENVS:-26}"
export TIME_SCALE="${TIME_SCALE:-8}"
export RUN_ID="${RUN_ID:-}"
export BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"

exec bash "${ROOT}/train_headless_jack_wood_food.bash" "$@"
