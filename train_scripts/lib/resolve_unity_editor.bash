#!/usr/bin/env bash
# Общий поиск Unity Editor (Linux Hub / UNITY_EDITOR / PATH).
resolve_unity_editor() {
  if [ -n "${UNITY_EDITOR:-}" ] && { [ -x "${UNITY_EDITOR}" ] || [ -f "${UNITY_EDITOR}" ]; }; then
    echo "${UNITY_EDITOR}"
    return 0
  fi

  local p
  for p in \
    "${HOME}/Unity/Hub/Editor/"*/Editor/Unity \
    "${HOME}/.local/share/Unity/Hub/Editor/"*/Editor/Unity \
    "/opt/unity/Editor/Unity" \
    "/usr/local/Unity/Editor/Unity"; do
    if [ -x "${p}" ]; then
      echo "${p}"
      return 0
    fi
  done

  if command -v Unity >/dev/null 2>&1; then
    echo "Unity"
    return 0
  fi

  # WSL: Unity Editor for Windows
  if [ -d "/mnt/c/Program Files/Unity/Hub/Editor" ]; then
    for p in "/mnt/c/Program Files/Unity/Hub/Editor/"*/Editor/Unity.exe; do
      if [ -f "${p}" ]; then
        echo "${p}"
        return 0
      fi
    done
  fi

  return 1
}
