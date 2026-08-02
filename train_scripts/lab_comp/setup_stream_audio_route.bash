#!/usr/bin/env bash
# Подготовка звука стрима: forest_stream sink + OBS pulse capture → forest_stream.monitor.
#   bash train_scripts/lab_comp/setup_stream_audio_route.bash
#   bash train_scripts/lab_comp/setup_stream_audio_route.bash --restart-obs
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
RESTART_OBS=0
for a in "$@"; do
  case "$a" in
    --restart-obs) RESTART_OBS=1 ;;
  esac
done

if ! command -v pactl >/dev/null 2>&1; then
  echo "[stream_audio] pactl нет — пропуск"
  exit 0
fi

bash "${ROOT}/train_scripts/lab_comp/mute_train_pulse_audio.bash"

TARGET="forest_stream.monitor"
changed=0
for SCENE in \
  "${HOME}/.config/obs-studio/basic/scenes/Безымянный.json" \
  "${HOME}/.config/obs-studio/basic/scenes/Untitled.json"
do
  [ -f "$SCENE" ] || continue
  python3 - "$SCENE" "$TARGET" <<'PY'
import json, sys, shutil, time, os
path, target = sys.argv[1], sys.argv[2]
with open(path, encoding="utf-8") as f:
    d = json.load(f)
chg = 0
for s in d.get("sources", []):
    tid = s.get("id") or s.get("unversioned_id") or ""
    if tid != "pulse_output_capture":
        continue
    st = s.setdefault("settings", {})
    old = st.get("device_id")
    if old != target:
        bak = path + ".bak_audio_" + str(int(time.time()))
        shutil.copy2(path, bak)
        st["device_id"] = target
        chg = 1
        print("[stream_audio] OBS %s: %s -> %s (bak %s)" % (os.path.basename(path), old, target, bak))
if chg:
    with open(path, "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print("CHANGED")
else:
    print("[stream_audio] OBS already on", target)
PY
  if [ $? -eq 0 ]; then
    :
  fi
done

if [ "$RESTART_OBS" = "1" ]; then
  export DISPLAY="${DISPLAY:-:1}"
  # shellcheck source=cpu_affinity.env.bash
  source "${ROOT}/train_scripts/lab_comp/cpu_affinity.env.bash" 2>/dev/null || true
  OBS_CPUS="${FOREST_OBS_CPUS:-4-5}"
  echo "[stream_audio] restart OBS with --startstreaming (сразу в эфир, Unity не трогаем)…"
  if pgrep -x obs >/dev/null 2>&1; then
    pkill -TERM -x obs || true
    sleep 1
    pkill -KILL -x obs 2>/dev/null || true
    sleep 1
  fi
  if command -v taskset >/dev/null 2>&1; then
    nohup taskset -c "${OBS_CPUS}" obs --startstreaming >/tmp/forest_obs_restart.log 2>&1 &
  else
    nohup obs --startstreaming >/tmp/forest_obs_restart.log 2>&1 &
  fi
  sleep 3
  echo "[stream_audio] obs pids: $(pgrep -x obs | tr '\n' ' ')"
fi

echo "[stream_audio] done — Unity stream sink=forest_stream, OBS capture=${TARGET}"
