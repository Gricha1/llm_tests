#!/usr/bin/env bash
# Train в фоне + stream на переднем плане (один терминал).
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

# shellcheck source=lab_comp_env.bash
source "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash"

mkdir -p logs
LOG="logs/train_${RUN_ID}.log"

RESUME=0
FORCE=0
for arg in "$@"; do
  [ "${arg}" = "--resume" ] && RESUME=1
  [ "${arg}" = "--force" ] && FORCE=1
done

TRAIN_ARGS=()
[ "${RESUME}" -eq 1 ] && TRAIN_ARGS+=(--resume)
[ "${FORCE}" -eq 1 ] && TRAIN_ARGS+=(--force)

if pgrep -f "train_headless_jack_lily_george.bash.*${RUN_ID}" >/dev/null 2>&1; then
  echo "[run_all] train уже запущен для RUN_ID=${RUN_ID}"
else
  echo "[run_all] train -> ${LOG}"
  nohup env BUILD="${BUILD}" RUN_ID="${RUN_ID}" DISPLAY="${DISPLAY}" \
    bash train_headless_jack_lily_george.bash "${TRAIN_ARGS[@]}" \
    >> "${LOG}" 2>&1 &
  echo "[run_all] train pid=$!"
  sleep 2
fi

exec bash train_scripts/lab_comp/run_stream.bash
