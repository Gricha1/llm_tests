#!/usr/bin/env bash
# Полный стоп блока Forest Lab Train: train + validate + Presentation stream.
# Survival followers (SS / LLM bot) и OBS не трогает.
set -eu
set -o pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

echo "[kill_mode_training] стоп всего блока Forest Lab Train…"
KEEP_TENSORBOARD=1 bash train_scripts/lab_comp/kill_train.bash || true
bash train_scripts/lab_comp/kill_validate.bash || true
bash train_scripts/lab_comp/kill_stream.bash || true

# хвостовые UI-слоты train/stream
for sess in fui_joint_jlg fui_joint_stream; do
  tmux kill-session -t "${sess}" 2>/dev/null || true
done
rm -f /tmp/forest_ui_fui_joint_jlg.pid /tmp/forest_ui_fui_joint_stream.pid 2>/dev/null || true

echo "[kill_mode_training] done"
