#!/usr/bin/env bash
# Устарело: stream + sentis. Используй run_train.bash (presentation + train в одном Unity).
echo "[run_stream] DEPRECATED: используй bash train_scripts/lab_comp/run_train.bash" >&2
exec bash "$(dirname "$0")/run_train.bash" "$@"
