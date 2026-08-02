#!/usr/bin/env bash
# Добить оставшиеся validate одним героем за раз (не пакет «все сразу»).
set -eu
cd ~/lab_work_space/forest_survival
BUILD=stream_forest_survival_2_12_07_2026
V=train_scripts/lab_comp/validate_ui_one.bash

# дождаться чужого flower если ещё идёт
while pgrep -f 'validate_ui_one.bash.*flower' >/dev/null 2>&1; do sleep 5; done
ls -lh results/lily_1_stage2/videos/ui_val_lily_flower.mp4 2>/dev/null || {
  echo "=== lily flower ==="
  bash "$V" "$BUILD" lily_1_stage2 flower 20 "" lily
}

for t in food water heat; do
  echo "=== george $t ==="
  bash "$V" "$BUILD" george_1 "$t" 20 "" george
done

echo "=== videos ==="
ls -lh results/97_stage2/videos/ui_val_jack_*.mp4
ls -lh results/lily_1_stage2/videos/ui_val_lily_*.mp4
ls -lh results/george_1/videos/ui_val_george_*.mp4
