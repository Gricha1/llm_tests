#!/usr/bin/env bash
# TensorBoard на lab_comp (логи ML-Agents в results/<RUN_ID>/).
# Обычно вызывается с ПК через open_tensorboard.bash.
#
# На сервере вручную:
#   RUN_ID=run_75 bash train_scripts/lab_comp/run_tensorboard.bash --daemon
# Весь results/ (для UI Jack+Lily+George сразу):
#   bash train_scripts/lab_comp/run_tensorboard.bash --daemon --all

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

# shellcheck source=lab_comp_env.bash
source "${ROOT}/train_scripts/lab_comp/lab_comp_env.bash"

PORT="${PORT:-6006}"
DAEMON=0
ALL_RUNS=0
RUN_ARGS=()

for arg in "$@"; do
  case "${arg}" in
    --daemon) DAEMON=1 ;;
    --all|all|.)
      ALL_RUNS=1
      ;;
    --help|-h)
      sed -n '2,10p' "$0"
      exit 0
      ;;
    *)
      RUN_ARGS+=("${arg}")
      ;;
  esac
done

if [ "${ALL_RUNS}" = "0" ] && [ "${#RUN_ARGS[@]}" -eq 0 ] && [ -n "${RUN_ID:-}" ]; then
  if [ "${RUN_ID}" = "all" ] || [ "${RUN_ID}" = "." ]; then
    ALL_RUNS=1
  else
    RUN_ARGS=("${RUN_ID}")
  fi
fi

# Train/UI: по умолчанию смотрим весь results/, иначе новый запуск затирает TB
# и в UI пропадают Jack/Lily чужие runs.
if [ "${ALL_RUNS}" = "0" ] && [ "${#RUN_ARGS[@]}" -eq 0 ]; then
  ALL_RUNS=1
fi

if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda activate mlagents
fi

if ! command -v tensorboard >/dev/null 2>&1; then
  echo "[tensorboard] pip install tensorboard"
  pip install -q "tensorboard>=2.14,<3"
fi

# Обычный --logdir (не name:path): иначе TB 2.x молча не видит runs.
# Несколько run → --logdir_spec name1:path1,name2:path2
LOGDIR=""
LOGDIR_SPEC=""
USE_PARENT=0

if [ "${ALL_RUNS}" = "1" ]; then
  LOGDIR="${ROOT}/results"
  mkdir -p "${LOGDIR}"
  USE_PARENT=1
  n_events="$(find "${LOGDIR}" -name 'events.out.tfevents*' 2>/dev/null | wc -l | tr -d ' ')"
  echo "[tensorboard] ALL results/: ${n_events} event-файлов → runs вида <run>/<Behavior>"
else
  for run_id in "${RUN_ARGS[@]}"; do
    run_id="${run_id#results/}"
    dir="${ROOT}/results/${run_id}"
    if [ ! -d "${dir}" ]; then
      echo "WARN: ${dir} нет — создаю (обучение ещё не писало events)" >&2
      mkdir -p "${dir}"
    fi
    n_events="$(find "${dir}" -name 'events.out.tfevents*' 2>/dev/null | wc -l | tr -d ' ')"
    if [ "${n_events}" = "0" ]; then
      echo "WARN: ${dir} без events.out.tfevents.* (обучение ещё не писало?)" >&2
    else
      echo "[tensorboard] ${run_id}: ${n_events} event-файлов"
    fi
    if [ -z "${LOGDIR}" ]; then
      LOGDIR="${dir}"
    fi
    if [ -n "${LOGDIR_SPEC}" ]; then
      LOGDIR_SPEC="${LOGDIR_SPEC},${run_id}:${dir}"
    else
      LOGDIR_SPEC="${run_id}:${dir}"
    fi
  done
fi

TB_PID_FILE="/tmp/forest_tensorboard_${PORT}.pid"
TB_LOG="/tmp/forest_tensorboard_${PORT}.log"

stop_old() {
  if [ -f "${TB_PID_FILE}" ]; then
    old_pid="$(cat "${TB_PID_FILE}" 2>/dev/null || true)"
    if [ -n "${old_pid}" ] && kill -0 "${old_pid}" 2>/dev/null; then
      kill "${old_pid}" 2>/dev/null || true
      sleep 0.5
    fi
    rm -f "${TB_PID_FILE}"
  fi
  pkill -f "tensorboard.*--port[= ]?${PORT}" 2>/dev/null || true
  sleep 0.3
}

start_tb() {
  if [ "${USE_PARENT}" = "1" ] || [ "${#RUN_ARGS[@]}" -le 1 ]; then
    echo "[tensorboard] --logdir ${LOGDIR} port=${PORT}"
    if [ "${DAEMON}" -eq 1 ]; then
      nohup tensorboard --logdir "${LOGDIR}" --bind_all --port "${PORT}" --reload_interval 30 \
        >"${TB_LOG}" 2>&1 &
    else
      exec tensorboard --logdir "${LOGDIR}" --bind_all --port "${PORT}" --reload_interval 30
    fi
  else
    echo "[tensorboard] --logdir_spec ${LOGDIR_SPEC} port=${PORT}"
    if [ "${DAEMON}" -eq 1 ]; then
      nohup tensorboard --logdir_spec "${LOGDIR_SPEC}" --bind_all --port "${PORT}" --reload_interval 30 \
        >"${TB_LOG}" 2>&1 &
    else
      exec tensorboard --logdir_spec "${LOGDIR_SPEC}" --bind_all --port "${PORT}" --reload_interval 30
    fi
  fi
}

if [ "${DAEMON}" -eq 1 ]; then
  stop_old
  start_tb
  echo $! >"${TB_PID_FILE}"
  sleep 1.5
  if kill -0 "$(cat "${TB_PID_FILE}")" 2>/dev/null; then
    echo "[tensorboard] ok pid=$(cat "${TB_PID_FILE}")"
    echo "[tensorboard] http://127.0.0.1:${PORT}  |  --bind_all → с ПК по IP сервера или туннель"
  else
    echo "ERROR: tensorboard не стартовал, см. ${TB_LOG}" >&2
    tail -30 "${TB_LOG}" >&2 || true
    exit 1
  fi
  exit 0
fi

start_tb
