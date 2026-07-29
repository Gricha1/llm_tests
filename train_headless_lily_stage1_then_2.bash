#!/usr/bin/env bash
# Lily: Stage1 → Stage2 подряд. Stage1 id пишется в results/.last_lily_stage1_run_id
#   bash train_headless_lily_stage1_then_2.bash
#   RUN_ID_STAGE1=run_100 bash train_headless_lily_stage1_then_2.bash
set -eu
set -o pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
cd "${ROOT}"

STAGE1_ID="${RUN_ID_STAGE1:-}"
STAGE2_ID="${RUN_ID_STAGE2:-}"

echo "[lily_pipeline] === STAGE 1 ==="
if [ -n "${STAGE1_ID}" ]; then
  RUN_ID="${STAGE1_ID}" bash "${ROOT}/train_headless_lily_stage1.bash" "$@"
else
  bash "${ROOT}/train_headless_lily_stage1.bash" "$@"
fi

STAGE1_ID="$(tr -d '\r\n' < "${ROOT}/results/.last_lily_stage1_run_id")"
echo "[lily_pipeline] Stage1 done: ${STAGE1_ID}"
echo "[lily_pipeline] === STAGE 2 (INIT_FROM=${STAGE1_ID}) ==="

if [ -n "${STAGE2_ID}" ]; then
  INIT_FROM="${STAGE1_ID}" RUN_ID="${STAGE2_ID}" bash "${ROOT}/train_headless_lily_stage2.bash"
else
  INIT_FROM="${STAGE1_ID}" bash "${ROOT}/train_headless_lily_stage2.bash"
fi

STAGE2_DONE="$(tr -d '\r\n' < "${ROOT}/results/.last_lily_stage2_run_id")"
echo "[lily_pipeline] готово: Stage1=${STAGE1_ID} → Stage2=${STAGE2_DONE}"
