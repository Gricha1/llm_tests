#!/usr/bin/env bash
# George Stage1: food/water/heat, без emptyDO / backward.
#   bash train_headless_george_stage1.bash
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
export HERO=george STAGE=1
exec bash "${ROOT}/train_headless_hero_stage.bash" "$@"
