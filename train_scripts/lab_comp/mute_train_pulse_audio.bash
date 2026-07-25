#!/usr/bin/env bash
# Train → mute / null-sink; стрим и OBS — всегда unmute.
# Не глушим «всё подряд»: иначе при гонке (Unity ещё без forestStreamOnly)
# Pulse запоминает Mute для FMOD и стрим стартует без звука.
set -eu

stream_pids=""
while read -r pid; do
  [ -z "$pid" ] && continue
  stream_pids="$stream_pids $pid"
done < <(pgrep -f 'forestStreamOnly' 2>/dev/null || true)

obs_pids=""
while read -r pid; do
  [ -z "$pid" ] && continue
  obs_pids="$obs_pids $pid"
done < <(pgrep -x obs 2>/dev/null || true)

echo "[mute_train_audio] stream pids:${stream_pids:- none} obs:${obs_pids:- none}"

is_pid_in() {
  local needle="$1"
  local list="$2"
  local k
  for k in $list; do
    if [ "$needle" = "$k" ]; then
      return 0
    fi
  done
  return 1
}

  # Headless train: -batchmode/-nographics, без forestStreamOnly.
  # Jack train: -forestTrainAllHeadless (в yaml часто no_graphics:false — без этого флага Pulse не глушил).
is_train_pid() {
  local pid="$1"
  local cmd
  [ -z "$pid" ] && return 1
  if is_pid_in "$pid" "$stream_pids"; then
    return 1
  fi
  cmd="$(tr '\0' ' ' <"/proc/${pid}/cmdline" 2>/dev/null || true)"
  [ -z "$cmd" ] && return 1
  case "$cmd" in
    *forestStreamOnly*) return 1 ;;
  esac
  case "$cmd" in
    *-batchmode*|*-nographics*|*forestTrainAllHeadless*) return 0 ;;
  esac
  return 1
}

sink_of_input() {
  local idx="$1"
  pactl list sink-inputs 2>/dev/null | awk -v id="$idx" '
    $0 ~ ("Sink Input #" id "$") {p=1; next}
    p && /^Sink Input #/ {exit}
    p && /Sink:/ {
      for (i = 1; i <= NF; i++) if ($i ~ /^[0-9]+$/) { print $i; exit }
    }
  '
}

null_sink_idx="$(pactl list short sinks 2>/dev/null | awk '$2 == "forest_train_null" {print $1; exit}')"

count=0
muted=0
unmuted=0
skipped=0
while IFS=$'\t' read -r idx rest; do
  [ -z "${idx:-}" ] && continue
  count=$((count + 1))
  info="$(pactl list sink-inputs 2>/dev/null | awk -v id="$idx" '
    $0 ~ ("Sink Input #" id "$") {p=1; next}
    p && /^Sink Input #/ {exit}
    p {print}
  ')"
  pid="$(printf '%s\n' "$info" | sed -n 's/.*application.process.id = "\([0-9]*\)"/\1/p' | head -1)"
  bin="$(printf '%s\n' "$info" | sed -n 's/.*application.process.binary = "\(.*\)"/\1/p' | head -1)"
  input_sink="$(sink_of_input "$idx")"

  if is_pid_in "${pid:-}" "$stream_pids" || is_pid_in "${pid:-}" "$obs_pids"; then
    pactl set-sink-input-mute "$idx" 0 2>/dev/null || true
    pactl set-sink-input-volume "$idx" 100% 2>/dev/null || true
    unmuted=$((unmuted + 1))
    echo "  UNMUTE #$idx pid=$pid bin=$bin (stream/obs)"
    continue
  fi

  # Mute только явный train (null-sink или headless cmdline).
  if [ -n "${null_sink_idx:-}" ] && [ "${input_sink:-}" = "$null_sink_idx" ]; then
    if pactl set-sink-input-mute "$idx" 1 2>/dev/null; then
      muted=$((muted + 1))
      echo "  mute #$idx pid=$pid bin=$bin (null-sink)"
    fi
    continue
  fi

  if is_train_pid "${pid:-}"; then
    if pactl set-sink-input-mute "$idx" 1 2>/dev/null; then
      muted=$((muted + 1))
      echo "  mute #$idx pid=$pid bin=$bin (train cmdline)"
    fi
    continue
  fi

  # Всё остальное (в т.ч. FMOD стрима до появления forestStreamOnly) — unmute.
  pactl set-sink-input-mute "$idx" 0 2>/dev/null || true
  skipped=$((skipped + 1))
  echo "  keep/unmute #$idx pid=$pid bin=$bin"
done < <(pactl list short sink-inputs 2>/dev/null || true)

# Hardware sink не muted (НЕ forest_train_null).
sink="$(pactl info 2>/dev/null | sed -n 's/^Default Sink: //p')"
if [ -z "$sink" ] || [ "$sink" = "forest_train_null" ]; then
  sink="$(pactl list short sinks 2>/dev/null | awk '$2 !~ /null/ {print $2; exit}')"
fi
if [ -n "$sink" ] && [ "$sink" != "forest_train_null" ]; then
  pactl set-default-sink "$sink" 2>/dev/null || true
  pactl set-sink-mute "$sink" 0 2>/dev/null || true
  pactl set-sink-volume "$sink" 100% 2>/dev/null || true
  while IFS=$'\t' read -r idx rest; do
    [ -z "${idx:-}" ] && continue
    info="$(pactl list sink-inputs 2>/dev/null | awk -v id="$idx" '
      $0 ~ ("Sink Input #" id "$") {p=1; next}
      p && /^Sink Input #/ {exit}
      p {print}
    ')"
    pid="$(printf '%s\n' "$info" | sed -n 's/.*application.process.id = "\([0-9]*\)"/\1/p' | head -1)"
    if is_pid_in "${pid:-}" "$stream_pids"; then
      pactl move-sink-input "$idx" "$sink" 2>/dev/null || true
      pactl set-sink-input-mute "$idx" 0 2>/dev/null || true
    fi
  done < <(pactl list short sink-inputs 2>/dev/null || true)
fi

echo "[mute_train_audio] inputs=$count muted=$muted unmuted_stream_obs=$unmuted kept=$skipped sink=${sink:-?}"
