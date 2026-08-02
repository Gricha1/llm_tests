#!/usr/bin/env bash
# Убить только OBS/onnx стрим Presentation, train и validate не трогать.
set -eu
set -o pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

echo "[kill_stream] останавливаю Presentation stream…"
touch "${ROOT}/.stream_stop_request" 2>/dev/null || true
sleep 1

# UI detached-слот стрима
PIDF=/tmp/forest_ui_fui_joint_stream.pid
if [ -f "$PIDF" ]; then
  old="$(tr -d ' \r\n' <"$PIDF" || true)"
  if [ -n "${old:-}" ]; then
    echo "[kill_stream] UI slot fui_joint_stream pid=${old}"
    kill -TERM "$old" 2>/dev/null || true
    sleep 1
    kill -KILL "$old" 2>/dev/null || true
    pkill -TERM -P "$old" 2>/dev/null || true
    pkill -KILL -P "$old" 2>/dev/null || true
  fi
  rm -f "$PIDF"
fi

pkill -TERM -f 'stream_onnx_infer\.py' 2>/dev/null || true
pkill -TERM -f 'forestStreamOnly' 2>/dev/null || true
pkill -TERM -f 'run_stream_onnx\.bash' 2>/dev/null || true
pkill -TERM -f 'restart_stream_for_run\.bash' 2>/dev/null || true
tmux kill-session -t fui_joint_stream 2>/dev/null || true
sleep 2
pkill -KILL -f 'stream_onnx_infer\.py' 2>/dev/null || true
pkill -KILL -f 'forestStreamOnly' 2>/dev/null || true
pkill -KILL -f 'run_stream_onnx\.bash' 2>/dev/null || true
pkill -KILL -f 'restart_stream_for_run\.bash' 2>/dev/null || true

rm -f "${ROOT}/.stream_stop_request" "${ROOT}/.stream_restart_request" 2>/dev/null || true

n_inf="$(pgrep -c -f 'stream_onnx_infer\.py' 2>/dev/null || true)"
n_u="$(pgrep -c -f 'forestStreamOnly' 2>/dev/null || true)"
echo "[kill_stream] осталось: onnx_infer=${n_inf:-0} forestStreamOnly=${n_u:-0}"
free -h | awk '/Mem:/{printf "[kill_stream] RAM available≈%s / %s\n", $7, $2}'
