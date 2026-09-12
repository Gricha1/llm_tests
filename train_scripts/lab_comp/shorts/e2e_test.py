#!/usr/bin/env python3
"""Safe Shorts E2E (no Twitch start): fake episodes → keep/delete → sync metadata."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(ROOT))

from interest import EpisodeInterestTracker, NoveltyState  # noqa: E402
from recorder import start_ffmpeg_record, stop_recorder, which_ffmpeg  # noqa: E402

SHORTS = Path(os.environ.get("SHORTS_ROOT") or (ROOT / "results" / "shorts_e2e"))
TEMP = SHORTS / "temp"
SAVED = SHORTS / "saved"
LOG = []


def log(msg: str) -> None:
    line = f"[e2e] {msg}"
    print(line, flush=True)
    LOG.append(line)


def make_test_mp4(path: Path, seconds: float = 2.0) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    ff = which_ffmpeg()
    if not ff:
        raise RuntimeError("ffmpeg missing")
    cmd = [
        ff,
        "-y",
        "-f",
        "lavfi",
        "-i",
        f"color=c=blue:s=640x360:d={seconds}",
        "-c:v",
        "libx264",
        "-pix_fmt",
        "yuv420p",
        "-an",
        str(path),
    ]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0 or not path.is_file() or path.stat().st_size < 1000:
        raise RuntimeError(f"lavfi mp4 failed: {r.stderr[:400]}")
    return path


def sha256(path: Path) -> str:
    import hashlib

    h = hashlib.sha256()
    with path.open("rb") as f:
        for c in iter(lambda: f.read(65536), b""):
            h.update(c)
    return h.hexdigest()


def save_meta(tracker: EpisodeInterestTracker, verdict: dict, path: Path, source_mode: str) -> dict:
    dest = SAVED / f"{tracker.episode_id}.mp4"
    if dest.exists():
        dest.unlink()
    shutil.copy2(path, dest)
    meta = {
        "video_id": tracker.episode_id,
        "episode_id": tracker.episode_id,
        "created_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "duration_sec": 2.0,
        "size_bytes": dest.stat().st_size,
        "source_mode": source_mode,
        "interest_score": verdict["interest_score"],
        "save_reasons": verdict["save_reasons"],
        "remote_path": str(dest),
        "local_path": "",
        "sha256": sha256(dest),
    }
    (SAVED / f"{tracker.episode_id}.json").write_text(
        json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return meta


def main() -> int:
    if SHORTS.exists():
        shutil.rmtree(SHORTS)
    TEMP.mkdir(parents=True)
    SAVED.mkdir(parents=True)
    novelty = NoveltyState()
    evidence: dict = {"ok": True, "cases": {}}

    # --- interesting episode ---
    eid = f"e2e_interest_{uuid.uuid4().hex[:8]}"
    tr = EpisodeInterestTracker(episode_id=eid, started_at=time.time())
    tr.note_event("BOSS_SPAWNED", progress=0.4)
    tr.note_event("VIEWER_BECAME_SOLDIER", progress=0.5)
    verdict = tr.evaluate(novelty)
    assert verdict["keep"], verdict
    mp4 = make_test_mp4(TEMP / f"{eid}.mp4")
    meta = save_meta(tr, verdict, mp4, "smart")
    mp4.unlink(missing_ok=True)
    evidence["cases"]["interesting_keep"] = {
        "video_id": eid,
        "path": meta["remote_path"],
        "size": meta["size_bytes"],
        "reasons": meta["save_reasons"],
        "sha256": meta["sha256"],
    }
    log(f"KEEP interesting {eid} size={meta['size_bytes']}")

    # --- boring episode (no events, progress below historical best) ---
    eid2 = f"e2e_boring_{uuid.uuid4().hex[:8]}"
    tr2 = EpisodeInterestTracker(episode_id=eid2, started_at=time.time())
    tr2.note_progress(0.05)  # below novelty.best after interesting ep
    v2 = tr2.evaluate(novelty)
    assert not v2["keep"], v2
    boring = make_test_mp4(TEMP / f"{eid2}.mp4")
    boring.unlink()
    assert not (SAVED / f"{eid2}.mp4").exists()
    evidence["cases"]["boring_delete"] = {"video_id": eid2, "deleted": True, "kept": False, "verdict": v2}
    log(f"DELETE boring {eid2}")

    # --- manual always keep ---
    eid3 = f"e2e_manual_{uuid.uuid4().hex[:8]}"
    tr3 = EpisodeInterestTracker(episode_id=eid3, started_at=time.time(), manual=True)
    v3 = tr3.evaluate(novelty)
    assert v3["keep"] and "manual_capture" in v3["save_reasons"]
    m3 = save_meta(tr3, v3, make_test_mp4(TEMP / f"{eid3}.mp4"), "manual")
    evidence["cases"]["manual_keep"] = {
        "video_id": eid3,
        "path": m3["remote_path"],
        "reasons": m3["save_reasons"],
    }
    log(f"KEEP manual {eid3}")

    # --- optional real x11grab smoke (non-fatal on Windows; on lab use DISPLAY=:0) ---
    os.environ.setdefault("SHORTS_DISPLAY", ":0")
    os.environ.setdefault("DISPLAY", os.environ.get("SHORTS_DISPLAY", ":0"))
    try:
        handle = start_ffmpeg_record(TEMP / "x11_smoke.mp4", episode_id="x11_smoke", width=640, height=360)
        time.sleep(1.5)
        stop_info = stop_recorder(handle)
        evidence["cases"]["x11grab"] = stop_info
        log(f"x11grab ok={stop_info.get('ok')} size={stop_info.get('size_bytes')}")
    except Exception as e:
        evidence["cases"]["x11grab"] = {"ok": False, "error": str(e), "note": "use SHORTS_DISPLAY=:0 on lab"}
        log(f"x11grab skipped/failed: {e}")

    # sync simulation: copy saved → local shorts mirror + verify hash
    local = Path(os.environ.get("FOREST_LOCAL_SHORTS_E2E") or (ROOT / ".train_lab_ui" / "shorts_e2e" / "saved"))
    if local.exists():
        shutil.rmtree(local)
    local.mkdir(parents=True)
    for p in SAVED.glob("*.mp4"):
        dest = local / p.name
        shutil.copy2(p, dest)
        meta_p = SAVED / (p.stem + ".json")
        m = json.loads(meta_p.read_text(encoding="utf-8"))
        assert sha256(dest) == m["sha256"]
        m["local_path"] = str(dest)
        (local / (p.stem + ".json")).write_text(json.dumps(m, indent=2), encoding="utf-8")
    evidence["cases"]["sync_verify"] = {
        "local_dir": str(local),
        "files": [p.name for p in local.glob("*.mp4")],
    }
    log(f"SYNC verified → {local}")

    # delete one
    victim = eid
    (SAVED / f"{victim}.mp4").unlink(missing_ok=True)
    (SAVED / f"{victim}.json").unlink(missing_ok=True)
    (local / f"{victim}.mp4").unlink(missing_ok=True)
    (local / f"{victim}.json").unlink(missing_ok=True)
    evidence["cases"]["delete"] = {
        "video_id": victim,
        "lab_gone": not (SAVED / f"{victim}.mp4").exists(),
        "local_gone": not (local / f"{victim}.mp4").exists(),
    }
    log(f"DELETE {victim} lab+local")

    out = SHORTS / "e2e_result.json"
    out.write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")
    (SHORTS / "e2e_log.txt").write_text("\n".join(LOG), encoding="utf-8")
    log(f"wrote {out}")
    print(json.dumps(evidence, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
