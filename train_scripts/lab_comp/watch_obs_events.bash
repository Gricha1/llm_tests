#!/usr/bin/env bash
# OBS / Shorts helper: tail Unity obs_stream_events.jsonl and optionally fire OBS hotkeys.
# Requires: OBS Studio with obs-websocket (optional). Without it — just prints markers for editors.
#
# Usage on lab_comp:
#   bash train_scripts/lab_comp/watch_obs_events.bash
#   FOREST_RESULTS_DIR=~/lab_work_space/forest_survival/results/jlg_finetune_2 bash ...
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RESULTS="${FOREST_RESULTS_DIR:-$ROOT/results/jlg_finetune_2}"
EVENTS="$RESULTS/obs_stream_events.jsonl"
# Fallback Unity Player persistent path (Linux)
FALLBACK="$HOME/.config/unity3d/DefaultCompany/forest_survival/obs_events/obs_stream_events.jsonl"

pick() {
  if [[ -f "$EVENTS" ]]; then echo "$EVENTS"; return; fi
  if [[ -f "$FALLBACK" ]]; then echo "$FALLBACK"; return; fi
  mkdir -p "$(dirname "$EVENTS")"
  touch "$EVENTS"
  echo "$EVENTS"
}

FILE="$(pick)"
echo "[obs_events] watching $FILE"
echo "[obs_events] Shorts cadence target: 3 clips/week (boss_spawn, boss_defeated, episode_wipe)"
tail -n0 -F "$FILE" | while IFS= read -r line; do
  echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) $line"
  # Optional: curl OBS websocket plugin / replay buffer — leave disabled by default.
  # if command -v obs-cli >/dev/null 2>&1; then obs-cli replay save; fi
done
