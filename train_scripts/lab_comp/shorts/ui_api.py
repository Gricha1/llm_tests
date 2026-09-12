"""Local Shorts control API helpers for train_lab_ui (lab SSH + local sync)."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import time
from pathlib import Path
from typing import Any, Callable, Optional

ROOT = Path(__file__).resolve().parents[3]
LOCAL_SHORTS = Path(os.environ.get("FOREST_LOCAL_SHORTS") or (ROOT / ".train_lab_ui" / "shorts"))
LOCAL_SAVED = LOCAL_SHORTS / "saved"
LOCAL_INDEX = LOCAL_SHORTS / "index.json"
REMOTE_DIR = "~/lab_work_space/forest_survival"
REMOTE_SHORTS = f"{REMOTE_DIR}/results/shorts"
STREAM_MODE_PATH = LOCAL_SHORTS / "stream_mode.txt"
REMOTE_STREAM_MODE = f"{REMOTE_DIR}/results/stream_mode.txt"

VALID_STREAM_MODES = ("autonomous", "hosted", "test")


def ensure_local_dirs() -> None:
    LOCAL_SAVED.mkdir(parents=True, exist_ok=True)
    LOCAL_SHORTS.mkdir(parents=True, exist_ok=True)
    if not STREAM_MODE_PATH.is_file():
        STREAM_MODE_PATH.write_text("autonomous\n", encoding="utf-8")


def file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def disk_stats(path: Path) -> dict[str, Any]:
    ensure_local_dirs()
    try:
        usage = shutil.disk_usage(str(path if path.is_dir() else path.parent))
    except OSError as e:
        return {"ok": False, "error": str(e)}
    folder = 0
    if path.is_dir():
        for p in path.rglob("*"):
            if p.is_file():
                try:
                    folder += p.stat().st_size
                except OSError:
                    pass
    return {
        "ok": True,
        "total": usage.total,
        "used": usage.used,
        "free": usage.free,
        "shorts_folder_size": folder,
        "path": str(path),
    }


def load_local_index() -> list[dict[str, Any]]:
    ensure_local_dirs()
    if not LOCAL_INDEX.is_file():
        return []
    try:
        data = json.loads(LOCAL_INDEX.read_text(encoding="utf-8"))
        return data if isinstance(data, list) else []
    except (OSError, json.JSONDecodeError):
        return []


def save_local_index(items: list[dict[str, Any]]) -> None:
    ensure_local_dirs()
    LOCAL_INDEX.write_text(json.dumps(items, ensure_ascii=False, indent=2), encoding="utf-8")


def get_stream_mode() -> str:
    ensure_local_dirs()
    try:
        mode = STREAM_MODE_PATH.read_text(encoding="utf-8").strip().lower()
    except OSError:
        mode = "autonomous"
    if mode not in VALID_STREAM_MODES:
        mode = "autonomous"
    return mode


def set_stream_mode(mode: str, ssh_run: Callable[..., Any]) -> dict[str, Any]:
    mode = (mode or "").strip().lower()
    if mode not in VALID_STREAM_MODES:
        return {"ok": False, "error": f"invalid mode {mode}"}
    ensure_local_dirs()
    STREAM_MODE_PATH.write_text(mode + "\n", encoding="utf-8")
    # Mirror to lab for bot/events persistence
    try:
        remote = (
            f"mkdir -p {REMOTE_DIR}/results && "
            f"printf '%s\\n' {mode} > {REMOTE_STREAM_MODE}"
        )
        ssh_run(None, remote, timeout=20)
    except Exception as e:
        return {"ok": True, "mode": mode, "lab_sync": False, "lab_error": str(e)}
    return {"ok": True, "mode": mode, "lab_sync": True}


def _ssh_python(cmd: str) -> str:
    py = (
        "if [ -x $HOME/anaconda3/envs/mlagents/bin/python ]; then "
        "PY=$HOME/anaconda3/envs/mlagents/bin/python; "
        "else PY=python3; fi; "
    )
    return py + cmd


def daemon_status(ssh_run: Callable[..., Any]) -> dict[str, Any]:
    remote = _ssh_python(
        f"cd {REMOTE_DIR} && "
        f"$PY - <<'PY'\n"
        "import json, pathlib\n"
        "p=pathlib.Path('results/shorts/daemon_state.json')\n"
        "print(p.read_text(encoding='utf-8') if p.is_file() else json.dumps({'ok':True,'status':'IDLE','collection_on':False}))\n"
        "PY"
    )
    try:
        r = ssh_run(None, remote, timeout=25)
        text = (r.stdout or "").strip()
        data = json.loads(text) if text else {"ok": True, "status": "IDLE"}
        data["stream_mode"] = get_stream_mode()
        return data
    except Exception as e:
        return {"ok": False, "status": "ERROR", "error": str(e), "stream_mode": get_stream_mode()}


def set_collection(on: bool, ssh_run: Callable[..., Any]) -> dict[str, Any]:
    flag = "1" if on else "0"
    remote = (
        f"mkdir -p {REMOTE_SHORTS} && "
        f"if [ '{flag}' = '1' ]; then echo 1 > {REMOTE_SHORTS}/collection_on; "
        f"else rm -f {REMOTE_SHORTS}/collection_on; fi; "
        f"bash {REMOTE_DIR}/train_scripts/lab_comp/shorts/run_shorts_daemon.bash --daemon >/dev/null 2>&1 || true; "
        f"echo OK"
    )
    try:
        ssh_run(None, remote, timeout=40)
        return {"ok": True, "collection_on": bool(on)}
    except Exception as e:
        return {"ok": False, "error": str(e)}


def request_manual(ssh_run: Callable[..., Any]) -> dict[str, Any]:
    remote = (
        f"mkdir -p {REMOTE_SHORTS} && "
        f"echo 1 > {REMOTE_SHORTS}/manual_next_episode.flag && "
        f"bash {REMOTE_DIR}/train_scripts/lab_comp/shorts/run_shorts_daemon.bash --daemon >/dev/null 2>&1 || true; "
        f"echo OK"
    )
    try:
        ssh_run(None, remote, timeout=40)
        st = daemon_status(ssh_run)
        return {"ok": True, "message": "ARMED_FOR_NEXT_EPISODE", "status": st}
    except Exception as e:
        return {"ok": False, "error": str(e)}


def lab_disk_stats(ssh_run: Callable[..., Any]) -> dict[str, Any]:
    # Avoid heredoc through UI ssh gate — simple python -c.
    remote = (
        f"cd {REMOTE_DIR} && "
        f"if [ -x $HOME/anaconda3/envs/mlagents/bin/python ]; then "
        f"PY=$HOME/anaconda3/envs/mlagents/bin/python; else PY=python3; fi; "
        f"$PY -c \"import json,shutil,pathlib; root=pathlib.Path('results/shorts'); root.mkdir(parents=True,exist_ok=True); "
        f"u=shutil.disk_usage('.'); folder=sum(p.stat().st_size for p in root.rglob('*') if p.is_file()); "
        f"print(json.dumps({{'ok':True,'total':u.total,'used':u.used,'free':u.free,'shorts_folder_size':folder,'path':str(root.resolve())}}))\""
    )
    try:
        r = ssh_run(None, remote, timeout=45)
        text = (r.stdout or "").strip()
        # last json line
        for line in reversed(text.splitlines()):
            line = line.strip()
            if line.startswith("{"):
                return json.loads(line)
        return {"ok": False, "error": f"no json (rc={r.returncode}) {(r.stderr or '')[:200]}"}
    except Exception as e:
        return {"ok": False, "error": str(e)}


def sync_pending_videos(ssh_run: Callable[..., Any], scp_bin: Callable[[], str], resolve_host: Callable[[], str]) -> dict[str, Any]:
    """Pull lab saved videos missing locally; verify size+sha256."""
    ensure_local_dirs()
    remote = _ssh_python(
        f"cd {REMOTE_DIR} && $PY - <<'PY'\n"
        "import json, pathlib, hashlib\n"
        "saved=pathlib.Path('results/shorts/saved')\n"
        "out=[]\n"
        "for meta in sorted(saved.glob('*.json'), key=lambda p: -p.stat().st_mtime):\n"
        "  try: m=json.loads(meta.read_text(encoding='utf-8'))\n"
        "  except Exception: continue\n"
        "  vid=m.get('video_id') or meta.stem\n"
        "  mp4=saved/(vid+'.mp4')\n"
        "  if not mp4.is_file(): continue\n"
        "  h=hashlib.sha256()\n"
        "  with mp4.open('rb') as f:\n"
        "    for c in iter(lambda:f.read(1024*1024), b''): h.update(c)\n"
        "  m['remote_sha256']=h.hexdigest(); m['remote_size']=mp4.stat().st_size; m['remote_path']=str(mp4.resolve())\n"
        "  out.append(m)\n"
        "print(json.dumps(out))\n"
        "PY"
    )
    try:
        r = ssh_run(None, remote, timeout=90)
        remote_items = json.loads((r.stdout or "").strip() or "[]")
    except Exception as e:
        return {"ok": False, "error": str(e), "synced": []}

    host = resolve_host()
    local_idx = {x.get("video_id"): x for x in load_local_index()}
    synced: list[str] = []
    errors: list[str] = []
    for m in remote_items:
        vid = str(m.get("video_id") or "")
        if not vid:
            continue
        local_mp4 = LOCAL_SAVED / f"{vid}.mp4"
        local_meta = LOCAL_SAVED / f"{vid}.json"
        need = (not local_mp4.is_file()) or int(local_mp4.stat().st_size) != int(m.get("remote_size") or -1)
        if need:
            remote_mp4 = f"{REMOTE_SHORTS}/saved/{vid}.mp4"
            try:
                # scp file
                cmd = [scp_bin(), "-o", "BatchMode=yes", f"{host}:{remote_mp4}", str(local_mp4)]
                p = subprocess.run(cmd, capture_output=True, text=True, timeout=180)
                if p.returncode != 0:
                    errors.append(f"{vid}: scp failed: {(p.stderr or p.stdout or '')[:200]}")
                    continue
            except Exception as e:
                errors.append(f"{vid}: {e}")
                continue
        if not local_mp4.is_file():
            errors.append(f"{vid}: missing after scp")
            continue
        size = local_mp4.stat().st_size
        if int(m.get("remote_size") or 0) and size != int(m["remote_size"]):
            errors.append(f"{vid}: size mismatch local={size} remote={m['remote_size']}")
            try:
                local_mp4.unlink()
            except OSError:
                pass
            continue
        sha = file_sha256(local_mp4)
        if m.get("remote_sha256") and sha != m["remote_sha256"]:
            errors.append(f"{vid}: hash mismatch")
            try:
                local_mp4.unlink()
            except OSError:
                pass
            continue
        m["local_path"] = str(local_mp4)
        m["size_bytes"] = size
        m["sha256"] = sha
        m["available"] = True
        local_meta.write_text(json.dumps(m, ensure_ascii=False, indent=2), encoding="utf-8")
        local_idx[vid] = m
        synced.append(vid)

    items = sorted(local_idx.values(), key=lambda x: str(x.get("created_at") or ""), reverse=True)
    save_local_index(items)
    return {"ok": len(errors) == 0, "synced": synced, "errors": errors, "count": len(items)}


def delete_video(video_id: str, ssh_run: Callable[..., Any]) -> dict[str, Any]:
    vid = (video_id or "").strip()
    if not vid or "/" in vid or "\\" in vid or ".." in vid:
        return {"ok": False, "error": "bad video_id"}
    local_err = ""
    remote_err = ""
    local_mp4 = LOCAL_SAVED / f"{vid}.mp4"
    local_meta = LOCAL_SAVED / f"{vid}.json"
    try:
        if local_mp4.is_file():
            local_mp4.unlink()
        if local_meta.is_file():
            local_meta.unlink()
    except OSError as e:
        local_err = str(e)
    items = [x for x in load_local_index() if x.get("video_id") != vid]
    save_local_index(items)
    remote = (
        f"rm -f {REMOTE_SHORTS}/saved/{vid}.mp4 {REMOTE_SHORTS}/saved/{vid}.json; "
        f"cd {REMOTE_DIR} && "
        f"if [ -x $HOME/anaconda3/envs/mlagents/bin/python ]; then "
        f"PY=$HOME/anaconda3/envs/mlagents/bin/python; else PY=python3; fi; "
        f"$PY -c \"import json,pathlib; p=pathlib.Path('results/shorts/index.json'); "
        f"vid={vid!r}; "
        f"idx=json.loads(p.read_text(encoding='utf-8')) if p.is_file() else []; "
        f"p.parent.mkdir(parents=True, exist_ok=True); "
        f"p.write_text(json.dumps([x for x in idx if x.get('video_id')!=vid], indent=2), encoding='utf-8'); "
        f"print('OK')\""
    )
    try:
        r = ssh_run(None, remote, timeout=40)
        if r.returncode != 0:
            remote_err = (r.stderr or r.stdout or "remote delete failed")[:300]
        else:
            # verify gone
            v = ssh_run(
                None,
                f"test ! -f {REMOTE_SHORTS}/saved/{vid}.mp4 && echo GONE || echo STILL",
                timeout=20,
            )
            if "GONE" not in (v.stdout or ""):
                remote_err = f"lab file still present after rm; out={(v.stdout or '')[:120]}"
    except Exception as e:
        remote_err = str(e)
    ok = not local_err and not remote_err
    return {
        "ok": ok,
        "partial_failure": (not ok) and (bool(local_err) != bool(remote_err) or bool(remote_err)),
        "local_error": local_err,
        "remote_error": remote_err,
        "video_id": vid,
    }


def list_videos() -> dict[str, Any]:
    ensure_local_dirs()
    items = load_local_index()
    # refresh existence
    for it in items:
        lp = Path(it.get("local_path") or (LOCAL_SAVED / f"{it.get('video_id')}.mp4"))
        it["local_exists"] = lp.is_file()
        if lp.is_file():
            it["local_path"] = str(lp)
            it["size_bytes"] = lp.stat().st_size
    return {"ok": True, "videos": items}


def play_path(video_id: str) -> dict[str, Any]:
    vid = (video_id or "").strip()
    p = LOCAL_SAVED / f"{vid}.mp4"
    if not p.is_file():
        return {"ok": False, "error": "local file missing"}
    return {"ok": True, "path": str(p.resolve())}
