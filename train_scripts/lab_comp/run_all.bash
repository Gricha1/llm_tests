#!/usr/bin/env bash
# Train only (12 headless). Стрим: run_stream_onnx.bash
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

echo "[run_all] train headless — стрим: RUN_ID=... bash train_scripts/lab_comp/run_stream_onnx.bash"
exec bash train_scripts/lab_comp/run_train.bash "${ARGS[@]}"
