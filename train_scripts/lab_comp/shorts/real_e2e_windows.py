#!/usr/bin/env python3
"""REAL Shorts E2E from THIS Windows PC: lab x11grab (:0) → scp → verify → play → delete."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import sys
import time
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "train_scripts" / "lab_comp"))
sys.path.insert(0, str(ROOT))

EVIDENCE_DIR = ROOT / "artifacts" / "reports" / "_final_evidence_staging"
REMOTE = "~/lab_work_space/forest_survival"
VIDEO_ID = f"real_e2e_{uuid.uuid4().hex[:8]}"


def _load_ui():
    path = ROOT / "train_scripts" / "lab_comp" / "train_lab_ui.py"
    spec = importlib.util.spec_from_file_location("train_lab_ui_helpers", path)
    mod = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    sys.modules["train_lab_ui_helpers"] = mod
    spec.loader.exec_module(mod)
    return mod


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    UI = _load_ui()
    from shorts import ui_api as SHORTS_API

    EVIDENCE_DIR.mkdir(parents=True, exist_ok=True)
    log: list[str] = []

    def L(msg: str) -> None:
        print(msg, flush=True)
        log.append(msg)

    # Must match train_lab_ui.ssh_run(host, remote_cmd, timeout=...)
    ssh_run = UI.ssh_run

    L(f"VIDEO_ID={VIDEO_ID}")
    L("canonical: SHORTS_DISPLAY=:0 DISPLAY=:0 XAUTHORITY=/run/user/1000/gdm/Xauthority")

    # Upload + run lab capture script (avoids Windows→SSH heredoc breakage)
    local_sh = ROOT / "train_scripts" / "lab_comp" / "shorts" / "lab_capture_one.sh"
    scp = UI.scp_bin()
    host = UI.resolve_ssh_host(UI.load_cfg().get("ssh_host") or "lab_comp")
    up = __import__("subprocess").run(
        [scp, "-o", "BatchMode=yes", str(local_sh), f"{host}:/tmp/lab_capture_one.sh"],
        capture_output=True,
        text=True,
        timeout=60,
    )
    if up.returncode != 0:
        L("SCP script fail: " + (up.stderr or up.stdout or ""))
        return 1
    r = ssh_run(
        None,
        f"sed -i 's/\\r$//' /tmp/lab_capture_one.sh && bash /tmp/lab_capture_one.sh {VIDEO_ID}",
        timeout=120,
    )
    out_all = (r.stdout or "") + "\n" + (r.stderr or "")
    if r.returncode != 0:
        L("CAPTURE FAIL:\n" + out_all[-1500:])
        return 1
    lines = [ln for ln in (r.stdout or "").splitlines() if ln.strip().startswith("{")]
    if not lines:
        L("CAPTURE FAIL: no JSON meta\n" + out_all[-1000:])
        return 1
    meta = json.loads(lines[-1])
    L(f"CAPTURE ok size={meta['size_bytes']} sha={meta['sha256'][:20]}...")

    sync = SHORTS_API.sync_pending_videos(
        ssh_run,
        UI.scp_bin,
        lambda: UI.resolve_ssh_host(UI.load_cfg().get("ssh_host") or "lab_comp"),
    )
    L("SYNC " + json.dumps(sync, ensure_ascii=False))

    local_mp4 = SHORTS_API.LOCAL_SAVED / f"{VIDEO_ID}.mp4"
    local_meta_path = SHORTS_API.LOCAL_SAVED / f"{VIDEO_ID}.json"
    if not local_mp4.is_file():
        L(f"FAIL local missing {local_mp4}")
        return 1
    local_sha = sha256_file(local_mp4)
    if local_sha != meta["sha256"] or local_mp4.stat().st_size != int(meta["size_bytes"]):
        L(f"FAIL verify sha/size local={local_sha} size={local_mp4.stat().st_size}")
        return 1

    local_meta = dict(meta)
    if local_meta_path.is_file():
        try:
            local_meta.update(json.loads(local_meta_path.read_text(encoding="utf-8")))
        except json.JSONDecodeError:
            pass
    local_meta["local_path"] = str(local_mp4.resolve())
    local_meta["remote_path"] = meta.get("remote_path") or ""
    local_meta["sha256"] = local_sha
    local_meta["size_bytes"] = local_mp4.stat().st_size
    local_meta_path.write_text(json.dumps(local_meta, indent=2), encoding="utf-8")
    # also update index
    idx = SHORTS_API.load_local_index()
    idx = [x for x in idx if x.get("video_id") != VIDEO_ID]
    idx.insert(0, local_meta)
    SHORTS_API.save_local_index(idx)

    assert local_meta["remote_path"] and local_meta["local_path"]
    assert local_meta["size_bytes"] > 0 and local_meta["sha256"]

    (EVIDENCE_DIR / "real_capture_metadata.json").write_text(
        json.dumps(local_meta, indent=2), encoding="utf-8"
    )

    play = SHORTS_API.play_path(VIDEO_ID)
    L("PLAY " + json.dumps(play))
    if not play.get("ok"):
        return 1
    try:
        os.startfile(play["path"])  # type: ignore[attr-defined]
        L("PLAY opened: " + play["path"])
        time.sleep(2.5)
    except Exception as e:
        L(f"PLAY fail: {e}")
        return 1

    deleted = SHORTS_API.delete_video(VIDEO_ID, ssh_run)
    L("DELETE " + json.dumps(deleted, ensure_ascii=False))
    local_gone = not local_mp4.is_file()
    chk = ssh_run(
        None,
        f"test ! -f {REMOTE}/results/shorts/saved/{VIDEO_ID}.mp4 && echo LAB_GONE || echo LAB_STILL",
        timeout=40,
    )
    lab_gone = "LAB_GONE" in (chk.stdout or "")
    L(f"local_gone={local_gone} lab_gone={lab_gone}")

    sync_txt = "\n".join(
        [
            f"video_id={VIDEO_ID}",
            f"remote_path={local_meta['remote_path']}",
            f"local_path={local_meta['local_path']}",
            f"size_bytes={local_meta['size_bytes']}",
            f"sha256={local_meta['sha256']}",
            f"sync_ok={sync.get('ok')}",
            f"play_opened={play.get('path')}",
            f"delete={deleted}",
            f"local_gone={local_gone}",
            f"lab_gone={lab_gone}",
            f"REAL_E2E={'PASS' if (deleted.get('ok') and local_gone and lab_gone) else 'FAIL'}",
        ]
    )
    (EVIDENCE_DIR / "real_capture_sync.txt").write_text(sync_txt, encoding="utf-8")
    L(sync_txt)
    return 0 if (deleted.get("ok") and local_gone and lab_gone) else 1


if __name__ == "__main__":
    raise SystemExit(main())
