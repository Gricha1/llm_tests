#!/usr/bin/env bash
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
export NUM_ENVS="${NUM_ENVS:-24}" TIME_SCALE="${TIME_SCALE:-8}" BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
exec bash "${ROOT}/train_headless_george_stage1.bash" "$@"
