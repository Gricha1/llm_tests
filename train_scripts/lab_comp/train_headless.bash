#!/usr/bin/env bash
# Обучение Jack на lab_comp: Linux headless-билд + mlagents-learn.
#
# Перед первым запуском:
#   1) bash train_scripts/lab_comp/setup_mlagents.bash   (на сервере)
#   2) Собрать Linux билд в Unity на Windows → build_versions/jack_cow.x86_64
#   3) bash train_scripts/lab_comp/sync_to_lab_comp.bash
#
# Запуск на сервере:
#   RUN_ID=jack_stream_05_07_2026_copy bash train_scripts/lab_comp/train_headless.bash
#   RUN_ID=jack_stream_05_07_2026_copy bash train_scripts/lab_comp/train_headless.bash --resume
#
# Переменные:
#   BUILD=jack_cow.x86_64   имя папки в build_versions/
#   NUM_ENVS=20             параллельных env
#   TIME_SCALE=5

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

BUILD="${BUILD:-jack_cow.x86_64}"
NUM_ENVS="${NUM_ENVS:-20}"
TIME_SCALE="${TIME_SCALE:-5}"

if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda activate mlagents
fi

if ! command -v mlagents-learn >/dev/null 2>&1; then
  echo "ERROR: mlagents-learn не найден. Сначала: bash train_scripts/lab_comp/setup_mlagents.bash" >&2
  exit 1
fi

BUILD_PATH="build_versions/${BUILD}"
if [ ! -d "${BUILD_PATH}" ]; then
  echo "ERROR: ${BUILD_PATH} не найден." >&2
  echo "Собери Linux Server/Headless билд в Unity на Windows и синхронизируй:" >&2
  echo "  bash train_scripts/lab_comp/sync_to_lab_comp.bash" >&2
  exit 1
fi

chmod +x "${BUILD_PATH}"/*.x86_64 2>/dev/null || true

export NUM_ENVS
export TIME_SCALE
export RUN_ID="${RUN_ID:-}"

exec bash train_scripts/train_jack.bash "${BUILD}" "${NUM_ENVS}" "$@"
