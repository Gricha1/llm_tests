#!/usr/bin/env bash
# Убить только train (mlagents + headless Unity), стрим (forestStreamOnly) не трогать.
#
#   bash train_scripts/lab_comp/kill_train.bash
#   KEEP_TENSORBOARD=1 bash train_scripts/lab_comp/kill_train.bash
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

echo "[kill_train] останавливаю mlagents-learn и headless Unity..."

# 1) trainer
pkill -TERM -f 'mlagents-learn' 2>/dev/null || true
sleep 1
pkill -KILL -f 'mlagents-learn' 2>/dev/null || true

# 2) headless train Unity: batchmode / TrainAllHeadless / launch через env
#    НЕ трогаем forestStreamOnly (стрим OBS).
pkill -TERM -f 'forestTrainAllHeadless' 2>/dev/null || true
pkill -TERM -f 'stream_forest_survival_.*\.x86_64.*-batchmode' 2>/dev/null || true
pkill -TERM -f 'build_versions/.*\.x86_64.*-batchmode' 2>/dev/null || true
sleep 2
pkill -KILL -f 'forestTrainAllHeadless' 2>/dev/null || true
pkill -KILL -f 'stream_forest_survival_.*\.x86_64.*-batchmode' 2>/dev/null || true
pkill -KILL -f 'build_versions/.*\.x86_64.*-batchmode' 2>/dev/null || true

# 3) обёртка launcher, если зависла без Unity
pkill -TERM -f 'launch_forest_env\.bash' 2>/dev/null || true
pkill -KILL -f 'launch_forest_env\.bash' 2>/dev/null || true

if [ "${KEEP_TENSORBOARD:-0}" != "1" ]; then
  pkill -TERM -f 'tensorboard.*forest_survival/results' 2>/dev/null || true
  sleep 1
  pkill -KILL -f 'tensorboard.*forest_survival/results' 2>/dev/null || true
fi

sleep 1

n_ml=0; n_ht=0; n_bm=0
n_ml="$(pgrep -c -f 'mlagents-learn' 2>/dev/null || true)"
n_ht="$(pgrep -c -f 'forestTrainAllHeadless' 2>/dev/null || true)"
n_bm="$(pgrep -c -f 'stream_forest_survival_.*\.x86_64.*-batchmode' 2>/dev/null || true)"
n_ml="${n_ml:-0}"
n_ht="${n_ht:-0}"
n_bm="${n_bm:-0}"

echo "[kill_train] осталось: mlagents=${n_ml} trainAllHeadless=${n_ht} batchmode_build=${n_bm}"
free -h | awk '/Mem:/{printf "[kill_train] RAM available≈%s / %s\n", $7, $2}'
