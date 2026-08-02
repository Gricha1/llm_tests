#!/usr/bin/env bash
# Train → forest_train_null + mute + cork.
# Stream Unity → forest_stream (отдельный null @48k) — OBS берёт forest_stream.monitor.
# Так звук стрима не идёт через S/PDIF с латентностью ~300мс и не делит микшер с 21 train FMOD.
set -eu

ensure_sinks() {
  if ! command -v pactl >/dev/null 2>&1; then
    return 0
  fi
  if ! pactl list short sinks 2>/dev/null | grep -q $'\tforest_train_null\t'; then
    pactl load-module module-null-sink \
      sink_name=forest_train_null \
      sink_properties=device.description=ForestTrainNull \
      >/dev/null 2>&1 || true
  fi
  if ! pactl list short sinks 2>/dev/null | grep -q $'\tforest_stream\t'; then
    pactl load-module module-null-sink \
      sink_name=forest_stream \
      sink_properties=device.description=ForestStream \
      rate=48000 channels=2 \
      >/dev/null 2>&1 || true
  fi
  # default = железо (не null), чтобы десктоп/прочее не уезжали в train null.
  hw="$(pactl list short sinks 2>/dev/null | awk '$2 !~ /null|forest_stream|forest_ui_val/ {print $2; exit}')"
  if [ -n "$hw" ]; then
    pactl set-default-sink "$hw" >/dev/null 2>&1 || true
  fi
  # train null не должен быть suspended — иначе FMOD спамит reconnect; cork достаточно.
  pactl suspend-sink forest_train_null 0 >/dev/null 2>&1 || true
  pactl suspend-sink forest_stream 0 >/dev/null 2>&1 || true
}

ensure_sinks

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
stream_sink_idx="$(pactl list short sinks 2>/dev/null | awk '$2 == "forest_stream" {print $1; exit}')"

count=0
muted=0
unmuted=0
skipped=0
moved_null=0
moved_stream=0
corked=0
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
  muted_flag="$(printf '%s\n' "$info" | awk '/Mute:/{print $2; exit}')"
  corked_flag="$(printf '%s\n' "$info" | awk '/Corked:/{print $2; exit}')"

  # OBS playback (редко) — не трогаем.
  if is_pid_in "${pid:-}" "$obs_pids"; then
    skipped=$((skipped + 1))
    continue
  fi

  # Stream Unity → forest_stream, unmute, uncork.
  if is_pid_in "${pid:-}" "$stream_pids"; then
    need=0
    if [ -n "${stream_sink_idx:-}" ] && [ "${input_sink:-}" != "$stream_sink_idx" ]; then
      if pactl move-sink-input "$idx" "$stream_sink_idx" 2>/dev/null; then
        moved_stream=$((moved_stream + 1))
        need=1
      fi
    fi
    if [ "${muted_flag:-}" = "yes" ]; then
      pactl set-sink-input-mute "$idx" 0 2>/dev/null || true
      need=1
    fi
    if [ "${corked_flag:-}" = "yes" ]; then
      pactl set-sink-input-cork "$idx" 0 2>/dev/null || true
      need=1
    fi
    if [ "$need" -eq 1 ]; then
      pactl set-sink-input-volume "$idx" 100% 2>/dev/null || true
      unmuted=$((unmuted + 1))
      echo "  STREAM #$idx pid=$pid -> forest_stream"
    else
      skipped=$((skipped + 1))
    fi
    continue
  fi

  # Train / прочий FMOD → null + mute (cork на старом Pulse нет).
  if is_train_pid "${pid:-}" \
    || { [ -n "${null_sink_idx:-}" ] && [ "${input_sink:-}" = "$null_sink_idx" ]; }; then
    changed=0
    if [ -n "${null_sink_idx:-}" ] && [ "${input_sink:-}" != "$null_sink_idx" ]; then
      if pactl move-sink-input "$idx" "$null_sink_idx" 2>/dev/null; then
        moved_null=$((moved_null + 1))
        changed=1
      fi
    fi
    if [ "${muted_flag:-}" != "yes" ]; then
      if pactl set-sink-input-mute "$idx" 1 2>/dev/null; then
        muted=$((muted + 1))
        changed=1
      fi
    fi
    if [ "$changed" -eq 1 ]; then
      echo "  mute+null #$idx pid=$pid bin=$bin (train)"
    else
      skipped=$((skipped + 1))
    fi
    continue
  fi

  skipped=$((skipped + 1))
  echo "  keep #$idx pid=$pid bin=$bin"
done < <(pactl list short sink-inputs 2>/dev/null || true)

# Train null: suspend — иначе Pulse крутит 20+ FMOD и рвёт звук стрима (~20% CPU).
# Stream на forest_stream не затрагивается.
if pactl list short sinks 2>/dev/null | grep -q $'\tforest_train_null\t'; then
  pactl suspend-sink forest_train_null 1 >/dev/null 2>&1 || true
fi

echo "[mute_train_audio] inputs=$count muted=$muted moved_null=$moved_null moved_stream=$moved_stream unmuted_stream=$unmuted kept=$skipped stream_sink=${stream_sink_idx:-?} null=${null_sink_idx:-?}"
