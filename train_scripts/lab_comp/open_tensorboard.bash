#!/usr/bin/env bash
# Один файл с твоего ПК: поднимает TensorBoard на lab_comp + SSH-туннель.
#
#   RUN_ID=run_75 bash train_scripts/lab_comp/open_tensorboard.bash
#
# Держи терминал открытым → http://127.0.0.1:6006
# Ctrl+C закрывает только туннель (TB на сервере остаётся).

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

RUN_ID="${RUN_ID:-}"
PORT="${PORT:-6006}"
REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"

if [ -z "${RUN_ID}" ]; then
  echo "Пример: RUN_ID=run_75 bash train_scripts/lab_comp/open_tensorboard.bash" >&2
  exit 1
fi

# Не запускать на самом lab_comp.
case "${ROOT}" in
  */lab_work_space/forest_survival)
    echo "Этот скрипт — с твоего ПК. На сервере уже TB:" >&2
    echo "  RUN_ID=${RUN_ID} bash train_scripts/lab_comp/run_tensorboard.bash --daemon" >&2
    exit 1
    ;;
esac

SSH=(ssh.exe -o BatchMode=yes -o ConnectTimeout=15)
REMOTE="lab_comp"
if ! "${SSH[@]}" "${REMOTE}" true 2>/dev/null; then
  SSH=(ssh.exe -o ConnectTimeout=15)
  REMOTE="reedgern@192.168.194.7"
  if ! "${SSH[@]}" -o BatchMode=yes "${REMOTE}" true 2>/dev/null; then
    echo "ERROR: ssh к lab_comp не работает. Проверь: ssh.exe lab_comp echo ok" >&2
    exit 1
  fi
fi

echo "[open_tensorboard] RUN_ID=${RUN_ID} → ${REMOTE}"
"${SSH[@]}" "${REMOTE}" \
  "cd ${REMOTE_DIR} && RUN_ID=${RUN_ID} PORT=${PORT} bash train_scripts/lab_comp/run_tensorboard.bash --daemon"

echo ""
echo "[open_tensorboard] туннель открыт → http://127.0.0.1:${PORT}"
echo "[open_tensorboard] не закрывай этот терминал; Ctrl+C = стоп туннеля"
echo ""

# -N: только проброс порта, без шелла
exec "${SSH[@]}" -N -L "${PORT}:127.0.0.1:${PORT}" "${REMOTE}"
