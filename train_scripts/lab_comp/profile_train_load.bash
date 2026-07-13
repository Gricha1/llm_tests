#!/usr/bin/env bash
# Профилирование CPU/GPU на lab_comp за 20 сек + график (запуск с Windows через WSL).
#
#   wsl bash train_scripts/lab_comp/profile_train_load.bash
#   wsl bash train_scripts/lab_comp/profile_train_load.bash --duration 30
#   wsl bash train_scripts/lab_comp/profile_train_load.bash --local   # уже на сервере
#
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

KEY="${SSH_KEY:-${HOME}/.ssh/lab_comp_key}"
REMOTE="${REMOTE:-reedgern@192.168.194.7}"

mkdir -p "${HOME}/.ssh"
if [ ! -f "${KEY}" ]; then
  cp /mnt/c/Users/User/.ssh/id_ed25519 "${KEY}" 2>/dev/null || true
  chmod 600 "${KEY}" 2>/dev/null || true
fi

DURATION=20
INTERVAL=1
MODE="--remote"

while [ $# -gt 0 ]; do
  case "$1" in
    --duration) DURATION="${2:?}"; shift 2 ;;
    --interval) INTERVAL="${2:?}"; shift 2 ;;
    --local) MODE="--local"; shift ;;
    --remote) MODE="--remote"; shift ;;
    *) echo "unknown arg: $1" >&2; exit 1 ;;
  esac
done

pick_python() {
  for cmd in python3.12 python3.11 python3.10 python3; do
    if command -v "${cmd}" >/dev/null 2>&1; then
      if "${cmd}" -c 'import sys; raise SystemExit(0 if sys.version_info >= (3, 7) else 1)' 2>/dev/null; then
        echo "${cmd}"
        return 0
      fi
    fi
  done
  return 1
}

PYTHON="$(pick_python)" || {
  echo "ERROR: нужен Python 3.7+ (python3.12 / python3.10 / python3)" >&2
  exit 1
}

export SSH_KEY="${KEY}"
export REMOTE="${REMOTE}"

echo "[profile] python=${PYTHON}"
echo "[profile] Запуск во время train/stream на сервере даст точные выводы."
exec "${PYTHON}" "${ROOT}/train_scripts/lab_comp/profile_train_load.py" \
  ${MODE} \
  --duration "${DURATION}" \
  --interval "${INTERVAL}" \
  --host "${REMOTE}" \
  --key "${KEY}" \
  --out-dir "${ROOT}/results/profiling"
