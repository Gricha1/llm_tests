#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT"
export FOREST_ROOT="$ROOT"
export RUN_ID="${RUN_ID:-jlg_finetune_2}"
export FOREST_RESULTS_DIR="${FOREST_RESULTS_DIR:-$ROOT/results/$RUN_ID}"
export SHORTS_ROOT="${SHORTS_ROOT:-$ROOT/results/shorts}"
export DISPLAY="${SHORTS_DISPLAY:-${DISPLAY:-:0}}"
if [[ -z "${XAUTHORITY:-}" ]]; then
  if [[ -f /run/user/$(id -u)/gdm/Xauthority ]]; then
    export XAUTHORITY=/run/user/$(id -u)/gdm/Xauthority
  elif [[ -f "$HOME/.Xauthority" ]]; then
    export XAUTHORITY="$HOME/.Xauthority"
  fi
fi
export SHORTS_DISPLAY="${SHORTS_DISPLAY:-$DISPLAY}"
PY="${PYTHON_BIN:-}"
if [[ -z "$PY" ]]; then
  if [[ -x "$HOME/anaconda3/envs/mlagents/bin/python" ]]; then
    PY="$HOME/anaconda3/envs/mlagents/bin/python"
  else
    PY="$(command -v python3)"
  fi
fi
mkdir -p "$SHORTS_ROOT/temp" "$SHORTS_ROOT/saved" "$ROOT/results"
LOG="$ROOT/results/shorts_daemon.log"
PIDF="$ROOT/results/shorts_daemon.pid"
DAEMON_PY="$ROOT/train_scripts/lab_comp/shorts/daemon.py"
if [[ "${1:-}" == "--daemon" ]]; then
  if [[ -f "$PIDF" ]]; then
    old=$(cat "$PIDF" || true)
    if [[ -n "${old:-}" ]]; then kill "$old" 2>/dev/null || true; fi
  fi
  nohup "$PY" "$DAEMON_PY" >>"$LOG" 2>&1 &
  echo $! >"$PIDF"
  echo "[shorts] daemon pid=$(cat "$PIDF") log=$LOG py=$PY"
  exit 0
fi
exec "$PY" "$DAEMON_PY"
