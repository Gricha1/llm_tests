#!/usr/bin/env bash
# Windows/WSL: билд + sync билда + скриптов + Unity-проекта для sentis + detect Unity на сервере.
#
#   wsl bash train_scripts/lab_comp/deploy_lab_comp.bash
#   wsl bash train_scripts/lab_comp/deploy_lab_comp.bash --skip-build
#   BUILD=stream_forest_survival_2_12_07_2026 RUN_ID=run_60 wsl bash train_scripts/lab_comp/deploy_lab_comp.bash
#
# После деплоя на сервере (2 терминала):
#   bash train_scripts/lab_comp/run_train.bash
#   bash train_scripts/lab_comp/run_stream.bash

set -eu
set -o pipefail

ROOT="/mnt/c/Grisha/unity_projects/forest_survival"
cd "${ROOT}"

BUILD="${BUILD:-stream_forest_survival_2_12_07_2026}"
BUILD="${BUILD%.x86_64}"
RUN_ID="${RUN_ID:-run_60}"
SKIP_BUILD=0

for arg in "$@"; do
  case "${arg}" in
    --skip-build) SKIP_BUILD=1 ;;
  esac
done

export BUILD RUN_ID

echo "=== deploy_lab_comp BUILD=${BUILD} RUN_ID=${RUN_ID} ==="

if [ "${SKIP_BUILD}" -eq 0 ]; then
  echo "[1/4] build Linux..."
  bash train_scripts/build_stream_linux.bash "${BUILD}"
else
  echo "[1/4] skip build (--skip-build)"
fi

echo "[2/4] sync build..."
bash train_scripts/lab_comp/sync_build.bash

echo "[3/4] sync scripts + configs..."
bash train_scripts/lab_comp/sync_scripts.bash

echo "[4/4] sync Unity project slice + detect UNITY_EDITOR on server..."
bash train_scripts/lab_comp/sync_unity_for_sentis.bash

echo ""
echo "=== deploy done ==="
echo "На сервере (ssh reedgern@192.168.194.7):"
echo "  cd ~/lab_work_space/forest_survival"
echo "  RUN_ID=${RUN_ID} bash train_scripts/lab_comp/run_train.bash      # терминал A"
echo "  RUN_ID=${RUN_ID} bash train_scripts/lab_comp/run_stream.bash     # терминал B"
echo ""
echo "Или одной командой (train в фоне):"
echo "  RUN_ID=${RUN_ID} bash train_scripts/lab_comp/run_all.bash"
