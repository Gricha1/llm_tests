#!/usr/bin/env bash
# Dual stream Twitch + VK на lab_comp (Ubuntu 18.04 / OBS 21).
# Multi-RTMP plugin сюда НЕ ставится: OBS слишком старый + glibc 2.27.
#
# Идея: OBS шлёт 1 поток на локальный nginx-rtmp (Docker),
# nginx сам пушит на Twitch и VK.
#
# На сервере:
#   # 1) один раз заполни ключи:
#   nano ~/.config/obs-studio/dual_stream.env
#   # 2) запуск релея:
#   bash train_scripts/lab_comp/setup_obs_dual_stream.bash start
#
# В OBS (один раз):
#   Settings → Stream → Custom / Пользовательский
#   Server: rtmp://127.0.0.1/live
#   Stream Key: forest
#   Начать трансляцию — уйдёт и на Twitch, и на VK.
#
# С Windows/WSL:
#   wsl bash train_scripts/lab_comp/setup_obs_dual_stream.bash --remote start

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
CMD="${1:-help}"
REMOTE_MODE=0
if [ "${CMD}" = "--remote" ]; then
  REMOTE_MODE=1
  shift || true
  CMD="${1:-start}"
fi

KEY="${SSH_KEY:-${HOME}/.ssh/lab_comp_key}"
REMOTE="${REMOTE:-reedgern@192.168.194.7}"
REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"

CONTAINER_NAME="${DUAL_STREAM_CONTAINER:-obs_dual_rtmp}"
RTMP_PORT="${DUAL_STREAM_PORT:-1935}"
ENV_FILE="${HOME}/.config/obs-studio/dual_stream.env"
CONF_DIR="${HOME}/.config/obs-studio/dual_stream"
CONF_FILE="${CONF_DIR}/nginx.conf"

if [ "${REMOTE_MODE}" -eq 1 ]; then
  mkdir -p "${HOME}/.ssh"
  if [ ! -f "${KEY}" ] && [ -f "/mnt/c/Users/User/.ssh/id_ed25519" ]; then
    cp "/mnt/c/Users/User/.ssh/id_ed25519" "${KEY}"
    chmod 600 "${KEY}"
  fi
  rsync -avz -e "ssh -i ${KEY} -o StrictHostKeyChecking=no" \
    "${ROOT}/train_scripts/lab_comp/setup_obs_dual_stream.bash" \
    "${ROOT}/train_scripts/lab_comp/install_obs_multi_rtmp.bash" \
    "${REMOTE}:${REMOTE_DIR}/train_scripts/lab_comp/"
  exec ssh -i "${KEY}" -o StrictHostKeyChecking=no "${REMOTE}" \
    "cd ${REMOTE_DIR} && bash train_scripts/lab_comp/setup_obs_dual_stream.bash ${CMD}"
fi

usage() {
  cat <<'EOF'
Usage:
  bash train_scripts/lab_comp/setup_obs_dual_stream.bash init     # создать dual_stream.env
  bash train_scripts/lab_comp/setup_obs_dual_stream.bash start    # поднять docker-релей
  bash train_scripts/lab_comp/setup_obs_dual_stream.bash stop
  bash train_scripts/lab_comp/setup_obs_dual_stream.bash status
  bash train_scripts/lab_comp/setup_obs_dual_stream.bash cleanup-plugin  # убрать несовместимый multi-rtmp

dual_stream.env пример:
  TWITCH_STREAM_KEY=live_xxxx
  # полностью URL сервера VK (без ключа) и отдельно ключ:
  VK_RTMP_URL=rtmp://vkvd123.mycdn.me/video
  VK_STREAM_KEY=xxxx
EOF
}

write_env_template() {
  mkdir -p "$(dirname "${ENV_FILE}")"
  if [ -f "${ENV_FILE}" ]; then
    echo "[dual] already exists: ${ENV_FILE}"
    return 0
  fi
  umask 077
  cat > "${ENV_FILE}" <<'EOF'
# Заполни и сохрани. Не коммить.
TWITCH_STREAM_KEY=PASTE_TWITCH_KEY_HERE

# Из настроек эфира VK (Custom RTMP)
VK_RTMP_URL=rtmp://PASTE_VK_SERVER_HERE
VK_STREAM_KEY=PASTE_VK_KEY_HERE

# Локальный путь, на который смотрит OBS
OBS_LOCAL_APP=live
OBS_LOCAL_KEY=forest
EOF
  echo "[dual] created ${ENV_FILE} — заполни ключи"
}

cleanup_bad_plugin() {
  local p="${HOME}/.config/obs-studio/plugins/obs-multi-rtmp"
  if [ -d "${p}" ]; then
    rm -rf "${p}"
    echo "[dual] removed incompatible plugin ${p}"
  else
    echo "[dual] multi-rtmp plugin not present"
  fi
}

load_env() {
  if [ ! -f "${ENV_FILE}" ]; then
    write_env_template
    echo "ERROR: заполни ${ENV_FILE} и снова запусти start" >&2
    exit 1
  fi
  # shellcheck disable=SC1090
  set -a
  # shellcheck source=/dev/null
  source "${ENV_FILE}"
  set +a
  if [ -z "${TWITCH_STREAM_KEY:-}" ] || [[ "${TWITCH_STREAM_KEY}" == PASTE_* ]]; then
    echo "ERROR: TWITCH_STREAM_KEY не задан в ${ENV_FILE}" >&2
    exit 1
  fi
  if [ -z "${VK_RTMP_URL:-}" ] || [[ "${VK_RTMP_URL}" == rtmp://PASTE_* ]]; then
    echo "ERROR: VK_RTMP_URL не задан в ${ENV_FILE}" >&2
    exit 1
  fi
  if [ -z "${VK_STREAM_KEY:-}" ] || [[ "${VK_STREAM_KEY}" == PASTE_* ]]; then
    echo "ERROR: VK_STREAM_KEY не задан в ${ENV_FILE}" >&2
    exit 1
  fi
  OBS_LOCAL_APP="${OBS_LOCAL_APP:-live}"
  OBS_LOCAL_KEY="${OBS_LOCAL_KEY:-forest}"
}

# nginx-rtmp push target = server/app/key
vk_push_url() {
  local base="${VK_RTMP_URL%/}"
  local key="${VK_STREAM_KEY}"
  echo "${base}/${key}"
}

write_nginx_conf() {
  mkdir -p "${CONF_DIR}"
  local twitch_push="rtmp://live.twitch.tv/app/${TWITCH_STREAM_KEY}"
  local vk_push
  vk_push="$(vk_push_url)"
  cat > "${CONF_FILE}" <<EOF
worker_processes auto;
error_log /tmp/nginx_error.log warn;
pid /tmp/nginx.pid;
events { worker_connections 1024; }
rtmp {
  server {
    listen ${RTMP_PORT};
    chunk_size 4096;
    application ${OBS_LOCAL_APP} {
      live on;
      record off;
      # OBS → этот app, дальше раздача:
      push ${twitch_push};
      push ${vk_push};
    }
  }
}
http {
  access_log /tmp/nginx_access.log;
  server {
    listen 8080;
    location / {
      default_type text/plain;
      return 200 'obs dual rtmp relay ok\\n';
    }
  }
}
EOF
  echo "[dual] wrote ${CONF_FILE}"
}

start_relay() {
  load_env
  cleanup_bad_plugin
  write_nginx_conf
  if ! command -v docker >/dev/null 2>&1; then
    echo "ERROR: docker не найден" >&2
    exit 1
  fi
  docker rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true
  # alfg/nginx-rtmp: штатный entrypoint перезаписывает nginx.conf — запускаем nginx напрямую.
  docker run -d --restart unless-stopped \
    --name "${CONTAINER_NAME}" \
    --entrypoint nginx \
    -p "127.0.0.1:${RTMP_PORT}:1935" \
    -p "127.0.0.1:18080:8080" \
    -v "${CONF_FILE}:/etc/nginx/nginx.conf:ro" \
    alfg/nginx-rtmp:latest \
    -g 'daemon off;'
  sleep 2
  docker ps --filter "name=${CONTAINER_NAME}" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
  if ! docker ps --filter "name=${CONTAINER_NAME}" --filter "status=running" --format '{{.Names}}' | grep -q "${CONTAINER_NAME}"; then
    echo "[dual] ERROR: контейнер не жив, логи:" >&2
    docker logs --tail 40 "${CONTAINER_NAME}" >&2 || true
    exit 1
  fi
  cat <<EOF

[dual] релей запущен.

В OBS (перезапусти OBS если был открыт):
  Settings → Stream
    Service: Custom / Пользовательский
    Server:  rtmp://127.0.0.1/${OBS_LOCAL_APP}
    Stream Key: ${OBS_LOCAL_KEY}

Потом «Начать трансляцию» — поток уйдёт на Twitch + VK.

Проверка HTTP: curl -s http://127.0.0.1:18080/
Логи: docker logs -f ${CONTAINER_NAME}
EOF
}

stop_relay() {
  docker rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true
  echo "[dual] stopped ${CONTAINER_NAME}"
}

status_relay() {
  docker ps -a --filter "name=${CONTAINER_NAME}" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}' || true
  if [ -f "${ENV_FILE}" ]; then
    echo "[dual] env: ${ENV_FILE}"
  else
    echo "[dual] env missing — run: bash train_scripts/lab_comp/setup_obs_dual_stream.bash init"
  fi
}

case "${CMD}" in
  help|-h|--help) usage ;;
  init) write_env_template ;;
  cleanup-plugin) cleanup_bad_plugin ;;
  start) start_relay ;;
  stop) stop_relay ;;
  status) status_relay ;;
  restart) stop_relay; start_relay ;;
  *) usage; exit 1 ;;
esac
