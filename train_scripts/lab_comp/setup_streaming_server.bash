#!/usr/bin/env bash
# Настройка lab_comp: OBS + Unity Hub + стрим через VNC.
# Запуск НА СЕРВЕРЕ: bash train_scripts/lab_comp/setup_streaming_server.bash
# Требует sudo (пароль root).

set -eu
set -o pipefail

UNITY_VERSION="${UNITY_VERSION:-6000.0.26f1}"
APPS_DIR="${HOME}/apps"
STREAM_DIR="${APPS_DIR}/stream"

echo "[lab_comp] === очистка кэшей (безопасно) ==="
pip cache purge 2>/dev/null || true
if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
  conda clean -a -y 2>/dev/null || true
fi

echo "[lab_comp] === Docker prune (если нужно место) ==="
if command -v docker >/dev/null 2>&1; then
  docker container prune -f 2>/dev/null || sudo docker container prune -f
  docker image prune -a -f 2>/dev/null || sudo docker image prune -a -f
  docker volume prune -f 2>/dev/null || sudo docker volume prune -f
fi

echo "[lab_comp] === OBS ==="
if ! command -v obs >/dev/null 2>&1; then
  sudo apt-get install -y obs-studio ffmpeg || {
    echo "WARN: apt install obs failed — отключи битые PPA в /etc/apt/sources.list.d/"
    sudo apt-get install -y obs-studio ffmpeg || true
  }
fi
obs --version 2>/dev/null | head -1 || echo "obs installed"

echo "[lab_comp] === Unity Hub (deb) ==="
if ! command -v unityhub >/dev/null 2>&1; then
  sudo wget -qO /etc/apt/trusted.gpg.d/unityhub.gpg \
    https://hub.unity3d.com/linux/repos/deb/public-key.gpg
  echo "deb [signed-by=/etc/apt/trusted.gpg.d/unityhub.gpg] https://hub.unity3d.com/linux/repos/deb stable main" \
    | sudo tee /etc/apt/sources.list.d/unityhub.list >/dev/null
  sudo apt-get update
  sudo apt-get install -y unity-hub
fi
unityhub --version 2>/dev/null || true

echo "[lab_comp] === Unity Editor ${UNITY_VERSION} ==="
echo "Unity 6000 официально требует Ubuntu 22.04+."
echo "На Ubuntu 18.04 установка может не пройти — нужен upgrade (см. upgrade_to_2204.bash)."

if command -v unityhub >/dev/null 2>&1; then
  unityhub -- --headless install --version "${UNITY_VERSION}" --module linux-il2cpp || {
    echo "WARN: headless install не удался — установи Editor через Unity Hub GUI на VNC."
  }
fi

mkdir -p "${STREAM_DIR}"

cat > "${STREAM_DIR}/start_vnc.sh" <<'EOF'
#!/usr/bin/env bash
# VNC desktop :1 для OBS (захват экрана).
set -eu
export DISPLAY="${DISPLAY:-:1}"
if pgrep -f "Xvnc.*:1" >/dev/null 2>&1; then
  echo "VNC :1 уже запущен"
else
  vncserver :1 -geometry 1920x1080 -depth 24
fi
# Разрешить OBS захватывать экран без пароля (локальная сеть)
x11vnc -display :1 -auth guess -forever -nopw -shared -rfbport 5900 -bg -o /tmp/x11vnc.log 2>/dev/null || true
echo "VNC: подключайся к $(hostname -I | awk '{print $1}'):5900"
echo "DISPLAY=${DISPLAY}"
EOF

cat > "${STREAM_DIR}/start_obs.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
export DISPLAY="${DISPLAY:-:1}"
echo "OBS на DISPLAY=${DISPLAY}"
obs &
EOF

cat > "${STREAM_DIR}/start_unity.sh" <<EOF
#!/usr/bin/env bash
set -eu
export DISPLAY="\${DISPLAY:-:1}"
ulimit -n 4096
PROJECT="\${1:-${HOME}/lab_work_space/forest_survival}"
if command -v unityhub >/dev/null 2>&1; then
  unityhub -- --projectPath "\${PROJECT}" &
else
  echo "Unity Hub не найден"
  exit 1
fi
EOF

chmod +x "${STREAM_DIR}"/*.sh

echo ""
echo "[lab_comp] === готово ==="
echo "Свободно на диске: $(df -h / | tail -1 | awk '{print $4}')"
echo ""
echo "Стрим:"
echo "  1) bash ${STREAM_DIR}/start_vnc.sh"
echo "  2) bash ${STREAM_DIR}/start_unity.sh ~/lab_work_space/forest_survival"
echo "  3) bash ${STREAM_DIR}/start_obs.sh  → Window Capture / Display Capture"
echo ""
echo "Unity 6000 на Ubuntu 18.04 может не работать."
echo "Если не ставится: bash train_scripts/lab_comp/upgrade_to_2204.bash (долго, перезагрузка)."
