#!/usr/bin/env bash
# Освободить место на lab_comp.
# Запуск на сервере: bash train_scripts/lab_comp/cleanup_disk.bash

set -eu
set -o pipefail

echo "=== До ==="
df -h / | tail -1
docker system df 2>/dev/null || true

echo ""
echo "=== Docker (все остановленные контейнеры, образы, volumes) ==="
docker container prune -f 2>/dev/null || sudo docker container prune -f
docker image prune -a -f 2>/dev/null || sudo docker image prune -a -f
docker volume prune -f 2>/dev/null || sudo docker volume prune -f

echo ""
echo "=== pip / conda cache ==="
pip cache purge 2>/dev/null || true
if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda clean -a -y
fi

echo ""
echo "=== Крупные папки (только показать, не удалять) ==="
du -xh --max-depth=2 "${HOME}" 2>/dev/null | sort -hr | head -15

echo ""
echo "Можно вручную удалить (если не нужны):"
echo "  ~/.guild/runs     (~50G старые эксперименты guild)"
echo "  ~/.cache/pip      (pip cache)"
echo "  ~/.cache/vscode-cpptools"
echo ""
echo "=== После ==="
df -h / | tail -1
