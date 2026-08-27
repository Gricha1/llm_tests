#!/usr/bin/env bash
# Привязать OBS Xcomposite к текущему окну Presentation Unity (forestStreamOnly).
# Вызывать перед стартом OBS. Train не трогает.
set -eu
export DISPLAY="${DISPLAY:-:1}"
if [ -z "${XAUTHORITY:-}" ] || [ ! -f "${XAUTHORITY}" ]; then
  if [ -f "/run/user/$(id -u)/gdm/Xauthority" ]; then
    export XAUTHORITY="/run/user/$(id -u)/gdm/Xauthority"
  elif [ -f "${HOME}/.Xauthority" ]; then
    export XAUTHORITY="${HOME}/.Xauthority"
  fi
fi

SCENE="${FOREST_OBS_SCENE:-${HOME}/.config/obs-studio/basic/scenes/Безымянный.json}"
TITLE="${FOREST_OBS_WINDOW_TITLE:-forest_survival}"
CLASS="${FOREST_OBS_WINDOW_CLASS:-stream_forest_survival_2_12_07_2026.x86_64}"

if [ ! -f "${SCENE}" ]; then
  echo "[bind_obs] skip: no scene ${SCENE}"
  exit 0
fi

WID_HEX=""
while read -r line; do
  if echo "${line}" | grep -q "${TITLE}" \
    && echo "${line}" | grep -qi 'stream_forest_survival'; then
    WID_HEX="$(echo "${line}" | awk '{print $1}')"
    break
  fi
done < <(xwininfo -root -tree 2>/dev/null || true)

if [ -z "${WID_HEX}" ]; then
  echo "[bind_obs] WARN: нет окна ${TITLE} на ${DISPLAY} — OBS оставлю как есть"
  exit 0
fi

WID_DEC=$((WID_HEX))
export FOREST_OBS_SCENE="${SCENE}"
export FOREST_OBS_WID_DEC="${WID_DEC}"
export FOREST_OBS_TITLE="${TITLE}"
export FOREST_OBS_CLASS="${CLASS}"

python3 - <<'PY'
import json, os
from pathlib import Path
p = Path(os.environ["FOREST_OBS_SCENE"])
new = "{}\n{}\n{}".format(
    os.environ["FOREST_OBS_WID_DEC"],
    os.environ["FOREST_OBS_TITLE"],
    os.environ["FOREST_OBS_CLASS"],
)
d = json.loads(p.read_text(encoding="utf-8"))
patched = 0
for s in d.get("sources") or []:
    if s.get("id") != "xcomposite_input":
        continue
    st = s.setdefault("settings", {})
    old = st.get("capture_window")
    if old == new:
        print("[bind_obs] already bound wid={}".format(os.environ["FOREST_OBS_WID_DEC"]))
        patched += 1
        continue
    st["capture_window"] = new
    print("[bind_obs] {} wid {} → {}".format(s.get("name"), repr(old), repr(new)))
    patched += 1
if not patched:
    print("[bind_obs] WARN: в сцене нет xcomposite_input")
else:
    p.write_text(json.dumps(d, ensure_ascii=False, indent=2), encoding="utf-8")
PY
