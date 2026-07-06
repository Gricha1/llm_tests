#!/usr/bin/env bash
set -eu
set -o pipefail

# Продолжить обучение Jack с чекпоинта jack_stream_05_07_2026_copy (~500k steps).
#
# Unity Editor (нажать Play, когда mlagents-learn ждёт):
#   bash train_scripts/resume_jack_stream_05_07_2026_copy.bash
#
# С более медленной симуляцией (реже шаги ML-Agents):
#   TIME_SCALE=2 bash train_scripts/resume_jack_stream_05_07_2026_copy.bash
#
# «Обычная» скорость кадров Unity (не ускорение mlagents):
#   TIME_SCALE=1 bash train_scripts/resume_jack_stream_05_07_2026_copy.bash
#
# Headless-билд:
#   bash train_scripts/resume_jack_stream_05_07_2026_copy.bash jack_cow.x86_64 20
#   TIME_SCALE=2 bash train_scripts/resume_jack_stream_05_07_2026_copy.bash jack_cow.x86_64 20

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
cd "${ROOT}"

RUN_ID="jack_stream_05_07_2026_copy"
COPY_DIR="results/${RUN_ID}"

if [ ! -d "${COPY_DIR}" ]; then
  echo "ERROR: ${COPY_DIR} not found. Сначала создайте копию run." >&2
  exit 1
fi

if [ ! -d "${COPY_DIR}/JackLowLevelAgent" ]; then
  echo "ERROR: ${COPY_DIR}/JackLowLevelAgent not found" >&2
  exit 1
fi

export RUN_ID
export NUM_ENVS="${NUM_ENVS:-1}"
export TIME_SCALE="${TIME_SCALE:-3}"

echo "[resume_jack_stream] continuing from: ${COPY_DIR}"
echo "[resume_jack_stream] num-envs: ${NUM_ENVS}, time-scale: ${TIME_SCALE}"

# --resume читает checkpoint.pt; короткий прерванный запуск мог перезаписать его на ~6k шагов.
echo "[resume_jack_stream] promoting latest valid checkpoint -> checkpoint.pt"
bash train_scripts/promote_latest_checkpoint.bash "${COPY_DIR}"

exec bash train_scripts/train_jack.bash "$@" --resume
