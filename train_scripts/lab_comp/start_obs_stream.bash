#!/usr/bin/env bash
# Полный эфир: Presentation Unity (если надо) + OBS --startstreaming.
#   RUN_ID=jlg_finetune_2 bash train_scripts/lab_comp/start_obs_stream.bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"
RUN_ID="${RUN_ID:?задайте RUN_ID}"

echo "[obs_stream] start RUN_ID=${RUN_ID}"

# 1) Presentation, если ещё нет
if ! pgrep -f 'stream_onnx_infer\.py' >/dev/null 2>&1 \
  && ! pgrep -f 'forestStreamOnly' >/dev/null 2>&1; then
  echo "[obs_stream] Presentation нет → restart_stream_for_run"
  bash "${ROOT}/train_scripts/lab_comp/restart_stream_for_run.bash"
else
  echo "[obs_stream] Presentation уже жив"
fi

# 2) OBS в эфир
bash "${ROOT}/train_scripts/lab_comp/restart_obs.bash"
echo "[obs_stream] done"
