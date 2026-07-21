#!/usr/bin/env bash
# С локального ПК: TensorBoard на lab_comp + SSH-туннель → http://localhost:6006
#
#   # на своём ПК (WSL / PowerShell), НЕ на server1:
#   RUN_ID=run_75 bash train_scripts/lab_comp/tensorboard_tunnel.bash
#
# На lab_comp только поднять TB (без туннеля):
#   RUN_ID=run_75 bash train_scripts/lab_comp/run_tensorboard.bash --daemon
# Потом с ПК: ssh -L 6006:127.0.0.1:6006 reedgern@192.168.194.7

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

PORT="${PORT:-6006}"
LOCAL_PORT="${LOCAL_PORT:-${PORT}}"
REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"
RUN_ID="${RUN_ID:-}"

if [ -z "${RUN_ID}" ]; then
  echo "Укажи RUN_ID, например: RUN_ID=run_75 $0" >&2
  exit 1
fi

# Уже на lab_comp — туннель к себе не нужен (порт 6006 занят самим TensorBoard).
on_lab_comp=0
if [ -d "${ROOT}/results" ] && hostname 2>/dev/null | grep -qiE 'server1|lab'; then
  on_lab_comp=1
fi
if [ -f "${ROOT}/.lab_comp_env" ] && [ "$(hostname -I 2>/dev/null | tr ' ' '\n' | grep -c '192.168.194.7' || true)" -ge 1 ]; then
  on_lab_comp=1
fi
# Каталог ~/lab_work_space на этой машине = мы на сервере обучения.
case "${ROOT}" in
  */lab_work_space/forest_survival) on_lab_comp=1 ;;
esac

if [ "${on_lab_comp}" -eq 1 ]; then
  echo "[tensorboard_tunnel] ты уже на lab_comp — туннель не нужен"
  RUN_ID="${RUN_ID}" PORT="${PORT}" bash "${ROOT}/train_scripts/lab_comp/run_tensorboard.bash" --daemon
  echo ""
  echo "С своего ПК открой туннель:"
  echo "  ssh -L ${LOCAL_PORT}:127.0.0.1:${PORT} reedgern@192.168.194.7"
  echo "или в браузере (если порт открыт в сети): http://192.168.194.7:${PORT}"
  echo "локально после туннеля: http://127.0.0.1:${LOCAL_PORT}"
  exit 0
fi

# shellcheck source=rsync_ssh.bash
source "${ROOT}/train_scripts/lab_comp/rsync_ssh.bash"
lab_comp_init_ssh

REMOTE_CMD="cd ${REMOTE_DIR} && RUN_ID=${RUN_ID} PORT=${PORT} bash train_scripts/lab_comp/run_tensorboard.bash --daemon"
echo "[tensorboard_tunnel] старт на lab_comp: ${RUN_ID}"
"${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" "${REMOTE_CMD}"

LOCAL_URL="http://127.0.0.1:${LOCAL_PORT}"
echo ""
echo "[tensorboard_tunnel] туннель → ${LOCAL_URL}"
echo "[tensorboard_tunnel] Ctrl+C — закрыть туннель (TensorBoard на сервере останется)"
echo ""

if [ "${LAB_COMP_RSYNC_SSH}" = "ssh.exe" ]; then
  exec ssh.exe -N -L "${LOCAL_PORT}:127.0.0.1:${PORT}" "${LAB_COMP_RSYNC_REMOTE}"
fi

exec ssh -N -L "${LOCAL_PORT}:127.0.0.1:${PORT}" "${LAB_COMP_RSYNC_REMOTE}"
