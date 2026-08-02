#!/usr/bin/env bash
# Убить только UI-validate (mp4), train и стрим не трогать.
set -eu
set -o pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

echo "[kill_validate] останавливаю validate_ui / forestValidate…"
ME=$$
PARENT=$PPID
skip() { [ "$1" = "$ME" ] || [ "$1" = "$PARENT" ]; }

for pid in $(pgrep -f 'train_scripts/lab_comp/validate_ui_one[.]bash' 2>/dev/null || true); do
  skip "$pid" && continue
  kill -TERM "$pid" 2>/dev/null || true
done
sleep 1
for proc in /proc/[0-9]*; do
  pid="${proc#/proc/}"
  skip "$pid" && continue
  [ -r "$proc/cmdline" ] || continue
  cmd="$(tr '\0' ' ' <"$proc/cmdline" 2>/dev/null || true)"
  [ -n "$cmd" ] || continue
  case "$cmd" in
    *validate_ui_one.bash*|*forestValidate*|*ui_val_*_frames*|*_ui_val_*)
      kill -TERM "$pid" 2>/dev/null || true
      ;;
  esac
done
sleep 2
for proc in /proc/[0-9]*; do
  pid="${proc#/proc/}"
  skip "$pid" && continue
  [ -r "$proc/cmdline" ] || continue
  cmd="$(tr '\0' ' ' <"$proc/cmdline" 2>/dev/null || true)"
  [ -n "$cmd" ] || continue
  case "$cmd" in
    *validate_ui_one.bash*|*forestValidate*|*ui_val_*_frames*|*_ui_val_*)
      kill -KILL "$pid" 2>/dev/null || true
      ;;
  esac
done

n="$(pgrep -c -f 'forestValidate|validate_ui_one' 2>/dev/null || true)"
echo "[kill_validate] осталось≈${n:-0}"
free -h | awk '/Mem:/{printf "[kill_validate] RAM available≈%s / %s\n", $7, $2}'
