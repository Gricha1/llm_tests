#!/usr/bin/env bash
# Windows/WSL: опционально собрать Linux в CLI → sync (скрипты + билд) на lab_comp.
#
# Билд уже сделал в Unity Editor — только залить:
#   wsl bash train_scripts/lab_comp/sync.bash
#   # или то же через deploy:
#   wsl bash train_scripts/lab_comp/deploy_lab_comp.bash --no-build
#
# Собрать CLI + залить:
#   wsl bash train_scripts/lab_comp/deploy_lab_comp.bash
#
# --no-build = НЕ собирать Unity, но билд на сервер ВСЁ РАВНО залить (если лежит в build_versions/).

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
BUILD="${BUILD%.x86_64}"
RUN_ID="${RUN_ID:-run_60}"
DO_COMPILE=1

for arg in "$@"; do
  case "${arg}" in
    --no-build|--skip-build) DO_COMPILE=0 ;;
  esac
done

export BUILD RUN_ID

echo "=== deploy_lab_comp BUILD=${BUILD} RUN_ID=${RUN_ID} ==="

if [ "${DO_COMPILE}" -eq 1 ]; then
  echo "[1/2] build Linux (CLI)..."
  bash train_scripts/build_stream_linux.bash "${BUILD}"
else
  echo "[1/2] skip Unity compile (--no-build) — льём уже собранный билд из build_versions/"
fi

echo "[2/2] sync (scripts + build)..."
bash train_scripts/lab_comp/sync.bash

echo ""
echo "=== deploy done ==="
echo "На сервере:"
echo "  cd ~/lab_work_space/forest_survival"
echo "  RUN_ID=${RUN_ID} bash train_headless_jack.bash"
echo "  bash train_scripts/lab_comp/run_stream_bot.bash"
echo ""
echo "TensorBoard (с ПК):"
echo "  RUN_ID=${RUN_ID} bash train_scripts/lab_comp/open_tensorboard.bash"
