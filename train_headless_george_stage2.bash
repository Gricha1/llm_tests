#!/usr/bin/env bash
# George Stage2: fine-tune + emptyDO / backward.
#   INIT_FROM=run_101 bash train_headless_george_stage2.bash
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
export HERO=george STAGE=2
exec bash "${ROOT}/train_headless_hero_stage.bash" "$@"
