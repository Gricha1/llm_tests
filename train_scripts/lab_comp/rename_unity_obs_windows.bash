#!/usr/bin/env bash
# Переименование окон Unity для OBS по PID + --mlagents-port (работает без пересборки билда).
#   bash train_scripts/lab_comp/rename_unity_obs_windows.bash stream_forest_survival_2_12_07_2026 5005
set -u

BUILD_SUBSTR="${1:?build name substring}"
BASE_PORT="${2:?base port}"

read_mlagents_port() {
  local pid=$1
  local arg next
  while IFS= read -r -d '' arg; do
    if [ "${arg}" = "--mlagents-port" ]; then
      IFS= read -r -d '' next || true
      if [[ "${next}" =~ ^[0-9]+$ ]]; then
        echo "${next}"
        return 0
      fi
    fi
  done < "/proc/${pid}/cmdline" 2>/dev/null
  return 1
}

set_title_for_window() {
  local wid=$1
  local title=$2
  if command -v xdotool >/dev/null 2>&1; then
    xdotool set_window --name "${title}" "${wid}" 2>/dev/null || true
  fi
  if command -v xprop >/dev/null 2>&1; then
    xprop -id "${wid}" -f _NET_WM_NAME 8u -set _NET_WM_NAME "${title}" 2>/dev/null || true
    xprop -id "${wid}" WM_NAME "${title}" 2>/dev/null || true
  fi
}

rename_pid() {
  local pid=$1
  local port worker title wid

  [ -r "/proc/${pid}/cmdline" ] || return 0
  tr '\0' ' ' < "/proc/${pid}/cmdline" | grep -q "${BUILD_SUBSTR}" || return 0

  port="$(read_mlagents_port "${pid}")" || return 0
  worker=$((port - BASE_PORT))
  if [ "${worker}" -lt 0 ] || [ "${worker}" -gt 11 ]; then
    return 0
  fi

  if [ "${worker}" -eq 0 ]; then
    title="forest_survival w${worker} PRESENTATION"
  else
    title="forest_survival w${worker} train"
  fi

  if command -v wmctrl >/dev/null 2>&1; then
    while read -r wid _rest; do
      [ -n "${wid}" ] || continue
      wmctrl -ir "${wid}" -T "${title}" 2>/dev/null || true
      wmctrl -ir "${wid}" -N "${title}" 2>/dev/null || true
      set_title_for_window "${wid}" "${title}"
    done < <(wmctrl -lp 2>/dev/null | awk -v p="${pid}" '$3 == p {print $1}')
  fi

  if command -v xdotool >/dev/null 2>&1; then
    for wid in $(xdotool search --pid "${pid}" 2>/dev/null || true); do
      set_title_for_window "${wid}" "${title}"
    done
  fi
}

echo "[rename_obs] BUILD=${BUILD_SUBSTR} base-port=${BASE_PORT} DISPLAY=${DISPLAY:-:0}"
if ! command -v wmctrl >/dev/null 2>&1 && ! command -v xdotool >/dev/null 2>&1; then
  echo "[rename_obs] WARNING: установи wmctrl или xdotool: sudo apt install wmctrl xdotool" >&2
fi

while true; do
  if ! pgrep -f "${BUILD_SUBSTR}" >/dev/null 2>&1; then
    sleep 3
    continue
  fi

  while read -r pid; do
    [ -n "${pid}" ] || continue
    rename_pid "${pid}"
  done < <(pgrep -f "${BUILD_SUBSTR}" 2>/dev/null || true)

  sleep 2
done
