#!/usr/bin/env bash
# Обёртка для mlagents --env: worker 0 с дисплеем, остальные без окна.
# Переменные задаёт train_headless_jack_lily_george.bash:
#   FOREST_BUILD_PATH, FOREST_BASE_PORT, FOREST_TRAIN_MODE=presentation|multi
set -eu
set -o pipefail

BUILD="${FOREST_BUILD_PATH:?FOREST_BUILD_PATH не задан}"
BASE_PORT="${FOREST_BASE_PORT:-}"
TRAIN_MODE="${FOREST_TRAIN_MODE:-presentation}"

chmod +x "${BUILD}" 2>/dev/null || true

ml_port=""
forest_base_port="${BASE_PORT}"
presentation_worker0=0

args=("$@")
i=0
while [ "${i}" -lt "$#" ]; do
  arg="${args[${i}]}"
  case "${arg}" in
    --mlagents-port)
      i=$((i + 1))
      ml_port="${args[${i}]}"
      ;;
    -forestBasePort|--forest-base-port)
      i=$((i + 1))
      forest_base_port="${args[${i}]}"
      ;;
    -forestPresentationWorker0|--forest-presentation-worker0)
      presentation_worker0=1
      ;;
  esac
  i=$((i + 1))
done

use_graphics=0
if [ "${TRAIN_MODE}" = "presentation" ] && [ "${presentation_worker0}" -eq 1 ]; then
  if [ -n "${ml_port}" ] && [ -n "${forest_base_port}" ]; then
    worker=$((ml_port - forest_base_port))
    if [ "${worker}" -eq 0 ]; then
      use_graphics=1
    fi
  fi
fi

if [ "${use_graphics}" -eq 1 ]; then
  exec "${BUILD}" "$@"
fi

# Headless: как mlagents --no-graphics (DISPLAY оставляем — без него SIGSEGV).
exec "${BUILD}" -batchmode -nographics "$@"
