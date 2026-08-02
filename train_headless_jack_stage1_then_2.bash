#!/usr/bin/env bash
# Jack: Stage1 → Stage2 подряд.
#   bash train_headless_jack_stage1_then_2.bash
#   RUN_ID_STAGE1=97 bash train_headless_jack_stage1_then_2.bash
#   RUN_ID_STAGE1=97 RUN_ID_STAGE2=97_stage2 bash train_headless_jack_stage1_then_2.bash
set -eu
set -o pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
cd "${ROOT}"

STAGE1_ID="${RUN_ID_STAGE1:-}"
STAGE2_ID="${RUN_ID_STAGE2:-}"

echo "[jack_pipeline] === STAGE 1 ==="
if [ -n "${STAGE1_ID}" ]; then
  RUN_ID="${STAGE1_ID}" bash "${ROOT}/train_headless_jack_stage1.bash" "$@"
else
  bash "${ROOT}/train_headless_jack_stage1.bash" "$@"
fi

# Stage1 пишет в RUN_ID; для wood_food нет .last_* — берём заданный id или последний из echo.
if [ -z "${STAGE1_ID}" ]; then
  if [ -f "${ROOT}/results/.last_jack_stage1_run_id" ]; then
    STAGE1_ID="$(tr -d '\r\n' < "${ROOT}/results/.last_jack_stage1_run_id")"
  else
    echo "ERROR: задай RUN_ID_STAGE1=... (нет .last_jack_stage1_run_id)" >&2
    exit 1
  fi
fi
echo "[jack_pipeline] Stage1 done: ${STAGE1_ID}"
echo "[jack_pipeline] === STAGE 2 (INIT_FROM=${STAGE1_ID}) ==="

if [ -n "${STAGE2_ID}" ]; then
  INIT_FROM="${STAGE1_ID}" RUN_ID="${STAGE2_ID}" bash "${ROOT}/train_headless_jack_stage2.bash"
else
  INIT_FROM="${STAGE1_ID}" bash "${ROOT}/train_headless_jack_stage2.bash"
fi

echo "[jack_pipeline] готово: Stage1=${STAGE1_ID} → Stage2=${STAGE2_ID:-${STAGE1_ID}_stage2}"
