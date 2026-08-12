#!/usr/bin/env bash
# Стоп только Streaming Survival (не train, не OBS).
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"
PID_FILE="${ROOT}/.streaming_survival.pid"

echo "[ss] stop Streaming Survival…"
if [ -f "${PID_FILE}" ]; then
  old="$(cat "${PID_FILE}" || true)"
  if [ -n "${old}" ]; then
    kill -TERM "${old}" 2>/dev/null || true
    sleep 1
    kill -KILL "${old}" 2>/dev/null || true
  fi
  rm -f "${PID_FILE}"
fi
pkill -TERM -f 'forestStreamingSurvival' 2>/dev/null || true
pkill -KILL -f 'forestStreamingSurvival' 2>/dev/null || true
echo "[ss] done"
