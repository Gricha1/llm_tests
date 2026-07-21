#!/usr/bin/env bash
# Train + presentation в одном Unity (lab_comp).
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

echo "[run_train] BUILD=${BUILD} RUN_ID=${RUN_ID} DISPLAY=${DISPLAY} (num-envs=26 headless; stream: run_stream_onnx.bash)"
exec env BUILD="${BUILD}" RUN_ID="${RUN_ID}" DISPLAY="${DISPLAY}" TRAIN_MODE=presentation \
  FOREST_TRAIN_ALL_HEADLESS=1 \
  bash train_headless_jack_lily_george.bash "${ARGS[@]}"
