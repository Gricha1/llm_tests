#!/usr/bin/env bash
# Обёртка для mlagents --env.
# TRAIN_MODE=presentation + FOREST_TRAIN_ALL_HEADLESS=1 → все без окна (стрим отдельно).
# TRAIN_MODE=presentation без ALL_HEADLESS → worker 0 с графикой (старый режим).
set -eu
set -o pipefail

BUILD="${FOREST_BUILD_PATH:?FOREST_BUILD_PATH не задан}"
BASE_PORT="${FOREST_BASE_PORT:-}"
TRAIN_MODE="${FOREST_TRAIN_MODE:-presentation}"
ALL_HEADLESS="${FOREST_TRAIN_ALL_HEADLESS:-0}"

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
if [ "${ALL_HEADLESS}" != "1" ] \
  && [ "${TRAIN_MODE}" = "presentation" ] \
  && [ "${presentation_worker0}" -eq 1 ]; then
  if [ -n "${ml_port}" ] && [ -n "${forest_base_port}" ]; then
    worker=$((ml_port - forest_base_port))
    if [ "${worker}" -eq 0 ]; then
      use_graphics=1
    fi
  fi
fi

if [ "${use_graphics}" -eq 1 ]; then
  if command -v taskset >/dev/null 2>&1 && [ -n "${FOREST_TRAIN_CPUS:-}" ]; then
    exec taskset -c "${FOREST_TRAIN_CPUS}" "${BUILD}" "$@"
  fi
  exec "${BUILD}" "$@"
fi

if command -v taskset >/dev/null 2>&1 && [ -n "${FOREST_TRAIN_CPUS:-}" ]; then
  exec taskset -c "${FOREST_TRAIN_CPUS}" "${BUILD}" -batchmode -nographics "$@"
fi
exec "${BUILD}" -batchmode -nographics "$@"
