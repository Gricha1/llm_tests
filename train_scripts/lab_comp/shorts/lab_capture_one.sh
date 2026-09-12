#!/usr/bin/env bash
# Real x11grab capture on lab — canonical DISPLAY=:0
set -euo pipefail
cd ~/lab_work_space/forest_survival
export SHORTS_DISPLAY=:0
export DISPLAY=:0
export XAUTHORITY=/run/user/1000/gdm/Xauthority
VID="${1:?video_id}"
$HOME/anaconda3/envs/mlagents/bin/python - <<PY
import json, time, hashlib, sys
from pathlib import Path
sys.path.insert(0, "train_scripts/lab_comp/shorts")
from recorder import start_ffmpeg_record, stop_recorder
vid = "${VID}"
out = Path("results/shorts/saved") / (vid + ".mp4")
out.parent.mkdir(parents=True, exist_ok=True)
h = start_ffmpeg_record(out, episode_id=vid, width=1280, height=720, fps=20)
time.sleep(3.0)
info = stop_recorder(h)
assert info.get("ok") and out.is_file() and out.stat().st_size > 50000, info
sha = hashlib.sha256(out.read_bytes()).hexdigest()
meta = {
  "video_id": vid,
  "episode_id": vid,
  "created_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
  "duration_sec": info.get("duration_sec"),
  "size_bytes": out.stat().st_size,
  "source_mode": "manual",
  "interest_score": 1.0,
  "save_reasons": ["manual_capture", "real_x11grab_e2e"],
  "remote_path": str(out.resolve()),
  "local_path": "",
  "sha256": sha,
}
(out.with_suffix(".json")).write_text(json.dumps(meta, indent=2), encoding="utf-8")
idx_p = Path("results/shorts/index.json")
idx = []
if idx_p.is_file():
  try: idx = json.loads(idx_p.read_text(encoding="utf-8"))
  except Exception: idx = []
idx = [x for x in idx if x.get("video_id") != vid]
idx.insert(0, meta)
idx_p.write_text(json.dumps(idx, indent=2), encoding="utf-8")
print(json.dumps(meta))
PY
