#!/usr/bin/env bash
# Run train_scripts/*.bash under WSL even when the file has Windows CRLF line endings.
# Usage (from project root or train_scripts/):
#   bash train_scripts/run.bash make_training_video_camA.bash run_8
#   bash run.bash make_training_video_camA.bash run_8
set -eu
script="${1:?usage: bash run.bash <script.bash> [args...]}"
shift
script_dir="$(cd "$(dirname "$0")" && pwd)"
if [ -f "${script}" ]; then
  target="$(cd "$(dirname "${script}")" && pwd)/$(basename "${script}")"
elif [ -f "${script_dir}/${script}" ]; then
  target="${script_dir}/${script}"
else
  echo "ERROR: not found: ${script} (also tried ${script_dir}/${script})" >&2
  exit 1
fi
exec bash <(sed 's/\r$//' "${target}") "$@"
