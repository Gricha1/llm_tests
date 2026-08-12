#!/usr/bin/env bash
# Полный стоп блока Survival followers: Streaming Survival + LLM bot.
# Train / Presentation onnx / OBS не трогает.
set -eu
set -o pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

echo "[kill_mode_streaming] стоп всего блока Survival followers…"
bash train_scripts/lab_comp/stop_streaming_survival.bash || true
bash train_scripts/lab_comp/llm_bot_lab.bash stop || true

tmux kill-session -t fui_streaming_survival 2>/dev/null || true
rm -f /tmp/forest_ui_fui_streaming_survival.pid 2>/dev/null || true

echo "[kill_mode_streaming] done"
