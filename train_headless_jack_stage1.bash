#!/usr/bin/env bash
set -eu
set -o pipefail

# Jack Stage1: wood/food/water/zombie без штрафов за пустой DO и ходьбу назад.
# Цель — выучить циклы задач. Дальше fine-tune: train_headless_jack_stage2.bash
#
#   bash train_headless_jack_stage1.bash
#   RUN_ID=run_98 bash train_headless_jack_stage1.bash
#   RUN_ID=run_98 bash train_headless_jack_stage1.bash --resume
#
# Lab: bash train_scripts/lab_comp/train_headless_jack_stage1.bash

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
cd "${ROOT}"

# Явно stage1 (без stage2-штрафов).
unset FOREST_JACK_STAGE2 || true
export FOREST_JACK_STAGE2=0

# По умолчанию свободный run_N (не затирать чужой id).
export RUN_ID="${RUN_ID:-jack_stage1}"

echo "[jack_stage1] Stage1: без emptyDO / backward penalties"
exec bash "${ROOT}/train_headless_jack_wood_food.bash" "$@"
