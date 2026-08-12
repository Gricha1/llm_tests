#!/usr/bin/env bash
# Убить только OBS/onnx стрим Presentation (супервизор + Unity).
# Train / validate / Streaming Survival / OBS не трогать.
set -eu
set -o pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

echo "[kill_stream] останавливаю Presentation stream (навсегда, без рестарта)…"

# Сначала флаг стопа — супервизор while true должен выйти, а не поднять снова.
touch "${ROOT}/.stream_stop_request" 2>/dev/null || true
rm -f "${ROOT}/.stream_restart_request" 2>/dev/null || true
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

# Старые tmux-сессии стрима (в т.ч. forest_survival_stream с июля)
for sess in fui_joint_stream forest_survival_stream stream_onnx stream; do
  tmux kill-session -t "${sess}" 2>/dev/null || true
done

# Супервизор / python onnx — до Unity
pkill -TERM -f 'run_stream_onnx\.bash' 2>/dev/null || true
pkill -TERM -f 'restart_stream_for_run\.bash' 2>/dev/null || true
pkill -TERM -f 'stream_onnx_infer\.py' 2>/dev/null || true
sleep 1
pkill -KILL -f 'run_stream_onnx\.bash' 2>/dev/null || true
pkill -KILL -f 'restart_stream_for_run\.bash' 2>/dev/null || true
pkill -KILL -f 'stream_onnx_infer\.py' 2>/dev/null || true

# Unity Presentation: forestStreamOnly БЕЗ Streaming Survival
# (иначе pkill forestStreamOnly убивает и Survival followers)
kill_presentation_unity() {
  local sig="$1"
  local pids
  pids="$(pgrep -f 'forestStreamOnly' 2>/dev/null || true)"
  for pid in ${pids}; do
    cmd="$(tr '\0' ' ' </proc/${pid}/cmdline 2>/dev/null || true)"
    if echo "${cmd}" | grep -q 'forestStreamingSurvival'; then
      continue
    fi
    echo "[kill_stream] ${sig} unity pid=${pid}"
    kill "-${sig}" "${pid}" 2>/dev/null || true
  done
}
kill_presentation_unity TERM
sleep 1
kill_presentation_unity KILL

rm -f "${ROOT}/.stream_stop_request" "${ROOT}/.stream_restart_request" 2>/dev/null || true

n_inf="$(pgrep -c -f 'stream_onnx_infer\.py' 2>/dev/null || echo 0)"
n_run="$(pgrep -c -f 'run_stream_onnx\.bash' 2>/dev/null || echo 0)"
n_u=0
for pid in $(pgrep -f 'forestStreamOnly' 2>/dev/null || true); do
  cmd="$(tr '\0' ' ' </proc/${pid}/cmdline 2>/dev/null || true)"
  if echo "${cmd}" | grep -q 'forestStreamingSurvival'; then
    continue
  fi
  n_u=$((n_u + 1))
done
echo "[kill_stream] осталось: onnx_infer=${n_inf:-0} run_stream=${n_run:-0} presentation_unity=${n_u}"
free -h | awk '/Mem:/{printf "[kill_stream] RAM available≈%s / %s\n", $7, $2}'
