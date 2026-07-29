#!/usr/bin/env bash
# Lily Stage2: fine-tune + emptyDO / backward. INIT_FROM не перезаписывается.
#   INIT_FROM=run_100 bash train_headless_lily_stage2.bash
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
export HERO=lily STAGE=2
exec bash "${ROOT}/train_headless_hero_stage.bash" "$@"
