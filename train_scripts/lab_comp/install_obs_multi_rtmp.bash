#!/usr/bin/env bash
# Установка obs-multi-rtmp на lab_comp (Ubuntu + OBS Studio).
#
# На сервере:
#   bash train_scripts/lab_comp/install_obs_multi_rtmp.bash
#
# С Windows/WSL:
#   wsl bash train_scripts/lab_comp/install_obs_multi_rtmp.bash --remote
#
# После установки перезапусти OBS. В доке «Multiple RTMP outputs» добавь VK:
#   server = rtmp://... из эфира VK
#   key    = ключ потока VK
#
# Опционально сразу пропиши VK (если уже есть URL+ключ):
#   VK_RTMP_URL='rtmp://...' VK_STREAM_KEY='xxxx' bash train_scripts/lab_comp/install_obs_multi_rtmp.bash

set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
REMOTE_MODE=0
for arg in "$@"; do
  case "${arg}" in
    --remote) REMOTE_MODE=1 ;;
  esac
done

KEY="${SSH_KEY:-${HOME}/.ssh/lab_comp_key}"
REMOTE="${REMOTE:-reedgern@192.168.194.7}"
REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"

if [ "${REMOTE_MODE}" -eq 1 ]; then
  mkdir -p "${HOME}/.ssh"
  if [ ! -f "${KEY}" ] && [ -f "/mnt/c/Users/User/.ssh/id_ed25519" ]; then
    cp "/mnt/c/Users/User/.ssh/id_ed25519" "${KEY}"
    chmod 600 "${KEY}"
  fi
  echo "[multi-rtmp] sync script -> ${REMOTE}:${REMOTE_DIR}"
  rsync -avz -e "ssh -i ${KEY} -o StrictHostKeyChecking=no" \
    "${ROOT}/train_scripts/lab_comp/install_obs_multi_rtmp.bash" \
    "${REMOTE}:${REMOTE_DIR}/train_scripts/lab_comp/"
  exec ssh -i "${KEY}" -o StrictHostKeyChecking=no "${REMOTE}" \
    "cd ${REMOTE_DIR} && VK_RTMP_URL='${VK_RTMP_URL:-}' VK_STREAM_KEY='${VK_STREAM_KEY:-}' bash train_scripts/lab_comp/install_obs_multi_rtmp.bash"
fi

detect_obs_version() {
  local ver=""
  if command -v obs >/dev/null 2>&1; then
    ver="$(obs --version 2>/dev/null | grep -oE '[0-9]+\.[0-9]+(\.[0-9]+)?' | head -1 || true)"
  fi
  if [ -z "${ver}" ] && command -v flatpak >/dev/null 2>&1; then
    if flatpak list 2>/dev/null | grep -qi 'com.obsproject.Studio'; then
      echo "ERROR: OBS через Flatpak. У плагина нет flatpak-сборки — поставь OBS из PPA/deb." >&2
      exit 1
    fi
  fi
  if [ -z "${ver}" ]; then
    # common: binary exists but --version needs display
    if [ -x /usr/bin/obs ]; then
      ver="$(/usr/bin/obs --version 2>/dev/null | grep -oE '[0-9]+\.[0-9]+(\.[0-9]+)?' | head -1 || true)"
    fi
  fi
  if [ -z "${ver}" ]; then
    ver="$(dpkg-query -W -f='${Version}\n' obs-studio 2>/dev/null | grep -oE '^[0-9]+\.[0-9]+' | head -1 || true)"
  fi
  echo "${ver}"
}

pick_asset_for_obs() {
  # stdout: TAG|ASSET_NAME|URL_HINT (asset from GitHub release API)
  local obs_major="$1"
  python3 - <<'PY' "${obs_major}"
import json, sys, urllib.request

obs_major = int(sys.argv[1])
url = "https://api.github.com/repos/sorayuki/obs-multi-rtmp/releases?per_page=30"
req = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json", "User-Agent": "forest-survival"})
with urllib.request.urlopen(req, timeout=30) as r:
    releases = json.load(r)

def score(rel):
    name = (rel.get("name") or "") + " " + (rel.get("tag_name") or "")
    s = 0
    if f"OBS {obs_major}" in name or f"for OBS {obs_major}" in name or f"OBS{obs_major}" in name.replace(" ", ""):
        s += 100
    if str(obs_major) in name:
        s += 10
    if rel.get("prerelease"):
        s -= 5
    return s

# Prefer version-matched releases, then newest
ranked = sorted(releases, key=lambda r: (score(r), r.get("published_at") or ""), reverse=True)

for rel in ranked:
    assets = rel.get("assets") or []
    # Prefer tar.xz (portable into ~/.config/obs-studio/plugins) then deb
    tar = None
    deb = None
    for a in assets:
        n = a.get("name") or ""
        if "linux" not in n.lower() and "x86_64" not in n:
            continue
        if n.endswith(".tar.xz") and "dbg" not in n:
            tar = a
        elif n.endswith(".deb") and "dbg" not in n and "dbgsym" not in n:
            deb = a
    chosen = tar or deb
    if not chosen:
        continue
    print(f"{rel['tag_name']}|{chosen['name']}|{chosen['browser_download_url']}")
    sys.exit(0)
print("", end="")
sys.exit(2)
PY
}

PLUGIN_DIR="${HOME}/.config/obs-studio/plugins"
mkdir -p "${PLUGIN_DIR}" "${HOME}/.cache/obs-multi-rtmp"

OBS_VER="$(detect_obs_version || true)"
DPKG_VER="$(dpkg-query -W -f='${Version}\n' obs-studio 2>/dev/null | head -1 || true)"
if [ -z "${OBS_VER}" ] && [ -z "${DPKG_VER}" ]; then
  echo "ERROR: OBS Studio не найден. Установи: sudo apt install obs-studio (или PPA obsproject)." >&2
  exit 1
fi
# Ubuntu 18.04 пакет часто врёт "0.0.1", реальная версия — из dpkg (21.0.2...)
if [[ "${OBS_VER}" == 0.* ]] && [ -n "${DPKG_VER}" ]; then
  OBS_VER="$(echo "${DPKG_VER}" | grep -oE '^[0-9]+\.[0-9]+(\.[0-9]+)?' | head -1)"
fi
OBS_MAJOR="$(echo "${OBS_VER}" | cut -d. -f1)"
echo "[multi-rtmp] OBS version: ${OBS_VER} (major=${OBS_MAJOR})"

if [ "${OBS_MAJOR}" -lt 28 ] 2>/dev/null; then
  echo "[multi-rtmp] OBS ${OBS_VER} слишком старый для sorayuki/obs-multi-rtmp."
  echo "[multi-rtmp] На lab_comp используй RTMP-релей:"
  echo "  bash train_scripts/lab_comp/setup_obs_dual_stream.bash init"
  echo "  # заполни ~/.config/obs-studio/dual_stream.env"
  echo "  bash train_scripts/lab_comp/setup_obs_dual_stream.bash start"
  # убрать, если предыдущая попытка положила несовместимый .so
  rm -rf "${HOME}/.config/obs-studio/plugins/obs-multi-rtmp" 2>/dev/null || true
  exit 0
fi

PICK="$(pick_asset_for_obs "${OBS_MAJOR}" || true)"
if [ -z "${PICK}" ]; then
  echo "ERROR: не нашёл linux-сборку multi-rtmp для OBS ${OBS_MAJOR} на GitHub." >&2
  echo "Смотри: https://github.com/sorayuki/obs-multi-rtmp/releases" >&2
  exit 1
fi

TAG="${PICK%%|*}"
REST="${PICK#*|}"
ASSET="${REST%%|*}"
URL="${REST#*|}"
echo "[multi-rtmp] release ${TAG} asset ${ASSET}"

CACHE="${HOME}/.cache/obs-multi-rtmp/${ASSET}"
if [ ! -f "${CACHE}" ]; then
  echo "[multi-rtmp] download..."
  curl -fsSL -L -o "${CACHE}" "${URL}"
fi

install_from_tar() {
  local archive="$1"
  local tmp
  tmp="$(mktemp -d)"
  tar -xJf "${archive}" -C "${tmp}"
  # archive often contains obs-multi-rtmp/bin|data
  if [ -d "${tmp}/obs-multi-rtmp" ]; then
    rm -rf "${PLUGIN_DIR}/obs-multi-rtmp"
    mv "${tmp}/obs-multi-rtmp" "${PLUGIN_DIR}/"
  else
    # nested differently
    local found
    found="$(find "${tmp}" -maxdepth 2 -type d -name 'obs-multi-rtmp' | head -1 || true)"
    if [ -n "${found}" ]; then
      rm -rf "${PLUGIN_DIR}/obs-multi-rtmp"
      mv "${found}" "${PLUGIN_DIR}/"
    else
      echo "ERROR: в архиве нет папки obs-multi-rtmp" >&2
      ls -la "${tmp}" >&2
      rm -rf "${tmp}"
      exit 1
    fi
  fi
  rm -rf "${tmp}"
}

if [[ "${ASSET}" == *.tar.xz ]]; then
  install_from_tar "${CACHE}"
elif [[ "${ASSET}" == *.deb ]]; then
  if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
    sudo dpkg -i "${CACHE}" || sudo apt-get -f install -y
  else
    echo "[multi-rtmp] deb нужен sudo. Пробую распаковать вручную в ~/.config/obs-studio/plugins..."
    tmp_deb="$(mktemp -d)"
    dpkg-deb -x "${CACHE}" "${tmp_deb}"
    # typical paths: usr/lib/x86_64-linux-gnu/obs-plugins/obs-multi-rtmp.so + data
    mkdir -p "${PLUGIN_DIR}/obs-multi-rtmp/bin/64bit" "${PLUGIN_DIR}/obs-multi-rtmp/data"
    find "${tmp_deb}" -name 'obs-multi-rtmp.so' -exec cp {} "${PLUGIN_DIR}/obs-multi-rtmp/bin/64bit/" \;
    if [ -d "${tmp_deb}/usr/share/obs/obs-plugins/obs-multi-rtmp" ]; then
      cp -a "${tmp_deb}/usr/share/obs/obs-plugins/obs-multi-rtmp/." "${PLUGIN_DIR}/obs-multi-rtmp/data/"
    fi
    rm -rf "${tmp_deb}"
    if [ ! -f "${PLUGIN_DIR}/obs-multi-rtmp/bin/64bit/obs-multi-rtmp.so" ]; then
      echo "ERROR: не удалось извлечь .so из deb. Запусти с sudo: sudo dpkg -i ${CACHE}" >&2
      exit 1
    fi
  fi
else
  echo "ERROR: неизвестный формат ${ASSET}" >&2
  exit 1
fi

echo "[multi-rtmp] plugin files:"
find "${PLUGIN_DIR}/obs-multi-rtmp" -type f 2>/dev/null | head -20 || true

# Optional: write a small helper note + VK placeholders into OBS profile instructions
NOTE="${HOME}/.config/obs-studio/multi_rtmp_vk_howto.txt"
cat > "${NOTE}" <<'NOTE'
VK + Twitch через Multi-RTMP:

1) Закрой OBS полностью и открой снова.
2) В OBS: View / Вид → Docks → «Multiple RTMP outputs» (или Multi-output).
3) Основной стрим оставь Twitch (Settings → Stream).
4) В Multi-RTMP: Add → имя VK:
   - Server URL: rtmp://... из настроек эфира VK
   - Stream Key: ключ потока VK
5) Start: «Начать трансляцию» (Twitch) + Start у выхода VK.

Ключи:
  export VK_RTMP_URL='rtmp://...'
  export VK_STREAM_KEY='...'
  bash train_scripts/lab_comp/install_obs_multi_rtmp.bash
NOTE

if [ -n "${VK_RTMP_URL:-}" ] && [ -n "${VK_STREAM_KEY:-}" ]; then
  SECRET="${HOME}/.config/obs-studio/vk_rtmp.env"
  umask 077
  cat > "${SECRET}" <<EOF
# Не коммить / не шарить. Используется только как шпаргалка для UI Multi-RTMP.
VK_RTMP_URL='${VK_RTMP_URL}'
VK_STREAM_KEY='${VK_STREAM_KEY}'
EOF
  echo "[multi-rtmp] VK credentials saved to ${SECRET} (заполни поля в UI Multi-RTMP вручную — JSON-пресет OBS у плагина разный по версиям)."
  echo "[multi-rtmp] URL=${VK_RTMP_URL}"
else
  echo "[multi-rtmp] VK URL/ключ не переданы — после рестарта OBS добавь выход вручную."
fi

echo
echo "[multi-rtmp] OK. Дальше:"
echo "  1) Перезапусти OBS на DISPLAY=:1 (закрой окно и открой снова)."
echo "  2) Вид → Документы → Multiple RTMP outputs"
echo "  3) Добавь VK (Custom RTMP) + ключ"
echo "  Howto: ${NOTE}"
