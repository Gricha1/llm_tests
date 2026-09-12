"""Local UI bridge for Shorts: status/sync/play/delete via SSH+SCP to lab_comp."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import time
from pathlib import Path
from typing import Any, Callable, Optional

ROOT = Path(__file__).resolve().parents[2]
LOCAL_SHORTS = Path(os.environ.get("FOREST_LOCAL_SHORTS") or (ROOT / "artifacts" / "shorts"))
LOCAL_SAVED = LOCAL_SHORTS / "saved"
LOCAL_INDEX = LOCAL_SHORTS / "index.json"
REMOTE_SHORTS = "~/lab_work_space/forest_survival/results/shorts"
REMOTE_ROOT = "~/lab_work_space/forest_survival"


def _ensure_local() -> None:
    LOCAL_SAVED.mkdir(parents=True, exist_ok=True)
    LOCAL_SHORTS.mkdir(parents=True, exist_ok=True)


def _file_sha256(path: Path, *, max_bytes: int = 0) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        if max_bytes > 0:
            h.update(f.read(max_bytes))
        else:
            while True:
                chunk = f.read(1024 * 1024)
                if not chunk:
                    break
                h.update(chunk)
    return h.hexdigest()


def _load_index() -> list[dict[str, Any]]:
    _ensure_local()
    if not LOCAL_INDEX.is_file():
        return []
    try:
        data = json.loads(LOCAL_INDEX.read_text(encoding="utf-8"))
        return data if isinstance(data, list) else []
    except (OSError, json.JSONDecodeError):
        return []


def _save_index(items: list[dict[str, Any]]) -> None:
    _ensure_local()
    LOCAL_INDEX.write_text(json.dumps(items, ensure_ascii=False, indent=2), encoding="utf-8")


def disk_stats_local() -> dict[str, Any]:
    _ensure_local()
    usage = shutil.disk_usage(str(LOCAL_SHORTS))
    folder = 0
    for p in LOCAL_SHORTS.rglob("*"):
        if p.is_file():
            try:
                folder += p.stat().st_size
            except OSError:
                pass
    return {
        "total": usage.total,
        "used": usage.used,
        "free": usage.free,
        "shorts_folder_size": folder,
        "path": str(LOCAL_SHORTS),
    }


class ShortsBridge:
    """Uses injected ssh_run / scp helpers from train_lab_ui."""

    def __init__(
        self,
        *,
        ssh_run: Callable[..., Any],
        scp_bin: Callable[[], str],
        resolve_host: Callable[[], str],
        ui_runs_on_lab: Callable[[], bool],
    ) -> None:
        self.ssh_run = ssh_run
        self.scp_bin = scp_bin
        self.resolve_host = resolve_host
        self.ui_runs_on_lab = ui_runs_on_lab
        self._disk_cache: dict[str, Any] = {"t": 0.0, "data": None}
        self._status_cache: dict[str, Any] = {"t": 0.0, "data": None}

    def _remote_py(self) -> str:
        return (
            "cd ~/lab_work_space/forest_survival && "
            "export FOREST_ROOT=$PWD RUN_ID=${RUN_ID:-jlg_finetune_2} "
            "SHORTS_ROOT=$PWD/results/shorts "
            "FOREST_RESULTS_DIR=$PWD/results/$RUN_ID; "
            "PY=$HOME/anaconda3/envs/mlagents/bin/python; "
            "[ -x \"$PY\" ] || PY=python3; "
        )

    def ensure_daemon(self) -> dict[str, Any]:
        remote = (
            self._remote_py()
            + "bash train_scripts/lab_comp/shorts/run_shorts_daemon.bash --daemon; "
            + "cat results/shorts/daemon_state.json 2>/dev/null || echo '{}'"
        )
        out = self.ssh_run(remote, timeout=25)
        text = (out or "").strip()
        try:
            return json.loads(text.splitlines()[-1] if text else "{}")
        except json.JSONDecodeError:
            return {"ok": True, "raw": text[-500:]}

    def lab_status(self, *, force: bool = False) -> dict[str, Any]:
        now = time.time()
        if not force and self._status_cache["data"] and now - float(self._status_cache["t"]) < 2.0:
            return dict(self._status_cache["data"])
        remote = (
            self._remote_py()
            + "mkdir -p results/shorts; "
            + "if [ ! -f results/shorts_daemon.pid ] || ! kill -0 $(cat results/shorts_daemon.pid) 2>/dev/null; then "
            + "bash train_scripts/lab_comp/shorts/run_shorts_daemon.bash --daemon >/dev/null; fi; "
            + "cat results/shorts/daemon_state.json 2>/dev/null || echo '{\"status\":\"IDLE\",\"collection_on\":false}'"
        )
        try:
            out = self.ssh_run(remote, timeout=20)
            data = json.loads((out or "").strip().splitlines()[-1])
        except Exception as e:
            data = {"ok": False, "status": "ERROR", "error": str(e), "collection_on": False}
        data["local_index_count"] = len(_load_index())
        self._status_cache = {"t": now, "data": data}
        return data

    def set_collection(self, on: bool) -> dict[str, Any]:
        val = "1" if on else "0"
        remote = (
            self._remote_py()
            + f"mkdir -p results/shorts; "
            + (f"echo 1 > results/shorts/collection_on; " if on else "rm -f results/shorts/collection_on; ")
            + "python - <<'PY'\n"
            "import json,time\n"
            "from pathlib import Path\n"
            "p=Path('results/shorts/daemon_state.json')\n"
            "d={}\n"
            "if p.is_file():\n"
            "  try: d=json.loads(p.read_text())\n"
            "  except Exception: d={}\n"
            f"d['collection_on']={str(bool(on))}\n"
            "d['status']=d.get('status') or 'WAITING_EPISODE'\n"
            "p.write_text(json.dumps(d,ensure_ascii=False,indent=2))\n"
            "print(json.dumps(d))\n"
            "PY"
        )
        # simpler flag-only + status refresh
        remote = (
            f"cd ~/lab_work_space/forest_survival && mkdir -p results/shorts && "
            + (f"echo 1 > results/shorts/collection_on" if on else "rm -f results/shorts/collection_on")
        )
        self.ssh_run(remote, timeout=15)
        self._status_cache["t"] = 0
        return self.lab_status(force=True)

    def request_manual(self) -> dict[str, Any]:
        remote = (
            "cd ~/lab_work_space/forest_survival && mkdir -p results/shorts && "
            "echo 1 > results/shorts/manual_next_episode.flag && "
            "python3 -c \"import json;from pathlib import Path;p=Path('results/shorts/daemon_state.json');"
            "d=json.loads(p.read_text()) if p.is_file() else {};"
            "d['manual_next']=True;d['status']='WAITING_EPISODE';"
            "p.write_text(json.dumps(d,ensure_ascii=False,indent=2));print(json.dumps(d))\""
        )
        try:
            out = self.ssh_run(remote, timeout=15)
            data = json.loads((out or "").strip().splitlines()[-1])
        except Exception as e:
            data = {"ok": False, "error": str(e)}
        self._status_cache["t"] = 0
        data["message"] = data.get("message") or "WAITING_FOR_NEXT_EPISODE"
        return data

    def sync_from_lab(self) -> dict[str, Any]:
        """Pull index + any missing saved videos; verify size."""
        _ensure_local()
        host = self.resolve_host()
        pulled = []
        errors = []
        if self.ui_runs_on_lab():
            remote_saved = Path.home() / "lab_work_space/forest_survival/results/shorts/saved"
            remote_index = Path.home() / "lab_work_space/forest_survival/results/shorts/index.json"
            items = []
            if remote_index.is_file():
                try:
                    items = json.loads(remote_index.read_text(encoding="utf-8"))
                except json.JSONDecodeError:
                    items = []
            for meta in items or []:
                vid = str(meta.get("video_id") or "")
                if not vid:
                    continue
                src = remote_saved / f"{vid}.mp4"
                dst = LOCAL_SAVED / f"{vid}.mp4"
                meta_dst = LOCAL_SAVED / f"{vid}.json"
                if src.is_file():
                    if (not dst.is_file()) or dst.stat().st_size != src.stat().st_size:
                        shutil.copy2(src, dst)
                    shutil.copy2(remote_saved / f"{vid}.json", meta_dst) if (remote_saved / f"{vid}.json").is_file() else None
                    meta["local_path"] = str(dst)
                    meta["size_bytes"] = dst.stat().st_size
                    meta["sha256_prefix"] = _file_sha256(dst, max_bytes=1024 * 1024)[:16]
                    meta["transfer_ok"] = True
                    pulled.append(vid)
                else:
                    errors.append(f"missing remote {vid}")
            # also scan saved dir
            for mp4 in remote_saved.glob("*.mp4") if remote_saved.is_dir() else []:
                vid = mp4.stem
                dst = LOCAL_SAVED / mp4.name
                if (not dst.is_file()) or dst.stat().st_size != mp4.stat().st_size:
                    shutil.copy2(mp4, dst)
                    pulled.append(vid)
            local_items = []
            for jp in LOCAL_SAVED.glob("*.json"):
                try:
                    m = json.loads(jp.read_text(encoding="utf-8"))
                except json.JSONDecodeError:
                    continue
                lp = LOCAL_SAVED / f"{m.get('video_id')}.mp4"
                if lp.is_file():
                    m["local_path"] = str(lp)
                    m["size_bytes"] = lp.stat().st_size
                    m["transfer_ok"] = True
                    local_items.append(m)
            _save_index(local_items)
            return {"ok": True, "pulled": pulled, "errors": errors, "count": len(local_items)}

        # SSH/SCP path
        tmp_index = LOCAL_SHORTS / "_remote_index.json"
        try:
            cmd = [
                self.scp_bin(),
                "-o",
                "BatchMode=yes",
                "-o",
                "ConnectTimeout=8",
                f"{host}:{REMOTE_SHORTS}/index.json",
                str(tmp_index),
            ]
            subprocess.run(cmd, check=False, capture_output=True, text=True, timeout=30)
        except Exception as e:
            errors.append(f"scp index: {e}")
        items = []
        if tmp_index.is_file():
            try:
                items = json.loads(tmp_index.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                items = []
        # list remote saved via ssh
        try:
            listing = self.ssh_run(
                f"ls -1 {REMOTE_SHORTS}/saved/*.mp4 2>/dev/null | xargs -n1 basename 2>/dev/null || true",
                timeout=20,
            )
            remote_names = [x.strip() for x in (listing or "").splitlines() if x.strip().endswith(".mp4")]
        except Exception as e:
            remote_names = []
            errors.append(str(e))

        by_id = {str(m.get("video_id")): m for m in (items or []) if m.get("video_id")}
        for name in remote_names:
            vid = name[:-4]
            dst = LOCAL_SAVED / name
            need = (not dst.is_file())
            remote_size = 0
            try:
                sz_out = self.ssh_run(f"stat -c%s {REMOTE_SHORTS}/saved/{name}", timeout=15)
                remote_size = int((sz_out or "0").strip().split()[0])
            except Exception:
                remote_size = 0
            if dst.is_file() and remote_size and dst.stat().st_size != remote_size:
                need = True
            if need:
                try:
                    cmd = [
                        self.scp_bin(),
                        "-o",
                        "BatchMode=yes",
                        "-o",
                        "ConnectTimeout=12",
                        f"{host}:{REMOTE_SHORTS}/saved/{name}",
                        str(dst),
                    ]
                    r = subprocess.run(cmd, capture_output=True, text=True, timeout=180)
                    if r.returncode != 0:
                        errors.append(f"scp {name}: {r.stderr[-200:]}")
                        continue
                except Exception as e:
                    errors.append(f"scp {name}: {e}")
                    continue
            # meta json
            meta_dst = LOCAL_SAVED / f"{vid}.json"
            try:
                subprocess.run(
                    [
                        self.scp_bin(),
                        "-o",
                        "BatchMode=yes",
                        f"{host}:{REMOTE_SHORTS}/saved/{vid}.json",
                        str(meta_dst),
                    ],
                    capture_output=True,
                    timeout=30,
                )
            except Exception:
                pass
            meta = by_id.get(vid) or {}
            if meta_dst.is_file():
                try:
                    meta = json.loads(meta_dst.read_text(encoding="utf-8"))
                except json.JSONDecodeError:
                    pass
            if not dst.is_file():
                errors.append(f"local missing after scp: {vid}")
                continue
            local_size = dst.stat().st_size
            transfer_ok = (remote_size == 0) or (local_size == remote_size and local_size > 1000)
            if not transfer_ok:
                errors.append(f"size mismatch {vid}: local={local_size} remote={remote_size}")
            meta["video_id"] = vid
            meta["local_path"] = str(dst)
            meta["remote_path"] = f"{REMOTE_SHORTS}/saved/{name}"
            meta["size_bytes"] = local_size
            meta["sha256_prefix"] = _file_sha256(dst, max_bytes=1024 * 1024)[:16]
            meta["transfer_ok"] = transfer_ok
            by_id[vid] = meta
            if transfer_ok:
                pulled.append(vid)
            meta_dst.write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")

        local_items = list(by_id.values())
        # include already-local only
        for jp in LOCAL_SAVED.glob("*.json"):
            try:
                m = json.loads(jp.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                continue
            vid = str(m.get("video_id") or jp.stem)
            if vid not in by_id and (LOCAL_SAVED / f"{vid}.mp4").is_file():
                m["local_path"] = str(LOCAL_SAVED / f"{vid}.mp4")
                m["transfer_ok"] = True
                local_items.append(m)
        _save_index(local_items)
        return {"ok": len(errors) == 0, "pulled": pulled, "errors": errors, "count": len(local_items)}

    def list_videos(self) -> dict[str, Any]:
        items = _load_index()
        # refresh from disk
        if not items:
            for jp in LOCAL_SAVED.glob("*.json"):
                try:
                    items.append(json.loads(jp.read_text(encoding="utf-8")))
                except json.JSONDecodeError:
                    pass
        return {"ok": True, "videos": items}

    def play_path(self, video_id: str) -> dict[str, Any]:
        path = LOCAL_SAVED / f"{video_id}.mp4"
        if not path.is_file():
            return {"ok": False, "error": "local file missing — sync first"}
        try:
            if os.name == "nt":
                os.startfile(str(path))  # type: ignore[attr-defined]
            elif sys_platform_is_darwin():
                subprocess.Popen(["open", str(path)])
            else:
                subprocess.Popen(["xdg-open", str(path)])
        except Exception as e:
            return {"ok": False, "error": str(e), "path": str(path)}
        return {"ok": True, "path": str(path)}

    def delete_video(self, video_id: str) -> dict[str, Any]:
        vid = (video_id or "").strip()
        local_err = ""
        remote_err = ""
        for p in (LOCAL_SAVED / f"{vid}.mp4", LOCAL_SAVED / f"{vid}.json"):
            try:
                if p.is_file():
                    p.unlink()
            except OSError as e:
                local_err = str(e)
        items = [x for x in _load_index() if str(x.get("video_id")) != vid]
        _save_index(items)
        try:
            self.ssh_run(
                f"rm -f {REMOTE_SHORTS}/saved/{vid}.mp4 {REMOTE_SHORTS}/saved/{vid}.json; "
                f"cd ~/lab_work_space/forest_survival && "
                f"python3 -c \"import json;from pathlib import Path;p=Path('results/shorts/index.json');"
                f"d=json.loads(p.read_text()) if p.is_file() else [];"
                f"d=[x for x in d if x.get('video_id')!='{vid}'];"
                f"p.write_text(json.dumps(d,ensure_ascii=False,indent=2))\"",
                timeout=25,
            )
        except Exception as e:
            remote_err = str(e)
        ok = not local_err and not remote_err
        return {
            "ok": ok,
            "partial_failure": bool(remote_err) and not local_err,
            "local_error": local_err,
            "remote_error": remote_err,
            "video_id": vid,
        }

    def disk_stats(self, *, force: bool = False) -> dict[str, Any]:
        now = time.time()
        if not force and self._disk_cache["data"] and now - float(self._disk_cache["t"]) < 60.0:
            return dict(self._disk_cache["data"])
        local = disk_stats_local()
        lab = {"error": "unavailable"}
        try:
            out = self.ssh_run(
                "python3 - <<'PY'\n"
                "import json,shutil\n"
                "from pathlib import Path\n"
                "p=Path.home()/('lab_work_space/forest_survival/results/shorts')\n"
                "p.mkdir(parents=True,exist_ok=True)\n"
                "u=shutil.disk_usage(str(p))\n"
                "folder=sum(x.stat().st_size for x in p.rglob('*') if x.is_file())\n"
                "print(json.dumps({'total':u.total,'used':u.used,'free':u.free,"
                "'shorts_folder_size':folder,'path':str(p)}))\n"
                "PY",
                timeout=25,
            )
            lab = json.loads((out or "").strip().splitlines()[-1])
        except Exception as e:
            lab = {"error": str(e)}
        data = {"ok": True, "local": local, "lab": lab, "refreshed_at": now}
        self._disk_cache = {"t": now, "data": data}
        return data

    def set_stream_mode(self, mode: str) -> dict[str, Any]:
        m = (mode or "autonomous").strip().lower()
        if m not in ("autonomous", "hosted", "test"):
            return {"ok": False, "error": "mode must be autonomous|hosted|test"}
        remote = (
            f"cd ~/lab_work_space/forest_survival && mkdir -p results && "
            f"echo {m} > results/stream_mode.txt && "
            f"echo {m}"
        )
        try:
            out = self.ssh_run(remote, timeout=15)
            return {"ok": True, "stream_mode": (out or m).strip()}
        except Exception as e:
            return {"ok": False, "error": str(e)}

    def get_stream_mode(self) -> dict[str, Any]:
        try:
            out = self.ssh_run(
                "cat ~/lab_work_space/forest_survival/results/stream_mode.txt 2>/dev/null || echo autonomous",
                timeout=15,
            )
            mode = (out or "autonomous").strip().splitlines()[-1].strip().lower()
            if mode not in ("autonomous", "hosted", "test"):
                mode = "autonomous"
            return {"ok": True, "stream_mode": mode}
        except Exception as e:
            return {"ok": False, "stream_mode": "autonomous", "error": str(e)}


def sys_platform_is_darwin() -> bool:
    return __import__("sys").platform == "darwin"
