#!/usr/bin/env bash
# Периодически: train → forest_train_null+mute, стрим/OBS → hardware.
#   bash train_scripts/lab_comp/watch_mute_train_audio.bash
# Стоп: pkill -f watch_mute_train_audio.bash
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
MUTE="${ROOT}/train_scripts/lab_comp/mute_train_pulse_audio.bash"
# Реже: mute-скрипт только чинит отклонения; частый pactl всё равно грузит Pulse.
INTERVAL="${FOREST_MUTE_INTERVAL_SEC:-30}"

if pgrep -f 'watch_mute_train_audio[.]bash' >/dev/null 2>&1; then
  me=$$
  for pid in $(pgrep -f 'watch_mute_train_audio[.]bash' 2>/dev/null || true); do
    [ "$pid" = "$me" ] && continue
    kill -TERM "$pid" 2>/dev/null || true
  done
  sleep 1
fi

echo "[watch_mute] interval=${INTERVAL}s log=/tmp/forest_mute_audio.log"
while true; do
  # Есть train или стрим — продолжаем; иначе можно спать дольше.
  if pgrep -f 'forestTrainAllHeadless|forestStreamOnly' >/dev/null 2>&1; then
    bash "${MUTE}" >>/tmp/forest_mute_audio.log 2>&1 || true
  fi
  sleep "${INTERVAL}"
done
