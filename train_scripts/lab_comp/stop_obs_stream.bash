#!/usr/bin/env bash
# Стоп эфира: Presentation Unity + OBS. Train / Survival не трогает.
#   bash train_scripts/lab_comp/stop_obs_stream.bash
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

echo "[obs_stream] stop Presentation…"
bash "${ROOT}/train_scripts/lab_comp/kill_stream.bash" || true

echo "[obs_stream] stop OBS…"
if pgrep -x obs >/dev/null 2>&1; then
  pkill -TERM -x obs || true
  sleep 1
  pkill -KILL -x obs 2>/dev/null || true
fi
echo "[obs_stream] obs left: $(pgrep -x obs | tr '\n' ' ' || echo none)"
echo "[obs_stream] done"
