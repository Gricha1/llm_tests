#!/usr/bin/env bash
# Запускать В СЕССИИ AnyDesk (не по SSH без DISPLAY).
# Открывает Unity Hub для установки Editor 6000.0.26f1.
set -eu

# AnyDesk / локальный GNOME обычно :0 или :1
if [ -z "${DISPLAY:-}" ]; then
  for d in :0 :1; do
    if xdpyinfo -display "${d}" >/dev/null 2>&1; then
      export DISPLAY="${d}"
      break
    fi
  done
fi

if [ -z "${DISPLAY:-}" ]; then
  echo "ERROR: нет DISPLAY. Запусти этот скрипт из терминала на рабочем столе AnyDesk."
  exit 1
fi

echo "DISPLAY=${DISPLAY}"
echo "Ubuntu: $(lsb_release -rs)"
echo ""
echo "Unity 6000 официально требует Ubuntu 22.04+."
echo "На 18.04 Hub может не дать установить Editor — тогда: bash train_scripts/lab_comp/upgrade_to_2204.bash"
echo ""

ulimit -n 4096
unityhub &

echo "В Hub: Installs → Install Editor → 6000.0.26f1 → Linux"
echo "Потом: Open → ~/lab_work_space/forest_survival"
