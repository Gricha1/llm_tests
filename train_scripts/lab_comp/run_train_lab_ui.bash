#!/usr/bin/env bash
# Запуск UI с Windows/WSL:
#   wsl bash train_scripts/lab_comp/run_train_lab_ui.bash
#   → http://127.0.0.1:8877
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"
export FOREST_UI_PORT="${FOREST_UI_PORT:-8877}"
exec python3 "${ROOT}/train_scripts/lab_comp/train_lab_ui.py" "$@"
