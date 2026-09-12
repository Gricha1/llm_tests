#!/usr/bin/env python3
"""REAL Shorts E2E (Windows): lab DISPLAY=:0 x11grab → scp → sha256 → play → delete.

Uses direct ssh/scp (not UI ssh gate) for reliable verification evidence.
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "train_scripts" / "lab_comp"))
sys.path.insert(0, str(ROOT))

from shorts import ui_api as SHORTS_API  # noqa: E402

HOST = "lab_comp"
REMOTE_ROOT = "~/lab_work_space/forest_survival"
EVIDENCE = ROOT / "artifacts" / "reports" / "_final_evidence_staging"
VIDEO_ID = f"real_e2e_{uuid.uuid4().hex[:8]}"


def run(cmd: list[str], timeout: float = 120) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, encoding="utf-8", errors="replace")


def ssh(remote: str, timeout: float = 120) -> subprocess.CompletedProcess:
    return run(["ssh", "-o", "BatchMode=yes", HOST, remote], timeout=timeout)


def scp_from(remote_path: str, local_path: Path, timeout: float = 180) -> subprocess.CompletedProcess:
    local_path.parent.mkdir(parents=True, exist_ok=True)
    return run(["scp", "-o", "BatchMode=yes", f"{HOST}:{remote_path}", str(local_path)], timeout=timeout)


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for c in iter(lambda: f.read(1024 * 1024), b""):
            h.update(c)
    return h.hexdigest()


def main() -> int:
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    lines: list[str] = []

    def L(m: str) -> None:
        print(m, flush=True)
        lines.append(m)

    L(f"VIDEO_ID={VIDEO_ID}")
    L("canonical capture: DISPLAY=:0 XAUTHORITY=/run/user/1000/gdm/Xauthority (OBS production)")

    # deploy capture script
    up = run(
        [
            "scp",
            "-o",
            "BatchMode=yes",
            str(ROOT / "train_scripts/lab_comp/shorts/lab_capture_one.sh"),
            f"{HOST}:/tmp/lab_capture_one.sh",
        ]
    )
    if up.returncode != 0:
        L("scp script fail: " + (up.stderr or up.stdout))
        return 1

    cap = ssh(f"sed -i 's/\\r$//' /tmp/lab_capture_one.sh && bash /tmp/lab_capture_one.sh {VIDEO_ID}", timeout=120)
    if cap.returncode != 0:
        L("CAPTURE FAIL: " + (cap.stderr or cap.stdout)[-1200:])
        return 1
    jlines = [ln for ln in (cap.stdout or "").splitlines() if ln.strip().startswith("{")]
    meta = json.loads(jlines[-1])
    L(f"CAPTURE size={meta['size_bytes']} sha={meta['sha256']}")
    if int(meta["size_bytes"]) < 50_000:
        L("FAIL: size too small (synthetic?)")
        return 1

    # sync to Windows local shorts folder
    SHORTS_API.ensure_local_dirs()
    local_mp4 = SHORTS_API.LOCAL_SAVED / f"{VIDEO_ID}.mp4"
    local_json = SHORTS_API.LOCAL_SAVED / f"{VIDEO_ID}.json"
    remote_mp4 = f"{REMOTE_ROOT}/results/shorts/saved/{VIDEO_ID}.mp4"
    pull = scp_from(remote_mp4, local_mp4)
    if pull.returncode != 0 or not local_mp4.is_file():
        L("SCP FAIL: " + (pull.stderr or pull.stdout))
        return 1
    local_sha = sha256_file(local_mp4)
    if local_sha != meta["sha256"] or local_mp4.stat().st_size != int(meta["size_bytes"]):
        L(f"VERIFY FAIL sha local={local_sha} remote={meta['sha256']}")
        return 1

    meta["local_path"] = str(local_mp4.resolve())
    meta["remote_path"] = meta.get("remote_path") or f"/home/reedgern/lab_work_space/forest_survival/results/shorts/saved/{VIDEO_ID}.mp4"
    meta["sha256"] = local_sha
    meta["size_bytes"] = local_mp4.stat().st_size
    assert meta["remote_path"] and meta["local_path"] and meta["sha256"] and meta["size_bytes"] > 0
    local_json.write_text(json.dumps(meta, indent=2), encoding="utf-8")
    idx = [x for x in SHORTS_API.load_local_index() if x.get("video_id") != VIDEO_ID]
    idx.insert(0, meta)
    SHORTS_API.save_local_index(idx)
    (EVIDENCE / "real_capture_metadata.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
    L(f"SYNC ok local_path={meta['local_path']}")

    # Play
    os.startfile(str(local_mp4))  # type: ignore[attr-defined]
    L(f"PLAY opened {local_mp4}")
    time.sleep(2.0)

    # Keep a copy of metadata evidence; delete for E2E proof
    # (UI screenshots will use a separate keep video created after this if needed)
    local_mp4.unlink(missing_ok=True)
    local_json.unlink(missing_ok=True)
    SHORTS_API.save_local_index([x for x in SHORTS_API.load_local_index() if x.get("video_id") != VIDEO_ID])
    rm = ssh(
        f"rm -fv {REMOTE_ROOT}/results/shorts/saved/{VIDEO_ID}.mp4 {REMOTE_ROOT}/results/shorts/saved/{VIDEO_ID}.json; "
        f"test ! -f {REMOTE_ROOT}/results/shorts/saved/{VIDEO_ID}.mp4 && echo LAB_GONE || echo LAB_STILL"
    )
    lab_gone = "LAB_GONE" in (rm.stdout or "")
    local_gone = not local_mp4.is_file()
    L(f"DELETE local_gone={local_gone} lab_out={(rm.stdout or '').strip()} lab_gone={lab_gone}")

    sync_txt = "\n".join(
        [
            f"video_id={VIDEO_ID}",
            f"remote_path={meta['remote_path']}",
            f"local_path={meta['local_path']}",
            f"size_bytes={meta['size_bytes']}",
            f"sha256={meta['sha256']}",
            "sync_method=scp+sha256",
            f"play_opened={meta['local_path']}",
            f"local_gone={local_gone}",
            f"lab_gone={lab_gone}",
            f"REAL_E2E={'PASS' if local_gone and lab_gone else 'FAIL'}",
        ]
    )
    (EVIDENCE / "real_capture_sync.txt").write_text(sync_txt + "\n", encoding="utf-8")
    L(sync_txt)

    # Leave a UI demo clip for screenshots (same real capture pipeline)
    demo_id = f"ui_demo_{uuid.uuid4().hex[:6]}"
    L(f"Creating UI demo clip {demo_id} for screenshots...")
    cap2 = ssh(f"bash /tmp/lab_capture_one.sh {demo_id}", timeout=120)
    j2 = [ln for ln in (cap2.stdout or "").splitlines() if ln.strip().startswith("{")]
    if not j2:
        L("UI demo capture failed: " + (cap2.stderr or cap2.stdout or "")[-500:])
        return 0 if local_gone and lab_gone else 1
    m2 = json.loads(j2[-1])
    demo_mp4 = SHORTS_API.LOCAL_SAVED / f"{demo_id}.mp4"
    scp_from(f"{REMOTE_ROOT}/results/shorts/saved/{demo_id}.mp4", demo_mp4)
    m2["local_path"] = str(demo_mp4.resolve())
    m2["sha256"] = sha256_file(demo_mp4)
    m2["size_bytes"] = demo_mp4.stat().st_size
    (SHORTS_API.LOCAL_SAVED / f"{demo_id}.json").write_text(json.dumps(m2, indent=2), encoding="utf-8")
    idx = SHORTS_API.load_local_index()
    idx = [x for x in idx if x.get("video_id") != demo_id]
    idx.insert(0, m2)
    SHORTS_API.save_local_index(idx)
    (EVIDENCE / "ui_demo_video_id.txt").write_text(demo_id + "\n", encoding="utf-8")
    # refresh metadata evidence with complete fields from first successful transfer
    L(f"UI_DEMO ready id={demo_id} size={m2['size_bytes']}")

    return 0 if local_gone and lab_gone else 1


if __name__ == "__main__":
    raise SystemExit(main())
