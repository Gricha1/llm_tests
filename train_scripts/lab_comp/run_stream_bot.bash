#!/usr/bin/env bash
# На lab_comp: поставить зависимости и запустить Twitch stream bot.
#
# Первый раз:
#   bash train_scripts/lab_comp/run_stream_bot.bash --setup
#
# Обычный запуск:
#   bash train_scripts/lab_comp/run_stream_bot.bash
#
# Фон:
#   bash train_scripts/lab_comp/run_stream_bot.bash --daemon
#
# Выключить LLM:
#   USE_LOCAL_LLM=false bash train_scripts/lab_comp/run_stream_bot.bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BOT_DIR="${ROOT}/stream_bot"
ENV_FILE="${BOT_DIR}/.env"
LOG="${ROOT}/results/stream_bot.log"
PID_FILE="${ROOT}/results/stream_bot.pid"

mkdir -p "${ROOT}/results"

if [ ! -d "${BOT_DIR}" ]; then
  echo "ERROR: нет ${BOT_DIR} — сначала с ПК: wsl bash train_scripts/lab_comp/sync.bash" >&2
  exit 1
fi

DO_SETUP=0
DAEMON=0
for arg in "$@"; do
  case "$arg" in
    --setup) DO_SETUP=1 ;;
    --daemon|-d) DAEMON=1 ;;
    --help|-h)
      echo "Usage: bash train_scripts/lab_comp/run_stream_bot.bash [--setup] [--daemon]"
      exit 0
      ;;
  esac
done

# .env: один раз создать из примера
if [ ! -f "${ENV_FILE}" ]; then
  if [ -f "${BOT_DIR}/.env.example" ]; then
    cp "${BOT_DIR}/.env.example" "${ENV_FILE}"
    echo "[stream_bot] создан ${ENV_FILE} — ЗАПОЛНИ TWITCH_CHANNEL (и OAuth если бот пишет в чат)"
  else
    echo "ERROR: нет .env.example" >&2
    exit 1
  fi
fi

# conda/python
PY=""
if [ -x "${HOME}/anaconda3/envs/mlagents/bin/python" ]; then
  PY="${HOME}/anaconda3/envs/mlagents/bin/python"
elif command -v python3 >/dev/null 2>&1; then
  PY="$(command -v python3)"
else
  echo "ERROR: python3 не найден" >&2
  exit 1
fi

if [ "${DO_SETUP}" = "1" ]; then
  echo "[stream_bot] pip install..."
  "${PY}" -m pip install -q -r "${BOT_DIR}/requirements.txt"
  if command -v ollama >/dev/null 2>&1; then
    MODEL="$(grep -E '^OLLAMA_MODEL=' "${ENV_FILE}" 2>/dev/null | cut -d= -f2- | tr -d '\r' || true)"
    MODEL="${MODEL:-qwen3:4b}"
    echo "[stream_bot] ollama pull ${MODEL} (может занять время)..."
    ollama pull "${MODEL}" || echo "[stream_bot] WARN: ollama pull не удался — поставь модель вручную или USE_LOCAL_LLM=false"
  else
    echo "[stream_bot] Ollama не найдена. Либо установи, либо в .env: USE_LOCAL_LLM=false"
  fi
  echo "[stream_bot] setup OK. Отредактируй ${ENV_FILE} и запусти без --setup"
  exit 0
fi

# минимальная проверка канала
CHANNEL="$(grep -E '^TWITCH_CHANNEL=' "${ENV_FILE}" 2>/dev/null | cut -d= -f2- | tr -d '\r' | tr -d ' ' || true)"
if [ -z "${CHANNEL}" ]; then
  echo "ERROR: в ${ENV_FILE} пустой TWITCH_CHANNEL — укажи канал без #" >&2
  echo "  nano ${ENV_FILE}" >&2
  exit 1
fi

export PYTHONPATH="${ROOT}${PYTHONPATH:+:${PYTHONPATH}}"
cd "${ROOT}"

run_bot() {
  # shellcheck disable=SC1090
  set -a
  # подхватить .env в окружение (не печатаем OAuth)
  # shellcheck source=/dev/null
  source <(grep -v '^#' "${ENV_FILE}" | grep -E '^[A-Za-z_][A-Za-z0-9_]*=' | sed 's/\r$//')
  set +a
  exec "${PY}" -m stream_bot.main
}

if [ "${DAEMON}" = "1" ]; then
  if [ -f "${PID_FILE}" ] && kill -0 "$(cat "${PID_FILE}")" 2>/dev/null; then
    echo "[stream_bot] уже запущен pid=$(cat "${PID_FILE}")"
    exit 0
  fi
  echo "[stream_bot] daemon → ${LOG}"
  (
    set -a
    # shellcheck source=/dev/null
    source <(grep -v '^#' "${ENV_FILE}" | grep -E '^[A-Za-z_][A-Za-z0-9_]*=' | sed 's/\r$//')
    set +a
    export PYTHONPATH="${ROOT}${PYTHONPATH:+:${PYTHONPATH}}"
    cd "${ROOT}"
    exec "${PY}" -m stream_bot.main
  ) >>"${LOG}" 2>&1 &
  echo $! >"${PID_FILE}"
  echo "[stream_bot] pid=$(cat "${PID_FILE}")  log=${LOG}"
  echo "  stop: kill \$(cat ${PID_FILE})"
  exit 0
fi

echo "[stream_bot] foreground | channel=${CHANNEL} | python=${PY}"
echo "[stream_bot] Ctrl+C = стоп"
run_bot
