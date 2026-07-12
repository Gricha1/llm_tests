#!/usr/bin/env bash
# Параллельное обучение 11 Env в одной сцене ForestScene (см. EnvTrainingConfig).
# Usage:
#   bash train_scripts/train_parallel_11.bash
#   bash train_scripts/train_parallel_11.bash jack_lily_train_together_1 1
#   RUN_ID=parallel_11 bash train_scripts/train_parallel_11.bash

set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export RUN_ID="${RUN_ID:-parallel_11}"
exec "$ROOT/train_scripts/train_jack_lily_george.bash" "$@"
