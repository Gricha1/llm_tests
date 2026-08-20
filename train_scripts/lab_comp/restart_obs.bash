#!/usr/bin/env bash
# Рестарт OBS Studio на lab_comp сразу в эфир (--startstreaming). Unity / train не трогает.
#   bash train_scripts/lab_comp/restart_obs.bash
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
export DISPLAY="${DISPLAY:-:1}"
# shellcheck source=cpu_affinity.env.bash
source "${ROOT}/train_scripts/lab_comp/cpu_affinity.env.bash" 2>/dev/null || true
OBS_CPUS="${FOREST_OBS_CPUS:-4-5}"
LOG="${FOREST_OBS_LOG:-/tmp/forest_obs_restart.log}"

if [ -z "${XAUTHORITY:-}" ] || [ ! -f "${XAUTHORITY}" ]; then
  if [ -f "/run/user/$(id -u)/gdm/Xauthority" ]; then
    export XAUTHORITY="/run/user/$(id -u)/gdm/Xauthority"
  elif [ -f "${HOME}/.Xauthority" ]; then
    export XAUTHORITY="${HOME}/.Xauthority"
  fi
fi

echo "[restart_obs] DISPLAY=${DISPLAY} XAUTHORITY=${XAUTHORITY:-none} cpus=${OBS_CPUS}"
if pgrep -x obs >/dev/null 2>&1; then
  echo "[restart_obs] stop old obs: $(pgrep -x obs | tr '\n' ' ')"
  pkill -TERM -x obs || true
  sleep 1
  pkill -KILL -x obs 2>/dev/null || true
  sleep 1
fi

# Звук: forest_stream.monitor (если есть pactl) — без падения если нет.
bash "${ROOT}/train_scripts/lab_comp/setup_stream_audio_route.bash" 2>/dev/null || true

if command -v taskset >/dev/null 2>&1; then
  nohup taskset -c "${OBS_CPUS}" obs --startstreaming >"${LOG}" 2>&1 &
else
  nohup obs --startstreaming >"${LOG}" 2>&1 &
fi
sleep 3
echo "[restart_obs] obs pids: $(pgrep -x obs | tr '\n' ' ' || echo none)"
echo "[restart_obs] log=${LOG}"
tail -20 "${LOG}" 2>/dev/null || true
