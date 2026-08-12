#!/usr/bin/env bash
set -eu
ROOT="/mnt/c/Grisha/unity_projects/forest_survival"
pkill -f "train_scripts/lab_comp/train_lab_ui.py" 2>/dev/null || true
sleep 1
cd "$ROOT"
nohup python3 train_scripts/lab_comp/train_lab_ui.py >/tmp/train_lab_ui.log 2>&1 &
sleep 1
pgrep -af train_lab_ui || true
echo "--- css ---"
curl -s http://127.0.0.1:8877/ | grep -A6 "llm-chat .row {" | head -10
echo "--- js ---"
curl -s http://127.0.0.1:8877/ | grep -F 'innerHTML = "<b>"' | head -2
