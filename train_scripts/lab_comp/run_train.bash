#!/usr/bin/env bash
# Train + presentation в одном Unity (lab_comp).
#
# Новый прогон (папки ещё нет — так и должно быть):
#   RUN_ID=run_80 bash train_scripts/lab_comp/run_train.bash
#
# Продолжить существующий:
#   RUN_ID=run_80 bash train_scripts/lab_comp/run_train.bash --resume
#
# Снести и начать заново тот же id:
#   RUN_ID=run_80 bash train_scripts/lab_comp/run_train.bash --force
#
# Нельзя: --resume на пустой/новый run (раньше падало; теперь тихо стартует с нуля).
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

# shellcheck source=lab_comp_env.bash
source "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash"

RESUME=0
FORCE=0
for arg in "$@"; do
  [ "${arg}" = "--resume" ] && RESUME=1
  [ "${arg}" = "--force" ] && FORCE=1
done

ARGS=()
[ "${RESUME}" -eq 1 ] && ARGS+=(--resume)
[ "${FORCE}" -eq 1 ] && ARGS+=(--force)

echo "[run_train] BUILD=${BUILD} RUN_ID=${RUN_ID} DISPLAY=${DISPLAY} (num-envs=28 headless + TensorBoard; stream: run_stream_onnx.bash)"

# Старые mlagents/Unity headless часто остаются после Ctrl+C и жрут RAM.
echo "[run_train] чищу предыдущий train (стрим не трогаю)..."
bash "${ROOT}/train_scripts/lab_comp/kill_train.bash" || true

exec env BUILD="${BUILD}" RUN_ID="${RUN_ID}" DISPLAY="${DISPLAY}" TRAIN_MODE=presentation \
  FOREST_TRAIN_ALL_HEADLESS=1 \
  bash train_headless_jack_lily_george.bash "${ARGS[@]}"
