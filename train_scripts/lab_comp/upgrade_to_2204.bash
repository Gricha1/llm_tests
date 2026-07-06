#!/usr/bin/env bash
# Ubuntu 18.04 → 20.04 → 22.04 (нужно для Unity 6000).
# ВНИМАНИЕ: долго, нужен reboot, SSH может оборваться.
# Запускать в tmux/screen на сервере:
#   tmux new -s upgrade
#   bash train_scripts/lab_comp/upgrade_to_2204.bash

set -eu
set -o pipefail

echo "=== Ubuntu upgrade для Unity 6000 ==="
echo "Текущая версия:"
lsb_release -a

echo ""
echo "Шаг 1: 18.04 → 20.04"
sudo do-release-upgrade -f DistUpgradeViewNonInteractive

echo ""
echo "ПЕРЕЗАГРУЗКА. После reboot запусти снова этот скрипт."
echo "Шаг 2 будет: 20.04 → 22.04"

if grep -q "20.04" /etc/os-release 2>/dev/null; then
  echo ""
  echo "Шаг 2: 20.04 → 22.04"
  sudo sed -i 's/Prompt=lts/Prompt=normal/' /etc/update-manager/release-upgrades 2>/dev/null || true
  sudo do-release-upgrade -f DistUpgradeViewNonInteractive
  echo "Готово. Перезагрузи: sudo reboot"
fi
