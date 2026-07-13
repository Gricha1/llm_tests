#!/usr/bin/env bash
# Проверка: Unity Editor реально запускается (glibc и т.п.).
check_unity_runnable() {
  local unity="${1:-}"
  [ -n "${unity}" ] && [ -f "${unity}" ] || return 1

  if [[ "${unity}" == *.exe ]]; then
    return 0
  fi

  local err=""
  if ! err="$("${unity}" -version 2>&1)"; then
    if echo "${err}" | grep -q 'GLIBC_2\.[0-9]'; then
      echo "${err}" | grep -m1 'GLIBC_' >&2 || true
      return 2
    fi
    echo "${err}" >&2
    return 1
  fi
  return 0
}

unity_glibc_hint() {
  echo "Unity 6000 нужен glibc >= 2.28 (Ubuntu 22.04+)." >&2
  echo "На сервере: bash train_scripts/lab_comp/upgrade_to_2204.bash" >&2
  echo "Или с Windows/WSL: RUN_ID=... bash train_scripts/lab_comp/sentis_bridge_wsl.bash" >&2
}
