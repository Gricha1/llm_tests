#!/usr/bin/env python3
"""Локальный UI: билд+синк и обучение Jack/Lily/George на lab_comp.

Запуск (из корня репо, WSL):
  python3 train_scripts/lab_comp/train_lab_ui.py
  → http://127.0.0.1:8877
  (порт не 8765 — тот под другой UI; свой: FOREST_UI_PORT=....)
"""
from __future__ import annotations

import html
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

HOST = os.environ.get("FOREST_UI_HOST", "0.0.0.0")
PORT = int(os.environ.get("FOREST_UI_PORT", "8877"))
ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
# lab_comp scripts dir for llm_bot_manager
_LAB_SCRIPTS = Path(__file__).resolve().parent
if str(_LAB_SCRIPTS) not in sys.path:
    sys.path.insert(0, str(_LAB_SCRIPTS))

CFG_PATH = ROOT / ".train_lab_ui.json"
LOG_DIR = ROOT / ".train_lab_ui" / "logs"
SS_PREVIEW_DIR = ROOT / ".train_lab_ui" / "ss_preview"
SS_PREVIEW_FRAME = SS_PREVIEW_DIR / "frame.jpg"
REMOTE_DIR = "~/lab_work_space/forest_survival"
DEFAULT_BUILD = "stream_forest_survival_2_12_07_2026"
DEFAULT_TB = "http://10.43.71.7:6006"

DEFAULT_CFG: dict[str, Any] = {
    "tb_url": DEFAULT_TB,
    "ssh_host": "lab_comp",
    "build": DEFAULT_BUILD,
    "jack": {
        "run_s1": "97",
        "run_s2": "97_stage2",
        "tb_s1": "97/JackLowLevelAgent",
        "tb_s2": "97_stage2/JackLowLevelAgent",
    },
    "lily": {
        "run_s1": "lily_1",
        "run_s2": "lily_1_stage2",
        "tb_s1": "",
        "tb_s2": "",
    },
    "george": {
        "run_s1": "george_1",
        "run_s2": "george_1_stage2",
        "tb_s1": "",
        "tb_s2": "",
    },
    "joint": {
        "init_jack": "97_stage2",
        "init_lily": "lily_1_stage2",
        "init_george": "george_1_stage2",
        "run_id": "jlg_finetune_1",
        "tb_run": "",
        "tb_runs": [],
    },
    "chart_tags": [
        "Environment/Cumulative Reward",
    ],
    "detail_chart_blocks": [
        {
            "title": "Environment",
            "tags": [
                "Environment/Cumulative Reward",
                "EpisodeReward",
                "Environment/Episode Length",
            ],
        },
        {
            "title": "Success rates",
            "tags": [
                "SuccessRate/Mean",
                "SuccessRate/Wood",
                "SuccessRate/WoodDeliver",
                "SuccessRate/Sheep",
                "SuccessRate/Water",
                "SuccessRate/Fire",
                "SuccessRate/Flower",
                "SuccessRate/ZombieCount",
            ],
        },
        {
            "title": "Train",
            "tags": [
                "Policy/Epsilon",
                "Policy/Beta",
                "Policy/Learning Rate",
                "Losses/Policy Loss",
                "Losses/Value Loss",
                "Policy/Extrinsic Value Estimate",
                "Policy/Extrinsic Reward",
                "Policy/Entropy",
            ],
        },
    ],
}

_cfg_lock = threading.Lock()
_jobs_lock = threading.Lock()
_jobs: dict[str, dict[str, Any]] = {}
_ssh_host_cache: str | None = None
_ssh_gate = threading.Semaphore(2)  # не больше 2 параллельных ssh — иначе UI на Windows клинит
_ssh_active_lock = threading.Lock()
# pid -> {proc, started, remote, host}
_ssh_active: dict[int, dict[str, Any]] = {}
_ssh_reaper_started = False
_SSH_STALE_SEC = 35.0  # висячий ssh старше этого — kill
_stats_lock = threading.Lock()
_stats_cache: dict[str, Any] = {"t": 0.0, "data": None}
_stats_inflight = False
_STATS_CACHE_TTL = 6.0
_STATS_STALE_MAX = 90.0
_ss_sync_lock = threading.Lock()
_ss_sync_last_at = 0.0
_SS_SYNC_INTERVAL = 25.0
_EMPTY_ACTIVITY = {
    "jack": False,
    "lily": False,
    "george": False,
    "joint": False,
    "validate": False,
    "stream": False,
    "streaming_survival": False,
    "llm_bot": False,
    "tasks": [],
}


# ── config ──────────────────────────────────────────────────────────


def load_cfg() -> dict[str, Any]:
    cfg = json.loads(json.dumps(DEFAULT_CFG))
    if CFG_PATH.is_file():
        try:
            raw = json.loads(CFG_PATH.read_text(encoding="utf-8"))
            if isinstance(raw, dict):
                deep_merge(cfg, raw)
        except Exception:
            pass
    return cfg


def save_cfg(cfg: dict[str, Any]) -> None:
    CFG_PATH.write_text(json.dumps(cfg, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def deep_merge(dst: dict, src: dict) -> None:
    for k, v in src.items():
        if isinstance(v, dict) and isinstance(dst.get(k), dict):
            deep_merge(dst[k], v)
        else:
            dst[k] = v


# ── ssh / shell ─────────────────────────────────────────────────────

# Сначала ssh-config alias (обычно живой), потом запасные IP.
SSH_FALLBACK_HOSTS = (
    "lab_comp",
    "reedgern@192.168.194.7",
    "reedgern@10.43.71.7",
    "lab_comp_local",
    "reedgern@192.168.50.18",
)
_LAB_SELF_IPS = frozenset(
    {
        "192.168.194.7",
        "10.43.71.7",
        "192.168.50.18",
    }
)
_SSH_HOST_FILE = ROOT / ".train_lab_ui_ssh_host"
_ui_local_mode: bool | None = None


def ui_runs_on_lab() -> bool:
    """True when this UI process already runs ON lab_comp.

    In that case Windows→lab SSH keys are irrelevant: the UI must not SSH to
    itself (BatchMode → Permission denied). Run shell commands locally instead.
    """
    global _ui_local_mode
    if _ui_local_mode is not None:
        return _ui_local_mode
    env = (os.environ.get("FOREST_UI_LOCAL") or "").strip().lower()
    if env in ("1", "true", "yes", "on"):
        _ui_local_mode = True
        return True
    if env in ("0", "false", "no", "off") or (
        os.environ.get("FOREST_UI_FORCE_SSH") or ""
    ).strip().lower() in ("1", "true", "yes"):
        _ui_local_mode = False
        return False
    try:
        root_s = str(ROOT).replace("\\", "/")
        if "/lab_work_space/forest_survival" in root_s or root_s.endswith(
            "/lab_work_space/forest_survival"
        ):
            _ui_local_mode = True
            return True
    except Exception:
        pass
    try:
        import socket

        hn = socket.gethostname().lower()
        if "server1" in hn or hn in ("lab_comp", "labcomp"):
            _ui_local_mode = True
            return True
        ips: set[str] = set()
        try:
            for info in socket.getaddrinfo(socket.gethostname(), None):
                ips.add(info[4][0])
        except Exception:
            pass
        # Also common primary interface guess
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("8.8.8.8", 80))
            ips.add(s.getsockname()[0])
            s.close()
        except Exception:
            pass
        if ips & _LAB_SELF_IPS:
            _ui_local_mode = True
            return True
    except Exception:
        pass
    _ui_local_mode = False
    return False


def ssh_candidates(preferred: str | None = None) -> list[str]:
    out: list[str] = []
    for h in (preferred, _ssh_host_cache, *SSH_FALLBACK_HOSTS):
        if h and h not in out:
            out.append(h)
    return out


def _ssh_bins() -> list[str]:
    bins: list[str] = []
    env_bin = os.environ.get("FOREST_UI_SSH_BIN")
    if env_bin:
        bins.append(env_bin)
    if shutil.which("ssh.exe"):
        bins.append("ssh.exe")
    if shutil.which("ssh"):
        bins.append("ssh")
    # unique preserve order
    seen: set[str] = set()
    uniq: list[str] = []
    for b in bins:
        if b not in seen:
            seen.add(b)
            uniq.append(b)
    return uniq or ["ssh"]


def _ssh_probe(bin_: str, host: str, connect_timeout: int = 6) -> bool:
    try:
        r = subprocess.run(
            [
                bin_,
                "-o", "BatchMode=yes",
                "-o", f"ConnectTimeout={connect_timeout}",
                "-o", "ConnectionAttempts=1",
                "-o", "ServerAliveInterval=3",
                "-o", "ServerAliveCountMax=2",
                host,
                "true",
            ],
            capture_output=True,
            timeout=connect_timeout + 6,
        )
        return r.returncode == 0
    except Exception:
        return False


def _load_saved_ssh_host() -> str | None:
    try:
        if _SSH_HOST_FILE.is_file():
            h = _SSH_HOST_FILE.read_text(encoding="utf-8").strip()
            return h or None
    except Exception:
        pass
    return None


def _save_ssh_host(host: str) -> None:
    try:
        _SSH_HOST_FILE.write_text(host.strip() + "\n", encoding="utf-8")
    except Exception:
        pass


def clear_ssh_host_cache() -> None:
    global _ssh_host_cache
    _ssh_host_cache = None


def resolve_ssh_host(preferred: str = "lab_comp", *, force: bool = False) -> str:
    """Пробует alias + оба IP (ZT / 194). Не кэширует мёртвый хост."""
    global _ssh_host_cache
    if ui_runs_on_lab():
        _ssh_host_cache = "local"
        return "local"
    if _ssh_host_cache and not force:
        return _ssh_host_cache
    saved = _load_saved_ssh_host()
    if saved == "local" and ui_runs_on_lab():
        _ssh_host_cache = "local"
        return "local"
    if saved and saved != "local" and not force and _ssh_probe(ssh_bin(), saved, connect_timeout=4):
        _ssh_host_cache = saved
        return saved
    for bin_ in _ssh_bins():
        for host in ssh_candidates(preferred):
            if host == "local":
                continue
            if _ssh_probe(bin_, host):
                _ssh_host_cache = host
                _save_ssh_host(host)
                os.environ["FOREST_UI_SSH_BIN"] = bin_
                return host
    # Не кэшируем preferred при полном фейле — иначе второй IP больше не пробуется.
    raise RuntimeError(
        "SSH: не достучались ни до одного адреса lab_comp "
        f"({', '.join(ssh_candidates(preferred))}). "
        "Если UI уже запущен НА lab_comp — поставь FOREST_UI_LOCAL=1 "
        "(или обнови train_lab_ui.py: локальный режим без SSH к себе)."
    )


def ssh_bin() -> str:
    return os.environ.get("FOREST_UI_SSH_BIN") or ("ssh.exe" if shutil.which("ssh.exe") else "ssh")


def scp_bin() -> str:
    return os.environ.get("FOREST_UI_SCP_BIN") or ("scp.exe" if shutil.which("scp.exe") else "scp")


def _ssh_common_opts() -> list[str]:
    """Быстрее рвём мёртвые коннекты, без долгих retry."""
    return [
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=5",
        "-o", "ConnectionAttempts=1",
        "-o", "ServerAliveInterval=3",
        "-o", "ServerAliveCountMax=2",
        "-o", "StrictHostKeyChecking=accept-new",
    ]


def _kill_ssh_proc(proc: subprocess.Popen) -> None:
    """Жёстко гасим ssh + дочерние (на Windows иначе остаются висячие ssh.exe)."""
    if proc is None:
        return
    try:
        if proc.poll() is not None:
            return
    except Exception:
        return
    pid = getattr(proc, "pid", None)
    try:
        proc.kill()
    except Exception:
        pass
    if pid and os.name == "nt":
        try:
            subprocess.run(
                ["taskkill", "/F", "/T", "/PID", str(pid)],
                capture_output=True,
                timeout=5,
            )
        except Exception:
            pass
    else:
        try:
            if pid:
                os.killpg(pid, 9)  # type: ignore[attr-defined]
        except Exception:
            try:
                proc.kill()
            except Exception:
                pass
    try:
        proc.wait(timeout=2)
    except Exception:
        pass


def cleanup_stale_ssh(max_age: float | None = None) -> int:
    """Убивает висячие SSH, запущенные этим UI. Возвращает число убитых."""
    age = float(_SSH_STALE_SEC if max_age is None else max_age)
    now = time.time()
    killed = 0
    with _ssh_active_lock:
        items = list(_ssh_active.items())
    for pid, meta in items:
        proc = meta.get("proc")
        started = float(meta.get("started") or 0)
        try:
            alive = proc is not None and proc.poll() is None
        except Exception:
            alive = False
        if not alive:
            with _ssh_active_lock:
                _ssh_active.pop(pid, None)
            continue
        if now - started < age:
            continue
        _kill_ssh_proc(proc)
        with _ssh_active_lock:
            _ssh_active.pop(pid, None)
        killed += 1
    return killed


def _ensure_ssh_reaper() -> None:
    global _ssh_reaper_started
    if _ssh_reaper_started:
        return
    _ssh_reaper_started = True

    def loop() -> None:
        while True:
            try:
                cleanup_stale_ssh()
            except Exception:
                pass
            time.sleep(8.0)

    threading.Thread(target=loop, name="ssh-stale-reaper", daemon=True).start()


def _ssh_popen(host: str, remote_cmd: str) -> subprocess.Popen:
    kwargs: dict[str, Any] = {
        "stdout": subprocess.PIPE,
        "stderr": subprocess.PIPE,
        "text": True,
        "encoding": "utf-8",
        "errors": "replace",
    }
    if os.name != "nt":
        kwargs["start_new_session"] = True
    return subprocess.Popen(
        [ssh_bin(), *_ssh_common_opts(), host, remote_cmd],
        **kwargs,
    )


def _ssh_communicate(proc: subprocess.Popen, timeout: float | None) -> subprocess.CompletedProcess:
    pid = int(proc.pid)
    # Never pass 0 — subprocess reports "timed out after 0.0 seconds" and looks broken.
    eff_timeout: float | None
    if timeout is None:
        eff_timeout = None
    else:
        eff_timeout = max(3.0, float(timeout))
    with _ssh_active_lock:
        _ssh_active[pid] = {
            "proc": proc,
            "started": time.time(),
            "host": "",
            "remote": "",
        }
    try:
        try:
            out, err = proc.communicate(timeout=eff_timeout)
        except subprocess.TimeoutExpired as e:
            _kill_ssh_proc(proc)
            try:
                out, err = proc.communicate(timeout=2)
            except Exception:
                out, err = "", ""
            raise subprocess.TimeoutExpired(e.cmd, eff_timeout or 0, output=out, stderr=err) from None
        return subprocess.CompletedProcess(
            proc.args, proc.returncode if proc.returncode is not None else -1, out, err
        )
    finally:
        with _ssh_active_lock:
            _ssh_active.pop(pid, None)


def _is_ssh_connect_error(r: subprocess.CompletedProcess | None, exc: BaseException | None = None) -> bool:
    text = ""
    if r is not None:
        text = ((r.stderr or "") + "\n" + (r.stdout or "")).lower()
    if exc is not None:
        text += "\n" + str(exc).lower()
    keys = (
        "timed out",
        "timeout",
        "connection refused",
        "connection timed out",
        "no route to host",
        "network is unreachable",
        "could not resolve hostname",
        "name or service not known",
        "connection reset by peer",
        "connection closed by remote host",
        "kex_exchange_identification",
        "banner exchange",
        "ssh_exchange_identification",
    )
    return any(k in text for k in keys)


def _local_shell_run(
    remote_cmd: str, timeout: float | None = 120
) -> subprocess.CompletedProcess:
    """Run a lab shell snippet locally (UI already on lab_comp)."""
    global _ssh_host_cache
    _ssh_host_cache = "local"
    # Expand ~/ for login-shell consistency with remote ssh snippets.
    cmd = f"cd {sh_quote(str(ROOT))} && {remote_cmd}"
    return subprocess.run(
        ["bash", "-lc", cmd],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=None if timeout is None else max(3.0, float(timeout)),
    )


def ssh_run(
    host: str | None,
    remote_cmd: str,
    timeout: float | None = 120,
    *,
    preferred: str | None = None,
    gate_timeout: float = 12.0,
    skip_if_busy: bool = False,
) -> subprocess.CompletedProcess:
    """SSH с failover: при timeout/refusal пробует остальные IP lab_comp.

    Если UI уже на lab — выполняет команду локально (без SSH к себе).
    Висячие ssh убиваются по timeout процесса и фоновым reaper'ом.
    skip_if_busy=True — для частых health-probe: не ждать слот, а сразу выйти.
    """
    global _ssh_host_cache
    if ui_runs_on_lab() or host == "local":
        try:
            return _local_shell_run(remote_cmd, timeout=timeout)
        except subprocess.TimeoutExpired as e:
            raise TimeoutError(f"local lab cmd timeout: {e}") from e

    _ensure_ssh_reaper()
    cleanup_stale_ssh()

    preferred = preferred or "lab_comp"
    try:
        preferred = (load_cfg().get("ssh_host") or preferred)
    except Exception:
        pass
    hosts: list[str] = []
    if host and host != "local":
        hosts.append(host)
    for h in ssh_candidates(preferred):
        if h and h != "local" and h not in hosts:
            hosts.append(h)

    last: subprocess.CompletedProcess | None = None
    last_exc: BaseException | None = None
    tried: list[str] = []

    got_gate = _ssh_gate.acquire(timeout=max(0.05, float(gate_timeout)))
    if not got_gate:
        cleanup_stale_ssh(max_age=12.0)  # агрессивнее чистим, если слот занят
        got_gate = _ssh_gate.acquire(timeout=2.0 if not skip_if_busy else 0.05)
    if not got_gate:
        raise TimeoutError("SSH занят (слишком много параллельных запросов к lab_comp)")
    try:
        for h in hosts:
            tried.append(h)
            proc: subprocess.Popen | None = None
            try:
                proc = _ssh_popen(h, remote_cmd)
                r = _ssh_communicate(proc, timeout)
                last = r
                if r.returncode == 0:
                    _ssh_host_cache = h
                    _save_ssh_host(h)
                    return r
                if not _is_ssh_connect_error(r):
                    # Команда на сервере упала — хост живой, не крутим IP.
                    _ssh_host_cache = h
                    _save_ssh_host(h)
                    return r
                clear_ssh_host_cache()
            except Exception as e:
                last_exc = e
                if proc is not None:
                    _kill_ssh_proc(proc)
                if not _is_ssh_connect_error(None, e):
                    raise
                clear_ssh_host_cache()
                continue
    finally:
        _ssh_gate.release()

    if last is not None:
        return last
    raise RuntimeError(f"SSH fail after {tried}: {last_exc}")


def sh_quote(s: str) -> str:
    return "'" + s.replace("'", "'\"'\"'") + "'"


try:
    from llm_bot_manager import LabBotManager

    LLM_BOT = LabBotManager(
        ssh_run=ssh_run,
        resolve_ssh_host=resolve_ssh_host,
        load_cfg=load_cfg,
        remote_dir=REMOTE_DIR,
        sh_quote=sh_quote,
    )
    _LLM_BOT_IMPORT_ERROR = ""
    _ensure_ssh_reaper()
except Exception as _llm_imp_err:  # pragma: no cover
    LLM_BOT = None  # type: ignore
    _LLM_BOT_IMPORT_ERROR = str(_llm_imp_err)
    _ensure_ssh_reaper()


class SsLabPreview:
    """Live screenshot of lab_comp DISPLAY=:1 (Streaming Survival Unity window)."""

    CAPTURE_CMD = (
        "DISPLAY=:1 import -window root -resize 960x -quality 50 jpeg:- 2>/dev/null"
    )

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()
        self._running = False
        self._last_ok_at = 0.0
        self._last_err = ""
        self._frames = 0
        self._interval = 1.6
        self._display = ":1"
        SS_PREVIEW_DIR.mkdir(parents=True, exist_ok=True)

    def status(self) -> dict[str, Any]:
        with self._lock:
            sz = SS_PREVIEW_FRAME.stat().st_size if SS_PREVIEW_FRAME.is_file() else 0
            return {
                "running": self._running,
                "display": self._display,
                "interval_sec": self._interval,
                "frames": self._frames,
                "last_ok_at": self._last_ok_at,
                "last_error": self._last_err,
                "frame_bytes": sz,
                "frame_url": "/api/ss_preview/frame.jpg",
                "has_frame": sz > 1000,
            }

    def start(self, interval_sec: float = 1.6) -> dict[str, Any]:
        with self._lock:
            self._interval = max(0.8, min(5.0, float(interval_sec or 1.6)))
            if self._running:
                return self.status()
            self._stop.clear()
            self._running = True
            self._last_err = ""
            self._thread = threading.Thread(
                target=self._loop, name="ss-lab-preview", daemon=True
            )
            self._thread.start()
        # Immediate first frame (best-effort)
        try:
            self.capture_once()
        except Exception as e:
            with self._lock:
                self._last_err = str(e)
        return self.status()

    def stop(self) -> dict[str, Any]:
        with self._lock:
            self._stop.set()
            self._running = False
            th = self._thread
        if th and th.is_alive():
            th.join(timeout=3.0)
        with self._lock:
            self._thread = None
        return self.status()

    def _loop(self) -> None:
        while not self._stop.is_set():
            try:
                self.capture_once()
            except Exception as e:
                with self._lock:
                    self._last_err = str(e)
            self._stop.wait(self._interval)
        with self._lock:
            self._running = False

    def capture_once(self) -> Path:
        data = self._ssh_capture_jpeg()
        if len(data) < 800 or data[:2] != b"\xff\xd8":
            raise RuntimeError(
                f"bad jpeg from lab ({len(data)} bytes) — is DISPLAY=:1 / Unity up?"
            )
        tmp = SS_PREVIEW_DIR / "frame.jpg.tmp"
        tmp.write_bytes(data)
        tmp.replace(SS_PREVIEW_FRAME)
        with self._lock:
            self._frames += 1
            self._last_ok_at = time.time()
            self._last_err = ""
        return SS_PREVIEW_FRAME

    def _ssh_capture_jpeg(self) -> bytes:
        """Binary capture of DISPLAY=:1. Local when UI runs on lab_comp."""
        if ui_runs_on_lab() or resolve_ssh_host() == "local":
            proc = subprocess.run(
                ["bash", "-lc", self.CAPTURE_CMD],
                capture_output=True,
                timeout=18,
            )
            out = proc.stdout or b""
            if proc.returncode not in (0, None) and not out:
                msg = (proc.stderr or b"").decode("utf-8", "replace")[:240]
                raise RuntimeError(f"import failed rc={proc.returncode}: {msg}")
            return out

        _ensure_ssh_reaper()
        cleanup_stale_ssh()
        host = resolve_ssh_host()
        got = _ssh_gate.acquire(timeout=4.0)
        if not got:
            raise TimeoutError("SSH занят — preview пропустил кадр")
        try:
            kwargs: dict[str, Any] = {
                "stdout": subprocess.PIPE,
                "stderr": subprocess.PIPE,
            }
            if os.name != "nt":
                kwargs["start_new_session"] = True
            proc = subprocess.Popen(
                [ssh_bin(), *_ssh_common_opts(), host, self.CAPTURE_CMD],
                **kwargs,
            )
            pid = int(proc.pid)
            with _ssh_active_lock:
                _ssh_active[pid] = {
                    "proc": proc,
                    "started": time.time(),
                    "host": host,
                    "remote": "ss_preview_import",
                }
            try:
                out, err = proc.communicate(timeout=18)
            except subprocess.TimeoutExpired:
                _kill_ssh_proc(proc)
                try:
                    out, err = proc.communicate(timeout=2)
                except Exception:
                    out, err = b"", b""
                raise TimeoutError("preview SSH timeout") from None
            finally:
                with _ssh_active_lock:
                    _ssh_active.pop(pid, None)
            if proc.returncode not in (0, None) and not out:
                msg = (err or b"").decode("utf-8", "replace")[:240]
                raise RuntimeError(f"import failed rc={proc.returncode}: {msg}")
            return out or b""
        finally:
            _ssh_gate.release()


SS_PREVIEW = SsLabPreview()


def remote_expand_cmd(path: str) -> str:
    """Раскрыть ~ в абсолютный путь на lab.

    Нельзя eval/echo без кавычек: в bash $_ — спецпеременная, и путь
    .../97_stage2/... превращается в .../97_<мусор>/...
    """
    return "python3 -c " + sh_quote(f"import os; print(os.path.expanduser({path!r}))")


SAFE_RUN_RE = re.compile(r"^[A-Za-z0-9._+-]+$")
VALIDATE_HEROES = ("jack", "lily", "george")
VALIDATE_TASKS_BY_HERO = {
    "jack": ("stream", "wood", "food", "water", "zombie"),
    "lily": ("stream", "food", "water", "heat", "flower"),
    "george": ("stream", "food", "water", "heat"),
}
VALIDATE_TASKS = tuple(sorted({t for ts in VALIDATE_TASKS_BY_HERO.values() for t in ts}))
HERO_CONFIG = {
    "jack": "Jack_single_agent.yaml",
    "lily": "Lily_single_agent.yaml",
    "george": "George_single_agent.yaml",
}
VIDEO_CACHE = ROOT / ".train_lab_ui" / "videos"


def sanitize_run_id(run_id: str) -> str:
    rid = (run_id or "").strip()
    if not rid or not SAFE_RUN_RE.match(rid):
        raise ValueError("RUN_ID: только буквы/цифры/._+-")
    return rid


def sanitize_hero(hero: str) -> str:
    h = (hero or "").strip().lower()
    if h not in VALIDATE_HEROES and h != "all":
        raise ValueError(f"HERO: ожидается {', '.join(VALIDATE_HEROES)}|all")
    return h


def sanitize_task(task: str, hero: str | None = None) -> str:
    t = (task or "").strip().lower()
    if hero:
        h = sanitize_hero(hero)
        allowed = VALIDATE_TASKS_BY_HERO[h]
        if t not in allowed:
            raise ValueError(f"TASK для {h}: ожидается {', '.join(allowed)}")
        return t
    if t not in VALIDATE_TASKS:
        raise ValueError(f"TASK: ожидается {', '.join(VALIDATE_TASKS)}")
    return t


def remote_validate_mp4(run_id: str, task: str, hero: str = "jack") -> str:
    h = sanitize_hero(hero)
    t = sanitize_task(task, h)
    return f"{REMOTE_DIR}/results/{run_id}/videos/ui_val_{h}_{t}.mp4"


def local_validate_mp4(run_id: str, task: str, hero: str = "jack") -> Path:
    h = sanitize_hero(hero)
    t = sanitize_task(task, h)
    return VIDEO_CACHE / f"{run_id}__{h}__{t}.mp4"


# ── jobs ────────────────────────────────────────────────────────────


def new_job(name: str, cmd_display: str) -> str:
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    jid = f"{int(time.time())}_{re.sub(r'[^a-zA-Z0-9_]+', '_', name)[:40]}"
    log_path = LOG_DIR / f"{jid}.log"
    with _jobs_lock:
        _jobs[jid] = {
            "id": jid,
            "name": name,
            "cmd": cmd_display,
            "status": "running",
            "started": time.time(),
            "ended": None,
            "returncode": None,
            "log_path": str(log_path),
            "tmux": None,
        }
    return jid


def append_log(jid: str, text: str) -> None:
    with _jobs_lock:
        job = _jobs.get(jid)
        if not job:
            return
        path = Path(job["log_path"])
    with path.open("a", encoding="utf-8", errors="replace") as f:
        f.write(text)
        if not text.endswith("\n"):
            f.write("\n")


def finish_job(jid: str, code: int) -> None:
    with _jobs_lock:
        job = _jobs.get(jid)
        if not job:
            return
        job["status"] = "ok" if code == 0 else "error"
        job["returncode"] = code
        job["ended"] = time.time()
        job_name = str(job.get("name") or "")
    if job_name.startswith("ss_") and not ui_runs_on_lab():
        threading.Thread(
            target=lambda: _ss_sync_runs_from_lab(),
            name=f"ss-sync-{jid}",
            daemon=True,
        ).start()


def job_snapshot(jid: str, tail: int = 80) -> dict[str, Any] | None:
    with _jobs_lock:
        job = _jobs.get(jid)
        if not job:
            return None
        data = dict(job)
    path = Path(data["log_path"])
    lines: list[str] = []
    if path.is_file():
        try:
            lines = path.read_text(encoding="utf-8", errors="replace").splitlines()[-tail:]
        except Exception:
            lines = []
    data["log_tail"] = "\n".join(lines)
    return data


def start_local_job(name: str, argv: list[str], cwd: Path | None = None, env: dict | None = None) -> str:
    jid = new_job(name, " ".join(argv))
    log_path = Path(_jobs[jid]["log_path"])

    def runner() -> None:
        append_log(jid, f"$ {' '.join(argv)}")
        try:
            p = subprocess.Popen(
                argv,
                cwd=str(cwd or ROOT),
                env=env,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
            )
            assert p.stdout is not None
            for line in p.stdout:
                append_log(jid, line.rstrip("\n"))
            code = p.wait()
            finish_job(jid, code)
            append_log(jid, f"[exit {code}]")
        except Exception as e:
            append_log(jid, f"[ERROR] {e}")
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def start_remote_detached(name: str, host: str, session: str, remote_bash: str) -> str:
    """Старт train на сервере независимо от UI.

    setsid+nohup: закрытие UI / обрыв SSH не шлёт SIGHUP и не роняет обучение.
    Локальный поток только читает лог; при смерти UI remote продолжает работать.
    """
    jid = new_job(name, remote_bash)
    log_remote = f"/tmp/forest_ui_{session}.log"
    pid_remote = f"/tmp/forest_ui_{session}.pid"
    with _jobs_lock:
        _jobs[jid]["tmux"] = session
        _jobs[jid]["remote_log"] = log_remote
        _jobs[jid]["remote_pid"] = pid_remote

    # Новый запуск той же кнопки сменяет предыдущий train этого слота.
    # Закрытие UI сюда не попадает — только явный повторный Start.
    remote = f"""
set -eu
cd {REMOTE_DIR}
LOG={sh_quote(log_remote)}
PIDF={sh_quote(pid_remote)}
if [ -f "$PIDF" ]; then
  old="$(tr -d ' \\r\\n' <"$PIDF" || true)"
  if [ -n "${{old:-}}" ] && kill -0 "$old" 2>/dev/null; then
    echo "[forest_ui] stop previous pid=$old (same slot {session})"
    kill -TERM "$old" 2>/dev/null || true
    sleep 2
    kill -KILL "$old" 2>/dev/null || true
  fi
fi
tmux has-session -t {sh_quote(session)} 2>/dev/null && tmux kill-session -t {sh_quote(session)} || true
rm -f "$LOG" "$PIDF"
touch "$LOG"
# Полный detach от SSH-сессии UI:
setsid nohup bash -lc {sh_quote(remote_bash + "; echo EXIT:$?")} >>"$LOG" 2>&1 </dev/null &
echo $! >"$PIDF"
# tmux только для удобного tail (не parent train-процесса)
tmux new-session -d -s {sh_quote(session)} "tail -n +1 -F $LOG" 2>/dev/null || true
echo STARTED pid="$(cat "$PIDF")" log="$LOG" session={session}
echo DETACHED=1
"""

    def runner() -> None:
        append_log(jid, f"[ssh {host}] detached slot={session}")
        append_log(jid, remote_bash)
        append_log(jid, "[note] закрытие UI не останавливает этот train на сервере")
        try:
            r = ssh_run(host, remote, timeout=60)
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            if r.returncode != 0:
                finish_job(jid, r.returncode)
                return
            log_path = Path(_jobs[jid]["log_path"])
            while True:
                check = ssh_run(
                    host,
                    f"PIDF={sh_quote(pid_remote)}; LOG={sh_quote(log_remote)}; "
                    f"alive=0; "
                    f"if [ -f \"$PIDF\" ]; then p=$(tr -d ' \\r\\n' <\"$PIDF\"); "
                    f"kill -0 \"$p\" 2>/dev/null && alive=1; fi; "
                    f"echo ALIVE:$alive; "
                    f"cat \"$LOG\" 2>/dev/null || true",
                    timeout=45,
                )
                out = check.stdout or ""
                m = re.search(r"ALIVE:(\d+)", out)
                body = re.sub(r"ALIVE:\d+\s*", "", out, count=1)
                header = (
                    f"[ssh {host}] detached:{session}\n{remote_bash}\n"
                    f"[note] UI follow only — remote survives UI close\n\n"
                )
                log_path.write_text(header + body, encoding="utf-8", errors="replace")
                if m and m.group(1) == "0":
                    code = 0 if "EXIT:0" in body else 1
                    finish_job(jid, code)
                    append_log(jid, f"[remote ended exit≈{code}]")
                    return
                time.sleep(4)
        except Exception as e:
            # Ошибка follow/SSH ≠ остановка train. Не трогаем remote.
            append_log(jid, f"[follow interrupted] {e}")
            append_log(jid, "[note] train на сервере мог продолжать работать (nohup)")
            with _jobs_lock:
                job = _jobs.get(jid)
                if job and job.get("status") == "running":
                    job["status"] = "detached"
                    job["ended"] = None

    threading.Thread(target=runner, daemon=True).start()
    return jid


# backward-compatible alias
start_remote_tmux = start_remote_detached

# ── tensorboard proxy ───────────────────────────────────────────────


_tb_prefer_ssh = False


def tb_get(base: str, path: str, timeout: float = 20) -> Any:
    global _tb_prefer_ssh
    url = base.rstrip("/") + path

    def via_ssh() -> Any:
        host = _ssh_host_cache or resolve_ssh_host_fast()
        if not host:
            raise RuntimeError("нет ssh к lab_comp для TensorBoard")
        remote_url = "http://127.0.0.1:6006" + path
        r = ssh_run(host, f"curl -s --max-time 25 {sh_quote(remote_url)}", timeout=timeout + 10)
        if r.returncode != 0 or not (r.stdout or "").strip():
            raise RuntimeError(f"TB via ssh failed: {(r.stderr or r.stdout or '')[:300]}")
        return json.loads(r.stdout)

    if _tb_prefer_ssh:
        return via_ssh()

    try:
        req = urllib.request.Request(url, headers={"User-Agent": "train_lab_ui"})
        with urllib.request.urlopen(req, timeout=2) as resp:
            return json.load(resp)
    except Exception:
        _tb_prefer_ssh = True
        return via_ssh()


def resolve_ssh_host_fast() -> str | None:
    """Быстрый probe всех IP; None если никто не отвечает."""
    global _ssh_host_cache
    if _ssh_host_cache:
        return _ssh_host_cache
    preferred = "lab_comp"
    try:
        preferred = load_cfg().get("ssh_host") or preferred
    except Exception:
        pass
    for bin_ in _ssh_bins():
        for host in ssh_candidates(preferred):
            if _ssh_probe(bin_, host, connect_timeout=4):
                _ssh_host_cache = host
                os.environ["FOREST_UI_SSH_BIN"] = bin_
                return host
    return None


def list_tb_runs(tb_url: str) -> list[str]:
    try:
        tags = tb_get(tb_url, "/data/plugin/scalars/tags")
        return sorted(tags.keys())
    except Exception:
        return []


def list_result_dirs(host: str) -> list[str]:
    try:
        r = ssh_run(host, f"ls -1 {REMOTE_DIR}/results 2>/dev/null | head -200", timeout=30)
        if r.returncode != 0:
            return []
        return [ln.strip() for ln in (r.stdout or "").splitlines() if ln.strip()]
    except Exception:
        return []


def _parse_activity_json(raw: str) -> dict[str, Any]:
    empty = dict(_EMPTY_ACTIVITY)
    empty["tasks"] = []
    try:
        data = json.loads(raw) if raw.startswith("{") else empty
    except Exception:
        data = empty
    for k in _EMPTY_ACTIVITY:
        if k == "tasks":
            data["tasks"] = list(data.get("tasks") or [])
        else:
            data[k] = bool(data.get(k))
    data["ok"] = True
    return data


# Remote: RAM + GPU + activity JSON в ОДНОМ ssh (иначе UI висит на двух вызовах).
_REMOTE_STATS_AND_ACTIVITY = r"""
set +e
echo '=== RAM ==='
free -h | awk '/Mem:/{printf "Mem used %s / %s (avail %s)\n", $3, $2, $7}'
free -h | awk '/Swap:/{printf "Swap used %s / %s\n", $3, $2}'
echo
echo '=== nvidia-smi ==='
nvidia-smi --query-gpu=index,name,memory.used,memory.total,utilization.gpu,temperature.gpu --format=csv,noheader,nounits 2>/dev/null \
  | awk -F',' '{gsub(/^ +| +$/,"",$1); gsub(/^ +| +$/,"",$2); gsub(/^ +| +$/,"",$3); gsub(/^ +| +$/,"",$4); gsub(/^ +| +$/,"",$5); gsub(/^ +| +$/,"",$6); printf "GPU%s %s | VRAM %s/%s MiB | util %s%% | temp %sC\n", $1, $2, $3, $4, $5, $6}'
if [ $? -ne 0 ]; then echo '(nvidia-smi недоступен)'; fi
echo
echo '=== top processes (cpu) ==='
ps -eo pid,pcpu,pmem,comm --sort=-pcpu | head -n 8
echo
echo '=== ACTIVITY_JSON ==='
python3 - <<'PY'
import json, os, re
me = str(os.getpid())
parent = str(os.getppid())
cmds = []
for pid in os.listdir("/proc"):
    if not pid.isdigit() or pid in (me, parent):
        continue
    try:
        raw = open("/proc/%s/cmdline" % pid, "rb").read()
    except OSError:
        continue
    if not raw:
        continue
    cmds.append(raw.replace(b"\0", b" ").decode("utf-8", "replace"))

def is_ui_noise(c):
    cl = c.lower()
    if "forest_ui_fui_" in cl and ("tail" in cl or "tmux" in cl):
        return True
    if "os.listdir(\"/proc\")" in c or "ACTIVITY_JSON" in c:
        return True
    return False

def train_yaml(name):
    for c in cmds:
        if is_ui_noise(c):
            continue
        if "mlagents-learn" not in c or name not in c:
            continue
        if "--inference" in c:
            continue
        return True
    return False

def is_joint_train():
    for c in cmds:
        if is_ui_noise(c):
            continue
        if "mlagents-learn" in c and "--inference" not in c:
            if "Jack_Lily_George" in c or "jack_lily_george" in c.lower():
                return True
            m = re.search(r"--run-id[=\s]+([^\s]+)", c)
            if m and m.group(1).startswith("jlg_"):
                return True
        if re.search(r"(?:^|[\s/])train_headless_jack_lily_george(?:_finetune)?\.bash\b", c):
            if "python3 -" in c:
                continue
            return True
    return False

ui_val = "_" + "ui_val_"
validate = any(
    (not is_ui_noise(c)) and (("validate_ui_one.bash" in c) or ("forestValidate" in c) or (ui_val in c))
    for c in cmds
)
stream = any(
    (not is_ui_noise(c))
    and (("stream_onnx_infer.py" in c) or ("forestStreamOnly" in c))
    and ("forestStreamingSurvival" not in c)
    for c in cmds
)
streaming_survival = any(
    (not is_ui_noise(c)) and ("forestStreamingSurvival" in c)
    for c in cmds
)
llm_bot = any(
    (not is_ui_noise(c))
    and (
        ("-m stream_bot.main" in c)
        or ("stream_bot/main.py" in c)
        or ("python" in c.lower() and "stream_bot.main" in c)
    )
    for c in cmds
)
joint = is_joint_train()
jack = (not joint) and train_yaml("Jack_single_agent")
lily = (not joint) and train_yaml("Lily_single_agent")
george = (not joint) and train_yaml("George_single_agent")

def run_id_hint():
    for c in cmds:
        if "mlagents-learn" not in c or "--inference" in c:
            continue
        m = re.search(r"--run-id[=\s]+([^\s]+)", c)
        if m:
            return m.group(1)
    for c in cmds:
        if "stream_onnx_infer.py" not in c:
            continue
        m = re.search(r"--run-id[=\s]+([^\s]+)", c)
        if m:
            return m.group(1)
    return ""

rid = run_id_hint()
tasks = []
if joint:
    tasks.append({"id": "train", "kind": "train", "label": "Joint train" + ((" · " + rid) if rid else ""), "kill": "train"})
else:
    if jack:
        tasks.append({"id": "jack", "kind": "train", "label": "Jack train" + ((" · " + rid) if rid else ""), "kill": "train"})
    if lily:
        tasks.append({"id": "lily", "kind": "train", "label": "Lily train" + ((" · " + rid) if rid else ""), "kill": "train"})
    if george:
        tasks.append({"id": "george", "kind": "train", "label": "George train" + ((" · " + rid) if rid else ""), "kill": "train"})
if validate:
    tasks.append({"id": "validate", "kind": "validate", "label": "Validate (mp4)", "kill": "validate"})
if stream:
    tasks.append({"id": "stream", "kind": "stream", "label": "Stream Presentation" + ((" · " + rid) if rid else ""), "kill": "stream"})
if streaming_survival:
    tasks.append({"id": "streaming_survival", "kind": "streaming_survival", "label": "Streaming Survival", "kill": "streaming_survival"})
if llm_bot:
    tasks.append({"id": "llm_bot", "kind": "llm_bot", "label": "LLM Bot", "kill": "llm_bot"})

print(json.dumps({
    "jack": bool(jack),
    "lily": bool(lily),
    "george": bool(george),
    "joint": bool(joint),
    "validate": bool(validate),
    "stream": bool(stream),
    "streaming_survival": bool(streaming_survival),
    "llm_bot": bool(llm_bot),
    "tasks": tasks,
}))
PY
"""


def fetch_lab_activity(host: str | None = None) -> dict[str, Any]:
    """Activity из кэша stats (отдельный SSH больше не делаем)."""
    with _stats_lock:
        cached = _stats_cache.get("data")
    if isinstance(cached, dict) and isinstance(cached.get("activity"), dict):
        return dict(cached["activity"])
    empty = dict(_EMPTY_ACTIVITY)
    empty["tasks"] = []
    empty["ok"] = False
    return empty


def _fetch_server_stats_uncached(host: str | None = None) -> dict[str, Any]:
    """RAM + nvidia-smi + activity JSON (SSH или local, если UI на lab)."""
    try:
        r = ssh_run(host, _REMOTE_STATS_AND_ACTIVITY, timeout=18, gate_timeout=8.0)
        used = "local" if ui_runs_on_lab() else (_ssh_host_cache or host or "?")
        full = ((r.stdout or "") + (r.stderr or "")).strip()
        marker = "=== ACTIVITY_JSON ==="
        if marker in full:
            head, _, tail = full.partition(marker)
            text = head.strip()
            act_line = ""
            for ln in reversed(tail.strip().splitlines()):
                ln = ln.strip()
                if ln.startswith("{"):
                    act_line = ln
                    break
            activity = _parse_activity_json(act_line)
        else:
            text = full
            activity = dict(_EMPTY_ACTIVITY)
            activity["tasks"] = []
            activity["ok"] = False
        return {
            "ok": r.returncode == 0 and bool(text),
            "text": text or "(пусто)",
            "host": used,
            "mode": "local" if ui_runs_on_lab() else "ssh",
            "activity": activity,
            "pending": False,
            "tried": ["local"] if ui_runs_on_lab() else [],
        }
    except Exception as e:
        clear_ssh_host_cache()
        act = dict(_EMPTY_ACTIVITY)
        act["tasks"] = []
        act["ok"] = False
        act["error"] = str(e)
        return {
            "ok": False,
            "text": f"[{'local' if ui_runs_on_lab() else 'ssh'}] {e}",
            "host": "local" if ui_runs_on_lab() else (host or "?"),
            "mode": "local" if ui_runs_on_lab() else "ssh",
            "activity": act,
            "pending": False,
            "tried": list(ssh_candidates("lab_comp")),
        }


def _stats_pending_payload(host: str | None = None) -> dict[str, Any]:
    act = dict(_EMPTY_ACTIVITY)
    act["tasks"] = []
    return {
        "ok": False,
        "pending": True,
        "text": "загрузка RAM / nvidia-smi c lab_comp...",
        "host": host or _ssh_host_cache or "lab_comp",
        "activity": act,
    }


def _stats_refresh_worker(host: str | None = None) -> None:
    global _stats_inflight
    try:
        # Не звать resolve_ssh_host_fast здесь — он сам может висеть минутами.
        # ssh_run уже перебирает IP при ошибке коннекта.
        h = host or _ssh_host_cache or "lab_comp"
        out = _fetch_server_stats_uncached(h)
        with _stats_lock:
            _stats_cache["t"] = time.time()
            _stats_cache["data"] = out
    except Exception as e:
        with _stats_lock:
            fail = _stats_pending_payload(host)
            fail["pending"] = False
            fail["text"] = f"[ssh] {e}"
            # не затираем хороший stale при ошибке refresh
            if _stats_cache.get("data") is None:
                _stats_cache["t"] = time.time()
                _stats_cache["data"] = fail
    finally:
        with _stats_lock:
            _stats_inflight = False


def fetch_server_stats(host: str | None = None) -> dict[str, Any]:
    """Никогда не блокирует HTTP: кэш сразу, SSH только в фоне."""
    global _stats_inflight
    now = time.time()
    kick = False
    with _stats_lock:
        cached = _stats_cache.get("data")
        age = now - float(_stats_cache.get("t") or 0)
        # если worker завис — сбросить флаг
        if _stats_inflight and age > 45 and cached is not None:
            _stats_inflight = False
        if _stats_inflight and cached is None and age > 45:
            _stats_inflight = False
        fresh = cached is not None and age < _STATS_CACHE_TTL
        stale_ok = cached is not None and age < _STATS_STALE_MAX
        if not fresh and not _stats_inflight:
            _stats_inflight = True
            kick = True
            if cached is None:
                _stats_cache["t"] = now  # точка отсчёта для watchdog
        out = cached if (fresh or stale_ok) else None
    if kick:
        threading.Thread(
            target=_stats_refresh_worker,
            args=(host,),
            name="lab-stats-refresh",
            daemon=True,
        ).start()
    if out is not None:
        return out
    return _stats_pending_payload(host)


def start_stats_background_loop() -> None:
    """Периодический refresh, чтобы pill загорался без ожидания первого клика."""

    def loop() -> None:
        # сначала быстро найти живой SSH (в фоне), потом stats
        try:
            resolve_ssh_host(load_cfg().get("ssh_host") or "lab_comp")
        except Exception:
            pass
        while True:
            try:
                h = _ssh_host_cache or _load_saved_ssh_host() or "lab_comp"
                fetch_server_stats(h)
            except Exception:
                pass
            time.sleep(_STATS_CACHE_TTL)

    threading.Thread(target=loop, name="lab-stats-loop", daemon=True).start()


# ── actions ─────────────────────────────────────────────────────────


def action_deploy(cfg: dict, skip_build: bool = False) -> str:
    build = cfg.get("build") or DEFAULT_BUILD
    env = os.environ.copy()
    env["BUILD"] = build
    argv = ["bash", str(ROOT / "train_scripts/lab_comp/deploy_lab_comp.bash")]
    if skip_build:
        argv.append("--no-build")
    return start_local_job("deploy_sync" if skip_build else "deploy_build_sync", argv, env=env)


def _remote_kill_train_cmd() -> str:
    """Kill train + UI slot. Inline fallback если на lab старый kill_train.bash."""
    return f"""
set +e
cd {REMOTE_DIR}
KEEP_TENSORBOARD=1 bash train_scripts/lab_comp/kill_train.bash
# fallback: слот joint UI (на случай старого kill_train.bash)
PIDF=/tmp/forest_ui_fui_joint_jlg.pid
if [ -f "$PIDF" ]; then
  old=$(tr -d ' \\r\\n' <"$PIDF")
  kill -TERM "$old" 2>/dev/null; sleep 1; kill -KILL "$old" 2>/dev/null
  pkill -TERM -P "$old" 2>/dev/null; pkill -KILL -P "$old" 2>/dev/null
  rm -f "$PIDF"
fi
tmux kill-session -t fui_joint_jlg 2>/dev/null || true
pkill -KILL -f 'train_headless_jack_lily_george' 2>/dev/null || true
pkill -KILL -f 'mlagents-learn' 2>/dev/null || true
echo "[kill_train] done"
exit 0
"""


def action_kill_train(cfg: dict) -> str:
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    jid = new_job("kill_train", "kill_train.bash")

    def runner() -> None:
        try:
            r = ssh_run(host, _remote_kill_train_cmd(), timeout=90)
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            finish_job(jid, r.returncode)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def action_restart_joint_train(cfg: dict, body: dict) -> str:
    """Стоп joint train → чистка → resume той же RUN_ID."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    build = cfg.get("build") or DEFAULT_BUILD
    jcfg = cfg.get("joint") or {}
    init_jack = sanitize_run_id(body.get("init_jack") or jcfg.get("init_jack") or "")
    init_lily = sanitize_run_id(body.get("init_lily") or jcfg.get("init_lily") or "")
    init_george = sanitize_run_id(body.get("init_george") or jcfg.get("init_george") or "")
    run_id = sanitize_run_id(body.get("run_id") or jcfg.get("run_id") or "")
    if not all((init_jack, init_lily, init_george, run_id)):
        raise ValueError("Для перезапуска нужны 3 базы и RUN_ID")
    if run_id in (init_jack, init_lily, init_george):
        raise ValueError(f"RUN_ID={run_id} совпадает с базой — затрём веса")
    jid = new_job("restart_joint_train", f"kill+resume {run_id}")

    def runner() -> None:
        try:
            append_log(jid, "[restart_joint] kill train…")
            r = ssh_run(host, _remote_kill_train_cmd(), timeout=90)
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            time.sleep(2)
            append_log(jid, f"[restart_joint] start resume RUN_ID={run_id}")
            # стартуем отдельным detached job; этот job = фаза kill+handoff
            start_jid = action_joint_train(
                cfg,
                {
                    "init_jack": init_jack,
                    "init_lily": init_lily,
                    "init_george": init_george,
                    "run_id": run_id,
                    "resume": True,
                },
            )
            append_log(jid, f"[restart_joint] started job={start_jid} BUILD={build}")
            finish_job(jid, 0)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def action_restart_joint_stream(cfg: dict, body: dict) -> str:
    """Стоп стрим → чистка → старт Presentation заново."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    jcfg = cfg.get("joint") or {}
    run_id = sanitize_run_id(body.get("run_id") or jcfg.get("run_id") or "")
    if not run_id:
        raise ValueError("Нужен RUN_ID для перезапуска стрима")
    jid = new_job("restart_joint_stream", f"kill+stream {run_id}")

    def runner() -> None:
        try:
            append_log(jid, "[restart_stream] kill stream…")
            r = ssh_run(
                host,
                f"cd {REMOTE_DIR} && bash train_scripts/lab_comp/kill_stream.bash",
                timeout=60,
            )
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            time.sleep(2)
            append_log(jid, f"[restart_stream] start RUN_ID={run_id}")
            start_jid = action_joint_stream(cfg, {"run_id": run_id})
            append_log(jid, f"[restart_stream] started job={start_jid}")
            finish_job(jid, 0)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def _stop_local_validate_ssh() -> None:
    """Оборвать локальный SSH-процесс текущей validate в UI."""
    global _validate_active_jid, _validate_proc
    with _validate_lock:
        old = _validate_proc
        old_jid = _validate_active_jid
        _validate_proc = None
        _validate_active_jid = None
        if old is not None and old.poll() is None:
            try:
                old.terminate()
            except Exception:
                pass
            try:
                old.kill()
            except Exception:
                pass
        if old_jid:
            with _jobs_lock:
                prev = _jobs.get(old_jid)
                if prev and prev.get("status") == "running":
                    prev["status"] = "error"
                    prev["returncode"] = -15
                    prev["ended"] = time.time()


def action_kill_validate(cfg: dict) -> str:
    """Только UI-validate (mp4). Train и стрим не трогает."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    _stop_local_validate_ssh()
    jid = new_job("kill_validate", "kill_validate.bash")

    def runner() -> None:
        try:
            r = ssh_run(
                host,
                f"cd {REMOTE_DIR} && bash train_scripts/lab_comp/kill_validate.bash",
                timeout=60,
            )
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            finish_job(jid, r.returncode)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def action_kill_stream(cfg: dict) -> str:
    """Только бесконечный Presentation-стрим. Train и validate не трогает."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    jid = new_job("kill_stream", "kill_stream.bash")

    def runner() -> None:
        try:
            r = ssh_run(
                host,
                f"cd {REMOTE_DIR} && bash train_scripts/lab_comp/kill_stream.bash",
                timeout=60,
            )
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            finish_job(jid, r.returncode)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def action_kill_mode_training(cfg: dict) -> str:
    """Полный стоп блока Forest Lab Train: train + validate + Presentation stream."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    jid = new_job("kill_mode_training", "kill_mode_training.bash")

    def runner() -> None:
        try:
            r = ssh_run(
                host,
                f"cd {REMOTE_DIR} && bash train_scripts/lab_comp/kill_mode_training.bash",
                timeout=120,
            )
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            finish_job(jid, r.returncode)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def action_kill_mode_streaming(cfg: dict) -> str:
    """Полный стоп блока Survival followers: SS + LLM bot."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    jid = new_job("kill_mode_streaming", "kill_mode_streaming.bash")

    def runner() -> None:
        try:
            r = ssh_run(
                host,
                f"cd {REMOTE_DIR} && bash train_scripts/lab_comp/kill_mode_streaming.bash",
                timeout=90,
            )
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            # локальный кэш бота
            try:
                if LLM_BOT is not None:
                    with LLM_BOT._lock:
                        LLM_BOT.bot_state = "stopped"
                        LLM_BOT.bot_http_ready = False
            except Exception:
                pass
            try:
                SS_PREVIEW.stop()
            except Exception:
                pass
            finish_job(jid, r.returncode)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def action_start_streaming_survival(cfg: dict) -> str:
    """Unity Streaming Survival без onnx/train."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    build = cfg.get("build") or DEFAULT_BUILD
    remote = (
        f"cd {REMOTE_DIR} && export BUILD={sh_quote(build)} && "
        f"bash train_scripts/lab_comp/start_streaming_survival.bash"
    )
    return start_remote_tmux("streaming_survival", host, "fui_streaming_survival", remote)


def action_stop_streaming_survival(cfg: dict) -> str:
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    jid = new_job("stop_streaming_survival", "stop_streaming_survival.bash")

    def runner() -> None:
        try:
            r = ssh_run(
                host,
                f"cd {REMOTE_DIR} && bash train_scripts/lab_comp/stop_streaming_survival.bash",
                timeout=60,
            )
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            finish_job(jid, r.returncode)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def _ss_remote_runs_dir() -> str:
    return f"{REMOTE_DIR}/artifacts/streaming_survival/test_runs"


def _ss_remote_checks_bash(checks_args: str) -> str:
    """Run run_streaming_survival_checks.py on lab (Unity lives there, not on Windows UI)."""
    return f"""
set -eu
cd {REMOTE_DIR}
PY="${{FOREST_SS_REMOTE_PYTHON:-}}"
if [ -z "$PY" ] || [ ! -x "$PY" ]; then
  if [ -x "${{HOME}}/anaconda3/envs/mlagents/bin/python" ]; then
    PY="${{HOME}}/anaconda3/envs/mlagents/bin/python"
  elif [ -x "${{HOME}}/anaconda3/bin/python" ]; then
    PY="${{HOME}}/anaconda3/bin/python"
  else
    PY=python3
  fi
fi
"$PY" -u scripts/run_streaming_survival_checks.py {checks_args}
"""


def _ss_list_remote_run_ids(host: str, limit: int = 12) -> list[str]:
    cmd = (
        f"ls -1d {_ss_remote_runs_dir()}/*/ 2>/dev/null "
        f"| xargs -n1 basename 2>/dev/null | sort -r | head -{max(1, min(limit, 40))}"
    )
    try:
        r = ssh_run(host, cmd, timeout=45)
    except Exception:
        return []
    out: list[str] = []
    for ln in (r.stdout or "").splitlines():
        name = Path(ln.strip()).name
        if re.match(r"^\d{8}_\d{6}_\d+$", name):
            out.append(name)
    return out


def _ss_pull_run_dir(host: str, run_id: str) -> bool:
    """Pull one test_runs/<id> folder from lab → local artifacts (for UI charts)."""
    name = Path(str(run_id).strip()).name
    if not re.match(r"^\d{8}_\d{6}_\d+$", name):
        return False
    base = _ss_runs_base()
    base.mkdir(parents=True, exist_ok=True)
    tar_bin = shutil.which("tar") or "tar"
    remote_cmd = f"cd {_ss_remote_runs_dir()} && tar czf - {sh_quote(name)}"
    try:
        p_ssh = subprocess.Popen(
            [ssh_bin(), *_ssh_common_opts(), host, remote_cmd],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        assert p_ssh.stdout is not None
        p_tar = subprocess.Popen(
            [tar_bin, "xzf", "-", "-C", str(base)],
            stdin=p_ssh.stdout,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        p_ssh.stdout.close()
        p_tar.communicate(timeout=300)
        rc_ssh = p_ssh.wait(timeout=10)
        rc_tar = p_tar.returncode
        ok = rc_ssh == 0 and rc_tar == 0 and (base / name).is_dir()
        if not ok:
            return False
        # LATEST pointer (best-effort)
        try:
            lr = ssh_run(
                host,
                f"cat {_ss_remote_runs_dir()}/LATEST 2>/dev/null || true",
                timeout=20,
            )
            latest = (lr.stdout or "").strip()
            if latest:
                (base / "LATEST").write_text(latest + "\n", encoding="utf-8")
        except Exception:
            pass
        return ok
    except Exception:
        return False


def _ss_sync_runs_from_lab(
    host: str | None = None,
    run_ids: list[str] | None = None,
    limit: int = 10,
) -> int:
    """Pull recent SS test runs from lab when UI runs on Windows (Unity on lab only)."""
    if ui_runs_on_lab():
        return 0
    try:
        host = host or resolve_ssh_host(load_cfg().get("ssh_host") or "lab_comp")
    except Exception:
        return 0
    ids = run_ids if run_ids is not None else _ss_list_remote_run_ids(host, limit=limit)
    synced = 0
    for rid in ids:
        local = _ss_runs_base() / rid
        if local.is_dir() and (local / "live_stress_report.json").is_file():
            continue
        if _ss_pull_run_dir(host, rid):
            synced += 1
    return synced


def _ss_maybe_sync_runs(force: bool = False) -> None:
    if ui_runs_on_lab():
        return
    global _ss_sync_last_at
    now = time.time()
    with _ss_sync_lock:
        if not force and now - _ss_sync_last_at < _SS_SYNC_INTERVAL:
            return
        _ss_sync_last_at = now
        already = getattr(_ss_maybe_sync_runs, "_inflight", False)
        if already and not force:
            return
        _ss_maybe_sync_runs._inflight = True  # type: ignore[attr-defined]

    def _bg() -> None:
        try:
            _ss_sync_runs_from_lab()
        except Exception:
            pass
        finally:
            _ss_maybe_sync_runs._inflight = False  # type: ignore[attr-defined]

    # Never block UI request threads on SSH/tar pull.
    threading.Thread(target=_bg, name="ss-sync-runs", daemon=True).start()


def _start_ss_checks_async(
    job_name: str,
    session: str,
    checks_args: str,
    *,
    eta_hint: str,
    log_intro: str,
) -> dict[str, Any]:
    """Live Stress / Full Runtime: always on lab unless UI itself runs on lab_comp.

    Windows UI must never run Unity checks locally — Unity is only on lab.
    """
    # os.name == "nt" → this PC; force SSH even if FOREST_UI_LOCAL was mis-set.
    on_lab = ui_runs_on_lab() and os.name != "nt"
    if on_lab:
        py = _ss_python()
        checks = ROOT / "scripts" / "run_streaming_survival_checks.py"
        argv = [py, "-u", str(checks)] + checks_args.split()
        jid = start_local_job(job_name, argv, cwd=ROOT)
        where = "lab (local)"
    else:
        cfg = load_cfg()
        host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
        remote_bash = _ss_remote_checks_bash(checks_args.strip())
        jid = start_remote_detached(job_name, host, session, remote_bash)
        where = f"lab_comp via ssh ({host})"
    return {
        "overall": "RUNNING",
        "async": True,
        "job_id": jid,
        "eta_hint": eta_hint,
        "log": (
            f"{log_intro} → {where} (job {jid}).\n"
            "Жди обновления статуса ниже. При Error в списке останется попытка с причиной."
        ),
    }


def _ss_artifacts_latest() -> Path | None:
    """Prefer newest live-stress/stats run for Diagnostics links.

    Previously preferred oldest full-runtime PASS, which had no stats/ →
    open table/chart returned 404 and cards stayed empty.
    """
    base = ROOT / "artifacts" / "streaming_survival" / "test_runs"
    if not base.is_dir():
        return None
    runs = sorted([d for d in base.iterdir() if d.is_dir()], reverse=True)

    def _read_summary(run: Path) -> dict[str, Any]:
        sp = run / "summary.json"
        if not sp.is_file():
            return {}
        try:
            return json.loads(sp.read_text(encoding="utf-8"))
        except Exception:
            return {}

    def _has_stats(run: Path) -> bool:
        return (run / "stats" / "stats_summary.json").is_file() or (
            run / "stats" / "parser_stats.csv"
        ).is_file()

    def _has_live(run: Path) -> bool:
        return (run / "live_stress_report.json").is_file()

    def _from_latest_file() -> Path | None:
        latest = base / "LATEST"
        if not latest.is_file():
            return None
        p = Path(latest.read_text(encoding="utf-8").strip())
        if not p.is_absolute():
            p = (ROOT / p).resolve()
        return p if p.is_dir() else None

    # 1) LATEST pointer if it has stats or live stress
    pointed = _from_latest_file()
    if pointed is not None and (_has_stats(pointed) or _has_live(pointed)):
        return pointed

    # 2) newest run with stats/
    for run in runs:
        if _has_stats(run):
            return run

    # 3) newest live stress
    for run in runs:
        if _has_live(run):
            return run

    # 4) LATEST even without stats
    if pointed is not None:
        return pointed

    # 5) full-runtime PASS (machine checks only)
    for run in runs:
        data = _read_summary(run)
        if data.get("full_runtime_status") == "PASS" and data.get("overall") == "PASS":
            return run

    return runs[0] if runs else None


def _ss_runs_base() -> Path:
    return ROOT / "artifacts" / "streaming_survival" / "test_runs"


def _ss_resolve_run(run_id: str | None = None) -> Path | None:
    """Resolve a specific test_runs/<id> dir, or fall back to latest."""
    base = _ss_runs_base()
    if run_id:
        name = Path(str(run_id).strip()).name
        if not name or name in (".", "..", "LATEST"):
            return None
        p = (base / name).resolve()
        try:
            p.relative_to(base.resolve())
        except ValueError:
            return None
        if p.is_dir():
            return p
        if not ui_runs_on_lab():
            # Kick background pull — never block HTTP on SSH/tar.
            threading.Thread(
                target=lambda: _ss_sync_runs_from_lab(run_ids=[name], limit=1),
                name=f"ss-pull-{name}",
                daemon=True,
            ).start()
        return None
    if not ui_runs_on_lab():
        _ss_maybe_sync_runs()
    return _ss_artifacts_latest()


def _ss_run_when_label(run: Path) -> str:
    """Human local date from dirname YYYYMMDD_HHMMSS_* (UTC) or mtime."""
    m = re.match(r"^(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})", run.name)
    if m:
        y, mo, d, h, mi, s = (int(x) for x in m.groups())
        try:
            # Live-stress run ids are created with datetime.now(timezone.utc).
            # Show wall-clock local time so 17:05 UTC → 20:05 MSK, not "17:05".
            dt = datetime(y, mo, d, h, mi, s, tzinfo=timezone.utc).astimezone()
            return dt.strftime("%Y-%m-%d %H:%M:%S")
        except Exception:
            return f"{y:04d}-{mo:02d}-{d:02d} {h:02d}:{mi:02d}:{s:02d}"
    try:
        return datetime.fromtimestamp(run.stat().st_mtime).strftime("%Y-%m-%d %H:%M:%S")
    except Exception:
        return "—"


def _ss_format_duration(sec: float | None) -> str:
    if sec is None or sec < 0:
        return "—"
    sec_i = int(round(sec))
    h, rem = divmod(sec_i, 3600)
    m, s = divmod(rem, 60)
    if h:
        return f"{h}ч {m}м"
    if m:
        return f"{m}м"
    return f"{s}с"


def _ss_run_start_ts(run: Path) -> float | None:
    m = re.match(r"^(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})", run.name)
    if not m:
        return None
    try:
        y, mo, d, h, mi, s = (int(x) for x in m.groups())
        return datetime(y, mo, d, h, mi, s, tzinfo=timezone.utc).timestamp()
    except Exception:
        return None


def _ss_run_duration_sec(run: Path, live: dict[str, Any]) -> float | None:
    """Best-effort wall time of a live-stress run."""
    for key in ("duration_sec", "elapsed_sec", "duration_s"):
        v = live.get(key)
        if v is not None:
            try:
                return float(v)
            except (TypeError, ValueError):
                pass
    # Prefer span of per-attempt result markers (closest to real wall time).
    try:
        results = sorted(run.glob("live_*_result.json"), key=lambda p: p.stat().st_mtime)
        if len(results) >= 2:
            return max(0.0, results[-1].stat().st_mtime - results[0].stat().st_mtime)
        if len(results) == 1:
            start = _ss_run_start_ts(run)
            if start is not None:
                return max(0.0, results[0].stat().st_mtime - start)
    except Exception:
        pass
    lp = run / "live_stress_report.json"
    start = _ss_run_start_ts(run)
    if lp.is_file() and start is not None:
        try:
            # Cap absurd values if report was rewritten long after the run.
            dur = lp.stat().st_mtime - start
            if 0 < dur < 12 * 3600:
                return dur
        except Exception:
            pass
    return None


def _ss_short_fail_reason(reason: str, max_len: int = 64) -> str:
    """Compact fail text for the run list (no teleport spam / huge wrap)."""
    r = str(reason or "").strip()
    if not r:
        return ""
    parts = [p.strip() for p in r.split("|") if p.strip()]
    kept: list[str] = []
    n_tele = 0
    for p in parts:
        pl = p.lower()
        if "illegal teleport" in pl or "max_jump exceeded" in pl:
            n_tele += 1
            continue
        # Hide noisy resource checker wording from the list row.
        if "delta=" in pl or "gained=" in pl or "need>=" in pl:
            # Keep a short token once.
            if "wood" in pl and "wood" not in " ".join(kept).lower():
                kept.append("wood")
            elif "water" in pl and "water" not in " ".join(kept).lower():
                kept.append("water")
            continue
        kept.append(p)
    if n_tele:
        kept.append(f"teleport×{n_tele}" if n_tele > 1 else "teleport")
    s = " · ".join(kept) if kept else r
    if len(s) > max_len:
        return s[: max_len - 1] + "…"
    return s


def _ss_extract_fail_reason(summary: dict[str, Any], live: dict[str, Any] | None = None) -> str:
    """Human reason for ERROR row: top-level or first failed attempt."""
    live = live or {}
    nested = summary.get("live_stress") if isinstance(summary.get("live_stress"), dict) else {}
    for src in (summary, live, nested):
        if not isinstance(src, dict):
            continue
        for key in ("reason", "error", "fail_reason"):
            v = str(src.get(key) or "").strip()
            if v and v.lower() not in ("ok", "none", "-"):
                return v
    for src in (live, nested, summary):
        if not isinstance(src, dict):
            continue
        failed = src.get("failed_attempts")
        if not isinstance(failed, list):
            failed = []
        attempts = src.get("attempts") if isinstance(src.get("attempts"), list) else []
        for a in list(failed) + list(attempts):
            if not isinstance(a, dict):
                continue
            if str(a.get("result") or "").upper() in ("PASS", "OK", ""):
                if a not in failed:
                    continue
            v = str(a.get("reason") or a.get("error") or a.get("fail_reason") or "").strip()
            if v:
                return v
    # Aggregate counters when attempt reason missing
    for src in (live, nested):
        if not isinstance(src, dict):
            continue
        bits: list[str] = []
        for key, label in (
            ("stuck_failures", "stuck"),
            ("teleport_failures", "teleport"),
            ("resource_guard_failures", "resource_guard"),
            ("parser_failures", "parser"),
            ("ground_failures", "ground"),
            ("visual_failures", "visual"),
        ):
            try:
                n = int(src.get(key) or 0)
            except (TypeError, ValueError):
                n = 0
            if n:
                bits.append(f"{label}={n}")
        if bits:
            return " · ".join(bits)
        if str(src.get("overall") or "").upper() in ("FAIL", "ERROR"):
            ap = src.get("attempts_passed")
            at = src.get("attempts_total") or src.get("n_attempts")
            if at is not None:
                return f"PASS {ap if ap is not None else 0}/{at}"
    return ""


def _ss_run_meta(run: Path) -> dict[str, Any]:
    summary: dict[str, Any] = {}
    sp = run / "summary.json"
    if sp.is_file():
        try:
            summary = json.loads(sp.read_text(encoding="utf-8"))
        except Exception:
            summary = {}
    live: dict[str, Any] = {}
    lp = run / "live_stress_report.json"
    if lp.is_file():
        try:
            live = json.loads(lp.read_text(encoding="utf-8"))
        except Exception:
            live = {}
    stats: dict[str, Any] = {}
    ssp = run / "stats" / "stats_summary.json"
    if ssp.is_file():
        try:
            stats = json.loads(ssp.read_text(encoding="utf-8"))
        except Exception:
            stats = {}
    has_stats = ssp.is_file() or (run / "stats" / "parser_stats.csv").is_file()
    has_live = lp.is_file()
    running_meta: dict[str, Any] = {}
    rp = run / "live_stress_running.json"
    if rp.is_file():
        try:
            running_meta = json.loads(rp.read_text(encoding="utf-8"))
        except Exception:
            running_meta = {"status": "RUNNING"}
    # In-progress: report not ready, but traj/screenshots/ping are active.
    # live_*_result.json appears only AFTER an attempt ends — during a long first
    # #do we only have trajectories/ — those must count or the run is invisible.
    live_attempt_files = sorted(run.glob("live_*_result.json"))
    activity_files: list[Path] = list(live_attempt_files)
    if rp.is_file():
        activity_files.append(rp)
    traj_dir = run / "trajectories"
    if traj_dir.is_dir():
        activity_files.extend(traj_dir.glob("*.jsonl"))
    shot_dir = run / "screenshots"
    if shot_dir.is_dir():
        activity_files.extend(p for p in shot_dir.rglob("*") if p.is_file())
    for name in ("runtime_ping.json", "world_map.json"):
        p = run / name
        if p.is_file():
            activity_files.append(p)
    newest_live_mtime = 0.0
    if activity_files:
        try:
            newest_live_mtime = max(f.stat().st_mtime for f in activity_files)
        except Exception:
            newest_live_mtime = 0.0
    recently_active = newest_live_mtime > 0 and (time.time() - newest_live_mtime) < 45 * 60
    has_live_artifacts = (
        bool(live_attempt_files)
        or bool(list(traj_dir.glob("*.jsonl")) if traj_dir.is_dir() else [])
        or rp.is_file()
    )
    in_progress = (not has_live) and has_live_artifacts and recently_active
    done_attempts = len(live_attempt_files)
    traj_attempts = 0
    if traj_dir.is_dir():
        traj_attempts = len({p.name.split("_trajectory")[0] for p in traj_dir.glob("*_trajectory.jsonl")})
    attempts_list = live.get("attempts") if isinstance(live.get("attempts"), list) else []
    attempts_n = (
        int(live.get("attempts_total") or 0)
        or int(live.get("n_attempts") or 0)
        or len(attempts_list)
        or int(summary.get("live_stress_attempts") or 0)
        or int(running_meta.get("attempts") or 0)
        or (40 if (has_live or in_progress) else 0)
    )
    attempts_passed = live.get("attempts_passed")
    if attempts_passed is None and attempts_list:
        attempts_passed = sum(1 for a in attempts_list if (a.get("result") or "").upper() == "PASS")
    if attempts_passed is None:
        attempts_passed = 0 if has_live else (None if not in_progress else None)
    else:
        attempts_passed = int(attempts_passed)
    attempts_failed = live.get("attempts_failed")
    if attempts_failed is None and attempts_n and attempts_passed is not None and has_live:
        attempts_failed = max(0, int(attempts_n) - int(attempts_passed))
    elif attempts_failed is not None:
        attempts_failed = int(attempts_failed)
    duration_sec = _ss_run_duration_sec(run, live) if has_live else None
    if in_progress and duration_sec is None:
        try:
            # Prefer file mtimes (wall clock). Dirname timestamp may be UTC while
            # local datetime() parse treats it as local → multi-hour skew.
            times = [f.stat().st_mtime for f in activity_files]
            if times:
                duration_sec = max(0.0, time.time() - min(times))
            else:
                duration_sec = max(0.0, time.time() - run.stat().st_mtime)
        except Exception:
            duration_sec = None
    # Stale unfinished stress (no report, idle >45m): still list, but as incomplete.
    unfinished = (not has_live) and has_live_artifacts and not in_progress
    # Early fail / aborted live-stress: only summary.json left — still a real attempt.
    summary_mode = str(summary.get("mode") or "")
    summary_live = summary_mode in ("live_stress", "live_stress_only", "full_qa")
    summary_reason = _ss_extract_fail_reason(summary, live)
    early_fail = (
        (not has_live)
        and (not in_progress)
        and (not unfinished)
        and summary_live
        and str(summary.get("overall") or "").upper()
        in ("FAIL", "ERROR", "INCOMPLETE", "RUNNING")
    )
    if unfinished:
        live_st_override = "INCOMPLETE"
        overall_override = "INCOMPLETE"
    elif early_fail:
        raw = str(summary.get("live_stress_status") or summary.get("overall") or "ERROR").upper()
        reason_l0 = summary_reason.lower()
        launch0 = any(
            m in reason_l0
            for m in (
                "unity runtime",
                "bot unreachable",
                "runtime down",
                "traceback",
                "exception",
            )
        ) or (not summary_reason)
        if raw in ("FAIL", "ERROR"):
            tag = "Error" if launch0 else "Failed"
            live_st_override = tag
            overall_override = tag
        else:
            live_st_override = raw
            overall_override = raw
    else:
        live_st_override = None
        overall_override = None
    duration_label = _ss_format_duration(duration_sec)
    mode = (
        summary.get("mode")
        or ("live_stress" if (has_live or in_progress or unfinished or early_fail) else None)
        or ("parser_only" if (run / "parser_results.json").is_file() and not has_live else None)
        or "checks"
    )
    overall = (
        overall_override
        or ("RUNNING" if in_progress else None)
        or summary.get("overall_qa_status")
        or summary.get("overall")
        or live.get("overall")
        or ("PASS" if has_stats else "—")
    )
    live_st = (
        live_st_override
        or ("RUNNING" if in_progress else None)
        or summary.get("live_stress_status")
        or live.get("overall")
        or ("—" if not has_live else "?")
    )
    # Normalize FAIL/PASS → Failed/Success for humans.
    # ERROR only for launch/runtime crashes (Unity down, bot unreachable, …).
    launch_error_markers = (
        "unity runtime",
        "bot unreachable",
        "missing trajectory",
        "runtime down",
        "traceback",
        "exception",
        "ssh",
        "timeout",
        "no such file",
        "permission denied",
    )
    reason_l = summary_reason.lower()
    is_launch_error = any(m in reason_l for m in launch_error_markers) or (
        early_fail and "delta=" not in reason_l and "need>=" not in reason_l
    )
    raw_live = str(live_st).upper()
    raw_overall = str(overall).upper()
    if raw_live in ("PASS", "OK", "SUCCESS"):
        live_st = "Success"
    elif raw_live in ("FAIL", "FAILED", "ERROR"):
        live_st = "Error" if is_launch_error else "Failed"
    if raw_overall in ("PASS", "OK", "SUCCESS"):
        overall = "Success"
    elif raw_overall in ("FAIL", "FAILED", "ERROR") and (has_live or early_fail or summary_reason):
        overall = "Error" if is_launch_error else "Failed"
    when = _ss_run_when_label(run)
    if has_live or in_progress or unfinished or early_fail:
        title = f"Live Stress {attempts_n}" if attempts_n else "Live Stress"
    elif mode == "parser_only":
        title = "Parser report"
    elif mode in ("full_runtime", "checks", "runtime_suite"):
        title = "Machine Checks"
    else:
        title = str(mode)
    status_show = live_st if (has_live or in_progress or unfinished or early_fail) else overall
    # List row: keep status short. Full reason stays in "reason" for details/tooltip.
    short_reason = _ss_short_fail_reason(summary_reason)
    parser_fail = stats.get("parser_failures")
    if parser_fail is None:
        parser_fail = live.get("parser_failures")
    # Primary line for humans: pass/fail/duration
    if in_progress:
        if traj_attempts > done_attempts:
            score_line = f"идёт {done_attempts + 1}/{attempts_n or '?'} · {duration_label}"
        else:
            score_line = f"сделано {done_attempts}/{attempts_n or '?'} · {duration_label}"
    elif unfinished:
        score_line = f"обрыв {done_attempts}/{attempts_n or '?'}"
    elif early_fail or (has_live and str(live_st) in ("Failed", "Error")):
        if attempts_n:
            score_line = f"{int(attempts_passed or 0)}/{attempts_n}"
        else:
            score_line = short_reason or str(live_st)
    elif has_live and attempts_passed is not None and attempts_failed is not None:
        if str(live_st) == "Success":
            score_line = f"Success · {duration_label}"
        else:
            score_line = f"{attempts_passed}/{attempts_n or (attempts_passed + attempts_failed)} · {duration_label}"
    else:
        score_line = duration_label if duration_label != "—" else ""
    subtitle_bits = []
    if score_line:
        subtitle_bits.append(score_line)
    if parser_fail is not None:
        subtitle_bits.append(f"parser {parser_fail}")
    if has_stats:
        subtitle_bits.append("stats")
    elif has_live:
        subtitle_bits.append("no stats")
    elif in_progress:
        subtitle_bits.append("идёт")
    elif unfinished:
        subtitle_bits.append("не завершён")
    elif early_fail and short_reason:
        subtitle_bits.append(short_reason)
    subtitle = " · ".join(subtitle_bits) if subtitle_bits else run.name
    label = f"{title} · {status_show} · {score_line} · {when}"
    try:
        mtime = run.stat().st_mtime
    except Exception:
        mtime = 0.0
    return {
        "id": run.name,
        "run_dir": str(run),
        "when": when,
        "mtime": mtime,
        "mode": mode,
        "title": title,
        "subtitle": subtitle,
        "score_line": score_line,
        "overall": overall,
        "status": status_show,
        "reason": summary_reason,
        "live_stress_status": live_st,
        "attempts": attempts_n,
        "attempts_passed": attempts_passed if has_live else (done_attempts if (in_progress or unfinished) else attempts_passed),
        "attempts_failed": attempts_failed if attempts_failed is not None else (int(attempts_n or 0) if early_fail else None),
        "duration_sec": duration_sec,
        "duration_label": duration_label,
        "parser_failures": parser_fail,
        "stats_dashboard_status": stats.get("stats_dashboard_status")
        or summary.get("stats_dashboard_status"),
        "has_stats": has_stats,
        "has_live": has_live or in_progress or unfinished or early_fail,
        "has_summary": sp.is_file(),
        "has_screenshots": (run / "screenshots").is_dir(),
        "label": label,
        "is_report": has_live or in_progress or unfinished or early_fail,
        "in_progress": in_progress,
        "done_attempts": done_attempts if (in_progress or unfinished) else None,
    }


def _ss_list_runs(limit: int = 60, reports_only: bool = True) -> list[dict[str, Any]]:
    """List test runs. Default: Live Stress reports (40-task) only, newest first."""
    if not ui_runs_on_lab():
        _ss_maybe_sync_runs()
    base = _ss_runs_base()
    if not base.is_dir():
        return []
    runs = [d for d in base.iterdir() if d.is_dir()]
    runs.sort(key=lambda p: p.name, reverse=True)
    out: list[dict[str, Any]] = []
    for run in runs:
        try:
            meta = _ss_run_meta(run)
        except Exception:
            meta = {
                "id": run.name,
                "run_dir": str(run),
                "when": _ss_run_when_label(run),
                "title": run.name,
                "subtitle": "",
                "status": "—",
                "label": run.name,
                "has_stats": False,
                "has_live": False,
                "is_report": False,
            }
        if reports_only and not meta.get("is_report"):
            continue
        out.append(meta)
        if len(out) >= max(1, min(limit, 120)):
            break
    # If no live-stress reports yet, fall back to any runs so UI isn't empty.
    if not out and reports_only:
        return _ss_list_runs(limit=limit, reports_only=False)
    return out


def _ss_reports_page_html(limit: int = 120) -> str:
    """Standalone HTML page: all Live Stress reports with dashboard links."""
    runs = _ss_list_runs(limit=limit, reports_only=True)
    rows: list[str] = []
    for r in runs:
        rid = html.escape(str(r.get("id") or ""))
        when = html.escape(str(r.get("when") or "—"))
        status = html.escape(str(r.get("status") or r.get("live_stress_status") or "—"))
        title = html.escape(str(r.get("title") or "Live Stress"))
        passed = r.get("attempts_passed")
        failed = r.get("attempts_failed")
        total = r.get("attempts") or 40
        dur = html.escape(str(r.get("duration_label") or "—"))
        score = (
            f"PASS {passed if passed is not None else '—'} / "
            f"FAIL {failed if failed is not None else '—'} из {total}"
        )
        dash = ""
        if r.get("has_stats"):
            dash = (
                f'<a href="/api/ss_diagnostics/stats_dashboard?run={urllib.parse.quote(str(r.get("id") or ""))}" '
                f'target="_blank" rel="noopener">открыть дашборд</a>'
            )
        else:
            dash = '<span style="opacity:.55">нет stats</span>'
        rows.append(
            "<tr>"
            f"<td><b>{title}</b> · {status}<br/>"
            f"<code style='font-size:12px;opacity:.8'>{rid}</code></td>"
            f"<td style='font-variant-numeric:tabular-nums;white-space:nowrap'>{html.escape(score)}<br/>"
            f"<span style='opacity:.75'>время: {dur}</span></td>"
            f"<td style='white-space:nowrap;text-align:right'>{when}</td>"
            f"<td>{dash}</td>"
            "</tr>"
        )
    body_rows = "\n".join(rows) if rows else (
        '<tr><td colspan="4">Нет Live Stress репортов. Запусти «3. Live Stress 40».</td></tr>'
    )
    return f"""<!doctype html>
<html lang="ru"><head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>Streaming Survival — все репорты</title>
<style>
  body {{ font-family: ui-sans-serif, system-ui, sans-serif; background:#12141a; color:#e8e8e8;
         margin:0; padding:24px; line-height:1.4; }}
  h1 {{ font-size:1.35rem; margin:0 0 8px; }}
  .hint {{ opacity:.75; margin:0 0 16px; }}
  table {{ width:100%; border-collapse:collapse; background:rgba(0,0,0,.2);
           border:1px solid #444; border-radius:8px; overflow:hidden; }}
  th, td {{ padding:10px 12px; border-bottom:1px solid #333; vertical-align:top; text-align:left; }}
  th {{ background:rgba(255,255,255,.04); font-size:12px; opacity:.8; font-weight:600; }}
  tr:hover td {{ background:rgba(106,166,255,.08); }}
  a {{ color:#7eb6ff; }}
  code {{ word-break:break-all; }}
</style>
</head><body>
<h1>Live Stress — все репорты</h1>
<p class="hint">Отдельная страница со всеми прогонами. «открыть дашборд» — таблицы и графики чекеров для этого run.</p>
<table>
  <thead><tr>
    <th>репорт · overall</th>
    <th>PASS / FAIL · время</th>
    <th style="text-align:right">дата старта</th>
    <th>дашборд</th>
  </tr></thead>
  <tbody>
{body_rows}
  </tbody>
</table>
</body></html>
"""


def _ss_load_report(run: Path) -> dict[str, Any]:
    """Load summary + stats + live for checker cards (no re-run)."""
    summary: dict[str, Any] = {}
    summary_path = run / "summary.json"
    if summary_path.is_file():
        try:
            summary = json.loads(summary_path.read_text(encoding="utf-8"))
        except Exception:
            summary = {}
    stats: dict[str, Any] = {}
    sp = run / "stats" / "stats_summary.json"
    if sp.is_file():
        try:
            stats = json.loads(sp.read_text(encoding="utf-8"))
            summary.setdefault("stats_dashboard_status", stats.get("stats_dashboard_status"))
            summary["stats_summary"] = stats
        except Exception:
            pass
    live: dict[str, Any] = {}
    lp = run / "live_stress_report.json"
    if lp.is_file():
        try:
            live = json.loads(lp.read_text(encoding="utf-8"))
            summary.setdefault("live_stress", live)
            summary.setdefault("live_stress_status", live.get("overall"))
        except Exception:
            pass
    log = summary_path.read_text(encoding="utf-8") if summary_path.is_file() else str(run)
    dash = ""
    if (run / "stats" / "stats_dashboard.html").is_file():
        dash = f"/api/ss_diagnostics/stats_dashboard?run={urllib.parse.quote(run.name)}"
    meta = _ss_run_meta(run)
    return {
        "overall": summary.get("overall", "UNKNOWN"),
        "summary": summary,
        "stats_summary": stats,
        "run_dir": str(run),
        "run_id": run.name,
        "run_when": meta.get("when"),
        "run_label": meta.get("label"),
        "log": log,
        "visual_status": summary.get("visual_status", "PENDING"),
        "overall_qa_status": summary.get("overall_qa_status", "UNKNOWN"),
        "machine_full_runtime_status": summary.get(
            "machine_full_runtime_status", summary.get("full_runtime_status")
        ),
        "live_stress_status": summary.get("live_stress_status"),
        "reason": meta.get("reason") or summary.get("reason") or "",
        "stats_dashboard_status": summary.get("stats_dashboard_status")
        or stats.get("stats_dashboard_status"),
        "stats_dashboard_url": dash,
        "has_stats": meta.get("has_stats"),
        "has_live": meta.get("has_live"),
    }


def _ss_python() -> str:
    """Prefer base anaconda (has matplotlib for stats charts) over mlagents env."""
    candidates = [
        os.environ.get("FOREST_SS_PYTHON") or "",
        "/home/reedgern/anaconda3/bin/python",
        str(Path(sys.executable).resolve().parents[2] / "bin" / "python")
        if "envs" in str(Path(sys.executable))
        else "",
        sys.executable,
    ]
    for c in candidates:
        if c and Path(c).is_file():
            return c
    return sys.executable


def _ss_ensure_stats(run: Path) -> Path | None:
    """Build stats/stats_dashboard.html for a run if missing. Returns dashboard path or None."""
    if run is None or not run.is_dir():
        return None
    dash = run / "stats" / "stats_dashboard.html"
    if dash.is_file():
        return dash
    if not (run / "live_stress_report.json").is_file():
        return None
    py = _ss_python()
    stats_script = ROOT / "scripts" / "generate_streaming_survival_test_stats.py"
    try:
        subprocess.run(
            [py, str(stats_script), "--run-dir", str(run)],
            cwd=str(ROOT),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=180,
        )
    except Exception:
        pass
    return dash if dash.is_file() else None


def run_ss_diagnostics(kind: str, run_id: str | None = None) -> dict[str, Any]:
    """Streaming Survival diagnostics (followers). Does not touch Training AI tab."""
    py = _ss_python()
    checks = ROOT / "scripts" / "run_streaming_survival_checks.py"
    export = ROOT / "scripts" / "export_streaming_survival_core_zip.py"
    visual = ROOT / "scripts" / "generate_ss_visual_check_pack.py"
    analyze = ROOT / "scripts" / "analyze_streaming_survival_failures.py"

    if kind in ("list_runs", "runs"):
        runs = _ss_list_runs(60, reports_only=True)
        latest = next((r for r in runs if r.get("has_live")), None)
        if latest is None:
            pointed = _ss_artifacts_latest()
            latest_id = pointed.name if pointed else None
        else:
            latest_id = latest.get("id")
        return {
            "overall": "PASS",
            "runs": runs,
            "latest_id": latest_id,
            "selected_id": (_ss_resolve_run(run_id).name if run_id and _ss_resolve_run(run_id) else None),
        }

    if kind == "last_report":
        run = _ss_resolve_run(run_id)
        if run is None:
            return {"overall": "FAIL", "log": "no test runs yet"}
        return _ss_load_report(run)

    if kind in ("job_status", "diag_job_status"):
        # polled by UI for long SS diagnostics jobs
        return {"overall": "PASS", "log": "use /api/jobs/<id>"}

    if kind == "export_zip":
        proc = subprocess.run(
            [py, str(export)],
            cwd=str(ROOT),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        zip_path = (proc.stdout or "").strip().splitlines()[-1] if proc.stdout else ""
        name = Path(zip_path).name if zip_path else ""
        return {
            "overall": "PASS" if proc.returncode == 0 and zip_path else "FAIL",
            "zip_path": zip_path,
            "zip_url": f"/api/ss_diagnostics/download_zip?name={urllib.parse.quote(name)}" if name else "",
            "log": (proc.stdout or "") + "\n" + (proc.stderr or ""),
        }

    if kind in (
        "live_stress",
        "live_stress_40",
        "live_stress_1",
        "live_stress_1_fast",
        "live_stress_40_fast",
    ):
        attempts = "1" if "1" in kind else "40"
        fast = kind.endswith("_fast")
        scale = "3" if fast else "1"
        eta = (
            ("~20–40 сек" if attempts == "1" else "~7–15 мин")
            if fast
            else ("~1–3 мин" if attempts == "1" else "20–45 минут")
        )
        tag = f"{attempts}_x{scale}" if fast else attempts
        where = "lab_comp" if not ui_runs_on_lab() else "локально"
        return _start_ss_checks_async(
            f"ss_live_stress_{tag}",
            f"fui_ss_live_stress_{tag}",
            f"--live-stress --attempts {attempts} --time-scale {scale}",
            eta_hint=eta,
            log_intro=f"Live Stress {attempts} (×{scale}) на {where}",
        )

    if kind in ("full_runtime_async",):
        where = "lab_comp" if not ui_runs_on_lab() else "локально"
        return _start_ss_checks_async(
            "ss_full_runtime",
            "fui_ss_full_runtime",
            "--full-runtime",
            eta_hint="10–25 минут",
            log_intro=f"Full Machine Checks на {where}",
        )

    if kind in ("stats_dashboard", "open_stats"):
        run = _ss_resolve_run(run_id)
        if run is None:
            return {"overall": "FAIL", "log": "no test runs"}
        dash = _ss_ensure_stats(run) or (run / "stats" / "stats_dashboard.html")
        report = _ss_load_report(run)
        report.update(
            {
                "overall": "PASS" if dash.is_file() else "FAIL",
                "stats_dashboard_path": str(dash) if dash.is_file() else "",
                "stats_dashboard_url": (
                    f"/api/ss_diagnostics/stats_dashboard?run={urllib.parse.quote(run.name)}"
                    if dash.is_file()
                    else ""
                ),
                "log": "stats ok" if dash.is_file() else "stats_dashboard.html missing",
            }
        )
        return report

    if kind in ("visual_pack", "visual_check_pack"):
        run = _ss_resolve_run(run_id)
        cmd = [py, str(visual)]
        if run is not None:
            cmd.extend(["--run-dir", str(run)])
        proc = subprocess.run(
            cmd,
            cwd=str(ROOT),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        run = _ss_resolve_run(run_id) or _ss_artifacts_latest()
        return {
            "overall": "PASS" if proc.returncode == 0 else "FAIL",
            "run_dir": str(run) if run else "",
            "run_id": run.name if run else "",
            "visual_folder": str(run / "screenshots") if run else "",
            "log": (proc.stdout or "") + "\n" + (proc.stderr or ""),
        }

    if kind in ("problem_finder", "analyze"):
        run = _ss_resolve_run(run_id)
        cmd = [py, str(analyze)]
        if run is not None:
            cmd.extend(["--run-dir", str(run)])
        proc = subprocess.run(
            cmd,
            cwd=str(ROOT),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        run = _ss_resolve_run(run_id) or _ss_artifacts_latest()
        report = ""
        if run and (run / "problem_finder_report.md").is_file():
            report = str(run / "problem_finder_report.md")
        out = {
            "overall": "PASS" if proc.returncode == 0 else "FAIL",
            "report_path": report,
            "run_dir": str(run) if run else "",
            "run_id": run.name if run else "",
            "log": (proc.stdout or "") + "\n" + (proc.stderr or ""),
        }
        if run is not None:
            out.update({k: v for k, v in _ss_load_report(run).items() if k not in out})
        return out

    if kind == "open_visual":
        run = _ss_resolve_run(run_id)
        if run is None:
            return {"overall": "FAIL", "log": "no test runs"}
        folder = run / "screenshots"
        folder.mkdir(parents=True, exist_ok=True)
        return {
            "overall": "PASS",
            "run_dir": str(run),
            "run_id": run.name,
            "visual_folder": str(folder),
            "tasks": str(run / "visual_check_tasks.json"),
            "verdicts": str(run / "visual_verdicts.json"),
            "log": f"Visual folder: {folder}",
        }

    if kind == "parser":
        cmd = [py, "-u", str(checks), "--parser-only"]
        proc = subprocess.run(
            cmd,
            cwd=str(ROOT),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=180,
        )
        run = _ss_artifacts_latest()
        summary = {}
        if run and (run / "summary.json").is_file():
            summary = json.loads((run / "summary.json").read_text(encoding="utf-8"))
        return {
            "overall": summary.get("overall") or ("PASS" if proc.returncode == 0 else "FAIL"),
            "summary": summary,
            "run_dir": str(run) if run else "",
            "run_id": run.name if run else "",
            "visual_status": summary.get("visual_status", "PENDING"),
            "overall_qa_status": summary.get("overall_qa_status", "UNKNOWN"),
            "machine_full_runtime_status": summary.get(
                "machine_full_runtime_status", summary.get("full_runtime_status")
            ),
            "log": (proc.stdout or "")[-12000:] + "\n" + (proc.stderr or "")[-4000:],
            "returncode": proc.returncode,
        }

    if kind in ("full_runtime", "world_registry", "scenarios", "all", "e2e", "runtime_suite"):
        where = "lab_comp" if not ui_runs_on_lab() else "локально"
        out = _start_ss_checks_async(
            "ss_full_runtime",
            "fui_ss_full_runtime",
            "--full-runtime",
            eta_hint="10–25 минут",
            log_intro=f"Full Machine Checks / Runtime Suite на {where}",
        )
        out["log"] = (
            out.get("log", "")
            + "\nЭто НЕ ошибка и не Training AI.\n"
            "Проверяет: world registry + сценарии Unity (вода/дом/дерево/телепорты/ground).\n"
            "Не путать с Live Stress 40 (тот шлёт реальные #do 40 раз)."
        )
        return out

    return {"overall": "FAIL", "log": f"unknown kind={kind}"}


def action_train(cfg: dict, hero: str, stage: str, body: dict) -> str:
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    build = cfg.get("build") or DEFAULT_BUILD
    h = cfg.get(hero) or {}
    run_s1 = (body.get("run_s1") or h.get("run_s1") or "").strip()
    run_s2 = (body.get("run_s2") or h.get("run_s2") or "").strip()
    resume = bool(body.get("resume"))
    resume_flag = " --resume" if resume else ""

    scripts = {
        ("jack", "s1"): "train_scripts/lab_comp/train_headless_jack_stage1.bash",
        ("jack", "s2"): "train_scripts/lab_comp/train_headless_jack_stage2.bash",
        ("jack", "cur"): "train_scripts/lab_comp/train_headless_jack_stage1_then_2.bash",
        ("lily", "s1"): "train_scripts/lab_comp/train_headless_lily_stage1.bash",
        ("lily", "s2"): "train_scripts/lab_comp/train_headless_lily_stage2.bash",
        ("lily", "cur"): "train_scripts/lab_comp/train_headless_lily_stage1_then_2.bash",
        ("george", "s1"): "train_scripts/lab_comp/train_headless_george_stage1.bash",
        ("george", "s2"): "train_scripts/lab_comp/train_headless_george_stage2.bash",
        ("george", "cur"): "train_scripts/lab_comp/train_headless_george_stage1_then_2.bash",
    }
    script = scripts[(hero, stage)]
    session = f"fui_{hero}_{stage}"

    if stage == "s1":
        if not run_s1:
            raise ValueError("Укажи номер/id run для Stage1")
        remote = (
            f"cd {REMOTE_DIR} && "
            f"export BUILD={sh_quote(build)} RUN_ID={sh_quote(run_s1)} && "
            f"bash {script}{resume_flag}"
        )
    elif stage == "s2":
        init_from = run_s1
        if not init_from:
            raise ValueError("Для Stage2 нужен Stage1 run (INIT_FROM)")
        rid = run_s2 or f"{init_from}_stage2"
        remote = (
            f"cd {REMOTE_DIR} && "
            f"export BUILD={sh_quote(build)} INIT_FROM={sh_quote(init_from)} RUN_ID={sh_quote(rid)} && "
            f"bash {script}{resume_flag}"
        )
    else:  # curriculum
        if not run_s1:
            raise ValueError("Укажи RUN_ID_STAGE1")
        env = f"export BUILD={sh_quote(build)} RUN_ID_STAGE1={sh_quote(run_s1)}"
        if run_s2:
            env += f" RUN_ID_STAGE2={sh_quote(run_s2)}"
        remote = f"cd {REMOTE_DIR} && {env} && bash {script}"

    return start_remote_tmux(f"train_{hero}_{stage}", host, session, remote)


_validate_lock = threading.Lock()
_validate_active_jid: str | None = None
_validate_proc: subprocess.Popen | None = None


def kill_remote_validates(host: str) -> str:
    """Убить все UI-validate на lab (не трогая train/stream)."""
    r = ssh_run(
        host,
        f"cd {REMOTE_DIR} && bash train_scripts/lab_comp/kill_validate.bash",
        timeout=60,
    )
    return ((r.stdout or "") + (r.stderr or "")).strip()


def action_validate(
    cfg: dict,
    run_id: str,
    task: str,
    seconds: str | int = 30,
    config_yaml: str | None = None,
    hero: str = "jack",
) -> str:
    """SSH: inference + ffmpeg на lab_comp; одновременно только одна validate."""
    global _validate_active_jid, _validate_proc
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    build = cfg.get("build") or DEFAULT_BUILD
    rid = sanitize_run_id(run_id)
    h = sanitize_hero(hero)
    tsk = sanitize_task(task, h)
    conf = (config_yaml or "").strip() or HERO_CONFIG[h]
    if not re.match(r"^[A-Za-z0-9._+-]+\.ya?ml$", conf):
        raise ValueError("config: только имя yaml в custom_configs/")
    try:
        sec = int(str(seconds).strip() or "30")
    except ValueError as e:
        raise ValueError("seconds: целое число") from e
    sec = max(10, min(sec, 180))

    with _validate_lock:
        # Локальный SSH прошлой validate
        old = _validate_proc
        old_jid = _validate_active_jid
        _validate_proc = None
        if old is not None and old.poll() is None:
            try:
                old.terminate()
            except Exception:
                pass
            try:
                old.kill()
            except Exception:
                pass
        if old_jid:
            with _jobs_lock:
                prev = _jobs.get(old_jid)
                if prev and prev.get("status") == "running":
                    prev["status"] = "error"
                    prev["returncode"] = -15
                    prev["ended"] = time.time()

    # На сервере — все старые validate (OOM-защита).
    kill_msg = kill_remote_validates(host)

    remote = (
        f"cd {REMOTE_DIR} && "
        f"bash train_scripts/lab_comp/validate_ui_one.bash "
        f"{sh_quote(build)} {sh_quote(rid)} {sh_quote(tsk)} {sec} "
        f"{sh_quote(conf)} {sh_quote(h)}"
    )
    jid = new_job(f"validate_{h}_{tsk}_{rid}", remote)
    with _validate_lock:
        _validate_active_jid = jid

    def runner() -> None:
        global _validate_active_jid, _validate_proc
        p: subprocess.Popen | None = None
        append_log(jid, f"[validate] kill old: {kill_msg or 'ok'}")
        append_log(jid, f"[validate] hero={h} config={conf}")
        append_log(jid, f"$ ssh {host} {remote}")
        try:
            p = subprocess.Popen(
                [ssh_bin(), "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", host, remote],
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
            )
            with _validate_lock:
                _validate_proc = p
            assert p.stdout is not None
            for line in p.stdout:
                append_log(jid, line.rstrip("\n"))
            code = p.wait()
            finish_job(jid, code)
            append_log(jid, f"[exit {code}]")
        except Exception as e:
            append_log(jid, f"[ERROR] {e}")
            finish_job(jid, 1)
        finally:
            with _validate_lock:
                if _validate_active_jid == jid:
                    _validate_active_jid = None
                if p is not None and _validate_proc is p:
                    _validate_proc = None

    threading.Thread(target=runner, daemon=True).start()
    return jid


def action_joint_train(cfg: dict, body: dict) -> str:
    """Дообучение троих: веса из INIT_FROM_* → новая папка RUN_ID."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    build = cfg.get("build") or DEFAULT_BUILD
    jcfg = cfg.get("joint") or {}
    init_jack = sanitize_run_id(body.get("init_jack") or jcfg.get("init_jack") or "")
    init_lily = sanitize_run_id(body.get("init_lily") or jcfg.get("init_lily") or "")
    init_george = sanitize_run_id(body.get("init_george") or jcfg.get("init_george") or "")
    run_id = sanitize_run_id(body.get("run_id") or jcfg.get("run_id") or "")
    resume = bool(body.get("resume"))
    for src in (init_jack, init_lily, init_george):
        if run_id == src:
            raise ValueError(f"RUN_ID={run_id} совпадает с базой {src} — затрём веса")
    resume_flag = " --resume" if resume else ""
    remote = (
        f"cd {REMOTE_DIR} && "
        f"export BUILD={sh_quote(build)} "
        f"INIT_FROM_JACK={sh_quote(init_jack)} "
        f"INIT_FROM_LILY={sh_quote(init_lily)} "
        f"INIT_FROM_GEORGE={sh_quote(init_george)} "
        f"RUN_ID={sh_quote(run_id)} && "
        f"bash train_scripts/lab_comp/train_headless_jack_lily_george_finetune.bash{resume_flag}"
    )
    return start_remote_tmux("train_joint_jlg", host, "fui_joint_jlg", remote)


def action_joint_stream(cfg: dict, body: dict) -> str:
    """Бесконечная Presentation: стрим onnx по RUN_ID (не короткое видео)."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    build = cfg.get("build") or DEFAULT_BUILD
    jcfg = cfg.get("joint") or {}
    run_id = sanitize_run_id(body.get("run_id") or jcfg.get("run_id") or "")
    if not run_id:
        raise ValueError("Нужен RUN_ID для стрима")
    remote = (
        f"cd {REMOTE_DIR} && "
        f"export BUILD={sh_quote(build)} RUN_ID={sh_quote(run_id)} && "
        f"bash train_scripts/lab_comp/restart_stream_for_run.bash"
    )
    return start_remote_tmux("stream_joint_jlg", host, "fui_joint_stream", remote)


def validate_remote_ready(host: str, run_id: str, task: str, hero: str = "jack") -> dict[str, Any]:
    rid = sanitize_run_id(run_id)
    h = sanitize_hero(hero)
    tsk = sanitize_task(task, h)
    remote = remote_validate_mp4(rid, tsk, h)
    r = ssh_run(
        host,
        f"f=$({remote_expand_cmd(remote)}); "
        f"if [ -f \"$f\" ]; then ls -l \"$f\" | awk '{{print $5}}'; else echo MISSING; fi",
        timeout=30,
    )
    text = (r.stdout or "").strip().splitlines()
    last = text[-1] if text else "MISSING"
    if last == "MISSING" or not last.isdigit():
        return {"ok": True, "ready": False, "run_id": rid, "task": tsk, "hero": h, "remote": remote}
    return {
        "ok": True,
        "ready": True,
        "run_id": rid,
        "task": tsk,
        "hero": h,
        "remote": remote,
        "bytes": int(last),
    }


def validate_pull_mp4(host: str, run_id: str, task: str, hero: str = "jack") -> dict[str, Any]:
    rid = sanitize_run_id(run_id)
    h = sanitize_hero(hero)
    tsk = sanitize_task(task, h)
    local = local_validate_mp4(rid, tsk, h)
    VIDEO_CACHE.mkdir(parents=True, exist_ok=True)
    r = ssh_run(host, remote_expand_cmd(remote_validate_mp4(rid, tsk, h)), timeout=20)
    remote_abs = (r.stdout or "").strip().splitlines()
    remote_abs = remote_abs[-1] if remote_abs else ""
    if not remote_abs or remote_abs.endswith("MISSING"):
        return {"ok": False, "error": "remote path resolve failed", "ready": False}
    check = ssh_run(host, f"test -f {sh_quote(remote_abs)} && echo OK || echo NO", timeout=20)
    if "OK" not in (check.stdout or ""):
        return {"ok": False, "error": f"нет файла на сервере: {remote_abs}", "ready": False}
    # Не scp.exe: UI в WSL, путь /mnt/c/... Windows-scp не открывает.
    # Тянем через ssh+cat, файл пишет сам Python.
    tmp = local.with_suffix(".mp4.part")
    try:
        with tmp.open("wb") as out:
            p = subprocess.run(
                [
                    ssh_bin(),
                    "-o",
                    "BatchMode=yes",
                    "-o",
                    "ConnectTimeout=15",
                    host,
                    f"cat {sh_quote(remote_abs)}",
                ],
                stdout=out,
                stderr=subprocess.PIPE,
                timeout=180,
            )
        if p.returncode != 0 or not tmp.is_file() or tmp.stat().st_size < 100:
            err = (p.stderr or b"").decode("utf-8", errors="replace").strip() or "ssh cat failed"
            tmp.unlink(missing_ok=True)
            return {"ok": False, "error": err, "ready": False}
        tmp.replace(local)
    except Exception as e:
        tmp.unlink(missing_ok=True)
        return {"ok": False, "error": str(e), "ready": False}
    return {"ok": True, "ready": True, "path": str(local), "bytes": local.stat().st_size, "run_id": rid, "task": tsk}


# ── HTML ────────────────────────────────────────────────────────────


HTML = r"""<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>Forest Lab</title>
<style>
:root {
  --bg: #12141a;
  --panel: #1a1d26;
  --panel2: #222632;
  --border: #2e3444;
  --text: #e8eaf0;
  --muted: #9aa3b5;
  --accent: #6aa6ff;
  --ok: #3ecf8e;
  --warn: #e6b84d;
  --bad: #ef6b6b;
  --jack: #6aa6ff;
  --lily: #d88cff;
  --george: #7ad4a0;
}
* { box-sizing: border-box; }
body {
  margin: 0; font-family: "Segoe UI", system-ui, sans-serif;
  background: var(--bg); color: var(--text); line-height: 1.35;
}
header {
  padding: 14px 20px; border-bottom: 1px solid var(--border);
  display: flex; gap: 16px; align-items: center; flex-wrap: wrap;
  background: var(--panel);
}
header h1 { font-size: 18px; margin: 0; font-weight: 650; }
header .meta { color: var(--muted); font-size: 13px; }
main { padding: 16px 20px 40px; display: grid; gap: 16px; }
.row { display: grid; gap: 16px; grid-template-columns: 1fr; }
@media (min-width: 1100px) {
  .heroes { grid-template-columns: 1fr 1fr 1fr; }
  .top { grid-template-columns: 1.1fr 0.9fr; }
}
.card {
  background: var(--panel); border: 1px solid var(--border); border-radius: 10px;
  padding: 14px; display: flex; flex-direction: column; gap: 10px; min-width: 0;
  transition: box-shadow .2s, border-color .2s;
}
.card.running {
  border-color: #ef6b6b;
  box-shadow: 0 0 0 1px rgba(239,107,107,.55), 0 0 18px rgba(239,107,107,.35);
  animation: cardPulse 1.2s ease-in-out infinite;
}
.card.streaming {
  border-color: #6aa6ff;
  box-shadow: 0 0 0 1px rgba(106,166,255,.5), 0 0 16px rgba(106,166,255,.3);
  animation: cardPulseBlue 1.4s ease-in-out infinite;
}
.card.running.streaming {
  border-color: #ef6b6b;
  box-shadow: 0 0 0 1px rgba(239,107,107,.55), 0 0 18px rgba(239,107,107,.35),
              0 0 0 3px rgba(106,166,255,.25);
  animation: cardPulse 1.2s ease-in-out infinite;
}
@keyframes cardPulse {
  0%, 100% { box-shadow: 0 0 0 1px rgba(239,107,107,.45), 0 0 10px rgba(239,107,107,.2); }
  50% { box-shadow: 0 0 0 2px rgba(239,107,107,.85), 0 0 22px rgba(239,107,107,.55); }
}
@keyframes cardPulseBlue {
  0%, 100% { box-shadow: 0 0 0 1px rgba(106,166,255,.4), 0 0 10px rgba(106,166,255,.2); }
  50% { box-shadow: 0 0 0 2px rgba(106,166,255,.8), 0 0 20px rgba(106,166,255,.45); }
}
.subtabs { display: flex; gap: 6px; margin-bottom: 4px; }
.subtab {
  flex: 1; text-align: center; padding: 8px 10px; border-radius: 8px;
  border: 1px solid var(--border); background: var(--panel2); color: var(--muted);
  cursor: pointer; font-size: 13px;
}
.subtab:hover { border-color: var(--accent); color: var(--text); }
.subtab.active {
  color: #d7e6ff; background: #243552; border-color: #3a5f99; font-weight: 600;
}
.subtab.has-live { box-shadow: inset 0 -2px 0 var(--bad); }
.subpanel { display: none; flex-direction: column; gap: 10px; }
.subpanel.active { display: flex; }
.pill.live {
  color: #0b1220; background: var(--bad); border-color: transparent; font-weight: 650;
}
.pill.live-stream {
  color: #0b1220; background: var(--accent); border-color: transparent; font-weight: 650;
}
.card h2 { margin: 0; font-size: 15px; display: flex; align-items: center; gap: 8px; }
.pill {
  font-size: 11px; padding: 2px 8px; border-radius: 999px; border: 1px solid var(--border);
  color: var(--muted);
}
.pill.on { color: #0b1220; background: var(--ok); border-color: transparent; }
.field { display: grid; gap: 4px; }
.field label { font-size: 11px; color: var(--muted); }
.field input, .field select {
  background: var(--panel2); border: 1px solid var(--border); color: var(--text);
  border-radius: 6px; padding: 7px 9px; font-size: 13px; width: 100%;
}
.field select[multiple] { min-height: 120px; padding: 4px; }
.grid2 { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
.btns { display: flex; flex-wrap: wrap; gap: 8px; }
button, .btn {
  appearance: none; border: 1px solid var(--border); background: var(--panel2);
  color: var(--text); border-radius: 7px; padding: 8px 12px; font-size: 13px;
  cursor: pointer;
}
button:hover { border-color: var(--accent); }
button.primary { background: #243552; border-color: #3a5f99; color: #d7e6ff; }
button.danger { background: #3a2226; border-color: #7a3a42; color: #ffd0d4; }
button.jack { border-color: #3a5f99; }
button.lily { border-color: #6b4a8a; }
button.george { border-color: #3d6b52; }
.chart-wrap {
  background: #0f1116; border: 1px solid var(--border); border-radius: 8px;
  padding: 8px; min-height: 180px; cursor: pointer; position: relative;
}
.chart-wrap:hover { border-color: var(--accent); }
.chart-wrap .click-hint {
  position: absolute; right: 8px; top: 6px; font-size: 10px; color: var(--muted);
}
.chart-wrap svg { width: 100%; height: 170px; display: block; }
.legend { display: flex; flex-wrap: wrap; gap: 10px; font-size: 11px; color: var(--muted); }
.legend i { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 4px; }
.modal-bg {
  display: none; position: fixed; inset: 0; background: rgba(0,0,0,.72); z-index: 50;
  padding: 24px; overflow: auto;
}
.modal-bg.open { display: block; }
.modal {
  max-width: 1100px; margin: 0 auto; background: var(--panel); border: 1px solid var(--border);
  border-radius: 12px; padding: 16px 18px 28px;
}
.modal h2 { margin: 0 0 8px; font-size: 18px; }
.modal-block { margin-top: 18px; }
.modal-block h3 { margin: 0 0 8px; font-size: 14px; color: var(--accent); }
.modal-grid { display: grid; gap: 12px; grid-template-columns: 1fr; }
@media (min-width: 900px) { .modal-grid { grid-template-columns: 1fr 1fr; } }
.modal-chart {
  background: #0f1116; border: 1px solid var(--border); border-radius: 8px; padding: 8px; min-height: 160px;
}
.modal-chart svg { width: 100%; height: 150px; display: block; }
.log {
  background: #0d0f14; border: 1px solid var(--border); border-radius: 8px;
  padding: 10px; font-family: ui-monospace, Consolas, monospace; font-size: 11px;
  height: 220px; overflow: auto; white-space: pre-wrap; color: #c9d0de;
}
.hint { font-size: 12px; color: var(--muted); }
.err { color: var(--bad); font-size: 12px; }
.server-pre {
  background: #0d0f14; border: 1px solid var(--border); border-radius: 8px;
  padding: 10px; font-family: ui-monospace, Consolas, monospace; font-size: 11px;
  white-space: pre-wrap; color: #c9d0de; max-height: 160px; overflow: auto; margin: 0;
}
.lab-tasks {
  display: flex; flex-direction: column; gap: 6px; margin: 8px 0 4px;
  min-height: 28px;
}
.lab-task {
  display: flex; align-items: center; gap: 8px;
  background: var(--panel2); border: 1px solid var(--border); border-radius: 8px;
  padding: 6px 8px; font-size: 12px;
}
.lab-task .lab-task-label { flex: 1; min-width: 0; }
.lab-task .lab-task-kind {
  font-size: 10px; padding: 1px 6px; border-radius: 999px; border: 1px solid var(--border);
  color: var(--muted); text-transform: uppercase;
}
.lab-task.kind-train .lab-task-kind { color: #ffd0d4; border-color: #7a3a42; }
.lab-task.kind-stream .lab-task-kind { color: #d7e6ff; border-color: #3a5f99; }
.lab-task.kind-validate .lab-task-kind { color: #ffe6b0; border-color: #8a6a2a; }
.lab-task button.lab-task-x,
.lab-task button.lab-task-r {
  appearance: none; width: 28px; height: 28px; padding: 0; border-radius: 6px;
  border: 1px solid #7a3a42; background: #3a2226; color: #ffd0d4;
  font-size: 15px; line-height: 1; cursor: pointer;
}
.lab-task button.lab-task-r {
  border-color: #3a5f99; background: #1a2430; color: #c8dcff; margin-right: 4px;
}
.lab-task button.lab-task-x:hover { border-color: var(--bad); }
.lab-task button.lab-task-r:hover { border-color: #6a9fff; }
.lab-tasks-empty { font-size: 12px; color: var(--muted); padding: 4px 2px; }
.train-btns button.primary { font-weight: 650; min-width: 140px; }
.llm-status { display:grid; grid-template-columns:1fr 1fr; gap:4px 12px; font-size:12px; margin:8px 0; color:var(--muted); }
.llm-status b { color:var(--text); font-weight:600; }
.llm-chat {
  height: 180px; overflow:auto; background:#0d1118; border:1px solid var(--line);
  border-radius:8px; padding:8px; font-size:12px; margin:6px 0;
}
.llm-chat .row {
  margin: 0 0 6px;
  padding: 0;
  line-height: 1.25;
  white-space: pre-wrap;
  font-family: inherit;
  font-size: inherit;
}
.llm-chat .row.user { color:#9ecbff; }
.llm-chat .row.bot { color:#9fe7b8; }
.llm-chat .row.system { color:#c9b27a; }
.llm-chat .row b { font-weight: 650; }
.llm-setup-log {
  height: 110px; overflow:auto; background:#0d1118; border:1px solid var(--line);
  border-radius:8px; padding:8px; font-size:11px; color:#a8b3c7; white-space:pre-wrap;
}
</style>
</head>
<body>
<header>
  <h1>Forest Lab</h1>
  <span class="meta" id="hdrMeta">старт…</span>
  <span class="meta">UI локальный · train/stats только через ssh lab_comp</span>
  <div style="margin-left:auto" class="btns">
    <button id="btnRefreshTb">Обновить TB</button>
    <button class="danger" id="btnKill" title="Только train: mlagents + headless Unity (стрим/validate не трогает)">⏹ Стоп train</button>
  </div>
</header>
<main>
  <div class="subtabs" style="max-width:720px;margin-bottom:4px">
    <button type="button" class="subtab active" id="tab-mode-training" data-mode="training">Forest Lab Train</button>
    <button type="button" class="subtab" id="tab-mode-streaming" data-mode="streaming">Survival followers</button>
  </div>

  <section class="card" id="card-server">
    <h2>Сервер <code>lab_comp</code> <span id="serverModeLabel">(SSH / local)</span> <span class="pill" id="serverPill">—</span></h2>
    <p class="hint" id="serverHostHint">RAM / nvidia-smi с lab. Если UI запущен на самом lab — режим local (без SSH к себе). С Windows UI ходит по SSH.</p>
    <pre class="server-pre" id="serverStats">загрузка RAM / nvidia-smi с lab_comp…</pre>
    <h2 style="margin-top:10px;font-size:14px">Сейчас на lab <span class="pill" id="labTasksPill">—</span></h2>
    <div class="lab-tasks" id="labTasks"><div class="lab-tasks-empty">нет активных задач</div></div>
    <p class="hint">× гасит только этот тип процесса (train / validate / stream / Streaming Survival). OBS не трогает.</p>
    <h2 style="margin-top:8px">Лог действий <span class="pill" id="jobPill">—</span></h2>
    <div class="field">
      <label>Активная задача</label>
      <select id="jobSelect"></select>
    </div>
    <div class="log" id="jobLog">нет задач</div>
  </section>

  <div id="panel-mode-training" class="mode-panel">
  <section class="card" style="margin-bottom:10px">
    <h2>Блок Forest Lab Train</h2>
    <p class="hint">Одним кликом гасит train + validate + Presentation onnx-стрим. Survival followers и OBS не трогает.</p>
    <div class="btns">
      <button type="button" class="danger" id="btnKillModeTraining">⏹ Стоп весь блок Train</button>
    </div>
  </section>
  <div class="row top">
    <section class="card">
      <h2>Билд + синк <span class="pill" id="deployPill">idle</span></h2>
      <div class="grid2">
        <div class="field">
          <label>BUILD</label>
          <input id="buildName" />
        </div>
        <div class="field">
          <label>TensorBoard URL</label>
          <input id="tbUrl" />
        </div>
      </div>
      <div class="btns">
        <button class="primary" id="btnDeploy">1. Собрать билд скриптом + sync</button>
        <button id="btnSyncOnly">2. Sync билда + скриптов (без сборки)</button>
        <button id="btnSaveCfg">Сохранить настройки</button>
      </div>
      <p class="hint">
        1) <code>build_stream_linux.bash</code> → потом sync на lab_comp.<br/>
        2) не билдит Unity: заливает уже готовый <code>build_versions/…</code> + scripts/configs.
      </p>
    </section>
  </div>

  <h2 style="margin:0;font-size:16px">Валидация (lab_comp → видео здесь)</h2>
  <section class="card" id="card-validate">
    <div class="field">
      <label>Агент (веса из RUN_ID)</label>
      <div class="btns" id="valHeroGroup" style="gap:12px">
        <label style="display:flex;align-items:center;gap:6px;cursor:pointer">
          <input type="radio" name="valHero" value="jack" checked /> Jack
        </label>
        <label style="display:flex;align-items:center;gap:6px;cursor:pointer">
          <input type="radio" name="valHero" value="lily" /> Lily
        </label>
        <label style="display:flex;align-items:center;gap:6px;cursor:pointer">
          <input type="radio" name="valHero" value="george" /> George
        </label>
      </div>
    </div>
    <div class="grid2">
      <div class="field">
        <label>RUN_ID (папка results/…)</label>
        <input id="valRun" value="97_stage2" />
      </div>
      <div class="field">
        <label>Задача</label>
        <select id="valTask"></select>
      </div>
    </div>
    <div class="grid2">
      <div class="field">
        <label>Длительность, сек</label>
        <input id="valSeconds" value="30" />
      </div>
      <div class="field">
        <label>Статус</label>
        <div class="pill" id="valPill">idle</div>
      </div>
    </div>
    <div class="btns">
      <button class="primary" id="btnValidate">▶ Validate на lab_comp + показать видео</button>
      <button class="danger" id="btnKillValidate" title="Убить только validate (mp4). Train и стрим не трогает.">⏹ Стоп validate</button>
      <button id="btnValRefresh">Обновить видео</button>
    </div>
    <p class="hint">
      Выбери агента — берутся только его веса из RUN_ID (для Jack: <code>…/JackLowLevelAgent</code>).
      Задачи меняются по герою. Одновременно одна validate. «Стоп validate» чистит только её память.
    </p>
    <video id="valVideo" controls playsinline style="width:100%;max-height:420px;background:#000;border-radius:8px;border:1px solid var(--border)"></video>
  </section>

  <h2 style="margin:0;font-size:16px">Запуск обучения</h2>
  <div class="row heroes" id="heroes"></div>
  <section class="card" id="card-joint-wrap">
    <div class="subtabs" role="tablist">
      <button type="button" class="subtab active" id="tab-joint-train" data-panel="panel-joint-train">
        Обучение троих <span class="pill" id="pill-joint">finetune</span>
      </button>
      <button type="button" class="subtab" id="tab-joint-stream" data-panel="panel-joint-stream">
        Стрим троих <span class="pill" id="pill-stream">idle</span>
      </button>
    </div>

    <div class="subpanel active" id="panel-joint-train">
      <div class="card" id="card-joint" style="border:none;padding:0;background:transparent;box-shadow:none">
        <h2>Совместное дообучение Jack + Lily + George</h2>
        <p class="hint">
          Укажи папки <code>results/…</code> с готовыми весами троих. Они только читаются.
          Чекпоинты пишутся в новую папку RUN_ID (базы не перезаписываются).
        </p>
        <div class="grid2">
          <div class="field">
            <label>База Jack (INIT_FROM_JACK)</label>
            <input id="jointInitJack" />
          </div>
          <div class="field">
            <label>База Lily (INIT_FROM_LILY)</label>
            <input id="jointInitLily" />
          </div>
        </div>
        <div class="grid2">
          <div class="field">
            <label>База George (INIT_FROM_GEORGE)</label>
            <input id="jointInitGeorge" />
          </div>
          <div class="field">
            <label>Новая папка RUN_ID (куда писать)</label>
            <input id="jointRunId" />
          </div>
        </div>
        <div class="grid2">
          <div class="field">
            <label>TB runs (несколько: Ctrl/Shift+клик)</label>
            <select id="jointTb" multiple size="6"></select>
          </div>
          <div class="field">
            <label>Быстрый выбор</label>
            <div class="btns" style="flex-wrap:wrap">
              <button type="button" id="btnJointTbTrio">Трое из RUN_ID</button>
              <button type="button" id="btnJointTbClear">Сбросить TB</button>
            </div>
            <p class="hint" style="margin:0">На графике сразу Jack + Lily + George (Cumulative Reward).</p>
          </div>
        </div>
        <div class="btns train-btns">
          <button class="primary" id="btnJointTrain">▶ Дообучить троих в новую папку</button>
          <button id="btnJointResume">Resume этой папки</button>
          <button id="btnRestartJointTrain" title="Стоп → чистка процессов → resume той же RUN_ID">↻ Перезапуск обучения</button>
          <button class="danger" id="btnKillJointTrain" title="Убить joint/любой train (mlagents + headless). Стрим и validate не трогает.">⏹ Стоп обучение</button>
        </div>
        <div class="legend" id="legend-joint"></div>
        <div class="chart-wrap" id="chart-joint" title="Клик — все метрики TB" style="cursor:pointer;min-height:140px"></div>
      </div>
    </div>

    <div class="subpanel" id="panel-joint-stream">
      <div class="card" id="card-stream" style="border:none;padding:0;background:transparent;box-shadow:none">
        <h2>Стрим Presentation (трое агентов)</h2>
        <p class="hint">
          Бесконечный стрим по весам из <code>RUN_ID</code> (без MaxStep).
          Короткое mp4 — в блоке Validate выше.
        </p>
        <div class="field">
          <label>RUN_ID для стрима (папка results/…)</label>
          <input id="streamRunId" />
        </div>
        <div class="btns train-btns">
          <button class="primary" id="btnJointStream">▶ Стрим Presentation (бесконечно)</button>
          <button id="btnRestartJointStream" title="Стоп → чистка → старт Presentation заново">↻ Перезапуск стрима</button>
          <button class="danger" id="btnKillStream" title="Убить только бесконечный стрим. Train и validate не трогает.">⏹ Стоп стрим</button>
        </div>
        <p class="hint">Синхронизируется с RUN_ID на вкладке обучения при сохранении конфига.</p>
      </div>
    </div>
  </section>
  </div><!-- /panel-mode-training -->

  <div id="panel-mode-streaming" class="mode-panel" style="display:none">
  <section class="card" style="margin-bottom:10px">
    <h2>Блок Survival followers</h2>
    <p class="hint">Одним кликом гасит Streaming Survival + LLM Bot. Train / Presentation onnx / OBS не трогает.</p>
    <div class="btns">
      <button type="button" class="danger" id="btnKillModeStreaming">⏹ Стоп весь блок Survival</button>
    </div>
  </section>
    <section class="card streaming" id="card-ss">
      <h2>Streaming Survival <span class="pill" id="ssPill">idle</span></h2>
      <p class="hint">Отдельная survival-среда со scripted follower-персонажами зрителей. Без обучения. Train / validate / Presentation onnx не трогает.</p>
      <div class="btns" style="flex-wrap:wrap">
        <button type="button" class="primary" id="btnSsStart">▶ Start Streaming Survival</button>
        <button type="button" class="danger" id="btnSsStop">⏹ Stop Streaming Survival</button>
      </div>
      <p class="hint">Команды чата: <code>#join</code> · <code>#do добывай воду</code> · <code>#do руби дерево</code> · <code>#do убивай овечек</code> · <code>#do поставь костер</code></p>
    </section>

    <section class="card" id="card-ss-preview">
      <h2>Lab screen preview <span class="pill" id="ssPreviewPill">off</span></h2>
      <p class="hint">Живой скриншот <b>lab_comp DISPLAY=:1</b> (Unity Streaming Survival) через SSH. Не трогает train / OBS.</p>
      <div class="btns" style="flex-wrap:wrap">
        <button type="button" class="primary" id="btnSsPreviewStart">▶ Start preview</button>
        <button type="button" class="danger" id="btnSsPreviewStop">⏹ Stop preview</button>
        <button type="button" id="btnSsPreviewOnce">1 кадр</button>
      </div>
      <p class="hint" id="ssPreviewMeta" style="min-height:1.2em">preview off</p>
      <div style="margin-top:8px;background:#0b0f16;border:1px solid #2a3344;border-radius:8px;overflow:hidden;min-height:180px;display:flex;align-items:center;justify-content:center">
        <img id="ssPreviewImg" alt="lab screen preview" style="max-width:100%;width:100%;height:auto;display:none;background:#000" />
        <span id="ssPreviewPlaceholder" style="color:#7a8499;font-size:13px;padding:24px">Нажми Start preview</span>
      </div>
    </section>

    <section class="card" id="card-llm-bot">
      <h2>LLM Bot <span class="pill" id="llmBotPill">stopped</span></h2>
      <p class="hint">Бот на <b>lab_comp</b> → UDP :5055 в Streaming Survival. Local debug без follower-check.</p>
      <div class="llm-status" id="llmStatusGrid">
        <div>Bot: <b id="llmStBot">—</b></div>
        <div>Mode: <b id="llmStMode">—</b></div>
        <div>Python deps: <b id="llmStDeps">—</b></div>
        <div>Ollama: <b id="llmStOllama">—</b></div>
        <div>Model: <b id="llmStModel">—</b></div>
        <div>HTTP :8765: <b id="llmStHttp">—</b></div>
      </div>
      <p class="hint" id="llmLastError" style="color:#ff8e8e;min-height:1em"></p>
      <div class="btns" style="flex-wrap:wrap">
        <button type="button" class="primary" id="btnLlmStart">Start LLM Bot</button>
        <button type="button" class="danger" id="btnLlmStop">Stop LLM Bot</button>
        <button type="button" id="btnLlmListen">Listen Twitch Chat: OFF</button>
      </div>
      <label style="font-size:12px;color:var(--muted);margin-top:10px;display:block">Local Debug Chat — #join / #do</label>
      <div class="llm-chat" id="llmChat"></div>
      <div class="btns" style="align-items:stretch;flex-wrap:wrap">
        <input id="llmLocalNick" style="width:140px" placeholder="ник" value="viewer" title="Ник персонажа в игре" />
        <input id="llmLocalMsg" style="flex:1;min-width:120px" placeholder="#join или #do добывай воду"
          value="#do добывай воду" />
        <button type="button" class="primary" id="btnLlmLocalSend">Send</button>
        <button type="button" id="btnLlmExitDebug" title="Убрать debug_user из мира">#exit debug_user</button>
        <button type="button" id="btnLlmChatClear">Clear</button>
      </div>
      <label style="font-size:12px;color:var(--muted);margin-top:10px;display:block">Debug actions (клик = #do)</label>
      <div class="btns" id="ssDebugActions" style="flex-wrap:wrap;gap:6px;margin-top:6px"></div>
      <label style="font-size:12px;color:var(--muted);margin-top:10px;display:block">Setup / bot logs</label>
      <pre class="llm-setup-log" id="llmBotLog">(пусто)</pre>
    </section>

    <section class="card" id="card-ss-diagnostics">
      <h2>Streaming Survival Diagnostics</h2>
      <div class="btns" style="flex-wrap:wrap;gap:6px">
        <button type="button" class="primary" id="btnSsDiagLiveStress1" title="~1–3 мин, 1 live #do, обычная скорость">Live Stress 1</button>
        <button type="button" id="btnSsDiagLiveStress1Fast" title="~20–40 сек, 1 live #do, Time.timeScale=3">Live Stress 1 ×3</button>
        <button type="button" id="btnSsDiagLiveStress" title="~20–45 мин, 40 live #do, обычная скорость">Live Stress 40</button>
        <button type="button" id="btnSsDiagLiveStressFast" title="~7–15 мин, 40 live #do, Time.timeScale=3">Live Stress 40 ×3</button>
        <button type="button" id="btnSsDiagStats" title="Открыть дашборд выбранного репорта">Дашборд</button>
        <button type="button" id="btnSsRunRefresh" title="Обновить список">Обновить</button>
      </div>
      <div style="margin-top:12px">
        <div id="ssRunList" role="listbox" aria-label="Live Stress reports"
          style="max-height:320px;overflow:auto;border:1px solid #555;border-radius:8px;background:rgba(0,0,0,.15)">
          <div class="hint" style="padding:10px">Загрузка…</div>
        </div>
        <p class="hint" id="ssRunMeta" style="margin-top:6px"></p>
      </div>
      <!-- hidden status hooks for JS (not shown) -->
      <span id="ssDiagParserStatus" style="display:none"></span>
      <span id="ssDiagRuntimeStatus" style="display:none"></span>
      <span id="ssDiagLiveStatus" style="display:none"></span>
      <span id="ssDiagVisualStatus" style="display:none"></span>
      <span id="ssDiagStatsStatus" style="display:none"></span>
      <span id="ssDiagQaStatus" style="display:none"></span>
      <span id="ssDiagContinuity" style="display:none"></span>
      <p class="hint" id="ssDiagStatus" style="min-height:1.2em;margin-top:8px"></p>
      <pre class="llm-setup-log" id="ssDiagLog" style="max-height:200px;display:none"></pre>
    </section>
  </div><!-- /panel-mode-streaming -->
</main>
<div class="modal-bg" id="detailModal">
  <div class="modal">
    <div class="btns" style="justify-content:space-between;align-items:center">
      <h2 id="detailTitle">Все метрики (TensorBoard)</h2>
      <button id="detailClose">Закрыть</button>
    </div>
    <p class="hint">Данные с lab_comp TensorBoard (как дашборд по блокам). Comet в проекте нет — все scalars TB.</p>
    <div id="detailBody">загрузка…</div>
  </div>
</div>
<script>
const COLORS = ["#6aa6ff","#ef6b6b","#3ecf8e","#e6b84d","#d88cff","#7ad4a0","#9aa3b5"];
const MAIN_TAG = "Environment/Cumulative Reward";
let CFG = null;
let TB_RUNS = [];
let LAST_JOB = null;

async function api(path, opts) {
  let r;
  const ctrl = new AbortController();
  // server_stats/llm status не должны висеть вечно в браузере
  const soft = (path || "").indexOf("/api/server_stats") === 0
    || (path || "").indexOf("/api/llm_bot/status") === 0
    || (path || "").indexOf("/api/llm_bot/local_chat") === 0
    || (path || "").indexOf("/api/ss_preview/") === 0
    || (path || "").indexOf("/api/tb/") === 0;
  const ms = (path || "").indexOf("/api/ss_diagnostics/") === 0 ? 600000
    : (soft ? 12000 : 180000);
  const timer = setTimeout(() => ctrl.abort(), ms);
  try {
    r = await fetch(path, Object.assign({}, opts || {}, { signal: ctrl.signal }));
  } catch (e) {
    const name = (e && e.name) || "";
    if (name === "AbortError") throw new Error("timeout (" + path + ")");
    throw new Error("Failed to fetch (" + path + ") — UI занят/перезапусти");
  } finally {
    clearTimeout(timer);
  }
  const txt = await r.text();
  let j = {};
  try { j = txt ? JSON.parse(txt) : {}; } catch (_) {
    throw new Error("bad JSON from " + path);
  }
  if (!r.ok) throw new Error(j.error || r.statusText || ("HTTP " + r.status));
  return j;
}

function el(tag, attrs={}, kids=[]) {
  const n = document.createElement(tag);
  for (const [k,v] of Object.entries(attrs)) {
    if (k === "className") n.className = v;
    else if (k === "text") n.textContent = v;
    else if (k.startsWith("on") && typeof v === "function") n.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k === "style" && typeof v === "string") n.setAttribute("style", v);
    else n.setAttribute(k, v);
  }
  for (const c of kids) n.append(c);
  return n;
}

function heroCard(hero, title, color) {
  const h = (CFG && CFG[hero]) || {};
  const root = el("section", {className:"card", id:`card-${hero}`});
  const chart = el("div", {
    className:"chart-wrap",
    id:`chart-${hero}`,
    title:"Нажми — все метрики TB",
    onClick: () => openDetail(hero, title),
  }, [el("span", {className:"click-hint", text:"клик → все графики"})]);
  root.append(
    el("h2", {}, [
      el("span", {text: title, style:`color:${color}`}),
      el("span", {className:"pill", id:`pill-${hero}`, text:"Cumulative Reward"}),
    ]),
    chart,
    el("div", {className:"legend", id:`legend-${hero}`}),
    el("div", {className:"grid2"}, [
      fieldSelect(`tb1-${hero}`, "TB Stage1", h.tb_s1),
      fieldSelect(`tb2-${hero}`, "TB Stage2", h.tb_s2),
    ]),
    el("div", {className:"grid2"}, [
      fieldInput(`run1-${hero}`, "База Stage1 = INIT_FROM (веса)", h.run_s1),
      fieldInput(`run2-${hero}`, "Папка Stage2 = RUN_ID (куда писать)", h.run_s2),
    ]),
    el("div", {className:"btns train-btns"}, [
      btn("▶ Запуск Stage1", "primary "+hero, () => train(hero,"s1")),
      btn("▶ Запуск Stage2", "primary "+hero, () => train(hero,"s2")),
      btn("▶ Curriculum 1→2", "primary "+hero, () => train(hero,"cur")),
      btn("Resume Stage1", "", () => train(hero,"s1", true)),
      btn("Resume Stage2", "", () => train(hero,"s2", true)),
    ]),
    el("p", {className:"hint", text: hintFor(hero)}),
  );
  return root;
}

function hintFor(hero) {
  if (hero==="jack") return "Stage2 читает веса из INIT_FROM (левое поле), пишет в RUN_ID (правое). Пример: 97 → 97_stage2; для базы 100 поставь слева 100, справа 100_stage2.";
  return "Stage2: INIT_FROM=левое поле, RUN_ID=правое. Curriculum Stage1→Stage2 на lab_comp.";
}

function fieldInput(id, label, value) {
  return el("div", {className:"field"}, [
    el("label", {text: label}),
    el("input", {id, value: value||""}),
  ]);
}

function fieldSelect(id, label, value) {
  const s = el("select", {id});
  s.append(el("option", {value:"", text:"— не выбран —"}));
  for (const r of TB_RUNS) {
    const o = el("option", {value:r, text:r});
    if (r === value) o.selected = true;
    s.append(o);
  }
  if (value && ![...s.options].some(o => o.value===value)) {
    const o = el("option", {value, text:value});
    o.selected = true;
    s.append(o);
  }
  return el("div", {className:"field"}, [el("label", {text:label}), s]);
}

function btn(text, cls, onClick) {
  return el("button", {className: cls, text, onClick});
}

function collectHero(hero) {
  return {
    run_s1: document.getElementById(`run1-${hero}`).value.trim(),
    run_s2: document.getElementById(`run2-${hero}`).value.trim(),
    tb_s1: document.getElementById(`tb1-${hero}`).value.trim(),
    tb_s2: document.getElementById(`tb2-${hero}`).value.trim(),
  };
}

function collectJoint() {
  const sel = document.getElementById("jointTb");
  const tb_runs = [...sel.selectedOptions].map((o) => o.value.trim()).filter(Boolean);
  return {
    init_jack: document.getElementById("jointInitJack").value.trim(),
    init_lily: document.getElementById("jointInitLily").value.trim(),
    init_george: document.getElementById("jointInitGeorge").value.trim(),
    run_id: document.getElementById("jointRunId").value.trim(),
    tb_run: tb_runs[0] || "",
    tb_runs,
  };
}

function jointPreferRuns(runId) {
  const rid = (runId || "").trim();
  if (!rid) return [];
  return ["JackLowLevelAgent","LilyLowLevelAgent","GeorgeLowLevelAgent"].map((b) => `${rid}/${b}`);
}

function fillJointCard() {
  const j = (CFG && CFG.joint) || {};
  document.getElementById("jointInitJack").value = j.init_jack || "";
  document.getElementById("jointInitLily").value = j.init_lily || "";
  document.getElementById("jointInitGeorge").value = j.init_george || "";
  document.getElementById("jointRunId").value = j.run_id || "";
  const streamRun = document.getElementById("streamRunId");
  if (streamRun) streamRun.value = j.run_id || "";
  const sel = document.getElementById("jointTb");
  let cur = Array.isArray(j.tb_runs) ? j.tb_runs.filter(Boolean) : [];
  if (!cur.length && j.tb_run) cur = [j.tb_run];
  // Если ничего не выбрано — по умолчанию трое из RUN_ID (если есть в TB).
  if (!cur.length) cur = jointPreferRuns(j.run_id);

  sel.innerHTML = "";
  const prefer = jointPreferRuns(j.run_id);
  const seen = new Set();
  for (const r of [...prefer, ...(TB_RUNS||[])]) {
    if (!r || seen.has(r)) continue;
    seen.add(r);
    const o = el("option", {value:r, text:r});
    if (cur.includes(r)) o.selected = true;
    sel.append(o);
  }
  // Сохранить выбранные, которых ещё нет в списке TB
  for (const r of cur) {
    if (seen.has(r)) continue;
    const o = el("option", {value:r, text:r});
    o.selected = true;
    sel.append(o);
  }
}

function selectJointTbTrio() {
  const rid = document.getElementById("jointRunId").value.trim();
  const want = new Set(jointPreferRuns(rid));
  if (!want.size) { flash("hdrMeta", "joint: укажи RUN_ID"); return; }
  const sel = document.getElementById("jointTb");
  // Добавить опции, если TB ещё не знает run
  for (const r of want) {
    if (![...sel.options].some((o) => o.value === r)) {
      sel.append(el("option", {value:r, text:r}));
    }
  }
  for (const o of sel.options) o.selected = want.has(o.value);
  loadJointChart();
  saveCfg();
}

function clearJointTb() {
  const sel = document.getElementById("jointTb");
  for (const o of sel.options) o.selected = false;
  loadJointChart();
  saveCfg();
}

async function trainJoint(resume=false) {
  await saveCfg();
  const j = collectJoint();
  if (!j.init_jack || !j.init_lily || !j.init_george || !j.run_id) {
    flash("hdrMeta", "joint: заполни 3 базы и RUN_ID");
    return;
  }
  const resp = await api("/api/action", {
    method:"POST", headers:{"Content-Type":"application/json"},
    body: JSON.stringify({action:"joint_train", resume, ...j}),
  });
  LAST_JOB = resp.job_id;
  flash("hdrMeta", `joint train → ${j.run_id}`);
  pollJobs();
}

async function startJointStream() {
  await saveCfg();
  const run_id = (document.getElementById("streamRunId").value.trim()
    || document.getElementById("jointRunId").value.trim());
  if (!run_id) { flash("hdrMeta", "stream: укажи RUN_ID"); return; }
  document.getElementById("jointRunId").value = run_id;
  document.getElementById("streamRunId").value = run_id;
  try {
    const resp = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"joint_stream", run_id }),
    });
    LAST_JOB = resp.job_id;
    flash("hdrMeta", `stream Presentation → ${run_id}`);
    pollJobs();
  } catch (e) {
    alert(e.message||e);
  }
}

function switchJointTab(panelId) {
  for (const btn of document.querySelectorAll("#card-joint-wrap .subtab")) {
    const on = btn.dataset.panel === panelId;
    btn.classList.toggle("active", on);
  }
  for (const panel of document.querySelectorAll("#card-joint-wrap .subpanel")) {
    panel.classList.toggle("active", panel.id === panelId);
  }
  saveUiView();
}

function switchModeTab(mode) {
  const train = mode === "training";
  document.getElementById("tab-mode-training").classList.toggle("active", train);
  document.getElementById("tab-mode-streaming").classList.toggle("active", !train);
  document.getElementById("panel-mode-training").style.display = train ? "" : "none";
  document.getElementById("panel-mode-streaming").style.display = train ? "none" : "";
  saveUiView();
}

const UI_VIEW_KEY = "forest_lab_ui_view_v1";
let _saveUiViewTimer = null;

function _nearestVisibleCardId() {
  const targetY = window.scrollY + Math.min(140, window.innerHeight * 0.18);
  let best = "";
  let bestDist = Infinity;
  for (const el of document.querySelectorAll("section.card[id]")) {
    const panel = el.closest(".mode-panel");
    if (panel && getComputedStyle(panel).display === "none") continue;
    const top = el.getBoundingClientRect().top + window.scrollY;
    const dist = Math.abs(top - targetY);
    if (dist < bestDist) {
      bestDist = dist;
      best = el.id;
    }
  }
  return best;
}

function saveUiView() {
  try {
    const streaming = !!(document.getElementById("tab-mode-streaming")
      && document.getElementById("tab-mode-streaming").classList.contains("active"));
    const jointBtn = document.querySelector("#card-joint-wrap .subtab.active");
    const view = {
      mode: streaming ? "streaming" : "training",
      joint: (jointBtn && jointBtn.dataset.panel) || "panel-joint-train",
      scrollY: Math.max(0, window.scrollY || window.pageYOffset || 0),
      anchor: _nearestVisibleCardId() || "",
    };
    localStorage.setItem(UI_VIEW_KEY, JSON.stringify(view));
  } catch (_) {}
}

function restoreUiView() {
  let view = null;
  try {
    view = JSON.parse(localStorage.getItem(UI_VIEW_KEY) || "null");
  } catch (_) {
    view = null;
  }
  if (!view || typeof view !== "object") return;
  if (view.mode === "streaming" || view.mode === "training") {
    // Avoid recursive save noise while restoring.
    const train = view.mode === "training";
    document.getElementById("tab-mode-training").classList.toggle("active", train);
    document.getElementById("tab-mode-streaming").classList.toggle("active", !train);
    document.getElementById("panel-mode-training").style.display = train ? "" : "none";
    document.getElementById("panel-mode-streaming").style.display = train ? "none" : "";
  }
  if (view.joint) {
    for (const btn of document.querySelectorAll("#card-joint-wrap .subtab")) {
      btn.classList.toggle("active", btn.dataset.panel === view.joint);
    }
    for (const panel of document.querySelectorAll("#card-joint-wrap .subpanel")) {
      panel.classList.toggle("active", panel.id === view.joint);
    }
  }
  const applyScroll = () => {
    if (view.anchor) {
      const el = document.getElementById(view.anchor);
      if (el && getComputedStyle(el).display !== "none") {
        el.scrollIntoView({ block: "start", behavior: "auto" });
        return;
      }
    }
    if (typeof view.scrollY === "number" && view.scrollY > 0) {
      window.scrollTo(0, view.scrollY);
    }
  };
  requestAnimationFrame(() => requestAnimationFrame(applyScroll));
  setTimeout(applyScroll, 250);
  setTimeout(applyScroll, 800);
}

function bindUiViewPersistence() {
  window.addEventListener("scroll", () => {
    clearTimeout(_saveUiViewTimer);
    _saveUiViewTimer = setTimeout(saveUiView, 120);
  }, { passive: true });
  window.addEventListener("beforeunload", saveUiView);
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden") saveUiView();
  });
}

async function startStreamingSurvival() {
  flash("hdrMeta", "start Streaming Survival…");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"start_streaming_survival" }),
    });
    LAST_JOB = j.job_id;
    document.getElementById("ssPill").textContent = "starting";
    pollJobs();
  } catch (e) { alert(e.message||e); }
}

async function stopStreamingSurvival() {
  if (!confirm("Остановить Streaming Survival? Train / Presentation onnx не трогаем.")) return;
  flash("hdrMeta", "stop Streaming Survival…");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"stop_streaming_survival" }),
    });
    LAST_JOB = j.job_id;
    document.getElementById("ssPill").textContent = "stopping";
    pollJobs();
  } catch (e) { alert(e.message||e); }
}

async function killValidateOnly() {
  if (!confirm("Остановить только validate (mp4)? Train и стрим не трогаем.")) return;
  flash("hdrMeta", "стоп validate…");
  document.getElementById("valPill").textContent = "stopping";
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"kill_validate" }),
    });
    LAST_JOB = j.job_id;
    document.getElementById("valPill").textContent = "stopped";
    document.getElementById("valPill").className = "pill";
    pollJobs();
  } catch (e) {
    alert(e.message||e);
  }
}

async function killStreamOnly() {
  if (!confirm("Остановить только бесконечный стрим Presentation? Train и validate не трогаем.")) return;
  flash("hdrMeta", "стоп стрим…");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"kill_stream" }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    setTimeout(pollServer, 1500);
  } catch (e) {
    alert(e.message||e);
  }
}

async function killModeTraining() {
  if (!confirm("Стоп весь блок Forest Lab Train?\\n\\nУбьёт: train + validate + Presentation stream.\\nНе трогает: Survival followers, OBS.")) return;
  flash("hdrMeta", "стоп блок Train…");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"kill_mode_training" }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    setTimeout(pollServer, 1500);
    setTimeout(pollServer, 5000);
  } catch (e) { alert(e.message||e); }
}

async function killModeStreaming() {
  if (!confirm("Стоп весь блок Survival followers?\\n\\nУбьёт: Streaming Survival + LLM Bot.\\nНе трогает: train, Presentation onnx, OBS.")) return;
  flash("hdrMeta", "стоп блок Survival…");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"kill_mode_streaming" }),
    });
    LAST_JOB = j.job_id;
    try { await pollLlmBot(); } catch (_) {}
    pollJobs();
    setTimeout(pollServer, 1500);
    setTimeout(pollServer, 5000);
  } catch (e) { alert(e.message||e); }
}

async function killTrainOnly() {
  if (!confirm("Остановить train на lab (mlagents + headless Unity)? Стрим и validate не трогаем.")) return;
  flash("hdrMeta", "стоп train…");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"kill_train" }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    setTimeout(pollServer, 1500);
    setTimeout(pollServer, 4000);
  } catch (e) {
    alert(e.message||e);
  }
}

const LLM_API = "/api/llm_bot";
let LLM_LISTEN = false;
let LLM_POLL_TIMER = null;

function renderSsDebugActions(s) {
  const host = document.getElementById("ssDebugActions");
  if (!host) return;
  const acts = (s && Array.isArray(s.available_actions) && s.available_actions.length)
    ? s.available_actions
    : [
    {action:"collect_water", hint:"вода"},
    {action:"collect_wood", hint:"дерево"},
    {action:"collect_food", hint:"еда"},
    {action:"kill_sheep", hint:"овечки"},
    {action:"build_campfire", hint:"костёр"},
    {action:"go_home", hint:"к дому"},
    {action:"idle", hint:"ждать"},
  ];
  // доп. кнопки цепочек
  const extras = [
    {label:"#join", msg:"#join"},
    {label:"#exit", msg:"#exit"},
    {label:"10 воды→10 дерева", msg:"#do добудь 10 воды затем 10 дерева"},
    {label:"гуляй", msg:"#do гуляй"},
  ];
  host.innerHTML = "";
  for (const a of acts) {
    const b = document.createElement("button");
    b.type = "button";
    b.textContent = a.action;
    b.title = a.hint || a.action;
    b.onclick = () => ssDebugSend("#do " + (a.hint ? a.hint.split("/")[0].trim() : a.action));
    host.append(b);
  }
  for (const e of extras) {
    const b = document.createElement("button");
    b.type = "button";
    b.textContent = e.label;
    b.onclick = () => ssDebugSend(e.msg);
    host.append(b);
  }
}

async function ssDebugSend(message) {
  const input = document.getElementById("llmLocalMsg");
  if (input) input.value = message;
  const nickEl = document.getElementById("llmLocalNick");
  const username = ((nickEl && nickEl.value) || "viewer").trim() || "viewer";
  try {
    const j = await api(LLM_API + "/local_chat", {
      method: "POST", headers: {"Content-Type":"application/json"},
      body: JSON.stringify({ username, message }),
    });
    renderLlmStatus(j.status || j);
    setTimeout(pollLlmBot, 800);
    setTimeout(pollLlmBot, 2500);
  } catch (e) { alert(e.message || e); }
}

function renderLlmStatus(s) {
  if (!s) return;
  renderSsDebugActions(s);
  const pill = document.getElementById("llmBotPill");
  const st = s.bot_state || "—";
  const live = (st === "running" || st === "starting");
  if (pill) {
    pill.textContent = st;
    pill.className = "pill" + (live ? " live" : "");
  }
  setCardState("card-llm-bot", live, false);
  const modeSs = document.getElementById("tab-mode-streaming");
  if (modeSs) {
    const ssLive = !!(document.getElementById("card-ss") && document.getElementById("card-ss").classList.contains("running"));
    modeSs.classList.toggle("has-live", ssLive || live);
  }
  const set = (id, v) => { const el = document.getElementById(id); if (el) el.textContent = v; };
  set("llmStBot", st + (s.host ? (" @ " + s.host) : " @ lab_comp"));
  LLM_LISTEN = !!s.listen_stream;
  set("llmStMode", LLM_LISTEN ? "Twitch stream" : "Local debug");
  set("llmStDeps", s.python_deps || "—");
  set("llmStOllama", s.ollama || "—");
  set("llmStModel", (s.model || "—") + (s.ollama_model ? " (" + s.ollama_model + ")" : ""));
  set("llmStHttp", s.bot_http_ready ? "ready" : "down");
  const err = document.getElementById("llmLastError");
  if (err) err.textContent = s.last_error ? ("Last error: " + s.last_error) : "";
  const btnL = document.getElementById("btnLlmListen");
  if (btnL) btnL.textContent = LLM_LISTEN ? "Listen Twitch Chat: ON" : "Listen Twitch Chat: OFF";

  const logEl = document.getElementById("llmBotLog");
  if (logEl && Array.isArray(s.logs)) {
    logEl.textContent = s.logs.length ? s.logs.join("\n") : "(пусто)";
    logEl.scrollTop = logEl.scrollHeight;
  }
  const chat = document.getElementById("llmChat");
  if (chat && Array.isArray(s.chat)) {
    // не дёргать вниз, если пользователь читает историю сверху
    const distBottom = chat.scrollHeight - chat.scrollTop - chat.clientHeight;
    const stickBottom = distBottom < 56;
    chat.innerHTML = "";
    const esc = (t) => String(t)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    for (const m of s.chat) {
      const body = String(m.text || "").replace(/^[\s\u00a0\u200b\uFEFF\u2028\u2029]+|[\s\u00a0\u200b\uFEFF\u2028\u2029]+$/g, "");
      if (!body) continue;
      const r = (m.role === "bot" || m.role === "system") ? m.role : "user";
      const who = (m.role || "user") + ":";
      const row = document.createElement("div");
      row.className = "row " + r;
      // <b>role:</b> + один <br> + текст — без пустой строки
      row.innerHTML = "<b>" + esc(who) + "</b><br>" + esc(body);
      chat.append(row);
    }
    if (stickBottom) chat.scrollTop = chat.scrollHeight;
  }
}

async function pollLlmBot() {
  try {
    const s = await api(LLM_API + "/status");
    renderLlmStatus(s);
  } catch (e) {
    const pill = document.getElementById("llmBotPill");
    if (pill) pill.textContent = "ui-err";
  }
}

function wireLlmBotUi() {
  if (!document.getElementById("btnLlmStart")) return;
  document.getElementById("btnLlmStart").onclick = async () => {
    flash("hdrMeta", "LLM bot starting…");
    try {
      const s = await api(LLM_API + "/start", { method: "POST", headers: {"Content-Type":"application/json"}, body: "{}" });
      renderLlmStatus(s);
    } catch (e) { alert(e.message || e); }
  };
  document.getElementById("btnLlmStop").onclick = async () => {
    try {
      const s = await api(LLM_API + "/stop", { method: "POST", headers: {"Content-Type":"application/json"}, body: "{}" });
      renderLlmStatus(s);
    } catch (e) { alert(e.message || e); }
  };
  document.getElementById("btnLlmListen").onclick = async () => {
    const next = !LLM_LISTEN;
    try {
      const j = await api(LLM_API + "/mode", {
        method: "POST", headers: {"Content-Type":"application/json"},
        body: JSON.stringify({ listen_stream: next }),
      });
      renderLlmStatus(j.status || j);
    } catch (e) { alert(e.message || e); }
  };
  const send = async () => {
    const input = document.getElementById("llmLocalMsg");
    const nickEl = document.getElementById("llmLocalNick");
    const message = (input.value || "").trim();
    const username = ((nickEl && nickEl.value) || "viewer").trim() || "viewer";
    if (!message) return;
    input.value = "";
    try {
      // accepted сразу; ответ бота подтянет pollLlmBot
      const j = await api(LLM_API + "/local_chat", {
        method: "POST", headers: {"Content-Type":"application/json"},
        body: JSON.stringify({ username, message }),
      });
      renderLlmStatus(j.status || j);
      setTimeout(pollLlmBot, 800);
      setTimeout(pollLlmBot, 2500);
    } catch (e) { alert(e.message || e); }
  };
  document.getElementById("btnLlmLocalSend").onclick = send;
  const btnExitDbg = document.getElementById("btnLlmExitDebug");
  if (btnExitDbg) {
    btnExitDbg.onclick = async () => {
      try {
        await api(LLM_API + "/local_chat", {
          method: "POST", headers: {"Content-Type":"application/json"},
          body: JSON.stringify({ username: "debug_user", message: "#exit" }),
        });
        setTimeout(pollLlmBot, 600);
      } catch (e) { alert(e.message || e); }
    };
  }
  document.getElementById("llmLocalMsg").addEventListener("keydown", (ev) => {
    if (ev.key === "Enter" && !ev.shiftKey) { ev.preventDefault(); send(); }
  });
  document.getElementById("btnLlmChatClear").onclick = async () => {
    try {
      await api(LLM_API + "/clear_chat", { method: "POST", headers: {"Content-Type":"application/json"}, body: "{}" });
      await pollLlmBot();
    } catch (e) { alert(e.message || e); }
  };
  pollLlmBot();
  if (LLM_POLL_TIMER) clearInterval(LLM_POLL_TIMER);
  LLM_POLL_TIMER = setInterval(pollLlmBot, 2000);
}

async function loadJointChart() {
  const host = document.getElementById("chart-joint");
  const leg = document.getElementById("legend-joint");
  const sel = document.getElementById("jointTb");
  const runs = [...sel.selectedOptions].map((o) => o.value.trim()).filter(Boolean);
  if (!runs.length) {
    drawChartInto(host, leg, {}, 140);
    if (host && !host.querySelector("svg")) host.textContent = "нет данных TB (выбери run / обнови TB)";
    document.getElementById("pill-joint").textContent = "finetune";
    return;
  }
  try {
    const q = new URLSearchParams({runs: runs.join("|"), tags: MAIN_TAG});
    const data = await api("/api/tb/series?"+q.toString());
    const series = {};
    Object.keys(data.series||{}).forEach((k) => {
      const runName = k.includes(" · ") ? k.split(" · ")[0] : k;
      series[runName] = data.series[k];
    });
    drawChartInto(host, leg, series, 140);
    document.getElementById("pill-joint").textContent = Object.keys(series).length
      ? `TB ${Object.keys(series).length}`
      : "нет точек";
  } catch (e) {
    document.getElementById("pill-joint").textContent = "TB err";
    if (host) host.textContent = String(e.message||e);
  }
}

async function saveCfg() {
  const body = {
    build: document.getElementById("buildName").value.trim(),
    tb_url: document.getElementById("tbUrl").value.trim(),
    jack: collectHero("jack"),
    lily: collectHero("lily"),
    george: collectHero("george"),
    joint: collectJoint(),
  };
  CFG = (await api("/api/config", {method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify(body)})).config;
  flash("hdrMeta", "настройки сохранены");
}

async function train(hero, stage, resume=false) {
  await saveCfg();
  const h = collectHero(hero);
  const j = await api("/api/action", {
    method:"POST", headers:{"Content-Type":"application/json"},
    body: JSON.stringify({action:"train", hero, stage, resume, ...h}),
  });
  LAST_JOB = j.job_id;
  pollJobs();
}

async function deploy(skip) {
  await saveCfg();
  const j = await api("/api/action", {
    method:"POST", headers:{"Content-Type":"application/json"},
    body: JSON.stringify({action: skip ? "sync_only" : "deploy"}),
  });
  LAST_JOB = j.job_id;
  document.getElementById("deployPill").textContent = "running";
  document.getElementById("deployPill").className = "pill on";
  pollJobs();
}

function flash(id, text) {
  const n = document.getElementById(id);
  if (n) n.textContent = text;
}

function fmtAxis(v) {
  const a = Math.abs(v);
  if (!isFinite(v)) return "—";
  if (a >= 1e6) return (v/1e6).toFixed(2) + "M";
  if (a >= 1e3) return (v/1e3).toFixed(a >= 1e4 ? 0 : 1) + "k";
  if (a >= 10) return v.toFixed(1);
  if (a >= 1) return v.toFixed(2);
  if (a >= 0.01) return v.toFixed(3);
  return v.toExponential(1);
}

function niceTicks(minV, maxV, count) {
  if (!isFinite(minV) || !isFinite(maxV)) return [0, 1];
  if (maxV === minV) {
    const d = Math.abs(maxV) || 1;
    return [minV - d*0.1, minV, minV + d*0.1];
  }
  const span = maxV - minV;
  const step0 = span / Math.max(1, count - 1);
  const pow = Math.pow(10, Math.floor(Math.log10(Math.abs(step0) || 1)));
  const err = step0 / pow;
  const step = (err >= 5 ? 5 : err >= 2 ? 2 : 1) * pow;
  const start = Math.ceil(minV / step) * step;
  const ticks = [];
  const nMax = Math.ceil((maxV - start) / step) + 2;
  for (let i = 0; i <= nMax; i++) {
    const v = start + i * step;
    if (v > maxV + step * 0.01) break;
    if (v >= minV - step * 0.01) ticks.push(v);
    if (ticks.length > 10) break;
  }
  if (ticks.length < 2) return [minV, maxV];
  return ticks;
}

function drawChartInto(host, legend, seriesMap, height) {
  if (!host) return;
  const hint = host.querySelector(".click-hint");
  host.innerHTML = "";
  if (hint) host.appendChild(hint);
  else if (host.id && host.id.startsWith("chart-")) {
    host.appendChild(el("span", {className:"click-hint", text:"клик → все графики"}));
  }
  if (legend) legend.innerHTML = "";
  const names = Object.keys(seriesMap).filter(n => (seriesMap[n]||[]).length);
  if (!names.length) {
    host.appendChild(document.createTextNode("нет данных TB (выбери run / обнови TB)"));
    return;
  }
  let maxX = 1, minY = Infinity, maxY = -Infinity;
  for (const n of names) {
    for (const p of seriesMap[n]) {
      maxX = Math.max(maxX, p.x);
      minY = Math.min(minY, p.y);
      maxY = Math.max(maxY, p.y);
    }
  }
  if (!isFinite(minY) || !isFinite(maxY)) { minY = 0; maxY = 1; }
  if (maxY === minY) {
    const pad = Math.abs(maxY) * 0.05 || 0.1;
    minY -= pad; maxY += pad;
  } else {
    const pad = (maxY - minY) * 0.05;
    minY -= pad; maxY += pad;
  }
  const W=640, H=height||170, padL=62, padR=14, padT=16, padB=30;
  const svg = document.createElementNS("http://www.w3.org/2000/svg","svg");
  svg.setAttribute("viewBox", `0 0 ${W} ${H}`);
  const sx = x => padL + (x/maxX)*(W-padL-padR);
  const sy = y => padT + (1-((y-minY)/(maxY-minY)))*(H-padT-padB);

  const yTicks = niceTicks(minY, maxY, 5);
  const xTicks = niceTicks(0, maxX, 5);
  // всегда показываем min/max Y, если niceTicks их срезал
  const yLabelSet = [...yTicks];
  if (!yLabelSet.some(v => Math.abs(v - minY) < (maxY-minY)*0.02)) yLabelSet.unshift(minY);
  if (!yLabelSet.some(v => Math.abs(v - maxY) < (maxY-minY)*0.02)) yLabelSet.push(maxY);

  for (const y of yLabelSet) {
    const yy = sy(y);
    const g = document.createElementNS(svg.namespaceURI,"line");
    g.setAttribute("x1", padL); g.setAttribute("x2", W-padR);
    g.setAttribute("y1", yy); g.setAttribute("y2", yy);
    g.setAttribute("stroke", "#5a6578"); g.setAttribute("stroke-width", "1");
    g.setAttribute("stroke-dasharray", "3 3");
    svg.appendChild(g);
    const t = document.createElementNS(svg.namespaceURI,"text");
    t.setAttribute("x", padL - 6); t.setAttribute("y", yy + 3);
    t.setAttribute("text-anchor", "end");
    t.setAttribute("fill", "#d0d6e2"); t.setAttribute("font-size", "11");
    t.setAttribute("font-family", "ui-monospace, Consolas, monospace");
    t.textContent = fmtAxis(y);
    svg.appendChild(t);
  }
  // нулевая отметка X
  {
    const t0 = document.createElementNS(svg.namespaceURI,"text");
    t0.setAttribute("x", padL); t0.setAttribute("y", H - 8);
    t0.setAttribute("text-anchor", "start");
    t0.setAttribute("fill", "#d0d6e2"); t0.setAttribute("font-size", "11");
    t0.setAttribute("font-family", "ui-monospace, Consolas, monospace");
    t0.textContent = "0";
    svg.appendChild(t0);
  }
  for (const x of xTicks) {
    if (x <= 0) continue;
    const xx = sx(x);
    const g = document.createElementNS(svg.namespaceURI,"line");
    g.setAttribute("x1", xx); g.setAttribute("x2", xx);
    g.setAttribute("y1", padT); g.setAttribute("y2", H-padB);
    g.setAttribute("stroke", "#5a6578"); g.setAttribute("stroke-width", "1");
    g.setAttribute("stroke-dasharray", "3 3");
    svg.appendChild(g);
    const t = document.createElementNS(svg.namespaceURI,"text");
    t.setAttribute("x", xx); t.setAttribute("y", H - 8);
    t.setAttribute("text-anchor", "middle");
    t.setAttribute("fill", "#d0d6e2"); t.setAttribute("font-size", "11");
    t.setAttribute("font-family", "ui-monospace, Consolas, monospace");
    t.textContent = fmtAxis(x);
    svg.appendChild(t);
  }

  const ax = document.createElementNS(svg.namespaceURI,"path");
  ax.setAttribute("d", `M${padL} ${padT} V${H-padB} H${W-padR}`);
  ax.setAttribute("stroke","#8b95a8"); ax.setAttribute("fill","none");
  ax.setAttribute("stroke-width","1.4");
  svg.appendChild(ax);

  names.forEach((name, i) => {
    const pts = seriesMap[name];
    const d = pts.map((p,j)=> `${j?"L":"M"}${sx(p.x).toFixed(1)} ${sy(p.y).toFixed(1)}`).join(" ");
    const path = document.createElementNS(svg.namespaceURI,"path");
    path.setAttribute("d", d);
    path.setAttribute("fill","none");
    path.setAttribute("stroke", COLORS[i % COLORS.length]);
    path.setAttribute("stroke-width","1.8");
    svg.appendChild(path);
    const last = pts[pts.length - 1];
    const lastTxt = last ? ` (${fmtAxis(last.y)})` : "";
    if (legend) legend.append(el("span", {}, [
      el("i", {style:`background:${COLORS[i % COLORS.length]}`}),
      document.createTextNode(name + lastTxt),
    ]));
  });
  host.appendChild(svg);
}

function drawChart(hero, seriesMap) {
  drawChartInto(
    document.getElementById(`chart-${hero}`),
    document.getElementById(`legend-${hero}`),
    seriesMap,
    170,
  );
}

async function loadCharts() {
  for (const hero of ["jack","lily","george"]) {
    const a = document.getElementById(`tb1-${hero}`);
    const b = document.getElementById(`tb2-${hero}`);
    if (!a || !b) continue;
    const runs = [a.value, b.value].filter(Boolean);
    if (!runs.length) { drawChart(hero, {}); continue; }
    try {
      const q = new URLSearchParams({runs: runs.join("|"), tags: MAIN_TAG});
      const data = await api("/api/tb/series?"+q.toString());
      // легенда = полный TB run id (97/Jack…), не Stage1/Stage2
      const mapped = {};
      Object.keys(data.series || {}).forEach((k) => {
        const runName = k.includes(" · ") ? k.split(" · ")[0] : k;
        mapped[`${runName} · Cumulative Reward`] = data.series[k];
      });
      drawChart(hero, mapped);
      document.getElementById(`pill-${hero}`).textContent = `${Object.keys(mapped).length} · Cumulative Reward`;
    } catch (e) {
      document.getElementById(`pill-${hero}`).textContent = "TB err";
      const host = document.getElementById(`chart-${hero}`);
      if (host) host.textContent = String(e.message||e);
    }
  }
  try { await loadJointChart(); } catch (_) {}
}

async function openDetail(hero, title) {
  const a = document.getElementById(`tb1-${hero}`);
  const b = document.getElementById(`tb2-${hero}`);
  const runs = [a && a.value, b && b.value].filter(Boolean);
  return openDetailRuns(title, runs, "Сначала выбери TB Stage1/Stage2");
}

async function openJointDetail() {
  const sel = document.getElementById("jointTb");
  const runs = [...sel.selectedOptions].map((o) => o.value.trim()).filter(Boolean);
  return openDetailRuns("Joint finetune", runs, "Сначала выбери TB runs (Трое из RUN_ID)");
}

async function openDetailRuns(title, runs, emptyHint) {
  const modal = document.getElementById("detailModal");
  const body = document.getElementById("detailBody");
  document.getElementById("detailTitle").textContent = `${title}: все метрики TensorBoard`;
  const runLine = (runs || []).map((r) => r).join("  |  ");
  body.innerHTML = "";
  body.append(el("p", {className:"hint", text: runLine || "нет runs"}));
  if (!runs || !runs.length) {
    body.append(document.createTextNode(emptyHint || "Сначала выбери TB runs"));
    modal.classList.add("open");
    return;
  }
  body.append(el("p", {className:"hint", text:"загрузка блоков…"}));
  modal.classList.add("open");

  const blocks = (CFG && CFG.detail_chart_blocks) || [];
  let allTags = [];
  try {
    const info = await api("/api/tb/run_tags?" + new URLSearchParams({runs: runs.join("|")}).toString());
    allTags = info.tags || [];
  } catch (_) {}

  while (body.children.length > 1) body.removeChild(body.lastChild);

  const used = new Set();
  const renderBlock = async (blockTitle, tags) => {
    const present = tags.filter(t => !allTags.length || allTags.includes(t));
    if (!present.length) return;
    present.forEach(t => used.add(t));
    const block = el("div", {className:"modal-block"});
    block.append(el("h3", {text: blockTitle}));
    const grid = el("div", {className:"modal-grid"});
    block.append(grid);
    body.append(block);
    for (const tag of present) {
      const cell = el("div", {className:"modal-chart"});
      const cap = el("div", {className:"hint", text: tag});
      const leg = el("div", {className:"legend"});
      const plot = el("div", {className:"modal-chart", style:"min-height:150px;border:none;padding:0"});
      cell.append(cap, leg, plot);
      grid.append(cell);
      try {
        const q = new URLSearchParams({runs: runs.join("|"), tags: tag});
        const data = await api("/api/tb/series?"+q.toString());
        const series = {};
        Object.keys(data.series||{}).forEach((k) => {
          const runName = k.includes(" · ") ? k.split(" · ")[0] : k;
          series[runName] = data.series[k];
        });
        drawChartInto(plot, leg, series, 150);
      } catch (e) {
        plot.textContent = String(e.message||e);
      }
    }
  };

  for (const bdef of blocks) {
    await renderBlock(bdef.title || "Block", bdef.tags || []);
  }
  const other = allTags.filter(t => !used.has(t));
  if (other.length) await renderBlock("Other", other);
  if (body.children.length <= 1) body.append(document.createTextNode("Нет scalar-тегов для выбранных runs"));
}

async function pollServer() {
  try {
    const s = await api("/api/server_stats");
    const host = s.host || "lab_comp";
    const mode = s.mode || (host === "local" ? "local" : "ssh");
    const tried = (s.tried || []).join(" → ");
    const modeLabel = document.getElementById("serverModeLabel");
    if (modeLabel) modeLabel.textContent = mode === "local" ? "(local на lab)" : "(только SSH)";
    document.getElementById("serverHostHint").textContent =
      s.ok
        ? (mode === "local"
          ? `host=local · UI уже на lab_comp, команды без SSH (это нормально)`
          : `host=${host} · данные по SSH с lab (не с твоего ПК)`)
        : (s.pending
          ? `host=${host} · опрос…`
          : (mode === "local"
            ? `local fail · ${s.text || "?"}`
            : `ssh fail · пробовали: ${tried || host}`));
    document.getElementById("serverStats").textContent =
      `[${mode} ${host}]\n` + (s.text || "(пусто)");
    const pill = document.getElementById("serverPill");
    if (s.ok) {
      pill.textContent = mode === "local" ? "local ok" : "ssh ok";
      pill.className = "pill on";
    } else if (s.pending) {
      pill.textContent = mode === "local" ? "local…" : "ssh…";
      pill.className = "pill";
    } else {
      pill.textContent = mode === "local" ? "local err" : "ssh err";
      pill.className = "pill";
    }
    applyLabActivity(s.activity || {});
  } catch (e) {
    document.getElementById("serverStats").textContent = "server_stats: " + (e.message||e);
    document.getElementById("serverPill").textContent = "err";
    document.getElementById("serverPill").className = "pill";
    applyLabActivity({});
  }
}

function setCardState(id, running, streaming) {
  const card = document.getElementById(id);
  if (!card) return;
  card.classList.toggle("running", !!running);
  card.classList.toggle("streaming", !!streaming && !running);
  if (running && streaming) {
    card.classList.add("running");
    card.classList.add("streaming");
  }
}

function setPillLive(id, live, streamLive, idleText) {
  const pill = document.getElementById(id);
  if (!pill) return;
  pill.classList.remove("live", "live-stream", "on");
  if (live) {
    pill.classList.add("live");
    pill.textContent = "RUNNING";
  } else if (streamLive) {
    pill.classList.add("live-stream");
    pill.textContent = "STREAM";
  } else if (idleText != null && (pill.textContent === "RUNNING" || pill.textContent === "STREAM")) {
    pill.textContent = idleText;
  }
}

function applyLabActivity(a) {
  a = a || {};
  const jack = !!a.jack, lily = !!a.lily, george = !!a.george;
  const joint = !!a.joint, validate = !!a.validate, stream = !!a.stream;
  const ss = !!a.streaming_survival;
  const bot = !!a.llm_bot;
  setCardState("card-jack", jack, false);
  setCardState("card-lily", lily, false);
  setCardState("card-george", george, false);
  setCardState("card-validate", validate, false);
  // Обёртка с вкладками: красный = train, синий = stream.
  setCardState("card-joint-wrap", joint, stream && !joint);
  if (joint && stream) setCardState("card-joint-wrap", true, true);
  // Survival followers: красный мигающий блок когда процесс жив.
  setCardState("card-ss", ss, false);
  setCardState("card-llm-bot", bot, false);
  // Preview card keeps its own running state from ssPreviewApplyStatus — don't clear it here.
  const previewLive = !!(document.getElementById("card-ss-preview")
    && document.getElementById("card-ss-preview").classList.contains("running"));

  const tabTrain = document.getElementById("tab-joint-train");
  const tabStream = document.getElementById("tab-joint-stream");
  if (tabTrain) tabTrain.classList.toggle("has-live", joint);
  if (tabStream) tabStream.classList.toggle("has-live", stream);
  const modeSs = document.getElementById("tab-mode-streaming");
  if (modeSs) modeSs.classList.toggle("has-live", ss || bot || previewLive);

  setPillLive("pill-jack", jack, false, null);
  setPillLive("pill-lily", lily, false, null);
  setPillLive("pill-george", george, false, null);
  setPillLive("pill-joint", joint, false, "finetune");
  setPillLive("pill-stream", stream, false, "idle");

  const ssPill = document.getElementById("ssPill");
  if (ssPill) {
    if (ss) {
      ssPill.textContent = "RUNNING";
      ssPill.className = "pill live";
    } else if (ssPill.textContent === "RUNNING" || ssPill.textContent === "starting") {
      ssPill.textContent = "idle";
      ssPill.className = "pill";
    }
  }

  const valPill = document.getElementById("valPill");
  if (valPill) {
    valPill.classList.remove("live", "on");
    if (validate) {
      valPill.classList.add("live");
      valPill.textContent = "RUNNING";
    } else if (valPill.textContent === "RUNNING") {
      valPill.textContent = "idle";
    }
  }

  renderLabTasks(a);
}

function renderLabTasks(a) {
  const host = document.getElementById("labTasks");
  const pill = document.getElementById("labTasksPill");
  if (!host) return;
  let tasks = Array.isArray(a.tasks) ? a.tasks.slice() : [];
  // fallback если старый activity без tasks
  if (!tasks.length) {
    if (a.joint) tasks.push({id:"train", kind:"train", label:"Joint train", kill:"train"});
    else {
      if (a.jack) tasks.push({id:"jack", kind:"train", label:"Jack train", kill:"train"});
      if (a.lily) tasks.push({id:"lily", kind:"train", label:"Lily train", kill:"train"});
      if (a.george) tasks.push({id:"george", kind:"train", label:"George train", kill:"train"});
    }
    if (a.validate) tasks.push({id:"validate", kind:"validate", label:"Validate (mp4)", kill:"validate"});
    if (a.stream) tasks.push({id:"stream", kind:"stream", label:"Stream Presentation", kill:"stream"});
    if (a.streaming_survival) tasks.push({id:"streaming_survival", kind:"streaming_survival", label:"Streaming Survival", kill:"streaming_survival"});
    if (a.llm_bot) tasks.push({id:"llm_bot", kind:"llm_bot", label:"LLM Bot", kill:"llm_bot"});
  }
  host.innerHTML = "";
  if (pill) {
    pill.textContent = tasks.length ? String(tasks.length) : "idle";
    pill.className = "pill" + (tasks.length ? " live" : "");
  }
  if (!tasks.length) {
    host.innerHTML = '<div class="lab-tasks-empty">нет активных задач</div>';
    return;
  }
  for (const t of tasks) {
    const row = document.createElement("div");
    row.className = "lab-task kind-" + (t.kind || t.kill || "train");
    const kind = document.createElement("span");
    kind.className = "lab-task-kind";
    kind.textContent = t.kind || t.kill || "?";
    const label = document.createElement("span");
    label.className = "lab-task-label";
    label.textContent = t.label || t.id || "?";
    const killKind = t.kill || t.kind;
    if (killKind === "train" || killKind === "stream") {
      const btnR = document.createElement("button");
      btnR.type = "button";
      btnR.className = "lab-task-r";
      btnR.title = "Перезапуск: стоп → чистка → старт";
      btnR.textContent = "↻";
      btnR.onclick = () => restartLabTask(killKind, t.label || t.id);
      row.append(kind, label, btnR);
    } else {
      row.append(kind, label);
    }
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "lab-task-x";
    btn.title = "Остановить: " + (killKind || "");
    btn.textContent = "×";
    btn.onclick = () => killLabTask(killKind, t.label || t.id);
    row.append(btn);
    host.append(row);
  }
}

async function killLabTask(kind, label) {
  if (kind === "llm_bot") {
    if (!confirm("Остановить LLM Bot на lab?" + (label ? "\n\n" + label : ""))) return;
    flash("hdrMeta", "стоп llm_bot…");
    try {
      const s = await api(LLM_API + "/stop", { method: "POST", headers: {"Content-Type":"application/json"}, body: "{}" });
      renderLlmStatus(s);
      setTimeout(pollServer, 1200);
      setTimeout(pollServer, 3500);
    } catch (e) { alert(e.message || e); }
    return;
  }
  const map = {
    train: { action: "kill_train", ask: "Остановить train на lab (mlagents + headless)? Стрим/validate не трогаем." },
    validate: { action: "kill_validate", ask: "Остановить только validate (mp4)?" },
    stream: { action: "kill_stream", ask: "Остановить только бесконечный стрим Presentation? OBS не гасим." },
    streaming_survival: { action: "stop_streaming_survival", ask: "Остановить Streaming Survival? Train / Presentation onnx не трогаем." },
  };
  const conf = map[kind];
  if (!conf) { alert("Неизвестный тип: " + kind); return; }
  if (!confirm(conf.ask + (label ? "\n\n" + label : ""))) return;
  flash("hdrMeta", "стоп " + kind + "…");
  try {
    const j = await api("/api/action", {
      method: "POST", headers: {"Content-Type":"application/json"},
      body: JSON.stringify({ action: conf.action }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    setTimeout(pollServer, 1500);
    setTimeout(pollServer, 4000);
  } catch (e) {
    alert(e.message || e);
  }
}

async function restartLabTask(kind, label) {
  if (kind === "train") return restartJointTrain(label);
  if (kind === "stream") return restartJointStream(label);
  alert("Перезапуск только для train / stream");
}

async function restartJointTrain(label) {
  const j = collectJoint();
  if (!j.init_jack || !j.init_lily || !j.init_george || !j.run_id) {
    flash("hdrMeta", "joint: заполни 3 базы и RUN_ID");
    return;
  }
  if (!confirm("Перезапуск joint train: стоп → чистка → resume той же папки?" + (label ? "\n\n" + label : ""))) return;
  await saveCfg();
  flash("hdrMeta", "перезапуск train…");
  try {
    const resp = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"restart_joint_train", resume:true, ...j }),
    });
    LAST_JOB = resp.job_id;
    pollJobs();
    setTimeout(pollServer, 2500);
    setTimeout(pollServer, 6000);
  } catch (e) {
    alert(e.message || e);
  }
}

async function restartJointStream(label) {
  const run_id = (document.getElementById("streamRunId").value.trim()
    || document.getElementById("jointRunId").value.trim());
  if (!run_id) { flash("hdrMeta", "stream: укажи RUN_ID"); return; }
  if (!confirm("Перезапуск стрима: стоп → чистка → старт Presentation?" + (label ? "\n\n" + label : ""))) return;
  document.getElementById("jointRunId").value = run_id;
  document.getElementById("streamRunId").value = run_id;
  await saveCfg();
  flash("hdrMeta", "перезапуск стрима…");
  try {
    const resp = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"restart_joint_stream", run_id }),
    });
    LAST_JOB = resp.job_id;
    pollJobs();
    setTimeout(pollServer, 2500);
    setTimeout(pollServer, 8000);
  } catch (e) {
    alert(e.message || e);
  }
}

async function pollJobs() {
  try {
    const j = await api("/api/jobs");
    const sel = document.getElementById("jobSelect");
    const cur = LAST_JOB || (j.jobs[0] && j.jobs[0].id);
    sel.innerHTML = "";
    for (const job of j.jobs) {
      const o = el("option", {value: job.id, text: `${job.status} · ${job.name} · ${job.id}`});
      if (job.id === cur) o.selected = true;
      sel.append(o);
    }
    if (!cur) return;
    const detail = await api("/api/jobs/"+encodeURIComponent(cur));
    document.getElementById("jobLog").textContent = detail.log_tail || "(пусто)";
    document.getElementById("jobPill").textContent = detail.status;
    document.getElementById("jobPill").className = "pill" + (detail.status==="running"?" on":"");
    if (detail.name && detail.name.startsWith("deploy")) {
      document.getElementById("deployPill").textContent = detail.status;
      document.getElementById("deployPill").className = "pill" + (detail.status==="running"?" on":"");
    }
    LAST_JOB = cur;
    if (detail.status === "running" || detail.status === "detached") setTimeout(pollJobs, 2500);
  } catch (e) {
    document.getElementById("jobLog").textContent = "jobs: " + (e.message||e);
  }
}

function renderHeroes() {
  const heroes = document.getElementById("heroes");
  heroes.innerHTML = "";
  heroes.append(
    heroCard("jack","Jack","var(--jack)"),
    heroCard("lily","Lily","var(--lily)"),
    heroCard("george","George","var(--george)"),
  );
  for (const hero of ["jack","lily","george"]) {
    document.getElementById(`tb1-${hero}`).onchange = loadCharts;
    document.getElementById(`tb2-${hero}`).onchange = loadCharts;
  }
  fillJointCard();
  document.getElementById("btnJointTrain").onclick = () => trainJoint(false);
  document.getElementById("btnJointResume").onclick = () => trainJoint(true);
  document.getElementById("btnJointStream").onclick = () => startJointStream();
  document.getElementById("btnKillStream").onclick = () => killStreamOnly();
  const btnKillJoint = document.getElementById("btnKillJointTrain");
  if (btnKillJoint) btnKillJoint.onclick = () => killTrainOnly();
  const btnRj = document.getElementById("btnRestartJointTrain");
  if (btnRj) btnRj.onclick = () => restartJointTrain();
  const btnRs = document.getElementById("btnRestartJointStream");
  if (btnRs) btnRs.onclick = () => restartJointStream();
  document.getElementById("btnJointTbTrio").onclick = () => selectJointTbTrio();
  document.getElementById("btnJointTbClear").onclick = () => clearJointTb();
  document.getElementById("jointTb").onchange = () => { loadJointChart(); saveCfg(); };
  document.getElementById("jointRunId").onchange = () => {
    const v = document.getElementById("jointRunId").value;
    const s = document.getElementById("streamRunId");
    if (s) s.value = v;
    fillJointCard();
    loadJointChart();
  };
  document.getElementById("streamRunId").onchange = () => {
    document.getElementById("jointRunId").value = document.getElementById("streamRunId").value;
    saveCfg();
  };
  document.getElementById("tab-joint-train").onclick = () => switchJointTab("panel-joint-train");
  document.getElementById("tab-joint-stream").onclick = () => switchJointTab("panel-joint-stream");
  document.getElementById("chart-joint").onclick = () => openJointDetail();
  loadJointChart();
}

function bindModeTabs() {
  const t = document.getElementById("tab-mode-training");
  const s = document.getElementById("tab-mode-streaming");
  if (t) t.onclick = () => switchModeTab("training");
  if (s) s.onclick = () => switchModeTab("streaming");
  const bStart = document.getElementById("btnSsStart");
  const bStop = document.getElementById("btnSsStop");
  if (bStart) bStart.onclick = () => startStreamingSurvival();
  if (bStop) bStop.onclick = () => stopStreamingSurvival();
  bindSsDiagnostics();
  bindSsPreview();
}

let _ssPreviewTimer = null;
let _ssPreviewRunning = false;

function bindSsPreview() {
  const startBtn = document.getElementById("btnSsPreviewStart");
  const stopBtn = document.getElementById("btnSsPreviewStop");
  const onceBtn = document.getElementById("btnSsPreviewOnce");
  if (startBtn) startBtn.onclick = () => ssPreviewStart();
  if (stopBtn) stopBtn.onclick = () => ssPreviewStop();
  if (onceBtn) onceBtn.onclick = () => ssPreviewOnce();
  ssPreviewPollStatus();
}

function ssPreviewApplyStatus(st) {
  _ssPreviewRunning = !!(st && st.running);
  setCardState("card-ss-preview", _ssPreviewRunning, false);
  const pill = document.getElementById("ssPreviewPill");
  const meta = document.getElementById("ssPreviewMeta");
  const img = document.getElementById("ssPreviewImg");
  const ph = document.getElementById("ssPreviewPlaceholder");
  if (pill) {
    if (_ssPreviewRunning) {
      pill.textContent = "LIVE";
      pill.className = "pill live";
    } else {
      pill.textContent = "off";
      pill.className = "pill";
    }
  }
  const modeSs = document.getElementById("tab-mode-streaming");
  if (modeSs) {
    const ssLive = !!(document.getElementById("card-ss") && document.getElementById("card-ss").classList.contains("running"));
    const botLive = !!(document.getElementById("card-llm-bot") && document.getElementById("card-llm-bot").classList.contains("running"));
    modeSs.classList.toggle("has-live", ssLive || botLive || _ssPreviewRunning);
  }
  if (meta) {
    const err = (st && st.last_error) ? (" · err: " + st.last_error) : "";
    const age = st && st.last_ok_at
      ? (" · last " + Math.max(0, Math.round(Date.now()/1000 - st.last_ok_at)) + "s ago")
      : "";
    meta.textContent = (_ssPreviewRunning ? "live " + (st.display || ":1") : "preview off")
      + " · frames=" + ((st && st.frames) || 0)
      + age + err;
  }
  if (st && st.has_frame && img) {
    img.style.display = "block";
    if (ph) ph.style.display = "none";
    img.src = "/api/ss_preview/frame.jpg?t=" + Date.now();
  }
  if (_ssPreviewRunning && !_ssPreviewTimer) {
    _ssPreviewTimer = setInterval(ssPreviewTick, 1600);
  }
  if (!_ssPreviewRunning && _ssPreviewTimer) {
    clearInterval(_ssPreviewTimer);
    _ssPreviewTimer = null;
  }
}

async function ssPreviewPollStatus() {
  try {
    const st = await api("/api/ss_preview/status");
    ssPreviewApplyStatus(st);
  } catch (_) {}
}

async function ssPreviewTick() {
  try {
    const st = await api("/api/ss_preview/status");
    ssPreviewApplyStatus(st);
  } catch (e) {
    const meta = document.getElementById("ssPreviewMeta");
    if (meta) meta.textContent = "preview poll: " + (e.message || e);
  }
}

async function ssPreviewStart() {
  try {
    const st = await api("/api/ss_preview/start", {
      method: "POST",
      headers: {"Content-Type":"application/json"},
      body: "{}",
    });
    ssPreviewApplyStatus(st);
  } catch (e) { alert(e.message || e); }
}

async function ssPreviewStop() {
  try {
    const st = await api("/api/ss_preview/stop", {
      method: "POST",
      headers: {"Content-Type":"application/json"},
      body: "{}",
    });
    ssPreviewApplyStatus(st);
  } catch (e) { alert(e.message || e); }
}

async function ssPreviewOnce() {
  try {
    const st = await api("/api/ss_preview/once", {
      method: "POST",
      headers: {"Content-Type":"application/json"},
      body: "{}",
    });
    ssPreviewApplyStatus(st);
  } catch (e) { alert(e.message || e); }
}

function bindSsDiagnostics() {
  const logEl = document.getElementById("ssDiagLog");
  const stEl = document.getElementById("ssDiagStatus");
  const zipEl = document.getElementById("ssDiagZipLink");
  const statsLink = document.getElementById("ssDiagStatsLink");
  const parserSt = document.getElementById("ssDiagParserStatus");
  const runtimeSt = document.getElementById("ssDiagRuntimeStatus");
  const liveSt = document.getElementById("ssDiagLiveStatus");
  const visualSt = document.getElementById("ssDiagVisualStatus");
  const statsSt = document.getElementById("ssDiagStatsStatus");
  const qaSt = document.getElementById("ssDiagQaStatus");
  const runList = document.getElementById("ssRunList");
  const runMeta = document.getElementById("ssRunMeta");
  let selectedRunId = "";
  let cachedReports = [];
  let runningJobRunId = "";
  let runningJobKind = "";
  function showLog(text) {
    if (!logEl) return;
    if (!text) {
      logEl.style.display = "none";
      logEl.textContent = "";
      return;
    }
    logEl.style.display = "block";
    logEl.textContent = text;
  }
  function dashUrl() {
    return "/api/ss_diagnostics/stats_dashboard"
      + (selectedRunId ? ("?run=" + encodeURIComponent(selectedRunId)) : "");
  }
  function highlightSelectedReport() {
    if (!runList) return;
    for (const row of runList.querySelectorAll("[data-run-id]")) {
      const on = row.getAttribute("data-run-id") === selectedRunId;
      row.style.background = on ? "rgba(106,166,255,.22)" : "transparent";
      row.style.outline = on ? "1px solid rgba(106,166,255,.55)" : "none";
      row.setAttribute("aria-selected", on ? "true" : "false");
    }
  }
  function renderReportList(runs, preferId, latestId) {
    cachedReports = runs || [];
    if (!runList) return;
    runList.innerHTML = "";
    if (!cachedReports.length && !runningJobRunId) {
      runList.innerHTML = '<div class="hint" style="padding:10px">Нет репортов. Запусти Live Stress 40.</div>';
      selectedRunId = "";
      return;
    }
    const ids = new Set(cachedReports.map(r => r.id));
    // preferId === "__latest__" = after new job: always pick newest report
    let keep = "";
    if (preferId === "__latest__") keep = latestId || "";
    else keep = preferId || selectedRunId || latestId || "";
    if (keep && ids.has(keep)) selectedRunId = keep;
    else if (cachedReports.length) selectedRunId = cachedReports[0].id;
    else selectedRunId = runningJobRunId || "";

    if (runningJobRunId) {
      const runRow = document.createElement("div");
      runRow.setAttribute("data-run-id", runningJobRunId);
      runRow.style.cssText = "display:grid;grid-template-columns:minmax(0,1.4fr) minmax(140px,1fr) auto;gap:10px;align-items:center;padding:9px 12px;border-bottom:1px solid #3a3a3a;background:rgba(230,184,77,.12)";
      const pending = String(runningJobRunId).indexOf("pending_") === 0;
      runRow.innerHTML = '<div style="min-width:0"><div style="font-weight:600">Live Stress · RUNNING</div>'
        + '<div class="hint" style="opacity:.85;margin-top:2px;font-size:12px;word-break:break-all">'
        + (pending ? ("старт… " + runningJobRunId) : runningJobRunId) + "</div></div>"
        + '<div style="font-variant-numeric:tabular-nums"><span style="color:#e6b84d">RUNNING</span><br/><span class="hint">длительность: идёт…</span></div>'
        + '<div style="text-align:right;white-space:nowrap;opacity:.95">сейчас</div>';
      runList.appendChild(runRow);
    }
    for (const r of cachedReports) {
      if (runningJobRunId && r.id === runningJobRunId) continue;
      const row = document.createElement("div");
      row.setAttribute("role", "option");
      row.setAttribute("data-run-id", r.id);
      row.style.cssText = "display:grid;grid-template-columns:minmax(0,1.4fr) minmax(140px,1fr) auto;gap:10px;align-items:center;padding:9px 12px;cursor:pointer;border-bottom:1px solid #3a3a3a";
      const left = document.createElement("div");
      left.style.cssText = "min-width:0";
      const t = document.createElement("div");
      t.style.cssText = "font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap";
      const stRaw = String(r.status || r.live_stress_status || "—");
      const stHead = stRaw.split(" · ")[0] || stRaw;
      const tot = r.attempts || 40;
      const passN = (r.attempts_passed === 0 || r.attempts_passed) ? r.attempts_passed : null;
      let head = (r.title || ("Live Stress " + tot)) + " · " + stHead;
      if (passN != null && (stHead === "Failed" || stHead === "Success" || stHead === "RUNNING")) {
        head = (r.title || ("Live Stress " + tot)) + " · " + stHead + " · " + passN + "/" + tot;
      }
      t.textContent = head;
      if (r.reason) t.title = String(r.reason);
      if (r.in_progress || r.status === "RUNNING") {
        row.style.background = "rgba(230,184,77,.12)";
      } else if (String(r.status || "").indexOf("Error") === 0 || String(r.status || "").indexOf("ERROR") === 0) {
        row.style.background = "rgba(239,107,107,.10)";
      } else if (String(r.status || "").indexOf("Failed") === 0 || String(r.status || "").indexOf("FAIL") === 0) {
        row.style.background = "rgba(239,107,107,.08)";
      }
      const idLine = document.createElement("div");
      idLine.className = "hint";
      idLine.style.cssText = "opacity:.7;margin-top:2px;font-size:11px;word-break:break-all";
      idLine.textContent = r.id || "";
      left.appendChild(t);
      left.appendChild(idLine);

      const mid = document.createElement("div");
      mid.style.cssText = "font-variant-numeric:tabular-nums;line-height:1.35";
      const total = r.attempts || 40;
      const score = document.createElement("div");
      if (r.in_progress || r.status === "RUNNING") {
        const done = (r.done_attempts != null) ? r.done_attempts : (r.attempts_passed ?? "—");
        score.innerHTML = '<span style="color:#e6b84d">RUNNING</span>'
          + ' <span class="hint">' + done + " / " + total + "</span>";
      } else if (String(r.status || "").indexOf("Error") === 0 || String(r.status || "").indexOf("ERROR") === 0) {
        score.innerHTML = '<span style="color:#ef6b6b">Error</span>';
        if (r.reason) score.title = String(r.reason);
      } else if (String(r.status || "").indexOf("Failed") === 0 || String(r.status || "").indexOf("FAIL") === 0 || r.reason) {
        const tot2 = r.attempts || 40;
        const p2 = (r.attempts_passed === 0 || r.attempts_passed) ? r.attempts_passed : 0;
        score.innerHTML = '<span style="color:#ef6b6b">Failed</span>'
          + ' <span class="hint">' + p2 + "/" + tot2 + "</span>";
        if (r.reason) score.title = String(r.reason);
      } else if (String(r.status || "").indexOf("Success") === 0 || String(r.status || "") === "PASS") {
        score.innerHTML = '<span style="color:#3ecf8e">Success</span>'
          + ' <span class="hint">из ' + total + "</span>";
      } else {
        const passed = (r.attempts_passed === 0 || r.attempts_passed) ? r.attempts_passed : "—";
        const failed = (r.attempts_failed === 0 || r.attempts_failed) ? r.attempts_failed : "—";
        score.innerHTML = '<span style="color:#3ecf8e">PASS ' + passed + "</span>"
          + " / <span style=\"color:#ef6b6b\">FAIL " + failed + "</span>"
          + ' <span class="hint">из ' + total + "</span>";
      }
      const dur = document.createElement("div");
      dur.className = "hint";
      dur.style.marginTop = "2px";
      dur.textContent = "время: " + (r.duration_label || "—");
      mid.appendChild(score);
      mid.appendChild(dur);

      const right = document.createElement("div");
      right.style.cssText = "text-align:right;white-space:nowrap;font-variant-numeric:tabular-nums;opacity:.95";
      right.textContent = r.when || "—";

      row.appendChild(left);
      row.appendChild(mid);
      row.appendChild(right);
      row.onclick = () => { loadRunReport(r.id); };
      runList.appendChild(row);
    }
    highlightSelectedReport();
  }
  function applyCheckerStats(_summary, _stats) {}
  function openSelectedDashboard() {
    window.open(dashUrl(), "_blank", "noopener");
  }
  function applySummary(summary, stats, extra) {
    if (!summary) return;
    if (parserSt) parserSt.textContent = summary.parser_only_status || summary.parser || "—";
    if (runtimeSt) {
      runtimeSt.textContent = summary.machine_full_runtime_status
        || summary.full_runtime_status
        || (summary.mode === "parser_only" ? "NOT RUN" : (summary.overall || "—"));
    }
    if (liveSt) {
      const rs = summary.reason || (extra && extra.reason) || "";
      const st = summary.live_stress_status || "NOT RUN";
      const up = String(st).toUpperCase();
      if ((up === "ERROR" || String(st) === "Error") && rs) liveSt.textContent = "Error · " + rs;
      else if ((up === "FAIL" || up === "FAILED" || String(st) === "Failed") && rs) liveSt.textContent = "Failed · " + rs;
      else if (up === "PASS" || String(st) === "Success") liveSt.textContent = "Success";
      else liveSt.textContent = st;
    }
    if (visualSt) visualSt.textContent = summary.visual_status || "PENDING";
    if (statsSt) statsSt.textContent = summary.stats_dashboard_status
      || (stats && stats.stats_dashboard_status) || "NOT RUN";
    if (qaSt) qaSt.textContent = summary.overall_qa_status || "—";
    const contEl = document.getElementById("ssDiagContinuity");
    if (contEl) {
      let maxJump = Number((stats && stats.max_position_jump) || summary.max_position_jump || 0);
      let maxSpeed = Number((stats && stats.max_speed) || summary.max_speed || 0);
      let illegal = Number(summary.illegal_teleports || 0);
      const rows = (summary.scenario_results && summary.scenario_results.scenarios)
        || summary.scenarios || [];
      for (const e of rows) {
        if (!e) continue;
        const j = Number(e.max_position_jump || 0);
        const s = Number(e.max_speed || 0);
        if (j > maxJump) maxJump = j;
        if (s > maxSpeed) maxSpeed = s;
        const arr = e.illegal_teleports;
        if (Array.isArray(arr)) illegal += arr.length;
        const u = e.unity_result;
        if (u && Array.isArray(u.illegal_teleports)) illegal += u.illegal_teleports.length;
      }
      const live = summary.live_stress || {};
      if (Number(live.max_position_jump || 0) > maxJump) maxJump = Number(live.max_position_jump);
      if (Number(live.max_speed || 0) > maxSpeed) maxSpeed = Number(live.max_speed);
      contEl.textContent =
        "Continuity: max jump " + maxJump.toFixed(2)
        + " · max speed " + maxSpeed.toFixed(2)
        + " · illegal teleports " + illegal
        + " · min ground " + (stats && stats.min_ground_clearance != null ? stats.min_ground_clearance : (live.min_ground_clearance ?? "—"));
    }
    applyCheckerStats(summary, stats);
    if (runMeta) runMeta.textContent = "";
    if (statsLink) statsLink.innerHTML = "";
  }
  async function loadRunReport(runId) {
    selectedRunId = runId || "";
    highlightSelectedReport();
    if (stEl) stEl.textContent = "";
    try {
      const j = await api("/api/ss_diagnostics/last_report", {
        method: "POST",
        headers: {"Content-Type": "application/json"},
        body: JSON.stringify({run: selectedRunId || null}),
      });
      if (j.run_id) selectedRunId = j.run_id;
      highlightSelectedReport();
      const summary = j.summary || {};
      if (j.visual_status) summary.visual_status = j.visual_status;
      if (j.overall_qa_status) summary.overall_qa_status = j.overall_qa_status;
      if (j.machine_full_runtime_status) summary.machine_full_runtime_status = j.machine_full_runtime_status;
      if (j.live_stress_status) summary.live_stress_status = j.live_stress_status;
      if (j.stats_dashboard_status) summary.stats_dashboard_status = j.stats_dashboard_status;
      applySummary(summary, j.stats_summary || summary.stats_summary, j);
      if (runMeta) runMeta.textContent = "";
      if (stEl) stEl.textContent = "";
      showLog("");
    } catch (e) {
      if (stEl) stEl.textContent = "ошибка: " + (e.message || e);
    }
  }
  async function refreshRuns(preferId) {
    try {
      const j = await api("/api/ss_diagnostics/runs");
      const runs = j.runs || [];
      const latestReport = (runs.find(r => r.has_live) || runs[0] || {}).id || j.latest_id || "";
      renderReportList(runs, preferId, latestReport);
    } catch (e) {
      if (runList) runList.innerHTML = '<div class="hint" style="padding:10px">Ошибка списка: '
        + String(e.message || e) + "</div>";
      if (runMeta) runMeta.textContent = "list error: " + (e.message || e);
    }
  }
  async function pollDiagJob(jobId, kind) {
    if (stEl) stEl.textContent = "RUNNING…";
    let n = 0;
    while (n < 720) { // up to ~60 min @ 5s
      n++;
      let detail = null;
      try { detail = await api("/api/jobs/" + encodeURIComponent(jobId)); } catch (_) {}
      if (detail) {
        const st = detail.status || "?";
        const logTail = detail.log_tail || "";
        const m = logTail.match(/test_runs\/([0-9]{8}_[0-9]{6}_[0-9]+)/);
        if (m && m[1] && m[1] !== runningJobRunId) {
          runningJobRunId = m[1];
          await refreshRuns(m[1]);
        } else if (n % 2 === 0) {
          // Keep history updated while job runs (disk may show RUNNING before result.json).
          await refreshRuns(runningJobRunId || selectedRunId);
        }
        if (stEl) stEl.textContent = st === "running" || st === "?" ? "RUNNING…" : st;
        showLog(logTail || ("job " + jobId));
        if (st === "ok" || st === "error") {
          const finishedRun = runningJobRunId;
          runningJobRunId = "";
          runningJobKind = "";
          const reasonMatch = logTail.match(/"reason"\s*:\s*"([^"]+)"/);
          const errReason = reasonMatch ? reasonMatch[1] : "";
          try {
            await refreshRuns((kind.indexOf("live") >= 0 || kind.indexOf("full") >= 0 || kind.indexOf("runtime") >= 0)
              ? "__latest__" : (finishedRun || "__latest__"));
            const pick = finishedRun || selectedRunId || "";
            await loadRunReport(pick);
            if (st === "ok") {
              if (stEl) stEl.textContent = "готово";
              showLog("");
            } else {
              if (stEl) stEl.textContent = errReason
                ? ("ERROR · " + errReason)
                : "ERROR · job failed";
              showLog(logTail || ("ERROR job " + jobId));
            }
          } catch (e2) {
            if (stEl) stEl.textContent = "ERROR · " + (e2.message || e2);
            showLog(logTail || String(e2.message || e2));
          }
          return;
        }
      }
      await new Promise(r => setTimeout(r, 5000));
    }
    if (stEl) stEl.textContent = "timeout job";
  }
  async function run(kind) {
    if (kind === "stats_dashboard") {
      if (stEl) stEl.textContent = "сборка дашборда…";
      try {
        const j = await api("/api/ss_diagnostics/stats_dashboard", {
          method: "POST",
          headers: {"Content-Type": "application/json"},
          body: JSON.stringify({run: selectedRunId || null}),
        });
        const url = j.stats_dashboard_url || dashUrl();
        if (j.overall === "FAIL" || !url) {
          if (stEl) stEl.textContent = "дашборд недоступен: " + (j.log || "нет stats");
          return;
        }
        window.open(url, "_blank", "noopener");
        if (stEl) stEl.textContent = "";
      } catch (e) {
        if (stEl) stEl.textContent = "ошибка дашборда: " + (e.message || e);
      }
      return;
    }
    if (stEl) stEl.textContent = "запуск…";
    showLog("…");
    try {
      const body = {};
      if (selectedRunId && ["last_report","stats_dashboard","open_stats","open_visual","problem_finder","analyze","visual_pack","visual_check_pack"].indexOf(kind) >= 0) {
        body.run = selectedRunId;
      }
      const j = await api("/api/ss_diagnostics/" + kind, {
        method: "POST",
        headers: {"Content-Type": "application/json"},
        body: JSON.stringify(body),
      });
      if (j.async && j.job_id) {
        runningJobKind = kind;
        // Placeholder until log reveals real test_runs/<id> — so RUNNING never "vanishes".
        runningJobRunId = "pending_" + j.job_id;
        if (kind.indexOf("live") >= 0) {
          if (liveSt) liveSt.textContent = "RUNNING";
          if (statsSt) statsSt.textContent = "RUNNING";
          if (qaSt) qaSt.textContent = "RUNNING";
        } else if (kind.indexOf("full") >= 0 || kind.indexOf("runtime") >= 0) {
          if (runtimeSt) runtimeSt.textContent = "RUNNING";
        }
        renderReportList(cachedReports, selectedRunId, cachedReports[0] && cachedReports[0].id);
        if (stEl) stEl.textContent = "RUNNING · " + (j.eta_hint || "");
        showLog(j.log || ("job " + j.job_id));
        LAST_JOB = j.job_id;
        pollDiagJob(j.job_id, kind);
        return;
      }
      if (j.run_id) {
        selectedRunId = j.run_id;
        await refreshRuns(j.run_id);
      } else if (["parser","live_stress","full_runtime","runtime_suite"].indexOf(kind) >= 0) {
        await refreshRuns(null);
      }
      const summary = j.summary || {};
      if (j.visual_status) summary.visual_status = j.visual_status;
      if (j.overall_qa_status) summary.overall_qa_status = j.overall_qa_status;
      if (j.machine_full_runtime_status) summary.machine_full_runtime_status = j.machine_full_runtime_status;
      if (j.live_stress_status) summary.live_stress_status = j.live_stress_status;
      if (j.stats_dashboard_status) summary.stats_dashboard_status = j.stats_dashboard_status;
      if (summary.overall || j.stats_summary || summary.live_stress) {
        applySummary(summary, j.stats_summary || summary.stats_summary, j);
      }
      if (stEl) stEl.textContent = (summary.overall || j.overall || "ok")
        + (j.run_id ? (" · " + j.run_id) : "");
      showLog(j.log || "");
      if (zipEl && j.zip_url) zipEl.innerHTML = "";
      if (statsLink) statsLink.innerHTML = "";
    } catch (e) {
      if (stEl) stEl.textContent = "ошибка";
      showLog(String(e.message || e));
    }
  }
  const map = {
    btnSsDiagLiveStress1: "live_stress_1",
    btnSsDiagLiveStress1Fast: "live_stress_1_fast",
    btnSsDiagLiveStress: "live_stress",
    btnSsDiagLiveStressFast: "live_stress_40_fast",
    btnSsDiagStats: "stats_dashboard",
  };
  for (const [id, kind] of Object.entries(map)) {
    const b = document.getElementById(id);
    if (b) b.onclick = () => run(kind);
  }
  const btnRefresh = document.getElementById("btnSsRunRefresh");
  if (btnRefresh) btnRefresh.onclick = async () => {
    await refreshRuns(selectedRunId);
    await loadRunReport(selectedRunId);
  };
  // Auto-fill report list; refresh while any Live Stress is in progress
  (async () => {
    await refreshRuns(null);
    await loadRunReport(selectedRunId || "");
    setInterval(async () => {
      const hasRunning = (cachedReports || []).some(r => r && (r.in_progress || r.status === "RUNNING"));
      if (!hasRunning && !runningJobRunId) return;
      await refreshRuns(selectedRunId);
    }, 8000);
  })();
}

async function startValidate() {
  const run_id = document.getElementById("valRun").value.trim();
  const hero = (document.querySelector('input[name="valHero"]:checked') || {}).value || "jack";
  const task = document.getElementById("valTask").value;
  const seconds = document.getElementById("valSeconds").value.trim() || "30";
  if (!run_id) { alert("Укажи RUN_ID"); return; }
  document.getElementById("valPill").textContent = "starting";
  document.getElementById("valPill").className = "pill on";
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({action:"validate", run_id, task, seconds, hero}),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    pollValidate(j.job_id, run_id, task, hero);
  } catch (e) {
    document.getElementById("valPill").textContent = "err";
    alert(e.message||e);
  }
}

async function pollValidate(jobId, runId, task, hero) {
  for (let i = 0; i < 180; i++) {
    await new Promise(r => setTimeout(r, 3000));
    let detail = null;
    try { detail = await api("/api/jobs/"+encodeURIComponent(jobId)); } catch (_) {}
    if (detail) {
      document.getElementById("valPill").textContent = detail.status;
      if (detail.status === "ok") {
        await refreshValidateVideo(runId, task, hero);
        return;
      }
      if (detail.status === "error") {
        const tail = (detail.log_tail || "").trim().split("\n").filter(Boolean);
        const last = tail[tail.length - 1] || "error";
        document.getElementById("valPill").textContent = last.slice(0, 80);
        document.getElementById("valPill").className = "pill";
        return;
      }
    }
    try {
      const v = await api("/api/validate/status?" + new URLSearchParams({run_id: runId, task, hero}).toString());
      if (v.ready) {
        await refreshValidateVideo(runId, task, hero);
        return;
      }
    } catch (_) {}
  }
  document.getElementById("valPill").textContent = "timeout";
}

async function refreshValidateVideo(runId, task, hero) {
  runId = runId || document.getElementById("valRun").value.trim();
  task = task || document.getElementById("valTask").value;
  hero = hero || (document.querySelector('input[name="valHero"]:checked') || {}).value || "jack";
  const q = new URLSearchParams({run_id: runId, task, hero, t: String(Date.now())});
  let pull = null;
  for (let i = 0; i < 8; i++) {
    try {
      pull = await api("/api/validate/pull?" + q.toString());
    } catch (e) {
      pull = {ok:false, error: String(e.message||e)};
    }
    if (pull && pull.ok) break;
    document.getElementById("valPill").textContent = ((pull && pull.error) || "waiting mp4").slice(0, 80);
    await new Promise(r => setTimeout(r, 2000));
  }
  if (!pull || !pull.ok) {
    document.getElementById("valPill").textContent = (pull && pull.error) || "no video";
    document.getElementById("valPill").className = "pill";
    return;
  }
  const vid = document.getElementById("valVideo");
  vid.src = "/api/validate/file?" + q.toString();
  vid.load();
  document.getElementById("valPill").textContent = "playing";
  document.getElementById("valPill").className = "pill on";
}

const VAL_TASKS = {
  jack: [
    ["wood","wood (дрова)"],["food","food (еда)"],["water","water (вода)"],
    ["zombie","zombie"],["stream","stream (презентация)"],
  ],
  lily: [
    ["food","food (еда)"],["water","water (вода)"],["heat","heat (костёр)"],
    ["flower","flower (цветы)"],["stream","stream (презентация)"],
  ],
  george: [
    ["food","food (еда)"],["water","water (вода)"],["heat","heat (костёр)"],
    ["stream","stream (презентация)"],
  ],
};

function fillValTasks() {
  const hero = (document.querySelector('input[name="valHero"]:checked') || {}).value || "jack";
  const sel = document.getElementById("valTask");
  const prev = sel.value;
  sel.innerHTML = "";
  for (const [v, label] of (VAL_TASKS[hero] || VAL_TASKS.jack)) {
    const o = el("option", {value:v, text:label});
    if (v === prev) o.selected = true;
    sel.append(o);
  }
  if (![...sel.options].some(o => o.selected)) sel.selectedIndex = 0;
}

async function boot() {
  try {
    CFG = (await api("/api/config")).config;
  } catch (e) {
    CFG = {build:"", tb_url:"", jack:{}, lily:{}, george:{}, chart_tags:[]};
    flash("hdrMeta", "config err: "+e);
  }
  document.getElementById("buildName").value = CFG.build || "";
  document.getElementById("tbUrl").value = CFG.tb_url || "";
  // Сразу рисуем кнопки запуска — не ждём TB.
  renderHeroes();
  document.getElementById("btnDeploy").onclick = () => deploy(false);
  document.getElementById("btnSyncOnly").onclick = () => deploy(true);
  document.getElementById("btnSaveCfg").onclick = saveCfg;
  document.getElementById("btnRefreshTb").onclick = async () => {
    try {
      const tb2 = await api("/api/tb/runs");
      TB_RUNS = tb2.runs || [];
      renderHeroes();
      await loadCharts();
      flash("hdrMeta", `TB runs ${TB_RUNS.length}`);
    } catch (e) { flash("hdrMeta", "TB refresh err: "+e); }
  };
  document.getElementById("btnKill").onclick = () => killTrainOnly();
  document.getElementById("btnKillValidate").onclick = () => killValidateOnly();
  document.getElementById("btnKillStream").onclick = () => killStreamOnly();
  const btnKillModeTrain = document.getElementById("btnKillModeTraining");
  if (btnKillModeTrain) btnKillModeTrain.onclick = () => killModeTraining();
  const btnKillModeSs = document.getElementById("btnKillModeStreaming");
  if (btnKillModeSs) btnKillModeSs.onclick = () => killModeStreaming();
  bindModeTabs();
  bindUiViewPersistence();
  restoreUiView();
  wireLlmBotUi();
  document.getElementById("jobSelect").onchange = (e) => { LAST_JOB = e.target.value; pollJobs(); };
  document.getElementById("detailClose").onclick = () => {
    document.getElementById("detailModal").classList.remove("open");
  };
  document.getElementById("detailModal").onclick = (e) => {
    if (e.target.id === "detailModal") e.currentTarget.classList.remove("open");
  };

  document.getElementById("btnValidate").onclick = () => startValidate();
  document.getElementById("btnValRefresh").onclick = () => refreshValidateVideo();
  document.querySelectorAll('input[name="valHero"]').forEach((r) => {
    r.addEventListener("change", fillValTasks);
  });
  fillValTasks();
  flash("hdrMeta", "UI ready");
  pollServer();
  pollJobs();
  setInterval(pollServer, 5000);
  setInterval(pollJobs, 8000);

  try {
    const tb = await api("/api/tb/runs");
    TB_RUNS = tb.runs || [];
    flash("hdrMeta", `TB ${CFG.tb_url||""} · runs ${TB_RUNS.length} · ssh ${(tb.ssh_host||"?")}`);
    renderHeroes();
    await loadCharts();
  } catch (e) {
    flash("hdrMeta", "TB пока недоступен — кнопки запуска работают");
  }
  restoreUiView();

  setInterval(loadCharts, 30000);
}
boot().catch(e => { document.getElementById("hdrMeta").textContent = "UI error: "+e; });
</script>
</body>
</html>
"""


# ── HTTP handler ────────────────────────────────────────────────────


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt: str, *args: Any) -> None:
        print(f"[ui] {self.address_string()} {fmt % args}")

    def _json(self, code: int, obj: Any) -> None:
        raw = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _read_json(self) -> dict:
        n = int(self.headers.get("Content-Length") or 0)
        if n <= 0:
            return {}
        raw = self.rfile.read(n).decode("utf-8", errors="replace")
        try:
            obj = json.loads(raw)
        except json.JSONDecodeError:
            return {}
        return obj if isinstance(obj, dict) else {}

    def do_GET(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        qs = urllib.parse.parse_qs(parsed.query)

        if path in ("/", "/index.html"):
            raw = HTML.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(raw)))
            self.end_headers()
            self.wfile.write(raw)
            return

        if path == "/api/ss_preview/status":
            self._json(200, SS_PREVIEW.status())
            return

        if path == "/api/ss_preview/frame.jpg":
            try:
                if not SS_PREVIEW_FRAME.is_file():
                    self.send_response(404)
                    self.send_header("Content-Type", "text/plain")
                    self.end_headers()
                    self.wfile.write(b"no frame")
                    return
                raw = SS_PREVIEW_FRAME.read_bytes()
                self.send_response(200)
                self.send_header("Content-Type", "image/jpeg")
                self.send_header("Cache-Control", "no-store, max-age=0")
                self.send_header("Content-Length", str(len(raw)))
                self.end_headers()
                self.wfile.write(raw)
            except Exception as e:
                self._json(500, {"error": str(e)})
            return

        if path == "/api/config":
            self._json(200, {"config": load_cfg()})
            return

        if path == "/api/llm_bot/status":
            if LLM_BOT is None:
                self._json(500, {"bot_state": "error", "last_error": _LLM_BOT_IMPORT_ERROR or "LLM_BOT import failed", "logs": [], "chat": []})
                return
            self._json(200, LLM_BOT.status())
            return

        if path == "/api/server_stats":
            cfg = load_cfg()
            # Только кэш. Lock с таймаутом — никогда не клинить HTTP.
            stats = None
            if _stats_lock.acquire(timeout=0.4):
                try:
                    cached = _stats_cache.get("data")
                    stats = dict(cached) if isinstance(cached, dict) else None
                finally:
                    _stats_lock.release()
            if stats is None:
                stats = _stats_pending_payload(
                    _ssh_host_cache or (cfg.get("ssh_host") or "lab_comp")
                )
            try:
                act = dict(stats.get("activity") or {})
                if LLM_BOT is not None and LLM_BOT.is_live():
                    act["llm_bot"] = True
                    tasks = list(act.get("tasks") or [])
                    if not any(t.get("kill") == "llm_bot" for t in tasks):
                        tasks.append({
                            "id": "llm_bot",
                            "kind": "llm_bot",
                            "label": "LLM Bot",
                            "kill": "llm_bot",
                        })
                    act["tasks"] = tasks
                stats["activity"] = act
            except Exception:
                pass
            if ui_runs_on_lab() or stats.get("mode") == "local" or stats.get("host") == "local":
                stats["mode"] = "local"
                stats["host"] = stats.get("host") or "local"
                stats["note"] = "UI on lab_comp: local shell (no SSH-to-self)"
                stats["tried"] = ["local"]
            else:
                stats["mode"] = stats.get("mode") or "ssh"
                stats["note"] = "stats via ssh only; not local PC"
                stats["tried"] = ssh_candidates(cfg.get("ssh_host") or "lab_comp")
            stats["ssh_cache"] = _ssh_host_cache or ""
            self._json(200, stats)
            return

        if path == "/api/tb/runs":
            cfg = load_cfg()
            runs = list_tb_runs(cfg.get("tb_url") or DEFAULT_TB)
            host = _ssh_host_cache or (cfg.get("ssh_host") or "lab_comp")
            results: list[str] = []
            # SSH results — только если хост уже зарезолвлен (не блокируем UI).
            if _ssh_host_cache:
                try:
                    results = list_result_dirs(_ssh_host_cache)
                except Exception:
                    results = []
            self._json(200, {"runs": runs, "ssh_host": host, "results": results})
            return

        if path == "/api/tb/run_tags":
            cfg = load_cfg()
            tb = (cfg.get("tb_url") or DEFAULT_TB).rstrip("/")
            runs = [r for r in (qs.get("runs", [""])[0] or "").split("|") if r]
            tagset: set[str] = set()
            try:
                all_tags = tb_get(tb, "/data/plugin/scalars/tags", timeout=40)
                for run in runs:
                    entry = all_tags.get(run)
                    if isinstance(entry, dict):
                        tagset.update(entry.keys())
                    elif isinstance(entry, list):
                        tagset.update(entry)
            except Exception as e:
                self._json(200, {"tags": [], "error": str(e), "runs": runs})
                return
            self._json(200, {"tags": sorted(tagset), "runs": runs})
            return

        if path == "/api/tb/series":
            cfg = load_cfg()
            tb = (cfg.get("tb_url") or DEFAULT_TB).rstrip("/")
            runs = [r for r in (qs.get("runs", [""])[0] or "").split("|") if r]
            tags = [t for t in (qs.get("tags", [""])[0] or "").split("|") if t]
            if not tags:
                tags = list(cfg.get("chart_tags") or DEFAULT_CFG["chart_tags"])
            series: dict[str, list[dict[str, float]]] = {}
            for run in runs:
                for tag in tags:
                    key = f"{run} · {tag.split('/')[-1]}"
                    try:
                        q = urllib.parse.urlencode({"run": run, "tag": tag})
                        data = tb_get(tb, f"/data/plugin/scalars/scalars?{q}", timeout=40)
                        # [[wall, step, value], ...]
                        pts = [{"x": float(p[1]), "y": float(p[2])} for p in data]
                        # downsample
                        if len(pts) > 300:
                            step = max(1, len(pts) // 300)
                            pts = pts[::step]
                        series[key] = pts
                    except Exception as e:
                        series[key] = []
                        print(f"[ui] tb series fail {run} {tag}: {e}")
            self._json(200, {"series": series})
            return

        if path == "/api/jobs":
            with _jobs_lock:
                jobs = sorted(_jobs.values(), key=lambda j: j["started"], reverse=True)[:30]
                out = [{k: j[k] for k in ("id", "name", "status", "started", "ended", "cmd", "tmux")} for j in jobs]
            self._json(200, {"jobs": out})
            return

        if path.startswith("/api/jobs/"):
            jid = urllib.parse.unquote(path[len("/api/jobs/") :])
            snap = job_snapshot(jid)
            if not snap:
                self._json(404, {"error": "job not found"})
                return
            self._json(200, snap)
            return

        if path == "/api/validate/status":
            cfg = load_cfg()
            host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
            try:
                rid = qs.get("run_id", [""])[0]
                task = qs.get("task", [""])[0]
                hero = qs.get("hero", ["jack"])[0]
                self._json(200, validate_remote_ready(host, rid, task, hero))
            except Exception as e:
                self._json(400, {"ok": False, "ready": False, "error": str(e)})
            return

        if path == "/api/validate/pull":
            cfg = load_cfg()
            host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
            try:
                rid = qs.get("run_id", [""])[0]
                task = qs.get("task", [""])[0]
                hero = qs.get("hero", ["jack"])[0]
                self._json(200, validate_pull_mp4(host, rid, task, hero))
            except Exception as e:
                self._json(400, {"ok": False, "error": str(e)})
            return

        if path == "/api/validate/file":
            try:
                rid = sanitize_run_id(qs.get("run_id", [""])[0])
                hero = sanitize_hero(qs.get("hero", ["jack"])[0])
                task = sanitize_task(qs.get("task", [""])[0], hero)
            except Exception as e:
                self._json(400, {"error": str(e)})
                return
            local = local_validate_mp4(rid, task, hero)
            if not local.is_file():
                self._json(404, {"error": "video not cached — сначала pull"})
                return
            data = local.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "video/mp4")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)
            return

        if path == "/api/ss_diagnostics/download_zip":
            name = qs.get("name", [""])[0]
            name = Path(name).name
            if not name or ".." in name or not name.endswith(".zip"):
                self._json(400, {"error": "bad name"})
                return
            zp = ROOT / "artifacts" / "streaming_survival" / "exports" / name
            if not zp.is_file():
                self._json(404, {"error": "zip not found"})
                return
            data = zp.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "application/zip")
            self.send_header("Content-Disposition", f'attachment; filename="{name}"')
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return

        if path == "/api/ss_diagnostics/stats_dashboard":
            run = _ss_resolve_run((qs.get("run") or [None])[0])
            if run is None:
                self._json(404, {"error": "no run"})
                return
            dash = _ss_ensure_stats(run) or (run / "stats" / "stats_dashboard.html")
            if not dash.is_file():
                self._json(404, {"error": "stats_dashboard.html missing", "run_dir": str(run), "run_id": run.name})
                return
            data = dash.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)
            return

        if path == "/api/ss_diagnostics/reports_page":
            data = _ss_reports_page_html().encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)
            return

        if path == "/api/ss_diagnostics/runs" or path.startswith("/api/ss_diagnostics/runs"):
            runs = _ss_list_runs(60, reports_only=True)
            latest = next((r for r in runs if r.get("has_live")), None)
            self._json(
                200,
                {
                    "runs": runs,
                    "latest_id": (latest or {}).get("id"),
                },
            )
            return

        if path.startswith("/api/ss_diagnostics/stats_file"):
            run = _ss_resolve_run((qs.get("run") or [None])[0])
            if run is None:
                self._json(404, {"error": "no run"})
                return
            rel = qs.get("path", [""])[0].replace("\\", "/").lstrip("/")
            if not rel or ".." in rel:
                self._json(400, {"error": "bad path"})
                return
            # Allow problem_finder at run root and stats/* / screenshots/*
            fp = (run / rel).resolve()
            run_res = run.resolve()
            try:
                fp.relative_to(run_res)
            except ValueError:
                self._json(404, {"error": "not found", "run_dir": str(run), "path": rel})
                return
            if not fp.is_file():
                # Helpful hint when charts missing (no matplotlib during stats gen)
                hint = ""
                if "charts/" in rel and (run / "stats" / "charts" / "README_NO_MATPLOTLIB.txt").is_file():
                    hint = " charts missing: regenerate stats with matplotlib"
                self._json(
                    404,
                    {
                        "error": "not found" + hint,
                        "run_dir": str(run),
                        "run_id": run.name,
                        "path": rel,
                        "exists_stats": (run / "stats").is_dir(),
                    },
                )
                return
            data = fp.read_bytes()
            ctype = "application/octet-stream"
            if fp.suffix == ".png":
                ctype = "image/png"
            elif fp.suffix in (".csv", ".md", ".txt"):
                ctype = "text/plain; charset=utf-8"
            elif fp.suffix == ".json":
                ctype = "application/json"
            elif fp.suffix == ".html":
                ctype = "text/html; charset=utf-8"
            self.send_response(200)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)
            return

        self._json(404, {"error": "not found"})

    def do_POST(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        body = self._read_json()

        if path == "/api/config":
            with _cfg_lock:
                cfg = load_cfg()
                deep_merge(cfg, body)
                save_cfg(cfg)
            self._json(200, {"config": cfg})
            return

        if path.startswith("/api/ss_preview/"):
            try:
                if path == "/api/ss_preview/start":
                    self._json(200, SS_PREVIEW.start(float(body.get("interval_sec") or 1.6)))
                    return
                if path == "/api/ss_preview/stop":
                    self._json(200, SS_PREVIEW.stop())
                    return
                if path == "/api/ss_preview/once":
                    SS_PREVIEW.capture_once()
                    self._json(200, SS_PREVIEW.status())
                    return
                if path == "/api/ss_preview/status":
                    self._json(200, SS_PREVIEW.status())
                    return
                self._json(404, {"error": f"unknown ss_preview path {path}"})
                return
            except Exception as e:
                self._json(500, {"ok": False, "error": str(e), **SS_PREVIEW.status()})
                return

        if path.startswith("/api/llm_bot/"):
            if LLM_BOT is None:
                self._json(500, {"ok": False, "error": _LLM_BOT_IMPORT_ERROR or "LLM_BOT import failed"})
                return
            try:
                if path == "/api/llm_bot/start":
                    self._json(200, LLM_BOT.start())
                    return
                if path == "/api/llm_bot/stop":
                    self._json(200, LLM_BOT.stop())
                    return
                if path == "/api/llm_bot/mode":
                    self._json(200, LLM_BOT.set_mode(bool(body.get("listen_stream"))))
                    return
                if path == "/api/llm_bot/local_chat":
                    self._json(
                        200,
                        LLM_BOT.local_chat(
                            str(body.get("username") or "viewer"),
                            str(body.get("message") or ""),
                        ),
                    )
                    return
                if path == "/api/llm_bot/clear_chat":
                    self._json(200, LLM_BOT.clear_chat())
                    return
                self._json(404, {"error": f"unknown llm_bot path {path}"})
                return
            except Exception as e:
                self._json(500, {"ok": False, "error": str(e), "status": LLM_BOT.status()})
                return

        if path.startswith("/api/ss_diagnostics/"):
            kind = path.rsplit("/", 1)[-1]
            try:
                run_id = body.get("run") or body.get("run_id") or None
                self._json(200, run_ss_diagnostics(kind, run_id=run_id))
            except Exception as e:
                self._json(500, {"overall": "FAIL", "log": str(e)})
            return

        if path == "/api/action":
            cfg = load_cfg()
            action = body.get("action")
            try:
                if action == "deploy":
                    jid = action_deploy(cfg, skip_build=False)
                elif action == "sync_only":
                    jid = action_deploy(cfg, skip_build=True)
                elif action == "kill_train":
                    jid = action_kill_train(cfg)
                elif action == "kill_validate":
                    jid = action_kill_validate(cfg)
                elif action == "kill_stream":
                    jid = action_kill_stream(cfg)
                elif action == "kill_mode_training":
                    jid = action_kill_mode_training(cfg)
                elif action == "kill_mode_streaming":
                    jid = action_kill_mode_streaming(cfg)
                elif action == "start_streaming_survival":
                    jid = action_start_streaming_survival(cfg)
                elif action == "stop_streaming_survival":
                    jid = action_stop_streaming_survival(cfg)
                elif action == "restart_joint_train":
                    with _cfg_lock:
                        cfg2 = load_cfg()
                        cfg2.setdefault("joint", {})
                        for k in ("init_jack", "init_lily", "init_george", "run_id", "tb_run", "tb_runs"):
                            if body.get(k) is not None:
                                cfg2["joint"][k] = body.get(k)
                        save_cfg(cfg2)
                    jid = action_restart_joint_train(cfg2, body)
                elif action == "restart_joint_stream":
                    if body.get("run_id"):
                        cfg2 = load_cfg()
                        cfg2.setdefault("joint", {})
                        cfg2["joint"]["run_id"] = body.get("run_id")
                        save_cfg(cfg2)
                        cfg = cfg2
                    jid = action_restart_joint_stream(cfg, body)
                elif action == "train":
                    hero = body.get("hero")
                    stage = body.get("stage")
                    if hero not in ("jack", "lily", "george") or stage not in ("s1", "s2", "cur"):
                        raise ValueError("hero/stage invalid")
                    # persist run fields
                    with _cfg_lock:
                        cfg2 = load_cfg()
                        cfg2.setdefault(hero, {})
                        for k in ("run_s1", "run_s2", "tb_s1", "tb_s2"):
                            if body.get(k) is not None:
                                cfg2[hero][k] = body.get(k)
                        save_cfg(cfg2)
                    jid = action_train(cfg, hero, stage, body)
                elif action == "joint_train":
                    with _cfg_lock:
                        cfg2 = load_cfg()
                        cfg2.setdefault("joint", {})
                        for k in ("init_jack", "init_lily", "init_george", "run_id", "tb_run", "tb_runs"):
                            if body.get(k) is not None:
                                cfg2["joint"][k] = body.get(k)
                        save_cfg(cfg2)
                    jid = action_joint_train(cfg, body)
                elif action == "joint_stream":
                    if body.get("run_id"):
                        cfg2 = load_cfg()
                        cfg2.setdefault("joint", {})
                        cfg2["joint"]["run_id"] = body.get("run_id")
                        save_cfg(cfg2)
                    jid = action_joint_stream(cfg, body)
                elif action == "validate":
                    jid = action_validate(
                        cfg,
                        body.get("run_id") or "",
                        body.get("task") or "",
                        body.get("seconds") or 30,
                        body.get("config"),
                        body.get("hero") or "jack",
                    )
                else:
                    raise ValueError(f"unknown action {action}")
                self._json(200, {"job_id": jid})
            except Exception as e:
                self._json(400, {"error": str(e)})
            return

        self._json(404, {"error": "not found"})


def main() -> None:
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    if not CFG_PATH.is_file():
        save_cfg(DEFAULT_CFG)
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    print(f"[train_lab_ui] http://127.0.0.1:{PORT}", flush=True)
    print(f"[train_lab_ui] bind={HOST}:{PORT}", flush=True)
    print(f"[train_lab_ui] root={ROOT}", flush=True)
    print(f"[train_lab_ui] config={CFG_PATH}", flush=True)
    print("[train_lab_ui] remote trains: setsid+nohup — UI close does NOT stop lab jobs", flush=True)
    # SSH resolve + stats в фоне — иначе UI ждёт таймауты и pill не зеленеет.
    threading.Thread(
        target=lambda: resolve_ssh_host(load_cfg().get("ssh_host") or "lab_comp"),
        daemon=True,
    ).start()
    start_stats_background_loop()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n[train_lab_ui] stop UI only — remote nohup trains keep running", flush=True)


if __name__ == "__main__":
    main()
