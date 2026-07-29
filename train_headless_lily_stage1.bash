#!/usr/bin/env bash
# Lily Stage1: food/water/heat/flower, без emptyDO / backward penalties.
#   bash train_headless_lily_stage1.bash
#   RUN_ID=run_100 bash train_headless_lily_stage1.bash
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
export HERO=lily STAGE=1
exec bash "${ROOT}/train_headless_hero_stage.bash" "$@"
