#!/usr/bin/env bash
# Управление stream_bot НА lab_comp (вызывается из Train Lab UI по SSH).
#
#   bash train_scripts/lab_comp/llm_bot_lab.bash setup
#   bash train_scripts/lab_comp/llm_bot_lab.bash start
#   bash train_scripts/lab_comp/llm_bot_lab.bash stop
#   bash train_scripts/lab_comp/llm_bot_lab.bash health
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BOT_DIR="${ROOT}/stream_bot"
ENV_FILE="${BOT_DIR}/.env"
LOG="${ROOT}/results/stream_bot.log"
PID_FILE="${ROOT}/results/stream_bot.pid"
MODEL="${OLLAMA_MODEL:-}"
# если не задан — возьмём из .env или первую доступную модель
if [ -z "${MODEL}" ] && [ -f "${ENV_FILE}" ]; then
  MODEL="$(grep -E '^OLLAMA_MODEL=' "${ENV_FILE}" 2>/dev/null | cut -d= -f2- | tr -d '\r' | tr -d ' ' || true)"
fi
MODEL="${MODEL:-qwen3:4b}"
CMD="${1:-}"

mkdir -p "${ROOT}/results"

pick_python() {
  if [ -x "${HOME}/anaconda3/envs/mlagents/bin/python" ]; then
    echo "${HOME}/anaconda3/envs/mlagents/bin/python"
  elif command -v python3 >/dev/null 2>&1; then
    command -v python3
  else
    echo "ERROR: python3 не найден на lab" >&2
    exit 1
  fi
}

ensure_env() {
  if [ -f "${ENV_FILE}" ]; then
    echo "[lab_bot] .env ok"
    return
  fi
  if [ -f "${BOT_DIR}/.env.example" ]; then
    cp "${BOT_DIR}/.env.example" "${ENV_FILE}"
  else
    cat >"${ENV_FILE}" <<EOF
TWITCH_BOT_NICK=
TWITCH_OAUTH=
TWITCH_CHANNEL=
UNITY_HOST=127.0.0.1
UNITY_PORT=5055
USE_LOCAL_LLM=true
OLLAMA_MODEL=${MODEL}
BOT_HTTP_HOST=127.0.0.1
BOT_HTTP_PORT=8765
LISTEN_STREAM_ON_START=false
EOF
  fi
  # local debug по умолчанию; Twitch не обязателен
  if ! grep -q '^LISTEN_STREAM_ON_START=' "${ENV_FILE}"; then
    echo "LISTEN_STREAM_ON_START=false" >>"${ENV_FILE}"
  fi
  if ! grep -q '^BOT_HTTP_HOST=' "${ENV_FILE}"; then
    echo "BOT_HTTP_HOST=127.0.0.1" >>"${ENV_FILE}"
  fi
  if ! grep -q '^UNITY_HOST=' "${ENV_FILE}"; then
    echo "UNITY_HOST=127.0.0.1" >>"${ENV_FILE}"
  fi
  echo "[lab_bot] created ${ENV_FILE}"
}

ensure_ollama() {
  export PATH="${HOME}/bin:/usr/local/bin:${PATH}"
  # 1) HTTP API (docker forest_ollama уже крутится на lab)
  if curl -s -m 3 http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
    echo "[lab_bot] ollama API http://127.0.0.1:11434 ok"
    return 0
  fi
  # 2) поднять существующий контейнер
  if command -v docker >/dev/null 2>&1; then
    if docker ps -a --format '{{.Names}}' | grep -qx 'forest_ollama'; then
      echo "[lab_bot] docker start forest_ollama…"
      docker start forest_ollama >/dev/null || true
      sleep 2
      if curl -s -m 3 http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
        echo "[lab_bot] ollama API ok after docker start"
        return 0
      fi
    fi
    # 3) создать контейнер с GPU
    echo "[lab_bot] docker run ollama/ollama (forest_ollama)…"
    docker rm -f forest_ollama >/dev/null 2>&1 || true
    docker run -d --gpus all --name forest_ollama \
      -v ollama:/root/.ollama \
      -p 11434:11434 \
      ollama/ollama
    sleep 4
    if curl -s -m 5 http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
      echo "[lab_bot] ollama API ok (new container)"
      return 0
    fi
  fi
  # 4) native install (на Ubuntu 18.04 часто ломается из‑за glibc)
  if ! command -v ollama >/dev/null 2>&1; then
    echo "[lab_bot] trying native ollama install (may fail on old glibc)…"
    curl -fsSL https://ollama.com/install.sh -o /tmp/ollama_install.sh || true
    sh /tmp/ollama_install.sh 2>/dev/null || sudo -n sh /tmp/ollama_install.sh 2>/dev/null || true
  fi
  if command -v ollama >/dev/null 2>&1 && ollama --version >/dev/null 2>&1; then
    if ! curl -s -m 2 http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
      nohup ollama serve >/tmp/ollama_serve.log 2>&1 &
      sleep 2
    fi
    if curl -s -m 3 http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
      return 0
    fi
  fi
  echo "[lab_bot] ollama=missing"
  echo "[lab_bot] ERROR: нет Ollama API на :11434. На lab уже должен быть docker forest_ollama."
  return 1
}

ollama_cli() {
  # предпочитаем docker exec — на lab native ollama ломается на glibc 2.27
  if command -v docker >/dev/null 2>&1 && docker ps --format '{{.Names}}' | grep -qx 'forest_ollama'; then
    docker exec forest_ollama ollama "$@"
    return $?
  fi
  if command -v ollama >/dev/null 2>&1; then
    ollama "$@"
    return $?
  fi
  return 127
}

model_ready() {
  local m="$1"
  local tags
  tags="$(curl -s -m 5 http://127.0.0.1:11434/api/tags 2>/dev/null || true)"
  if echo "$tags" | grep -q "\"name\":\"${m}\""; then
    return 0
  fi
  # частичное совпадение (qwen3:4b vs name field)
  if echo "$tags" | grep -Fq "\"${m}\""; then
    return 0
  fi
  return 1
}

cmd_setup() {
  # чистый лог UI, без старых twitch-хвостов
  : >"${LOG}"
  ensure_env
  # гарантируем base url для docker ollama
  if ! grep -q '^OLLAMA_BASE_URL=' "${ENV_FILE}"; then
    echo "OLLAMA_BASE_URL=http://127.0.0.1:11434" >>"${ENV_FILE}"
  fi
  PY="$(pick_python)"
  echo "[lab_bot] python=${PY}"
  echo "[lab_bot] pip install…"
  "${PY}" -m pip install -q -r "${BOT_DIR}/requirements.txt"
  echo "[lab_bot] python_deps=ready"

  if ! ensure_ollama; then
    exit 2
  fi
  echo "[lab_bot] ollama=running"
  if model_ready "${MODEL}"; then
    echo "[lab_bot] model=ready ${MODEL}"
  elif [ "${MODEL}" != "qwen2.5:3b" ] && model_ready "qwen2.5:3b"; then
    echo "[lab_bot] WARN: ${MODEL} нет, используем qwen2.5:3b (уже на lab)"
    MODEL="qwen2.5:3b"
    # обновим .env чтобы stream_bot взял ту же модель
    if grep -q '^OLLAMA_MODEL=' "${ENV_FILE}"; then
      sed -i "s/^OLLAMA_MODEL=.*/OLLAMA_MODEL=${MODEL}/" "${ENV_FILE}"
    else
      echo "OLLAMA_MODEL=${MODEL}" >>"${ENV_FILE}"
    fi
    echo "[lab_bot] model=ready ${MODEL}"
  else
    echo "[lab_bot] model=pulling ${MODEL}"
    ollama_cli pull "${MODEL}"
    echo "[lab_bot] model=ready ${MODEL}"
  fi
  echo "[lab_bot] setup_ok"
}

is_running() {
  if [ -f "${PID_FILE}" ]; then
    local pid
    pid="$(tr -d ' \r\n' <"${PID_FILE}" || true)"
    if [ -n "${pid}" ] && kill -0 "${pid}" 2>/dev/null; then
      return 0
    fi
  fi
  return 1
}

cmd_stop() {
  echo "[lab_bot] stop…"
  curl -s -m 5 -X POST "http://127.0.0.1:8765/shutdown" -H "Content-Type: application/json" -d '{}' >/dev/null 2>&1 || true
  sleep 1
  if [ -f "${PID_FILE}" ]; then
    pid="$(tr -d ' \r\n' <"${PID_FILE}" || true)"
    if [ -n "${pid}" ]; then
      kill -TERM "${pid}" 2>/dev/null || true
      sleep 1
      kill -KILL "${pid}" 2>/dev/null || true
    fi
    rm -f "${PID_FILE}"
  fi
  pkill -f 'python.*-m stream_bot.main' 2>/dev/null || true
  echo "[lab_bot] stopped"
}

cmd_start() {
  ensure_env
  cmd_setup
  cmd_stop || true
  PY="$(pick_python)"
  export PYTHONPATH="${ROOT}${PYTHONPATH:+:${PYTHONPATH}}"
  export LISTEN_STREAM_ON_START=false
  export BOT_HTTP_HOST=127.0.0.1
  export BOT_HTTP_PORT=8765
  export UNITY_HOST=127.0.0.1
  export OLLAMA_MODEL="${MODEL}"
  # подхватить .env без печати секретов
  set -a
  # shellcheck disable=SC1090
  source <(grep -v '^#' "${ENV_FILE}" | grep -E '^[A-Za-z_][A-Za-z0-9_]*=' | sed 's/\r$//' || true)
  set +a
  export LISTEN_STREAM_ON_START=false
  export BOT_HTTP_HOST=127.0.0.1
  export BOT_HTTP_PORT=8765
  cd "${ROOT}"
  : >"${LOG}"
  echo "[lab_bot] spawn ${PY} -m stream_bot.main"
  nohup "${PY}" -m stream_bot.main >>"${LOG}" 2>&1 &
  echo $! >"${PID_FILE}"
  echo "[lab_bot] pid=$(cat "${PID_FILE}") log=${LOG}"
  # wait health
  for i in $(seq 1 40); do
    if curl -s -m 2 "http://127.0.0.1:8765/health" | grep -q '"ok"'; then
      # force local debug
      curl -s -m 5 -X POST "http://127.0.0.1:8765/mode" \
        -H "Content-Type: application/json" \
        -d '{"listen_stream":false}' >/dev/null || true
      echo "[lab_bot] health_ok"
      exit 0
    fi
    sleep 0.5
  done
  echo "[lab_bot] ERROR: нет /health за 20с"
  tail -n 40 "${LOG}" || true
  exit 1
}

cmd_health() {
  if curl -s -m 2 "http://127.0.0.1:8765/health" | grep -q '"ok"'; then
    echo "ok"
    exit 0
  fi
  echo "down"
  exit 1
}

case "${CMD}" in
  setup) cmd_setup ;;
  start) cmd_start ;;
  stop) cmd_stop ;;
  health) cmd_health ;;
  *)
    echo "Usage: $0 setup|start|stop|health" >&2
    exit 1
    ;;
esac
