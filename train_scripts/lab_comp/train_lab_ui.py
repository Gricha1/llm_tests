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
        "init_george": "george_1",
        "run_id": "jlg_finetune_1",
        "tb_run": "",
        "tb_runs": [],
        "locked": None,
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
_ssh_gate = threading.Semaphore(1)  # один живой ssh к lab — иначе на Windows копятся ssh.exe
_ssh_active_lock = threading.Lock()
# pid -> {proc, started, remote, host}
_ssh_active: dict[int, dict[str, Any]] = {}
_ssh_reaper_started = False
_SSH_STALE_SEC = 22.0  # висячий ssh старше этого — kill
_SSH_ORPHAN_SEC = 18.0  # сироты BatchMode вне учёта UI
_SSH_WARN_COUNT = 6  # жёлтый/осторожно
_SSH_BAD_COUNT = 10  # красный — близко к лимиту MaxSessions
_ssh_count_cache: dict[str, Any] = {"t": 0.0, "local": 0}
_ssh_count_lock = threading.Lock()
_stats_lock = threading.Lock()
_stats_cache: dict[str, Any] = {"t": 0.0, "data": None}
_stats_inflight = False
_STATS_CACHE_TTL = 8.0
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
    "obs": False,
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
    """Короткий ping. Идёт через общий gate — не плодит параллельные ssh.exe."""
    if ui_runs_on_lab() or host == "local":
        return True
    got = _ssh_gate.acquire(timeout=2.0)
    if not got:
        return False
    try:
        r = subprocess.run(
            [
                bin_,
                "-o", "BatchMode=yes",
                "-o", f"ConnectTimeout={connect_timeout}",
                "-o", "ConnectionAttempts=1",
                "-o", "ServerAliveInterval=2",
                "-o", "ServerAliveCountMax=1",
                host,
                "true",
            ],
            capture_output=True,
            timeout=connect_timeout + 3,
        )
        return r.returncode == 0
    except Exception:
        return False
    finally:
        _ssh_gate.release()


def cleanup_orphan_batch_ssh(max_age: float | None = None) -> int:
    """Убивает сиротские BatchMode ssh к lab (часто остаются после timeout на Windows).

    Не трогает интерактивные/Cursor сессии без BatchMode=yes.
    """
    age_lim = float(_SSH_ORPHAN_SEC if max_age is None else max_age)
    if os.name != "nt":
        return 0
    killed = 0
    try:
        r = subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-Command",
                "$cut = (Get-Date).AddSeconds(-%d); "
                "Get-CimInstance Win32_Process -Filter \"Name = 'ssh.exe'\" | "
                "Where-Object { "
                "  if (%d -le 0) { $true } "
                "  else { $_.CreationDate -and ([Management.ManagementDateTimeConverter]::ToDateTime($_.CreationDate) -lt $cut) } "
                "} | "
                "ForEach-Object { "
                "  $cl = [string]$_.CommandLine; "
                "  if ($cl -match 'BatchMode=yes' -and ("
                "      ($cl -match 'lab_comp') -or ($cl -match '192\\.168\\.194\\.7') -or "
                "      ($cl -match '10\\.43\\.71\\.7') -or ($cl -match '192\\.168\\.50\\.18') -or "
                "      ($cl -match 'lab_comp_local')"
                "    )) { $_.ProcessId } "
                "}" % (int(max(0, age_lim)), int(max(0, age_lim))),
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=8,
        )
        pids = []
        for ln in (r.stdout or "").splitlines():
            ln = ln.strip()
            if ln.isdigit():
                pids.append(int(ln))
        tracked = set()
        with _ssh_active_lock:
            tracked = set(_ssh_active.keys())
        for pid in pids:
            if pid in tracked:
                continue
            try:
                subprocess.run(
                    ["taskkill", "/F", "/T", "/PID", str(pid)],
                    capture_output=True,
                    timeout=4,
                )
                killed += 1
            except Exception:
                pass
    except Exception:
        pass
    return killed


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
    # сироты BatchMode — отдельно, без рекурсии
    if os.name == "nt":
        killed += cleanup_orphan_batch_ssh(max_age=max(age, _SSH_ORPHAN_SEC))
    return killed


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


def _lab_ssh_cmdline_markers() -> tuple[str, ...]:
    return (
        "lab_comp",
        "lab_comp_local",
        "192.168.194.7",
        "10.43.71.7",
        "192.168.50.18",
    )


def _count_local_ssh_to_lab() -> int:
    """Сколько ssh.exe/ssh на этом ПК смотрят на lab (без нового SSH)."""
    markers = _lab_ssh_cmdline_markers()
    now = time.time()
    with _ssh_count_lock:
        age = now - float(_ssh_count_cache.get("t") or 0)
        if age < 3.0 and _ssh_count_cache.get("t"):
            return int(_ssh_count_cache.get("local") or 0)
    n = 0
    try:
        if os.name == "nt":
            r = subprocess.run(
                [
                    "powershell",
                    "-NoProfile",
                    "-Command",
                    "Get-CimInstance Win32_Process -Filter \"Name = 'ssh.exe'\" "
                    "| Select-Object -ExpandProperty CommandLine",
                ],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=4,
            )
            lines = (r.stdout or "").splitlines()
        else:
            r = subprocess.run(
                ["ps", "-eo", "args="],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=3,
            )
            lines = [
                ln
                for ln in (r.stdout or "").splitlines()
                if re.search(r"(^|[/\s])ssh(\.exe)?(\s|$)", ln)
            ]
        for ln in lines:
            low = ln.lower()
            if any(m.lower() in low for m in markers):
                n += 1
    except Exception:
        n = 0
    with _ssh_count_lock:
        _ssh_count_cache["t"] = now
        _ssh_count_cache["local"] = n
    return n


def ssh_sessions_snapshot(activity: dict[str, Any] | None = None) -> dict[str, Any]:
    """Сводка SSH для UI: локальные к lab + активные у UI + на lab (:22)."""
    with _ssh_active_lock:
        ui_n = 0
        for meta in _ssh_active.values():
            proc = meta.get("proc")
            try:
                if proc is not None and proc.poll() is None:
                    ui_n += 1
            except Exception:
                pass
    local_n = _count_local_ssh_to_lab()
    remote_n = None
    if isinstance(activity, dict) and activity.get("ssh_remote") is not None:
        try:
            remote_n = int(activity.get("ssh_remote"))
        except (TypeError, ValueError):
            remote_n = None
    # Главный риск «too many sessions» — коннекты с ЭТОГО ПК к lab (BatchMode UI).
    # remote (:22) считает ВСЕХ клиентов (Cursor и т.д.) — не раздуваем им красный total.
    total = max(local_n, ui_n)
    level = "ok"
    if total >= _SSH_BAD_COUNT:
        level = "bad"
    elif total >= _SSH_WARN_COUNT:
        level = "warn"
    return {
        "local": local_n,
        "ui": ui_n,
        "remote": remote_n,
        "total": total,
        "warn_at": _SSH_WARN_COUNT,
        "bad_at": _SSH_BAD_COUNT,
        "level": level,
    }


def _ensure_ssh_reaper() -> None:
    global _ssh_reaper_started
    if _ssh_reaper_started:
        return
    _ssh_reaper_started = True

    def loop() -> None:
        # сразу подчистить зомби от прошлого запуска UI
        try:
            cleanup_orphan_batch_ssh(max_age=8.0)
            cleanup_stale_ssh(max_age=12.0)
        except Exception:
            pass
        while True:
            try:
                cleanup_stale_ssh()
            except Exception:
                pass
            time.sleep(5.0)

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
                # connect fail — процесс мог остаться зомби на Windows
                if proc is not None:
                    _kill_ssh_proc(proc)
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


def _fmt_steps(n: int | None) -> str:
    if n is None or n < 0:
        return ""
    if n >= 1_000_000:
        s = f"{n / 1_000_000:.1f}".rstrip("0").rstrip(".")
        return f"{s}M"
    if n >= 1000:
        if n % 1000 == 0:
            return f"{n // 1000}k"
        return f"{n / 1000:.1f}".rstrip("0").rstrip(".") + "k"
    return str(n)


def probe_joint_run_weights(host: str, runs: dict[str, str]) -> dict[str, Any]:
    """Проверка results/<run>/<behavior>/*.pt → exists + max step."""
    # runs: { "jack": "97_stage2", "lily": "...", "george": "...", "run": "jlg_..." }
    mapping = {
        "jack": ("JackLowLevelAgent",),
        "lily": ("LilyLowLevelAgent",),
        "george": ("GeorgeLowLevelAgent",),
        "run": ("JackLowLevelAgent", "LilyLowLevelAgent", "GeorgeLowLevelAgent"),
    }
    parts: list[str] = []
    order: list[tuple[str, str]] = []
    for key, behaviors in mapping.items():
        rid = sanitize_run_id(runs.get(key) or "")
        if not rid:
            continue
        for beh in behaviors:
            order.append((key, beh))
            parts.append(f"{rid}|{beh}")
    if not parts:
        return {"ok": True, "items": {}}

    remote = f"""
python3 - <<'PY'
import os, re, json
root = os.path.expanduser("{REMOTE_DIR}/results")
specs = {parts!r}
out = []
for spec in specs:
    run, beh = spec.split("|", 1)
    d = os.path.join(root, run, beh)
    exists = os.path.isdir(d)
    best = -1
    if exists:
        rx = re.compile(r"^" + re.escape(beh) + r"-(\\d+)\\.pt$")
        for name in os.listdir(d):
            m = rx.match(name)
            if m:
                best = max(best, int(m.group(1)))
        if best < 0 and os.path.isfile(os.path.join(d, "checkpoint.pt")):
            best = 0
    out.append({{"run": run, "behavior": beh, "exists": exists, "steps": best}})
print(json.dumps(out, ensure_ascii=False))
PY
"""
    try:
        r = ssh_run(host, remote, timeout=25, gate_timeout=12.0)
        rows = json.loads((r.stdout or "").strip().splitlines()[-1] if (r.stdout or "").strip() else "[]")
    except Exception as e:
        return {"ok": False, "error": str(e), "items": {}}

    items: dict[str, Any] = {}
    for key, _ in mapping.items():
        rid = sanitize_run_id(runs.get(key) or "")
        if not rid:
            continue
        beh_rows = [row for row in rows if row.get("run") == rid]
        if key == "run":
            steps_map = {
                row["behavior"].replace("LowLevelAgent", ""): row.get("steps", -1)
                for row in beh_rows
            }
            has_w = any(int(row.get("steps", -1)) >= 0 and row.get("exists") for row in beh_rows)
            exists = any(row.get("exists") for row in beh_rows) or bool(beh_rows)
            # folder may exist even if empty — check dir
            if not beh_rows:
                exists = False
            parts_txt = []
            for short, beh_full in (
                ("Jack", "JackLowLevelAgent"),
                ("Lily", "LilyLowLevelAgent"),
                ("George", "GeorgeLowLevelAgent"),
            ):
                st = next((int(row.get("steps", -1)) for row in beh_rows if row.get("behavior") == beh_full), -1)
                if st > 0:
                    parts_txt.append(f"{short} {_fmt_steps(st)}")
                elif st == 0:
                    parts_txt.append(f"{short} ckpt")
            items[key] = {
                "run_id": rid,
                "exists": exists,
                "has_weights": has_w,
                "steps": steps_map,
                "label": ("есть · " + " · ".join(parts_txt)) if parts_txt else ("есть (нет .pt)" if exists else "нет папки"),
            }
        else:
            want_beh = mapping[key][0]
            row = next((x for x in beh_rows if x.get("behavior") == want_beh), None)
            st = int(row.get("steps", -1)) if row else -1
            exists = bool(row and row.get("exists"))
            if st > 0:
                label = f"есть · {_fmt_steps(st)}"
            elif st == 0:
                label = "есть · checkpoint.pt"
            elif exists:
                label = "есть (нет .pt)"
            else:
                label = "нет папки"
            items[key] = {
                "run_id": rid,
                "exists": exists,
                "has_weights": st >= 0 and exists,
                "steps": st,
                "label": label,
            }
    return {"ok": True, "items": items}


def _parse_activity_json(raw: str) -> dict[str, Any]:
    empty = dict(_EMPTY_ACTIVITY)
    empty["tasks"] = []
    empty["stream_fps"] = {}
    try:
        data = json.loads(raw) if raw.startswith("{") else empty
    except Exception:
        data = empty
    if not isinstance(data, dict):
        data = empty
    out = dict(empty)
    for k in _EMPTY_ACTIVITY:
        if k == "tasks":
            out["tasks"] = list(data.get("tasks") or [])
        else:
            out[k] = bool(data.get(k))
    # stream_fps / ssh_remote не булевы — протаскиваем как есть
    fps = data.get("stream_fps")
    out["stream_fps"] = fps if isinstance(fps, dict) else {}
    if data.get("ssh_remote") is not None:
        try:
            out["ssh_remote"] = int(data.get("ssh_remote"))
        except Exception:
            pass
    # страховка: если в tasks есть stream/obs — флаги тоже true
    for t in out["tasks"]:
        if not isinstance(t, dict):
            continue
        kind = str(t.get("kind") or "")
        if kind == "stream":
            out["stream"] = True
        if kind == "obs":
            out["obs"] = True
    out["ok"] = True
    if isinstance(data.get("metrics"), dict):
        out["metrics"] = data["metrics"]
    return out


def _extract_activity_json_line(blob: str) -> str:
    """Берём самый полный ACTIVITY JSON (не случайный {…} из stderr)."""
    best = ""
    best_score = -1
    for ln in (blob or "").splitlines():
        s = ln.strip()
        if not s.startswith("{") or "stream" not in s:
            continue
        score = 0
        if '"tasks"' in s:
            score += 5
        if '"stream_fps"' in s:
            score += 3
        if '"obs"' in s:
            score += 2
        if '"llm_bot"' in s:
            score += 1
        score += min(len(s), 50000) // 1000
        if score > best_score:
            best_score = score
            best = s
    if best:
        return best
    # fallback: последняя строка-объект
    for ln in reversed((blob or "").splitlines()):
        s = ln.strip()
        if s.startswith("{"):
            return s
    return ""


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
import json, os, re, time
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
if not stream:
    try:
        import subprocess as _sp
        stream = (
            _sp.call(["pgrep", "-f", "stream_onnx_infer\\.py"], stdout=_sp.DEVNULL, stderr=_sp.DEVNULL) == 0
            or _sp.call(["pgrep", "-f", "forestStreamOnly"], stdout=_sp.DEVNULL, stderr=_sp.DEVNULL) == 0
        )
    except Exception:
        pass
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
obs = any((not is_ui_noise(c)) and re.search(r"(?:^|[\s/])obs(?:\s|$)", c) for c in cmds)
if not obs:
    try:
        import subprocess as _sp
        obs = _sp.call(["pgrep", "-x", "obs"], stdout=_sp.DEVNULL, stderr=_sp.DEVNULL) == 0
    except Exception:
        obs = False
if obs:
    tasks.append({"id": "obs", "kind": "obs", "label": "OBS Studio (--startstreaming)", "kill": "obs"})

def stream_fps():
    root = os.path.expanduser("~/lab_work_space/forest_survival/results")
    now = time.time()
    js = os.path.join(root, "stream_fps.json")
    jsonl = os.path.join(root, "stream_fps.jsonl")

    def _recent_points(window_s: float = 10.5, max_pts: int = 25):
        pts = []
        if not os.path.isfile(jsonl):
            return pts
        try:
            with open(jsonl, "rb") as f:
                f.seek(0, 2)
                n = f.tell()
                f.seek(max(0, n - 262144))
                tail = f.read().decode("utf-8", "replace")
        except OSError:
            return pts
        cutoff = now - window_s
        for ln in tail.splitlines():
            ln = ln.strip()
            if not ln:
                continue
            try:
                d = json.loads(ln)
            except Exception:
                continue
            if not isinstance(d, dict):
                continue
            ts = d.get("ts")
            if not isinstance(ts, (int, float)) or ts < cutoff:
                continue
            fps_v = d.get("fps")
            hitch_v = d.get("hitch_dt_ms")
            pts.append(
                {
                    "ts": round(float(ts), 2),
                    "fps": float(fps_v) if isinstance(fps_v, (int, float)) else None,
                    "hitch_dt_ms": int(hitch_v) if isinstance(hitch_v, (int, float)) else None,
                    "kind": str(d.get("kind") or ""),
                }
            )
        if len(pts) > max_pts:
            pts = pts[-max_pts:]
        return pts

    def _decorate(out):
        hist = _recent_points(10.5, 25)
        out["history_10s"] = hist
        out["stream_on"] = bool(stream)
        n = out.get("fps")
        hitch = out.get("hitch_dt_ms")
        lag_now = isinstance(n, (int, float)) and float(n) < 15.0
        lag_now = lag_now or (isinstance(hitch, (int, float)) and int(hitch) >= 800)
        lag_10s = any(
            ((p.get("fps") is not None and float(p.get("fps")) < 15.0)
             or (p.get("hitch_dt_ms") is not None and int(p.get("hitch_dt_ms")) >= 800))
            for p in hist
        )
        out["lag_now"] = bool(lag_now)
        out["lag_10s"] = bool(lag_10s)
        # min/max: из snapshot сессии, иначе из jsonl за ~30 мин
        fmin = out.get("fps_min")
        fmax = out.get("fps_max")
        if not isinstance(fmin, (int, float)) or not isinstance(fmax, (int, float)):
            long_hist = _recent_points(30 * 60, 400)
            vals = [
                float(p["fps"])
                for p in long_hist
                if isinstance(p.get("fps"), (int, float))
            ]
            if isinstance(n, (int, float)):
                vals.append(float(n))
            if vals:
                fmin = min(vals) if not isinstance(fmin, (int, float)) else fmin
                fmax = max(vals) if not isinstance(fmax, (int, float)) else fmax
        if isinstance(fmin, (int, float)):
            out["fps_min"] = round(float(fmin), 1)
        if isinstance(fmax, (int, float)):
            out["fps_max"] = round(float(fmax), 1)
        return out

    if os.path.isfile(js) and (now - os.path.getmtime(js) < 12):
        try:
            with open(js, encoding="utf-8") as f:
                d = json.load(f)
            if isinstance(d, dict):
                d["source"] = d.get("source") or "json"
                d["age_s"] = round(now - os.path.getmtime(js), 1)
                return _decorate(d)
        except Exception:
            pass
    logs = []
    try:
        for name in os.listdir(root):
            if name.startswith("stream_onnx_") and name.endswith(".log"):
                logs.append(os.path.join(root, name))
    except OSError:
        return {}
    if not logs:
        return {}
    path = max(logs, key=os.path.getmtime)
    try:
        with open(path, "rb") as f:
            f.seek(0, 2)
            n = f.tell()
            f.seek(max(0, n - 32000))
            tail = f.read().decode("utf-8", "replace")
    except OSError:
        return {}
    summary = hitch = ""
    for ln in tail.splitlines():
        if " fps≈" in ln and "steps=" in ln:
            summary = ln.strip()
        if "HITCH " in ln:
            hitch = ln.strip()
    out = {"source": "log", "log": os.path.basename(path), "summary": summary, "hitch": hitch}
    m = re.search(
        r"steps=(\d+) fps≈([\d.]+) step≈([\d.]+)ms hitches=(\d+) slow\(>(\d+)ms\)=(\d+)",
        summary,
    )
    if m:
        out.update(
            steps=int(m.group(1)),
            fps=float(m.group(2)),
            step_ms=float(m.group(3)),
            hitches=int(m.group(4)),
            slow=int(m.group(6)),
        )
    hm = re.search(r"HITCH step=(\d+) dt=(\d+)ms .* / (\d+) FPS", hitch)
    if hm:
        out.update(
            hitch_step=int(hm.group(1)),
            hitch_dt_ms=int(hm.group(2)),
            hitch_ema_fps=int(hm.group(3)),
        )
    try:
        out["age_s"] = round(now - os.path.getmtime(path), 1)
    except OSError:
        pass
    return _decorate(out)

ssh_remote = 0
try:
    import subprocess as _sp
    out = _sp.check_output(
        "ss -tn state established '( sport = :22 )' 2>/dev/null | tail -n +2 | wc -l",
        shell=True,
        universal_newlines=True,
        timeout=2,
    )
    ssh_remote = int((out or "0").strip() or 0)
except Exception:
    ssh_remote = 0

def _gib(n):
    return round(float(n) / (1024.0 ** 3), 1)

def collect_metrics():
    m = {}
    try:
        info = {}
        with open("/proc/meminfo") as f:
            for ln in f:
                parts = ln.split()
                if len(parts) >= 2:
                    info[parts[0].rstrip(":")] = int(parts[1])
        total_b = info.get("MemTotal", 0) * 1024
        avail_b = info.get("MemAvailable", 0) * 1024
        used_b = max(0, total_b - avail_b)
        m["ram"] = {
            "used_gib": _gib(used_b),
            "total_gib": _gib(total_b),
            "avail_gib": _gib(avail_b),
            "pct": round(100.0 * used_b / total_b, 1) if total_b else 0.0,
        }
        st = info.get("SwapTotal", 0) * 1024
        sf = info.get("SwapFree", 0) * 1024
        su = max(0, st - sf)
        m["swap"] = {
            "used_gib": _gib(su),
            "total_gib": _gib(st),
            "pct": round(100.0 * su / st, 1) if st else 0.0,
        }
    except Exception:
        pass
    try:
        import shutil
        home = os.path.expanduser("~")
        u = shutil.disk_usage(home)
        m["disk"] = {
            "path": home,
            "free_gib": _gib(u.free),
            "used_gib": _gib(u.used),
            "total_gib": _gib(u.total),
            "pct": round(100.0 * u.used / u.total, 1) if u.total else 0.0,
        }
    except Exception:
        pass
    gpus = []
    try:
        import subprocess as _sp
        out = _sp.check_output(
            [
                "nvidia-smi",
                "--query-gpu=index,name,memory.used,memory.total,utilization.gpu,temperature.gpu",
                "--format=csv,noheader,nounits",
            ],
            universal_newlines=True,
            timeout=3,
        )
        for ln in (out or "").strip().splitlines():
            parts = [p.strip() for p in ln.split(",")]
            if len(parts) < 6:
                continue
            vu, vt = float(parts[2]), float(parts[3])
            gpus.append({
                "index": int(float(parts[0])),
                "name": parts[1],
                "vram_used_mib": vu,
                "vram_total_mib": vt,
                "vram_used_gib": round(vu / 1024.0, 1),
                "vram_total_gib": round(vt / 1024.0, 1),
                "vram_pct": round(100.0 * vu / vt, 1) if vt else 0.0,
                "util": int(float(parts[4])),
                "temp": int(float(parts[5])),
            })
    except Exception:
        pass
    m["gpus"] = gpus
    procs = []
    try:
        import subprocess as _sp
        import pwd
        out = _sp.check_output(
            ["nvidia-smi", "--query-compute-apps=pid,used_gpu_memory", "--format=csv,noheader,nounits"],
            universal_newlines=True,
            timeout=3,
        )
        for ln in (out or "").strip().splitlines():
            parts = [p.strip() for p in ln.split(",")]
            if len(parts) < 2:
                continue
            try:
                pid = int(float(parts[0]))
                used = float(parts[1])
            except Exception:
                continue
            user, comm = "?", "?"
            try:
                with open("/proc/%d/status" % pid) as f:
                    for sl in f:
                        if sl.startswith("Name:"):
                            comm = sl.split(":", 1)[1].strip()
                        elif sl.startswith("Uid:"):
                            uid = int(sl.split()[1])
                            try:
                                user = pwd.getpwuid(uid).pw_name
                            except Exception:
                                user = str(uid)
            except Exception:
                pass
            procs.append({
                "pid": pid,
                "user": user,
                "comm": comm,
                "used_mib": used,
                "used_gib": round(used / 1024.0, 1),
                "label": "%s %.1fG" % (user, used / 1024.0),
            })
    except Exception:
        pass
    m["gpu_procs"] = procs
    top = []
    try:
        import subprocess as _sp
        out = _sp.check_output(
            ["ps", "-eo", "pid,pcpu,pmem,comm", "--sort=-pcpu"],
            universal_newlines=True,
            timeout=2,
        )
        for ln in (out or "").strip().splitlines()[1:8]:
            parts = ln.split(None, 3)
            if len(parts) < 4:
                continue
            top.append({
                "pid": int(parts[0]),
                "cpu": float(parts[1]),
                "mem": float(parts[2]),
                "comm": parts[3],
            })
    except Exception:
        pass
    m["top_cpu"] = top
    return m

print(json.dumps({
    "jack": bool(jack),
    "lily": bool(lily),
    "george": bool(george),
    "joint": bool(joint),
    "validate": bool(validate),
    "stream": bool(stream),
    "streaming_survival": bool(streaming_survival),
    "llm_bot": bool(llm_bot),
    "obs": bool(obs),
    "tasks": tasks,
    "stream_fps": stream_fps(),
    "ssh_remote": int(ssh_remote),
    "metrics": collect_metrics(),
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
        t0 = time.time()
        r = ssh_run(host, _REMOTE_STATS_AND_ACTIVITY, timeout=18, gate_timeout=8.0)
        latency_ms = int(max(0.0, (time.time() - t0) * 1000))
        used = "local" if ui_runs_on_lab() else (_ssh_host_cache or host or "?")
        # stdout отдельно: stderr часто содержит чужие {...} и ломал парсер activity
        out_s = (r.stdout or "").strip()
        err_s = (r.stderr or "").strip()
        full = (out_s + ("\n" + err_s if err_s else "")).strip()
        marker = "=== ACTIVITY_JSON ==="
        metrics: dict[str, Any] = {}
        if marker in out_s:
            head, _, tail = out_s.partition(marker)
            text = head.strip()
            act_line = _extract_activity_json_line(tail)
            activity = _parse_activity_json(act_line)
            raw_m = activity.pop("metrics", None) if isinstance(activity, dict) else None
            if isinstance(raw_m, dict):
                metrics = raw_m
        elif marker in full:
            head, _, tail = full.partition(marker)
            text = head.strip()
            act_line = _extract_activity_json_line(tail)
            activity = _parse_activity_json(act_line)
            raw_m = activity.pop("metrics", None) if isinstance(activity, dict) else None
            if isinstance(raw_m, dict):
                metrics = raw_m
        else:
            text = full
            activity = dict(_EMPTY_ACTIVITY)
            activity["tasks"] = []
            activity["ok"] = False
        return {
            "ok": r.returncode == 0 and bool(text or metrics or (activity or {}).get("ok")),
            "text": text or "(пусто)",
            "host": used,
            "mode": "local" if ui_runs_on_lab() else "ssh",
            "activity": activity,
            "metrics": metrics,
            "latency_ms": latency_ms,
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
            "metrics": {},
            "latency_ms": None,
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
        "metrics": {},
        "latency_ms": None,
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


_RE_SNAP_AGENT = re.compile(
    r"^\s*(Jack|Lily|George)=(?:null|\((?P<x>-?\d+(?:\.\d+)?),(?P<z>-?\d+(?:\.\d+)?)\)"
    r"(?:\s+y=(?P<y>-?\d+(?:\.\d+)?))?"
    r"(?:\s+hp=(?P<hp>-?\d+))?"
    r"(?:\s+sat=(?P<sat>-?\d+))?"
    r"(?:\s+heat=(?P<heat>-?\d+))?"
    r"(?:\s+water=(?P<water>-?\d+))?"
    r"(?:\s+wood=(?P<wood>-?\d+))?"
    r"(?P<dead>\s+DEAD)?)",
    re.M,
)
_RE_SNAP_XZ_LIST = re.compile(
    r"^\s*(trees|sheep|zombies)=(?:\[(?P<body>[^\]]*)\]|\[\])\s+n=(?P<n>\d+)(?:/(?P<target>\d+))?",
    re.M,
)
_RE_XZ_PAIR = re.compile(r"\((-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)\)")
_RE_RECT_XZ = re.compile(
    r"\((-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),sx=(-?\d+(?:\.\d+)?),sz=(-?\d+(?:\.\d+)?)\)"
)
_RE_LANDMARKS = re.compile(
    r"^\s*landmarks(?:\s+house=\((?P<hx>-?\d+(?:\.\d+)?),(?P<hz>-?\d+(?:\.\d+)?)\))?"
    r"(?:\s+lakes=(?:\[(?P<lakes>[^\]]*)\]|\[\]))?"
    r"(?:\s+stones=(?:\[(?P<stones>[^\]]*)\]|\[\]))?"
    r"(?:\s+n_stones=\d+)?"
    r"(?:\s+fences=(?:\[(?P<fences>[^\]]*)\]|\[\]))?",
    re.M,
)

# ForestScene / SS world map fallback (пока стрим без нового DLL).
_PRES_LANDMARKS_FALLBACK_PATH = ROOT / "train_scripts" / "lab_comp" / "pres_world_landmarks_fallback.json"
_PRES_LANDMARKS_FALLBACK: dict[str, Any] = {
    "house": {"x": -3.27, "z": 18.85},
    "lakes": [{"x": 21.96, "z": 33.84, "sx": 12.0, "sz": 10.0}],
    "stones": [{"x": 22.23, "z": 18.05}],
    "fences": [],
    "source": "scene_fallback",
}


def _load_pres_landmarks_fallback() -> dict[str, Any]:
    try:
        if _PRES_LANDMARKS_FALLBACK_PATH.is_file():
            data = json.loads(_PRES_LANDMARKS_FALLBACK_PATH.read_text(encoding="utf-8"))
            if isinstance(data, dict) and (data.get("house") or data.get("fences") or data.get("lakes")):
                data.setdefault("source", "file_fallback")
                return data
    except Exception:
        pass
    return dict(_PRES_LANDMARKS_FALLBACK)


def _parse_xz_list(body: str) -> list[dict[str, float]]:
    out: list[dict[str, float]] = []
    if not body:
        return out
    for m in _RE_XZ_PAIR.finditer(body):
        out.append({"x": float(m.group(1)), "z": float(m.group(2))})
    return out


def _parse_rect_list(body: str) -> list[dict[str, float]]:
    out: list[dict[str, float]] = []
    if not body:
        return out
    for m in _RE_RECT_XZ.finditer(body):
        out.append({
            "x": float(m.group(1)),
            "z": float(m.group(2)),
            "sx": float(m.group(3)),
            "sz": float(m.group(4)),
        })
    return out


def _parse_landmarks_block(block: str) -> dict[str, Any] | None:
    m = _RE_LANDMARKS.search(block)
    if not m:
        return None
    lm: dict[str, Any] = {
        "house": None,
        "lakes": [],
        "stones": [],
        "fences": [],
        "source": "log",
    }
    if m.group("hx") is not None:
        lm["house"] = {"x": float(m.group("hx")), "z": float(m.group("hz"))}
    lm["lakes"] = _parse_rect_list(m.group("lakes") or "")
    lm["stones"] = _parse_xz_list(m.group("stones") or "")
    lm["fences"] = _parse_rect_list(m.group("fences") or "")
    if not lm["house"] and not lm["lakes"] and not lm["stones"] and not lm["fences"]:
        return None
    return lm


def _ensure_landmarks(data: dict[str, Any]) -> dict[str, Any]:
    """Подставляет fallback из ForestScene/SS map, если стрим ещё не пишет landmarks."""
    lm = data.get("landmarks")
    if isinstance(lm, dict) and (
        lm.get("house") or lm.get("lakes") or lm.get("stones") or lm.get("fences")
    ):
        # если live/log дал дом/озеро без забора — дорисуем забор из fallback
        fb = _load_pres_landmarks_fallback()
        if not lm.get("fences") and fb.get("fences"):
            lm = dict(lm)
            lm["fences"] = fb["fences"]
            if not lm.get("stones") and fb.get("stones"):
                lm["stones"] = fb["stones"]
            lm["source"] = str(lm.get("source") or "partial") + "+fence_fallback"
            data["landmarks"] = lm
    else:
        data["landmarks"] = _load_pres_landmarks_fallback()
    # кадры эпизода наследуют landmarks для карты
    frames = data.get("episode_frames")
    shared = data.get("landmarks")
    if isinstance(frames, dict) and isinstance(shared, dict):
        for key in ("start", "end"):
            fr = frames.get(key)
            if not isinstance(fr, dict):
                continue
            fl = fr.get("landmarks")
            if not isinstance(fl, dict) or not (
                fl.get("house") or fl.get("lakes") or fl.get("fences") or fl.get("stones")
            ):
                fr["landmarks"] = shared
            elif not fl.get("fences") and shared.get("fences"):
                fl = dict(fl)
                fl["fences"] = shared["fences"]
                if not fl.get("stones") and shared.get("stones"):
                    fl["stones"] = shared["stones"]
                fr["landmarks"] = fl
    return data


def _empty_agent(name: str) -> dict[str, Any]:
    return {"name": name, "ok": False}


def _map_frame_from_snap(snap: dict[str, Any] | None) -> dict[str, Any] | None:
    """Урезанный кадр для XZ-карты (начало/конец эпизода)."""
    if not isinstance(snap, dict) or not snap.get("ok", True):
        return None
    keys = (
        "ts", "reason", "episode_index", "t",
        "trees", "sheep", "zombies",
        "trees_n", "sheep_n", "zombies_n",
        "trees_target", "sheep_target",
        "jack", "lily", "george", "landmarks",
    )
    out = {k: snap.get(k) for k in keys if k in snap}
    out["ok"] = True
    return out


def parse_presentation_world_log(text: str) -> dict[str, Any]:
    """Парсит stream_world_snapshot.log → текущий SNAP + last_round / episode."""
    if not text:
        return {"ok": False, "error": "empty log"}

    # Блоки SNAP: от строки [SNAP:…] до следующего timestamp-события/SNAP.
    snap_starts = [m.start() for m in re.finditer(r"(?m)^\d{4}-\d{2}-\d{2}[^\n]*\[SNAP:", text)]
    if not snap_starts:
        return {"ok": False, "error": "no SNAP in log"}

    def parse_snap(block: str) -> dict[str, Any]:
        head = block.split("\n", 1)[0]
        reason, env, ep_idx, tval = "tick", "?", 0, 0.0
        hm = re.search(r"\[SNAP:([^\]]+)\]\s+env=(\S+)\s+ep=(\d+)\s+t=([\d.]+)", head)
        if hm:
            reason, env = hm.group(1), hm.group(2)
            ep_idx, tval = int(hm.group(3)), float(hm.group(4))
        else:
            hm_old = re.search(r"\[SNAP:([^\]]+)\]\s+env=(\S+)\s+t=([\d.]+)", head)
            if hm_old:
                reason, env = hm_old.group(1), hm_old.group(2)
                tval = float(hm_old.group(3))
        ts = head[:23] if len(head) >= 23 else ""
        agents = {"jack": _empty_agent("Jack"), "lily": _empty_agent("Lily"), "george": _empty_agent("George")}
        for am in _RE_SNAP_AGENT.finditer(block):
            name = am.group(1)
            key = name.lower()
            if am.group(0).endswith("=null") or "null" in am.group(0).split("=", 1)[-1][:4]:
                agents[key] = _empty_agent(name)
                continue
            agents[key] = {
                "name": name,
                "ok": True,
                "x": float(am.group("x")),
                "z": float(am.group("z")),
                "y": float(am.group("y") or 0),
                "hp": int(am.group("hp") or -1),
                "water": int(am.group("water") or -1),
                "wood": int(am.group("wood") or -1),
                "dead": bool(am.group("dead")),
            }
        world: dict[str, Any] = {
            "trees": [], "sheep": [], "zombies": [],
            "trees_n": 0, "sheep_n": 0, "zombies_n": 0,
            "trees_target": 0, "sheep_target": 0,
        }
        for lm in _RE_SNAP_XZ_LIST.finditer(block):
            kind = lm.group(1)
            pts = _parse_xz_list(lm.group("body") or "")
            n = int(lm.group("n") or 0)
            target = int(lm.group("target") or 0)
            world[kind] = pts
            world[f"{kind}_n"] = n
            if kind in ("trees", "sheep") and target:
                world[f"{kind}_target"] = target
        landmarks = _parse_landmarks_block(block)
        out = {
            "ok": True,
            "ts": ts,
            "reason": reason,
            "env": env,
            "episode_index": ep_idx,
            "t": tval,
            **world,
            "jack": agents["jack"],
            "lily": agents["lily"],
            "george": agents["george"],
        }
        if landmarks:
            out["landmarks"] = landmarks
        return out

    # episode / last_round из последовательности SNAP
    episode = {"water": 0, "wood": 0, "sheep_killed": 0, "trees_chopped": 0}
    last_round = {"water": 0, "wood": 0, "sheep_killed": 0, "trees_chopped": 0, "episode": 0, "at": ""}
    prev_w = {"jack": -1, "lily": -1, "george": -1}
    prev_wood = {"jack": -1, "george": -1}
    prev_sheep = prev_trees = -1
    ep_active = False
    episode_index = 0
    inferred_ep = 0
    cur_start: dict[str, Any] | None = None
    prev_snap: dict[str, Any] | None = None
    last_completed_start: dict[str, Any] | None = None
    last_completed_end: dict[str, Any] | None = None
    last_completed_ep = 0

    def gain(acc_key: str, who: str, now: int, store: dict) -> None:
        if now < 0:
            return
        prev = store.get(who, -1)
        if prev >= 0 and now > prev:
            episode[acc_key] += now - prev
        store[who] = now

    for i, start in enumerate(snap_starts):
        end = snap_starts[i + 1] if i + 1 < len(snap_starts) else len(text)
        block = text[start:end]
        snap = parse_snap(block)
        snap_ep = int(snap.get("episode_index") or 0)
        is_reset = "reset" in str(snap.get("reason") or "").lower()
        if is_reset and ep_active:
            last_round = {
                "water": episode["water"],
                "wood": episode["wood"],
                "sheep_killed": episode["sheep_killed"],
                "trees_chopped": episode["trees_chopped"],
                "episode": episode_index or inferred_ep,
                "at": snap.get("ts") or "",
            }
            # конец завершённого эпизода — последний SNAP до reset
            end_fr = _map_frame_from_snap(prev_snap) or _map_frame_from_snap(snap)
            last_completed_end = end_fr
            last_completed_start = cur_start
            last_completed_ep = int(last_round.get("episode") or 0)
            episode = {"water": 0, "wood": 0, "sheep_killed": 0, "trees_chopped": 0}
            ep_active = False
        jw = int((snap.get("jack") or {}).get("water") or -1)
        lw = int((snap.get("lily") or {}).get("water") or -1)
        gw = int((snap.get("george") or {}).get("water") or -1)
        jwood = int((snap.get("jack") or {}).get("wood") or -1)
        gwood = int((snap.get("george") or {}).get("wood") or -1)
        sn = int(snap.get("sheep_n") or 0)
        tn = int(snap.get("trees_n") or 0)
        if is_reset or not ep_active:
            if is_reset:
                inferred_ep += 1
            elif inferred_ep <= 0:
                inferred_ep = 1
            episode_index = snap_ep if snap_ep > 0 else inferred_ep
            ep_active = True
            episode = {"water": 0, "wood": 0, "sheep_killed": 0, "trees_chopped": 0}
            prev_w = {"jack": jw, "lily": lw, "george": gw}
            prev_wood = {"jack": jwood, "george": gwood}
            prev_sheep, prev_trees = sn, tn
            cur_start = _map_frame_from_snap(snap)
            prev_snap = snap
            continue
        if snap_ep > 0:
            episode_index = snap_ep
        gain("water", "jack", jw, prev_w)
        gain("water", "lily", lw, prev_w)
        gain("water", "george", gw, prev_w)
        gain("wood", "jack", jwood, prev_wood)
        gain("wood", "george", gwood, prev_wood)
        if prev_sheep >= 0 and sn < prev_sheep:
            episode["sheep_killed"] += prev_sheep - sn
        if prev_trees >= 0 and tn < prev_trees:
            episode["trees_chopped"] += prev_trees - tn
        prev_sheep, prev_trees = sn, tn
        prev_snap = snap

    last = parse_snap(text[snap_starts[-1]:])
    if int(last.get("episode_index") or 0) > 0:
        episode_index = int(last["episode_index"])
    elif episode_index <= 0:
        episode_index = inferred_ep or 1
    last["episode_index"] = episode_index
    last["episode"] = episode
    last["last_round"] = last_round
    last["source"] = "log"
    # Кадры: предпочтительно последний завершённый эпизод; иначе start текущий + live как «сейчас»
    fr_start = last_completed_start or cur_start or _map_frame_from_snap(last)
    fr_end = last_completed_end or _map_frame_from_snap(last)
    pending = last_completed_end is None
    ep_for_frames = last_completed_ep or (int(last_round.get("episode") or 0) if not pending else episode_index)
    last["episode_frames"] = {
        "episode": ep_for_frames,
        "pending": pending,
        "start": fr_start,
        "end": fr_end,
    }
    return _ensure_landmarks(last)


_pres_world_cache: dict[str, Any] = {"t": 0.0, "key": "", "data": None}
_pres_world_lock = threading.Lock()
_PRES_WORLD_TTL = 8.0


def fetch_presentation_world(host: str | None, run_id: str) -> dict[str, Any]:
    """Читает stream_world_live.json (предпочтительно) или парсит snapshot.log с lab."""
    rid = sanitize_run_id(run_id) if (run_id or "").strip() else ""
    key = rid or "_"
    now = time.time()
    with _pres_world_lock:
        if (
            _pres_world_cache.get("data") is not None
            and _pres_world_cache.get("key") == key
            and now - float(_pres_world_cache.get("t") or 0) < _PRES_WORLD_TTL
        ):
            return dict(_pres_world_cache["data"])

    h = host or _ssh_host_cache or "lab_comp"
    remote = f"""
set +e
RID={sh_quote(rid)}
ROOT={REMOTE_DIR}/results
pick_json=""
pick_log=""
if [ -n "$RID" ] && [ -f "$ROOT/$RID/stream_world_live.json" ]; then pick_json="$ROOT/$RID/stream_world_live.json"; fi
if [ -z "$pick_json" ] && [ -f "$ROOT/stream_world_live.json" ]; then pick_json="$ROOT/stream_world_live.json"; fi
if [ -n "$RID" ] && [ -f "$ROOT/$RID/stream_world_snapshot.log" ]; then pick_log="$ROOT/$RID/stream_world_snapshot.log"; fi
if [ -z "$pick_log" ] && [ -f "$ROOT/stream_world_snapshot.log" ]; then pick_log="$ROOT/stream_world_snapshot.log"; fi
if [ -z "$pick_log" ]; then
  pick_log=$(ls -1t "$ROOT"/*/stream_world_snapshot.log 2>/dev/null | head -1)
fi
echo "=== META ==="
echo "json=$pick_json"
echo "log=$pick_log"
if [ -n "$pick_json" ]; then
  echo "=== JSON ==="
  cat "$pick_json" 2>/dev/null
  echo
fi
if [ -n "$pick_log" ]; then
  echo "=== LOG ==="
  tail -c 180000 "$pick_log" 2>/dev/null
fi
"""
    try:
        r = ssh_run(h, remote, timeout=25, gate_timeout=3.0, skip_if_busy=True)
        raw = ((r.stdout or "") + (r.stderr or "")).strip()
    except TimeoutError as e:
        # SSH занят другим опросом — отдать кэш / мягкий pending, не плодить сессии
        with _pres_world_lock:
            cached = _pres_world_cache.get("data")
            if isinstance(cached, dict) and cached.get("ok"):
                out = dict(cached)
                out["pending"] = True
                out["note"] = "ssh busy — cached"
                return out
        return {"ok": False, "pending": True, "error": str(e), "run_id": rid}
    except Exception as e:
        out = {"ok": False, "error": str(e), "run_id": rid}
        with _pres_world_lock:
            _pres_world_cache.update(t=now, key=key, data=out)
        return out

    meta_json = ""
    meta_log = ""
    json_body = ""
    log_body = ""
    if "=== META ===" in raw:
        _, _, rest = raw.partition("=== META ===")
        if "=== JSON ===" in rest:
            meta_part, _, after_json = rest.partition("=== JSON ===")
            for ln in meta_part.splitlines():
                if ln.startswith("json="):
                    meta_json = ln[5:].strip()
                if ln.startswith("log="):
                    meta_log = ln[4:].strip()
            if "=== LOG ===" in after_json:
                json_body, _, log_body = after_json.partition("=== LOG ===")
            else:
                json_body = after_json
        elif "=== LOG ===" in rest:
            meta_part, _, log_body = rest.partition("=== LOG ===")
            for ln in meta_part.splitlines():
                if ln.startswith("json="):
                    meta_json = ln[5:].strip()
                if ln.startswith("log="):
                    meta_log = ln[4:].strip()
        else:
            for ln in rest.splitlines():
                if ln.startswith("json="):
                    meta_json = ln[5:].strip()
                if ln.startswith("log="):
                    meta_log = ln[4:].strip()
    else:
        log_body = raw

    json_body = json_body.strip()
    if json_body.startswith("{"):
        try:
            data = json.loads(json_body)
            if isinstance(data, dict) and data.get("ok", True):
                data["ok"] = True
                data["source"] = "json"
                data["run_id"] = rid
                data["path_json"] = meta_json
                data["path_log"] = meta_log
                if not data.get("episode_index"):
                    ep_obj = data.get("episode")
                    if isinstance(ep_obj, dict) and ep_obj.get("index"):
                        data["episode_index"] = ep_obj.get("index")
                data = _ensure_landmarks(data)
                with _pres_world_lock:
                    _pres_world_cache.update(t=now, key=key, data=data)
                return data
        except Exception:
            pass

    parsed = parse_presentation_world_log(log_body.strip())
    parsed["run_id"] = rid
    parsed["path_json"] = meta_json
    parsed["path_log"] = meta_log
    with _pres_world_lock:
        _pres_world_cache.update(t=now, key=key, data=parsed)
    return parsed


# ── actions ─────────────────────────────────────────────────────────

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


def action_restart_obs(cfg: dict) -> str:
    """Рестарт OBS Studio с --startstreaming (сразу в эфир). Unity не трогает."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    jid = new_job("restart_obs", "restart_obs.bash")

    def runner() -> None:
        try:
            r = ssh_run(
                host,
                f"cd {REMOTE_DIR} && bash train_scripts/lab_comp/restart_obs.bash",
                timeout=90,
            )
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            finish_job(jid, r.returncode)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def action_start_obs_stream(cfg: dict, run_id: str = "") -> str:
    """Полный эфир: Presentation (если надо) + OBS --startstreaming."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    rid = sanitize_run_id(run_id) if (run_id or "").strip() else sanitize_run_id(
        ((cfg.get("joint") or {}).get("run_id") or cfg.get("stream_run_id") or "")
    )
    if not rid:
        raise ValueError("Нужен RUN_ID для стрима")
    jid = new_job("start_obs_stream", f"start_obs_stream {rid}")

    def runner() -> None:
        try:
            r = ssh_run(
                host,
                f"cd {REMOTE_DIR} && export RUN_ID={sh_quote(rid)} && "
                f"bash train_scripts/lab_comp/start_obs_stream.bash",
                timeout=180,
            )
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            finish_job(jid, r.returncode)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid


def action_stop_obs_stream(cfg: dict) -> str:
    """Стоп эфира: Presentation + OBS."""
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    jid = new_job("stop_obs_stream", "stop_obs_stream.bash")

    def runner() -> None:
        try:
            r = ssh_run(
                host,
                f"cd {REMOTE_DIR} && bash train_scripts/lab_comp/stop_obs_stream.bash",
                timeout=90,
            )
            append_log(jid, (r.stdout or "") + (r.stderr or ""))
            finish_job(jid, r.returncode)
        except Exception as e:
            append_log(jid, str(e))
            finish_job(jid, 1)

    threading.Thread(target=runner, daemon=True).start()
    return jid

def action_repair_spawn(cfg: dict, run_id: str = "", force: bool = True) -> str:
    """Детект пустого леса/овец по snapshot + флаг .forest_repair_spawners для Unity.

    force=True (кнопка UI): всегда пишет флаг ResetSpawners.
    force=False: только если trees/sheep ниже порога в последнем SNAP.
    """
    host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
    rid = sanitize_run_id(run_id) if (run_id or "").strip() else sanitize_run_id(
        ((cfg.get("joint") or {}).get("run_id") or cfg.get("stream_run_id") or "")
    )
    mode = "force" if force else "detect"
    jid = new_job("repair_spawn", f"spawn_repair {mode} {rid or '_'}")

    def runner() -> None:
        try:
            rid_q = sh_quote(rid) if rid else "''"
            if force:
                cmd = f"""
set +e
cd {REMOTE_DIR}
cat > .forest_repair_spawners <<'EOF'
manual ui repair_spawn
utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
run_id={rid}
reason=force_ui
EOF
# подставить реальный utc
sed -i "s|^utc=.*|utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)|" .forest_repair_spawners 2>/dev/null || true
echo "wrote .forest_repair_spawners"
cat .forest_repair_spawners
sleep 10
if [ -f .forest_repair_spawners_result ]; then
  echo '=== result ==='
  cat .forest_repair_spawners_result
elif [ -f .forest_repair_spawners ]; then
  echo 'WARN: flag still present — Unity StreamSpawnRepairWatcher not running (нужен свежий DLL)?'
fi
echo EXIT:0
"""
            else:
                cmd = f"""
set +e
cd {REMOTE_DIR}
source ~/anaconda3/etc/profile.d/conda.sh 2>/dev/null
conda activate mlagents 2>/dev/null || true
python3 -u train_scripts/lab_comp/watch_stream_spawn_repair.py --once --run-id {rid_q} --bad-streak 1 --repair-cooldown 0
echo EXIT:$?
if [ -f .forest_repair_spawners_result ]; then
  echo '=== result ==='
  cat .forest_repair_spawners_result
fi
"""
            r = ssh_run(host, cmd, timeout=45)
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
  padding: 0; border-bottom: 1px solid var(--border);
  background: linear-gradient(180deg, #1c2230 0%, #141820 100%);
}
.topnav {
  display: flex; gap: 8px; padding: 10px 14px 8px; align-items: stretch;
  max-width: 1100px; margin: 0 auto; width: 100%; box-sizing: border-box;
}
.mode-tab {
  flex: 1; text-align: center; padding: 12px 10px 14px; border-radius: 12px;
  border: 1px solid var(--border); background: #171c28; color: var(--muted);
  cursor: pointer; font-size: 14px; font-weight: 650; letter-spacing: 0.01em;
  transition: border-color .15s, background .15s, color .15s, box-shadow .15s;
  position: relative;
}
.mode-tab:hover { border-color: #4a628a; color: var(--text); background: #1c2434; }
.mode-tab.active {
  color: #e8f0ff; background: linear-gradient(180deg, #2a3d5c 0%, #1e2d46 100%);
  border-color: #5a84c4; box-shadow: 0 0 0 1px rgba(106,166,255,.25);
}
.mode-tab.has-live::after {
  content: ""; position: absolute; left: 18%; right: 18%; bottom: 5px; height: 3px;
  border-radius: 999px; background: var(--bad);
  box-shadow: 0 0 10px rgba(239,107,107,.7);
}
/* live: только красная обводка + полоска снизу, без заливки всей вкладки */
.mode-tab.has-live {
  border-color: #c45a5a;
  box-shadow: 0 0 0 1px rgba(239,107,107,.4);
}
.mode-tab.active.has-live {
  border-color: #ef6b6b;
  box-shadow: 0 0 0 1px rgba(239,107,107,.5);
}
.status-bar {
  padding: 2px 16px 8px; font-size: 11px; color: var(--muted); min-height: 1.2em;
  max-width: 1100px; margin: 0 auto; width: 100%; box-sizing: border-box;
}
.status-bar #hdrMeta { color: var(--muted); }
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
/* warn-live только для аварийных вкладок; OBS Stream = только нижняя полоска (has-live) */
.subtab.warn-live {
  border-color: #ef6b6b;
  background: #4a1f2a;
  color: #ffd9d9;
}
.pill.warn { color: #0b1220; background: var(--warn); border-color: transparent; font-weight: 650; }
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
input:disabled, select:disabled {
  opacity: 0.75; cursor: not-allowed; background: #181c26; color: #c5cddc;
}
.field { display: grid; gap: 4px; }
.field label { font-size: 11px; color: var(--muted); }
.field.locked label::after {
  content: " · зафиксировано";
  color: var(--bad); font-weight: 600; font-size: 10px;
}
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
button.live-on {
  background: #3a2226; border-color: #ef6b6b; color: #ffd0d4; font-weight: 650;
  box-shadow: 0 0 0 1px rgba(239,107,107,.55), 0 0 14px rgba(239,107,107,.4);
  animation: cardPulse 1.2s ease-in-out infinite;
}
button.live-on:hover { border-color: #ff8a8a; }
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
.run-status {
  display: block; margin-top: 4px; font-size: 11px; font-family: ui-monospace, Consolas, monospace;
  color: var(--muted); min-height: 1.2em;
}
.run-status.ok { color: #3ecf8e; }
.run-status.missing { color: #e07070; }
.run-status.checking { color: var(--muted); opacity: 0.8; }
.server-pre {
  background: #0d0f14; border: 1px solid var(--border); border-radius: 8px;
  padding: 10px; font-family: ui-monospace, Consolas, monospace; font-size: 11px;
  white-space: pre-wrap; color: #c9d0de; max-height: 160px; overflow: auto; margin: 0;
}
.host-card {
  background: #141820; border: 1px solid #2a3344; border-radius: 12px;
  padding: 14px 14px 12px; display: flex; flex-direction: column; gap: 12px;
}
.host-card-head {
  display: flex; align-items: center; justify-content: space-between; gap: 10px;
}
.host-card-title {
  font-size: 18px; font-weight: 700; color: #f2f5fa; letter-spacing: 0.01em;
}
.host-online {
  font-size: 11px; font-weight: 650; letter-spacing: 0.04em; text-transform: uppercase;
  padding: 4px 10px; border-radius: 999px; border: 1px solid #2f6b4a;
  color: #7dffb3; background: #163528;
}
.host-online.off {
  border-color: #6b3a3a; color: #ffb0b0; background: #3a1c1c;
}
.host-online.pending {
  border-color: #6b5a2a; color: #ffe6a0; background: #3a3218;
}
.host-meter { display: grid; gap: 4px; }
.host-meter-row {
  display: flex; align-items: baseline; justify-content: space-between; gap: 10px;
  font-size: 12px;
}
.host-meter-label {
  color: #9aa6b8; font-weight: 650; font-size: 11px; letter-spacing: 0.04em;
  text-transform: uppercase;
}
.host-meter-val { color: #d7dee9; font-variant-numeric: tabular-nums; text-align: right; }
.host-bar {
  height: 7px; border-radius: 999px; background: #222937; overflow: hidden;
}
.host-bar > i {
  display: block; height: 100%; width: 0%; border-radius: inherit;
  transition: width .35s ease;
}
.host-bar.ram > i { background: linear-gradient(90deg, #3ecf8e, #6dffb5); }
.host-bar.disk > i { background: linear-gradient(90deg, #9b6bff, #c4a0ff); }
.host-bar.vram > i { background: linear-gradient(90deg, #4ea1ff, #8ec5ff); }
.host-bar.util > i { background: linear-gradient(90deg, #f0a33a, #ffd27a); }
.host-bar.fps > i { background: linear-gradient(90deg, #6aa6ff, #9ec4ff); }
.host-bar.fps.warn > i { background: linear-gradient(90deg, #f0a33a, #ffd27a); }
.host-bar.fps.bad > i { background: linear-gradient(90deg, #ef6b6b, #ff9a9a); }
.host-card.fps-warn {
  border-color: #8a6a2a;
  box-shadow: 0 0 0 1px rgba(240,163,58,.35);
}
.host-card.fps-bad {
  border-color: #c45a5a;
  box-shadow: 0 0 0 1px rgba(239,107,107,.45), 0 0 14px rgba(239,107,107,.25);
}
.host-fps-status {
  display: inline-flex; align-items: center; gap: 6px;
  font-size: 12px; font-weight: 700; letter-spacing: 0.03em;
  padding: 4px 10px; border-radius: 999px; margin-bottom: 8px;
}
.host-fps-status.ok {
  color: #7dffb3; background: #163528; border: 1px solid #2f6b4a;
}
.host-fps-status.warn {
  color: #ffe6a0; background: #3a3218; border: 1px solid #6b5a2a;
}
.host-fps-status.bad {
  color: #ffd0d4; background: #3a1c1c; border: 1px solid #7a3a42;
}
.host-gpu {
  background: #10151e; border: 1px solid #273247; border-radius: 10px;
  padding: 10px; display: grid; gap: 10px;
}
.host-gpu-head {
  display: flex; align-items: baseline; justify-content: space-between; gap: 8px;
  flex-wrap: wrap;
}
.host-gpu-head strong { font-size: 13px; color: #e8eef8; }
.host-gpu-head span { font-size: 12px; color: #9aa6b8; }
.host-chips { display: flex; flex-wrap: wrap; gap: 6px; }
.host-chip {
  font-size: 11px; padding: 3px 8px; border-radius: 999px;
  background: #1a2740; border: 1px solid #35517a; color: #b7d0f5;
}
.host-chip.cpu {
  background: #1c222c; border-color: #3a4456; color: #c5cddc;
}
.host-raw summary {
  cursor: pointer; color: var(--muted); font-size: 11px; user-select: none;
}
.host-raw[open] summary { margin-bottom: 6px; }
.pres-world {
  margin-top: 12px; padding: 12px; border-radius: 12px;
  background: #141820; border: 1px solid #2a3344;
  display: flex; flex-direction: column; gap: 10px;
}
.pres-world-head {
  display: flex; align-items: baseline; justify-content: space-between; gap: 10px; flex-wrap: wrap;
}
.pres-world-head h3 { margin: 0; font-size: 14px; font-weight: 700; }
.pres-kpis {
  display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px;
}
@media (min-width: 900px) {
  .pres-kpis { grid-template-columns: repeat(6, minmax(0, 1fr)); }
}
.pres-kpi {
  background: #10151e; border: 1px solid #273247; border-radius: 10px;
  padding: 8px 10px; display: grid; gap: 2px;
}
.pres-kpi.accent { border-color: #3a5f99; background: #152033; }
.pres-kpi .k {
  font-size: 10px; color: #9aa6b8; text-transform: uppercase; letter-spacing: 0.04em; font-weight: 650;
}
.pres-kpi .v {
  font-size: 18px; font-weight: 700; color: #e8eef8; font-variant-numeric: tabular-nums;
}
.pres-agents {
  display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px;
}
.pres-agent {
  background: #10151e; border: 1px solid #273247; border-radius: 10px; padding: 8px 10px;
  font-size: 12px;
}
.pres-agent .name { font-weight: 700; margin-bottom: 2px; }
.pres-agent.jack .name { color: #6aa6ff; }
.pres-agent.lily .name { color: #e070b0; }
.pres-agent.george .name { color: #3ecf8e; }
.pres-agent.dead { opacity: 0.55; border-color: #7a3a42; }
.pres-map-wrap {
  background: #0d1016; border: 1px solid #273247; border-radius: 10px; padding: 8px;
}
.pres-map-pair {
  display: grid; grid-template-columns: 1fr 1fr; gap: 10px;
}
@media (max-width: 900px) {
  .pres-map-pair { grid-template-columns: 1fr; }
}
.pres-map-title {
  font-size: 12px; font-weight: 650; color: #c5d0e0; margin: 0 0 6px;
}
.pres-map-title .sub { font-weight: 500; color: #7a8499; margin-left: 6px; }
#presWorldMapStart, #presWorldMapEnd {
  width: 100%; height: auto; display: block; border-radius: 8px;
  background: #0a0c10;
}
.pres-map-legend {
  display: flex; flex-wrap: wrap; gap: 10px; margin-top: 6px; font-size: 11px; color: #9aa6b8;
}
.pres-map-legend .lg::before {
  content: ""; display: inline-block; width: 8px; height: 8px; border-radius: 50%;
  margin-right: 5px; vertical-align: middle;
}
.pres-map-legend .jack::before { background: #6aa6ff; }
.pres-map-legend .lily::before { background: #e070b0; }
.pres-map-legend .george::before { background: #3ecf8e; }
.pres-map-legend .tree::before { background: #4a8f4a; border-radius: 2px; }
.pres-map-legend .sheep::before { background: #e8e0d0; }
.pres-map-legend .zombie::before { background: #c45a5a; }
.pres-map-legend .lake::before { background: #3a7ec8; border-radius: 2px; }
.pres-map-legend .house::before { background: #c9a227; border-radius: 2px; }
.pres-map-legend .fence::before { background: #8b6914; border-radius: 1px; }
.pres-map-legend .stone::before { background: #9a9aa0; border-radius: 2px; }
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
  <nav class="topnav" role="tablist" aria-label="Разделы">
    <button type="button" class="mode-tab active" id="tab-mode-training" data-mode="training">Forest Lab Train</button>
    <button type="button" class="mode-tab" id="tab-mode-streaming" data-mode="streaming">Survival followers</button>
    <button type="button" class="mode-tab" id="tab-mode-obs" data-mode="obs">OBS Stream</button>
  </nav>
  <div class="status-bar"><span id="hdrMeta"></span></div>
</header>
<main>
  <section class="card" id="card-server">
    <h2>Сервер <code>lab_comp</code> <span id="serverModeLabel">(SSH / local)</span> <span class="pill" id="serverPill">—</span></h2>
    <p class="hint" id="serverHostHint">RAM / nvidia-smi с lab. Если UI запущен на самом lab — режим local (без SSH к себе). С Windows UI ходит по SSH.</p>
    <div class="host-card" id="serverHostCard">
      <div class="host-card-head">
        <div class="host-card-title" id="serverHostTitle">lab_comp</div>
        <div class="host-online pending" id="serverHostOnline">…</div>
      </div>
      <div id="serverHostMeters"><div class="hint">загрузка метрик…</div></div>
    </div>
    <details class="host-raw">
      <summary>сырой SSH-вывод</summary>
      <pre class="server-pre" id="serverStats">загрузка RAM / nvidia-smi с lab_comp…</pre>
    </details>
    <h2 style="margin-top:10px;font-size:14px">Сейчас на lab <span class="pill" id="labTasksPill">—</span></h2>
    <div class="lab-tasks" id="labTasks"><div class="lab-tasks-empty">нет активных задач</div></div>
    <p class="hint">× гасит только этот тип процесса (train / validate / stream / Streaming Survival / OBS).</p>
    <h2 style="margin-top:10px;font-size:14px">SSH сессии <span class="pill" id="obsSshPill">—</span></h2>
    <pre class="server-pre" id="obsSshNow">считаем…</pre>
    <p class="hint" id="obsSshHint">Локальные ssh → lab + сессии на lab (:22). Зелёный &lt; 6, жёлтый 6–9, красный ≥ 10 (лимит «too many»).</p>
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
      <button type="button" id="btnRefreshTb">Обновить TB</button>
      <button type="button" class="danger" id="btnKill" title="Только train: mlagents + headless Unity (стрим/validate не трогает)">⏹ Стоп train</button>
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
          Базы <code>results/…</code> только читаются. Чекпоинты пишутся в «Новую папку»
          (если папка уже есть с весами — автоматически Resume).
        </p>
        <div class="grid2">
          <div class="field">
            <label>База Jack</label>
            <input id="jointInitJack" />
            <span class="run-status" id="jointStatusJack">—</span>
          </div>
          <div class="field">
            <label>База Lily</label>
            <input id="jointInitLily" />
            <span class="run-status" id="jointStatusLily">—</span>
          </div>
        </div>
        <div class="grid2">
          <div class="field">
            <label>База George</label>
            <input id="jointInitGeorge" />
            <span class="run-status" id="jointStatusGeorge">—</span>
          </div>
          <div class="field">
            <label>Новая папка</label>
            <input id="jointRunId" />
            <span class="run-status" id="jointStatusRun">—</span>
          </div>
        </div>
        <p class="hint" id="jointLiveBanner" style="display:none;margin:4px 0 0;color:#ffd0d4;font-weight:600"></p>
        <div class="grid2">
          <div class="field">
            <label>TB runs (несколько: Ctrl/Shift+клик)</label>
            <select id="jointTb" multiple size="6"></select>
          </div>
          <div class="field">
            <label>Быстрый выбор</label>
            <div class="btns" style="flex-wrap:wrap">
              <button type="button" id="btnJointTbTrio" title="Только выбрать Jack+Lily+George из новой папки">Трое из новой папки</button>
              <button type="button" class="primary" id="btnJointTbDraw" title="Скачать серии TB и нарисовать Cumulative Reward">Нарисовать TB</button>
              <button type="button" class="danger" id="btnJointTbClear" title="Снять выбор runs и очистить график">Выключить TB</button>
            </div>
            <p class="hint" style="margin:0">Трое — только выбор. Нарисовать TB — график. Выключить — убрать график.</p>
          </div>
        </div>
        <div class="btns train-btns">
          <button class="primary" id="btnJointTrain" title="Если папка уже есть — Resume, иначе старт с баз">▶ Запуск дообучения</button>
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
          <button class="danger" id="btnKillStream" title="Убить только бесконечный стрим. Train и validate не трогает.">⏹ Стоп стрим</button>
          <button type="button" id="btnRepairSpawn" title="Если trees/sheep = 0 по snapshot — флаг ResetTrees/ResetSheep в Unity">🔧 Починить спавн</button>
        </div>
        <p class="hint">Синхронизируется с RUN_ID на вкладке обучения при сохранении конфига.</p>

        <div class="pres-world" id="presWorldCard">
          <div class="pres-world-head">
            <h3>Мир стрима <span class="pill" id="presWorldPill">—</span> <span class="pill" id="presWorldFpsPill">—</span></h3>
            <span class="hint" id="presWorldMeta">ждём snapshot…</span>
          </div>
          <div class="host-card" id="streamFpsCard" style="padding:10px;margin:0 0 10px">
            <div id="streamFpsMeters"><div class="hint">ждём hitch/fps…</div></div>
            <details class="host-raw" style="margin-top:6px">
              <summary>детали FPS</summary>
              <pre class="server-pre" id="streamFps">ждём hitch/fps из stream_onnx лога…</pre>
            </details>
            <p class="hint" style="margin:6px 0 0">Окно ~500 шагов · <code>stream_onnx_*.log</code> / <code>stream_fps.json</code>. Ниже ~15 — стрим лагает.</p>
          </div>
          <div class="pres-kpis" id="presWorldKpis">
            <div class="pres-kpi"><span class="k">Эпизод</span><span class="v" id="presKpiEpisode">—</span></div>
            <div class="pres-kpi"><span class="k">Овцы</span><span class="v" id="presKpiSheep">—</span></div>
            <div class="pres-kpi"><span class="k">Деревья</span><span class="v" id="presKpiTrees">—</span></div>
            <div class="pres-kpi"><span class="k">Зомби</span><span class="v" id="presKpiZombies">—</span></div>
            <div class="pres-kpi accent"><span class="k">Раунд · вода</span><span class="v" id="presKpiWater">—</span></div>
            <div class="pres-kpi accent"><span class="k">Раунд · дерево</span><span class="v" id="presKpiWood">—</span></div>
            <div class="pres-kpi accent"><span class="k">Раунд · овцы</span><span class="v" id="presKpiSheepKill">—</span></div>
          </div>
          <div class="pres-agents" id="presWorldAgents"></div>
          <div class="pres-map-pair">
            <div class="pres-map-wrap">
              <div class="pres-map-title">Начало эпизода <span class="sub" id="presMapStartTitle">—</span></div>
              <canvas id="presWorldMapStart" width="640" height="360" title="Кадр начала эпизода"></canvas>
            </div>
            <div class="pres-map-wrap">
              <div class="pres-map-title">Конец эпизода <span class="sub" id="presMapEndTitle">—</span></div>
              <canvas id="presWorldMapEnd" width="640" height="360" title="Кадр конца эпизода"></canvas>
            </div>
          </div>
          <div class="pres-map-legend" style="margin-top:8px">
            <span class="lg jack">Jack</span>
            <span class="lg lily">Lily</span>
            <span class="lg george">George</span>
            <span class="lg tree">деревья</span>
            <span class="lg sheep">овцы</span>
            <span class="lg zombie">зомби</span>
            <span class="lg lake">озеро</span>
            <span class="lg house">дом</span>
            <span class="lg fence">забор</span>
            <span class="lg stone">камни</span>
          </div>
          <p class="hint" style="margin:0">Два кадра последнего завершённого эпизода (или «сейчас», пока раунд ещё идёт). Ориентиры — дом / озеро / забор / камни.</p>
        </div>
      </div>
    </div>
  </section>
  </div><!-- /panel-mode-training -->

  <div id="panel-mode-streaming" class="mode-panel" style="display:none">
  <section class="card" style="margin-bottom:10px">
    <h2>Блок Survival followers</h2>
    <p class="hint">Одним кликом гасит Streaming Survival (+ preview). LLM Bot теперь во вкладке OBS Stream.</p>
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
      <p class="hint">Скриншот <b>lab_comp DISPLAY=:1</b> только по кнопке — кадр не крутится сам. Не трогает train / OBS.</p>
      <div class="btns" style="flex-wrap:wrap">
        <button type="button" class="primary" id="btnSsPreviewStart">▶ Показать (live)</button>
        <button type="button" class="danger" id="btnSsPreviewStop">⏹ Скрыть</button>
        <button type="button" id="btnSsPreviewOnce">1 кадр</button>
      </div>
      <p class="hint" id="ssPreviewMeta" style="min-height:1.2em">preview off — кадр скрыт</p>
      <div id="ssPreviewBox" style="margin-top:8px;background:#0b0f16;border:1px solid #2a3344;border-radius:8px;overflow:hidden;min-height:120px;display:flex;align-items:center;justify-content:center">
        <img id="ssPreviewImg" alt="lab screen preview" style="max-width:100%;width:100%;height:auto;display:none;background:#000" />
        <span id="ssPreviewPlaceholder" style="color:#7a8499;font-size:13px;padding:24px">Кадр скрыт — жми «Показать» или «1 кадр»</span>
      </div>
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

  <div id="panel-mode-obs" class="mode-panel" style="display:none">
    <section class="card" id="card-obs-stream">
      <h2>OBS Stream</h2>
      <div class="btns" style="flex-wrap:wrap;margin:4px 0 8px">
        <button type="button" class="primary" id="btnObsStartStream" title="Unity Presentation + OBS --startstreaming">● Запись</button>
        <button type="button" class="danger" id="btnObsStopStream" title="Стоп Presentation + OBS">⏹ Стоп стрим</button>
      </div>
      <p class="hint" id="obsProcHint">статус…</p>
    </section>

    <section class="card" id="card-llm-bot">
      <h2>LLM Bot <span class="pill" id="llmBotPill">stopped</span></h2>
      <p class="hint">Бот на <b>lab_comp</b> → UDP :5055 (Twitch / local debug). Roster + #stats в SQLite.</p>
      <div class="llm-status" id="llmStatusGrid">
        <div>Bot: <b id="llmStBot">—</b></div>
        <div>Mode: <b id="llmStMode">—</b></div>
        <div>Python deps: <b id="llmStDeps">—</b></div>
        <div>Ollama: <b id="llmStOllama">—</b></div>
        <div>Model: <b id="llmStModel">—</b></div>
        <div>HTTP :8765: <b id="llmStHttp">—</b></div>
      </div>
      <p class="hint" id="llmLastError" style="color:#ff8e8e;min-height:1em"></p>
      <div class="card" style="margin:10px 0;padding:10px 12px;border:1px solid #2a3344;background:#0b0f16">
        <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;flex-wrap:wrap">
          <strong>Игроки (SQLite)</strong>
          <span class="pill" id="llmRosterPill">0</span>
          <button type="button" id="btnLlmRosterMode" title="Переключить: только в игре / вся история">В игре</button>
          <button type="button" id="btnLlmRosterResync" title="Отправить список в Unity после рестарта стрима">↻ Resync → Unity</button>
        </div>
        <p class="hint" style="margin:6px 0 8px">База <code>stream_bot.sqlite3</code>: кто писал #join, когда, вода/дерево/овцы/костры. #exit → не «в игре», но в истории остаётся. После рестарта стрима жми Resync (или он сам после restart_stream).</p>
        <div id="llmRosterEmpty" class="hint" style="padding:6px 0">Пока никого — зрители пишут #join в чат</div>
        <div style="overflow:auto;max-height:320px">
          <table id="llmRosterTable" style="width:100%;border-collapse:collapse;font-size:12px;display:none">
            <thead>
              <tr style="color:#9aa4b8;text-align:left">
                <th style="padding:4px 6px">#</th>
                <th style="padding:4px 6px">Ник</th>
                <th style="padding:4px 6px">Статус</th>
                <th style="padding:4px 6px">Действие</th>
                <th style="padding:4px 6px">Вход</th>
                <th style="padding:4px 6px">💧</th>
                <th style="padding:4px 6px">🪵</th>
                <th style="padding:4px 6px">🐑</th>
                <th style="padding:4px 6px">🔥</th>
              </tr>
            </thead>
            <tbody id="llmRosterBody"></tbody>
          </table>
        </div>
      </div>
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
  </div><!-- /panel-mode-obs -->
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
    || (path || "").indexOf("/api/presentation_world") === 0
    || (path || "").indexOf("/api/tb/") === 0;
  // joint weights = SSH на lab, может быть 15–40с — не рвать на 12с
  const jointW = (path || "").indexOf("/api/joint/run_weights") === 0;
  const ms = (path || "").indexOf("/api/ss_diagnostics/") === 0 ? 600000
    : (jointW ? 60000 : (soft ? 12000 : 180000));
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

const JOINT_LOCK_IDS = ["jointInitJack", "jointInitLily", "jointInitGeorge", "jointRunId"];
let _jointLockGraceUntil = 0;
let _jointStoppingUntil = 0;

function isJointStopping() {
  return Date.now() < _jointStoppingUntil;
}

function beginJointStopping() {
  _jointLockGraceUntil = 0;
  _jointStoppingUntil = Date.now() + 120000;
  applyJointTrainLock(true);
}

function setKillTrainBtn(mode) {
  const btnKill = document.getElementById("btnKillJointTrain");
  if (!btnKill) return;
  if (mode === "stopping") {
    btnKill.textContent = "● Останавливается…";
    btnKill.classList.add("live-on");
    btnKill.disabled = true;
    btnKill.title = "Идёт остановка train на lab — дождись завершения";
  } else {
    btnKill.textContent = "⏹ Стоп обучение";
    btnKill.classList.remove("live-on");
    btnKill.disabled = false;
    btnKill.title = "Убить joint/любой train (mlagents + headless). Стрим и validate не трогает.";
  }
}

function readJointLocked() {
  const L = (CFG && CFG.joint && CFG.joint.locked) || null;
  if (!L || typeof L !== "object") return null;
  if (!L.init_jack && !L.init_lily && !L.init_george && !L.run_id) return null;
  return {
    init_jack: String(L.init_jack || ""),
    init_lily: String(L.init_lily || ""),
    init_george: String(L.init_george || ""),
    run_id: String(L.run_id || ""),
  };
}

async function persistJointLocked(snap) {
  if (!CFG) CFG = {};
  if (!CFG.joint) CFG.joint = {};
  CFG.joint.locked = snap;
  try {
    const body = {
      build: document.getElementById("buildName").value.trim(),
      tb_url: document.getElementById("tbUrl").value.trim(),
      jack: collectHero("jack"),
      lily: collectHero("lily"),
      george: collectHero("george"),
      joint: Object.assign({}, collectJoint(), { locked: snap }),
    };
    CFG = (await api("/api/config", {
      method: "POST",
      headers: {"Content-Type": "application/json"},
      body: JSON.stringify(body),
    })).config;
  } catch (_) {}
}

function applyJointTrainLock(jointRunning) {
  const btn = document.getElementById("btnJointTrain");
  const banner = document.getElementById("jointLiveBanner");
  let snap = readJointLocked();

  // Пользователь нажал стоп — пока процессы ещё живы, не возвращаем «Запущено».
  if (isJointStopping()) {
    if (!jointRunning) {
      _jointStoppingUntil = 0;
      // fall through → idle unlock
    } else {
      if (!snap) {
        snap = collectJoint();
        persistJointLocked({
          init_jack: snap.init_jack,
          init_lily: snap.init_lily,
          init_george: snap.init_george,
          run_id: snap.run_id,
        });
      }
      const setVal = (id, v) => {
        const n = document.getElementById(id);
        if (n && v != null) n.value = v;
      };
      setVal("jointInitJack", snap.init_jack);
      setVal("jointInitLily", snap.init_lily);
      setVal("jointInitGeorge", snap.init_george);
      setVal("jointRunId", snap.run_id);
      for (const id of JOINT_LOCK_IDS) {
        const n = document.getElementById(id);
        if (!n) continue;
        n.disabled = true;
        if (n.parentElement) n.parentElement.classList.add("locked");
      }
      if (btn) {
        btn.textContent = "● Остановка…";
        btn.classList.remove("live-on");
        btn.classList.remove("primary");
        btn.disabled = true;
        btn.title = "Остановка обучения — запуск недоступен";
      }
      setKillTrainBtn("stopping");
      if (banner) {
        banner.style.display = "block";
        banner.textContent =
          "Остановка: Jack←" + (snap.init_jack || "?")
          + " · Lily←" + (snap.init_lily || "?")
          + " · George←" + (snap.init_george || "?")
          + "  →  " + (snap.run_id || "?");
      }
      return;
    }
  }

  // Сразу после старта activity ещё может не видеть train — не снимаем lock.
  if (!jointRunning && snap && Date.now() < _jointLockGraceUntil) {
    jointRunning = true;
  }
  if (jointRunning) {
    if (!snap) {
      snap = collectJoint();
      persistJointLocked({
        init_jack: snap.init_jack,
        init_lily: snap.init_lily,
        init_george: snap.init_george,
        run_id: snap.run_id,
      });
    }
    const setVal = (id, v) => {
      const n = document.getElementById(id);
      if (n && v != null) n.value = v;
    };
    setVal("jointInitJack", snap.init_jack);
    setVal("jointInitLily", snap.init_lily);
    setVal("jointInitGeorge", snap.init_george);
    setVal("jointRunId", snap.run_id);
    for (const id of JOINT_LOCK_IDS) {
      const n = document.getElementById(id);
      if (!n) continue;
      n.disabled = true;
      if (n.parentElement) n.parentElement.classList.add("locked");
    }
    if (btn) {
      btn.textContent = "● Запущено";
      btn.classList.add("live-on");
      btn.classList.remove("primary");
      btn.disabled = true;
      btn.title = "Обучение идёт — поля зафиксированы. Стоп обучение, чтобы менять.";
    }
    setKillTrainBtn("idle");
    if (banner) {
      banner.style.display = "block";
      banner.textContent =
        "Запущено: Jack←" + (snap.init_jack || "?")
        + " · Lily←" + (snap.init_lily || "?")
        + " · George←" + (snap.init_george || "?")
        + "  →  " + (snap.run_id || "?");
    }
  } else {
    _jointLockGraceUntil = 0;
    _jointStoppingUntil = 0;
    for (const id of JOINT_LOCK_IDS) {
      const n = document.getElementById(id);
      if (!n) continue;
      n.disabled = false;
      if (n.parentElement) n.parentElement.classList.remove("locked");
    }
    if (btn) {
      btn.textContent = "▶ Запуск дообучения";
      btn.classList.remove("live-on");
      btn.classList.add("primary");
      btn.disabled = false;
      btn.title = "Если папка уже есть — Resume, иначе старт с баз";
    }
    setKillTrainBtn("idle");
    if (banner) {
      banner.style.display = "none";
      banner.textContent = "";
    }
    if (readJointLocked()) persistJointLocked(null);
  }
}

function jointPreferRuns(runId) {
  const rid = (runId || "").trim();
  if (!rid) return [];
  return ["JackLowLevelAgent","LilyLowLevelAgent","GeorgeLowLevelAgent"].map((b) => `${rid}/${b}`);
}

function fillJointCard() {
  const j = (CFG && CFG.joint) || {};
  const locked = readJointLocked();
  const src = locked || j;
  document.getElementById("jointInitJack").value = src.init_jack || "";
  document.getElementById("jointInitLily").value = src.init_lily || "";
  document.getElementById("jointInitGeorge").value = src.init_george || "";
  document.getElementById("jointRunId").value = src.run_id || j.run_id || "";
  const streamRun = document.getElementById("streamRunId");
  if (streamRun) streamRun.value = (locked && locked.run_id) || j.run_id || "";
  const sel = document.getElementById("jointTb");
  const liveRid = (document.getElementById("jointRunId").value || "").trim() || (j.run_id || "");
  let cur = Array.isArray(j.tb_runs) ? j.tb_runs.filter(Boolean) : [];
  if (!cur.length && j.tb_run) cur = [j.tb_run];
  const prefer = jointPreferRuns(liveRid);
  // Если сохранённые TB runs не из текущей «Новой папки» — берём тройку из поля.
  if (prefer.length && (!cur.length || !cur.some((r) => r.startsWith(liveRid + "/")))) {
    cur = prefer;
  }

  sel.innerHTML = "";
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
  refreshJointWeightStatus();
}

function selectJointTbTrio() {
  const rid = document.getElementById("jointRunId").value.trim();
  const want = new Set(jointPreferRuns(rid));
  if (!want.size) { flash("hdrMeta", "joint: укажи новую папку"); return; }
  const sel = document.getElementById("jointTb");
  for (const r of want) {
    if (![...sel.options].some((o) => o.value === r)) {
      sel.append(el("option", {value:r, text:r}));
    }
  }
  for (const o of sel.options) o.selected = want.has(o.value);
  saveCfg();
  flash("hdrMeta", `TB выбраны: трое из ${rid} — жми «Нарисовать TB»`);
}

function setJointTbBtnBusy(btn, busyText) {
  if (!btn) return () => {};
  const prev = {
    text: btn.textContent,
    disabled: btn.disabled,
    title: btn.title,
    live: btn.classList.contains("live-on"),
  };
  btn.textContent = busyText;
  btn.disabled = true;
  btn.classList.add("live-on");
  btn.title = busyText;
  return () => {
    btn.textContent = prev.text;
    btn.disabled = prev.disabled;
    btn.title = prev.title;
    if (!prev.live) btn.classList.remove("live-on");
  };
}

async function drawJointTb() {
  const sel = document.getElementById("jointTb");
  const runs = [...sel.selectedOptions].map((o) => o.value.trim()).filter(Boolean);
  if (!runs.length) {
    flash("hdrMeta", "TB: сначала выбери runs (или «Трое из новой папки»)");
    return;
  }
  const btn = document.getElementById("btnJointTbDraw");
  const restore = setJointTbBtnBusy(btn, "● Рисуется…");
  flash("hdrMeta", "TB: рисую…");
  try {
    await loadJointChart();
    await saveCfg();
    flash("hdrMeta", `TB нарисован · ${runs.length} run(s)`);
  } catch (e) {
    flash("hdrMeta", "TB err: " + (e.message || e));
  } finally {
    restore();
  }
}

async function clearJointTb() {
  const btn = document.getElementById("btnJointTbClear");
  const restore = setJointTbBtnBusy(btn, "● Выключается…");
  flash("hdrMeta", "TB: выключаю…");
  try {
    const sel = document.getElementById("jointTb");
    for (const o of sel.options) o.selected = false;
    await loadJointChart();
    await saveCfg();
    flash("hdrMeta", "TB выключен");
  } catch (e) {
    flash("hdrMeta", "TB err: " + (e.message || e));
  } finally {
    restore();
  }
}

let _jointWeightTimer = null;
function scheduleJointWeightStatus() {
  if (_jointWeightTimer) clearTimeout(_jointWeightTimer);
  _jointWeightTimer = setTimeout(() => refreshJointWeightStatus(), 350);
}

function setJointStatusEl(id, item) {
  const eln = document.getElementById(id);
  if (!eln) return;
  if (!item) {
    eln.className = "run-status";
    eln.textContent = "—";
    return;
  }
  const ok = !!(item.exists && item.has_weights);
  eln.className = "run-status " + (ok ? "ok" : (item.exists ? "ok" : "missing"));
  eln.textContent = item.label || (ok ? "есть" : "нет папки");
}

async function refreshJointWeightStatus() {
  const j = collectJoint();
  for (const id of ["jointStatusJack","jointStatusLily","jointStatusGeorge","jointStatusRun"]) {
    const eln = document.getElementById(id);
    if (eln) { eln.className = "run-status checking"; eln.textContent = "…"; }
  }
  if (!j.init_jack && !j.init_lily && !j.init_george && !j.run_id) {
    setJointStatusEl("jointStatusJack", null);
    setJointStatusEl("jointStatusLily", null);
    setJointStatusEl("jointStatusGeorge", null);
    setJointStatusEl("jointStatusRun", null);
    return;
  }
  try {
    const q = new URLSearchParams({
      jack: j.init_jack || "",
      lily: j.init_lily || "",
      george: j.init_george || "",
      run_id: j.run_id || "",
    });
    const data = await api("/api/joint/run_weights?" + q.toString());
    const items = (data && data.items) || {};
    if (data && data.ok === false && !Object.keys(items).length) {
      const err = String(data.error || "ssh fail");
      for (const id of ["jointStatusJack","jointStatusLily","jointStatusGeorge","jointStatusRun"]) {
        const eln = document.getElementById(id);
        if (eln) { eln.className = "run-status missing"; eln.textContent = "ssh: " + err.slice(0, 80); }
      }
      return;
    }
    setJointStatusEl("jointStatusJack", items.jack);
    setJointStatusEl("jointStatusLily", items.lily);
    setJointStatusEl("jointStatusGeorge", items.george);
    setJointStatusEl("jointStatusRun", items.run);
  } catch (e) {
    const msg = String((e && e.message) || e || "err");
    for (const id of ["jointStatusJack","jointStatusLily","jointStatusGeorge","jointStatusRun"]) {
      const eln = document.getElementById(id);
      if (eln) { eln.className = "run-status missing"; eln.textContent = "не удалось проверить · " + msg.slice(0, 60); }
    }
  }
}

async function trainJoint() {
  await saveCfg();
  const j = collectJoint();
  if (!j.init_jack || !j.init_lily || !j.init_george || !j.run_id) {
    flash("hdrMeta", "joint: заполни 3 базы и новую папку");
    return;
  }
  let resume = false;
  try {
    const q = new URLSearchParams({
      jack: j.init_jack, lily: j.init_lily, george: j.init_george, run_id: j.run_id,
    });
    const info = await api("/api/joint/run_weights?" + q.toString());
    const run = (info && info.items && info.items.run) || {};
    resume = !!(run.exists && run.has_weights);
  } catch (_) {
    resume = false;
  }
  const snap = {
    init_jack: j.init_jack,
    init_lily: j.init_lily,
    init_george: j.init_george,
    run_id: j.run_id,
  };
  await persistJointLocked(snap);
  _jointLockGraceUntil = Date.now() + 60000;
  applyJointTrainLock(true);
  const resp = await api("/api/action", {
    method:"POST", headers:{"Content-Type":"application/json"},
    body: JSON.stringify({action:"joint_train", resume, ...j}),
  });
  LAST_JOB = resp.job_id;
  flash("hdrMeta", resume
    ? `resume дообучения → ${j.run_id}`
    : `запуск дообучения → ${j.run_id}`);
  pollJobs();
  setTimeout(pollServer, 1500);
  setTimeout(pollServer, 5000);
}

async function startJointStream() {
  await saveCfg();
  const run_id = (document.getElementById("streamRunId").value.trim()
    || document.getElementById("jointRunId").value.trim());
  if (!run_id) { flash("hdrMeta", "stream: укажи RUN_ID"); return; }
  document.getElementById("jointRunId").value = run_id;
  document.getElementById("streamRunId").value = run_id;
  _streamStoppingUntil = 0;
  _streamStartingUntil = Date.now() + 90000;
  applyStreamButtons("starting");
  schedulePresWorldPoll(true);
  flash("hdrMeta", `stream Presentation → ${run_id}`);
  try {
    const resp = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"joint_stream", run_id }),
    });
    LAST_JOB = resp.job_id;
    pollJobs();
    setTimeout(pollServer, 2000);
    setTimeout(pollServer, 8000);
  } catch (e) {
    _streamStartingUntil = 0;
    applyStreamButtons("stopped");
    alert(e.message||e);
  }
}

let _streamStartingUntil = 0;
let _streamStoppingUntil = 0;

function isStreamStarting() { return Date.now() < _streamStartingUntil; }
function isStreamStopping() { return Date.now() < _streamStoppingUntil; }

function applyStreamButtons(streamState) {
  const btnStart = document.getElementById("btnJointStream");
  const btnStop = document.getElementById("btnKillStream");
  const runInp = document.getElementById("streamRunId");
  if (!btnStart || !btnStop) return;
  let st = String(streamState || "stopped");
  if (st === "true" || st === "1") st = "running";
  if (st === "false" || st === "0") st = "stopped";
  if (isStreamStopping() && st !== "stopped") st = "stopping";
  if (isStreamStarting() && st !== "running" && st !== "stopping") st = "starting";

  const lockRun = (st === "running" || st === "starting" || st === "stopping");
  if (runInp) {
    runInp.disabled = lockRun;
    if (runInp.parentElement) runInp.parentElement.classList.toggle("locked", lockRun);
  }

  if (st === "running") {
    _streamStartingUntil = 0;
    _streamStoppingUntil = 0;
    btnStart.textContent = "● Запущено";
    btnStart.classList.add("live-on");
    btnStart.classList.remove("primary");
    btnStart.disabled = true;
    btnStart.title = "Стрим уже идёт — останови Стоп стрим";
    btnStop.textContent = "⏹ Стоп стрим";
    btnStop.classList.remove("live-on");
    btnStop.disabled = false;
    btnStop.title = "Убить только бесконечный стрим. Train и validate не трогает.";
  } else if (st === "starting") {
    btnStart.textContent = "● Запускается…";
    btnStart.classList.add("live-on");
    btnStart.classList.remove("primary");
    btnStart.disabled = true;
    btnStart.title = "Идёт запуск Presentation-стрима на lab";
    btnStop.textContent = "⏹ Стоп стрим";
    btnStop.classList.remove("live-on");
    btnStop.disabled = true;
    btnStop.title = "Дождись запуска";
  } else if (st === "stopping") {
    btnStart.textContent = "● Остановка…";
    btnStart.classList.remove("live-on");
    btnStart.classList.remove("primary");
    btnStart.disabled = true;
    btnStart.title = "Идёт остановка стрима";
    btnStop.textContent = "● Останавливается…";
    btnStop.classList.add("live-on");
    btnStop.disabled = true;
    btnStop.title = "Идёт остановка Presentation-стрима";
  } else {
    if (st === "stopped") {
      _streamStartingUntil = 0;
      _streamStoppingUntil = 0;
    }
    btnStart.textContent = "▶ Стрим Presentation (бесконечно)";
    btnStart.classList.remove("live-on");
    btnStart.classList.add("primary");
    btnStart.disabled = false;
    btnStart.title = "";
    btnStop.textContent = "⏹ Стоп стрим";
    btnStop.classList.remove("live-on");
    btnStop.disabled = false;
    btnStop.title = "Убить только бесконечный стрим. Train и validate не трогает.";
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
  if (panelId === "panel-joint-stream") pollPresWorld();
  saveUiView();
}

function switchModeTab(mode) {
  const train = mode === "training";
  const streaming = mode === "streaming";
  const obs = mode === "obs";
  document.getElementById("tab-mode-training").classList.toggle("active", train);
  document.getElementById("tab-mode-streaming").classList.toggle("active", streaming);
  document.getElementById("tab-mode-obs").classList.toggle("active", obs);
  document.getElementById("panel-mode-training").style.display = train ? "" : "none";
  document.getElementById("panel-mode-streaming").style.display = streaming ? "" : "none";
  document.getElementById("panel-mode-obs").style.display = obs ? "" : "none";
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
    const obs = !!(document.getElementById("tab-mode-obs")
      && document.getElementById("tab-mode-obs").classList.contains("active"));
    const jointBtn = document.querySelector("#card-joint-wrap .subtab.active");
    const view = {
      mode: obs ? "obs" : (streaming ? "streaming" : "training"),
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
  if (view.mode === "streaming" || view.mode === "training" || view.mode === "obs") {
    // Avoid recursive save noise while restoring.
    const train = view.mode === "training";
    const streaming = view.mode === "streaming";
    const obs = view.mode === "obs";
    document.getElementById("tab-mode-training").classList.toggle("active", train);
    document.getElementById("tab-mode-streaming").classList.toggle("active", streaming);
    document.getElementById("tab-mode-obs").classList.toggle("active", obs);
    document.getElementById("panel-mode-training").style.display = train ? "" : "none";
    document.getElementById("panel-mode-streaming").style.display = streaming ? "" : "none";
    document.getElementById("panel-mode-obs").style.display = obs ? "" : "none";
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
  _streamStartingUntil = 0;
  _streamStoppingUntil = Date.now() + 90000;
  applyStreamButtons("stopping");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"kill_stream" }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    setTimeout(pollServer, 1500);
    setTimeout(pollServer, 5000);
    setTimeout(pollServer, 12000);
  } catch (e) {
    _streamStoppingUntil = 0;
    applyStreamButtons("running");
    alert(e.message||e);
  }
}

async function repairStreamSpawn() {
  if (!confirm("Починить спавн деревьев/овец в Presentation?\\n\\nСкрипт напишет .forest_repair_spawners → Unity сделает ResetSpawners.\\nНужен свежий билд со StreamSpawnRepairWatcher + фикс TreeSpawner.")) return;
  flash("hdrMeta", "repair spawn…");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({
        action: "repair_spawn",
        run_id: (document.getElementById("streamRunId")||{}).value || (document.getElementById("jointRunId")||{}).value || "",
        force: true,
      }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
  } catch (e) { alert(e.message||e); }
}

async function killModeTraining() {
  if (!confirm("Стоп весь блок Forest Lab Train?\\n\\nУбьёт: train + validate + Presentation stream.\\nНе трогает: Survival followers, OBS.")) return;
  flash("hdrMeta", "стоп блок Train…");
  beginJointStopping();
  _streamStartingUntil = 0;
  _streamStoppingUntil = Date.now() + 90000;
  applyStreamButtons("stopping");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"kill_mode_training" }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    setTimeout(pollServer, 1500);
    setTimeout(pollServer, 5000);
    setTimeout(pollServer, 15000);
  } catch (e) {
    _jointStoppingUntil = 0;
    applyJointTrainLock(false);
    _streamStoppingUntil = 0;
    applyStreamButtons("running");
    alert(e.message||e);
  }
}

async function killModeStreaming() {
  if (!confirm("Стоп весь блок Survival followers?\\n\\nУбьёт: Streaming Survival + LLM Bot.\\nНе трогает: train, Presentation onnx, OBS.")) return;
  flash("hdrMeta", "стоп блок Survival…");
  _llmStartingUntil = 0;
  _llmStoppingUntil = Date.now() + 90000;
  applyLlmBotButtons("stopping");
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
    setTimeout(pollLlmBot, 3000);
  } catch (e) {
    _llmStoppingUntil = 0;
    alert(e.message||e);
  }
}

async function killTrainOnly() {
  if (!confirm("Остановить train на lab (mlagents + headless Unity)? Стрим и validate не трогаем.")) return;
  flash("hdrMeta", "стоп train…");
  beginJointStopping();
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"kill_train" }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    setTimeout(pollServer, 1500);
    setTimeout(pollServer, 4000);
    setTimeout(pollServer, 12000);
  } catch (e) {
    _jointStoppingUntil = 0;
    applyJointTrainLock(true);
    alert(e.message||e);
  }
}

const LLM_API = "/api/llm_bot";
let LLM_LISTEN = false;
let LLM_POLL_TIMER = null;
let _llmStartingUntil = 0;
let _llmStoppingUntil = 0;

function applyLlmBotButtons(botState) {
  const btnStart = document.getElementById("btnLlmStart");
  const btnStop = document.getElementById("btnLlmStop");
  if (!btnStart || !btnStop) return;
  let st = String(botState || "stopped");
  if (Date.now() < _llmStoppingUntil && st !== "stopped" && st !== "error") st = "stopping";
  if (Date.now() < _llmStartingUntil && st !== "running" && st !== "error" && st !== "stopping") st = "starting";
  if (st === "running") {
    _llmStartingUntil = 0;
    _llmStoppingUntil = 0;
    btnStart.textContent = "● Запущено";
    btnStart.classList.add("live-on");
    btnStart.classList.remove("primary");
    btnStart.disabled = true;
    btnStart.title = "LLM Bot уже работает на lab";
    btnStop.textContent = "⏹ Stop LLM Bot";
    btnStop.classList.remove("live-on");
    btnStop.disabled = false;
    btnStop.title = "Остановить LLM Bot на lab";
  } else if (st === "starting") {
    btnStart.textContent = "● Запускается…";
    btnStart.classList.add("live-on");
    btnStart.classList.remove("primary");
    btnStart.disabled = true;
    btnStart.title = "Идёт запуск LLM Bot на lab";
    btnStop.textContent = "⏹ Stop LLM Bot";
    btnStop.classList.remove("live-on");
    btnStop.disabled = true;
    btnStop.title = "Дождись запуска или ошибки";
  } else if (st === "stopping") {
    btnStart.textContent = "● Остановка…";
    btnStart.classList.remove("live-on");
    btnStart.classList.remove("primary");
    btnStart.disabled = true;
    btnStart.title = "Идёт остановка LLM Bot";
    btnStop.textContent = "● Останавливается…";
    btnStop.classList.add("live-on");
    btnStop.disabled = true;
    btnStop.title = "Идёт остановка LLM Bot на lab";
  } else {
    // stopped / error / unknown
    if (st === "stopped" || st === "error") {
      _llmStartingUntil = 0;
      _llmStoppingUntil = 0;
    }
    btnStart.textContent = "▶ Start LLM Bot";
    btnStart.classList.remove("live-on");
    btnStart.classList.add("primary");
    btnStart.disabled = false;
    btnStart.title = "Запустить LLM Bot на lab_comp";
    btnStop.textContent = "⏹ Stop LLM Bot";
    btnStop.classList.remove("live-on");
    btnStop.disabled = false;
    btnStop.title = "Остановить LLM Bot на lab";
  }
}

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
    pill.className = "pill" + (live || st === "stopping" ? " live" : "");
  }
  setCardState("card-llm-bot", live || st === "stopping", false);
  applyLlmBotButtons(st);
  const modeSs = document.getElementById("tab-mode-streaming");
  if (modeSs) {
    const ssLive = !!(document.getElementById("card-ss") && document.getElementById("card-ss").classList.contains("running"));
    const previewLive = !!(document.getElementById("card-ss-preview") && document.getElementById("card-ss-preview").classList.contains("running"));
    modeSs.classList.toggle("has-live", ssLive || previewLive);
  }
  const modeObs = document.getElementById("tab-mode-obs");
  if (modeObs) {
    // не гасим has-live из‑за bot; applyLabActivity выставит по stream/obs
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
  renderLlmRoster(s.roster || s.active_users || [], s.roster_count, s.players || null);
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

let LLM_ROSTER_HISTORY = false;
let LLM_ROSTER_CACHE = { roster: [], players: [], roster_count: 0 };

function fmtRosterTime(ts) {
  if (!ts) return "—";
  try {
    const d = new Date(Number(ts) * 1000);
    if (Number.isNaN(d.getTime())) return "—";
    return d.toLocaleString("ru-RU", { day:"2-digit", month:"2-digit", hour:"2-digit", minute:"2-digit" });
  } catch (_) { return "—"; }
}

function renderLlmRoster(roster, count, players) {
  const activeRows = Array.isArray(roster) ? roster : [];
  const histRows = Array.isArray(players) ? players : activeRows;
  LLM_ROSTER_CACHE = {
    roster: activeRows,
    players: histRows,
    roster_count: (count != null) ? Number(count) : activeRows.length,
  };
  const rows = LLM_ROSTER_HISTORY ? histRows : activeRows;
  const nActive = LLM_ROSTER_CACHE.roster_count;
  const pill = document.getElementById("llmRosterPill");
  const empty = document.getElementById("llmRosterEmpty");
  const table = document.getElementById("llmRosterTable");
  const body = document.getElementById("llmRosterBody");
  const modeBtn = document.getElementById("btnLlmRosterMode");
  if (modeBtn) modeBtn.textContent = LLM_ROSTER_HISTORY ? "Вся история" : "В игре";
  if (pill) {
    pill.textContent = LLM_ROSTER_HISTORY
      ? (rows.length + " / " + nActive + " in")
      : String(nActive);
  }
  if (!body || !table || !empty) return;
  if (!rows.length) {
    empty.style.display = "block";
    table.style.display = "none";
    body.innerHTML = "";
    return;
  }
  empty.style.display = "none";
  table.style.display = "table";
  const esc = (t) => String(t)
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  const num = (v) => {
    const n = Number(v);
    return Number.isFinite(n) ? String(n) : "0";
  };
  body.innerHTML = rows.map((u, i) => {
    const name = esc(u.username || u.user || "?");
    const act = esc(u.action_name || u.action || "idle");
    const since = fmtRosterTime(u.joined_at);
    const live = !!(u.is_active === true || u.is_active === 1 || (!LLM_ROSTER_HISTORY));
    const st = live ? '<span style="color:#3ecf8e">in</span>' : '<span style="color:#7a8499">out</span>';
    const dim = live ? "" : "opacity:.65";
    return `<tr style="border-top:1px solid #1e2633;${dim}">
      <td style="padding:4px 6px;color:#7a8499">${i + 1}</td>
      <td style="padding:4px 6px"><b>${name}</b></td>
      <td style="padding:4px 6px">${st}</td>
      <td style="padding:4px 6px">${act}</td>
      <td style="padding:4px 6px;color:#9aa4b8;white-space:nowrap">${since}</td>
      <td style="padding:4px 6px">${num(u.total_water_collected)}</td>
      <td style="padding:4px 6px">${num(u.total_wood_collected)}</td>
      <td style="padding:4px 6px">${num(u.total_sheep_killed)}</td>
      <td style="padding:4px 6px">${num(u.total_campfires_built)}</td>
    </tr>`;
  }).join("");
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
    _llmStoppingUntil = 0;
    _llmStartingUntil = Date.now() + 120000;
    applyLlmBotButtons("starting");
    try {
      const s = await api(LLM_API + "/start", { method: "POST", headers: {"Content-Type":"application/json"}, body: "{}" });
      renderLlmStatus(s);
      setTimeout(pollLlmBot, 1500);
      setTimeout(pollLlmBot, 5000);
    } catch (e) {
      _llmStartingUntil = 0;
      applyLlmBotButtons("stopped");
      alert(e.message || e);
    }
  };
  document.getElementById("btnLlmStop").onclick = async () => {
    flash("hdrMeta", "LLM bot stopping…");
    _llmStartingUntil = 0;
    _llmStoppingUntil = Date.now() + 90000;
    applyLlmBotButtons("stopping");
    try {
      const s = await api(LLM_API + "/stop", { method: "POST", headers: {"Content-Type":"application/json"}, body: "{}" });
      renderLlmStatus(s);
      setTimeout(pollLlmBot, 1500);
      setTimeout(pollLlmBot, 5000);
      setTimeout(pollServer, 2000);
    } catch (e) {
      _llmStoppingUntil = 0;
      applyLlmBotButtons("running");
      alert(e.message || e);
    }
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
  const btnResync = document.getElementById("btnLlmRosterResync");
  if (btnResync) btnResync.onclick = async () => {
    flash("hdrMeta", "roster resync → Unity…");
    try {
      const r = await api(LLM_API + "/resync_roster", { method: "POST", headers: {"Content-Type":"application/json"}, body: "{}" });
      if (r.status) renderLlmStatus(r.status);
      else await pollLlmBot();
    } catch (e) { alert(e.message || e); }
  };
  const btnMode = document.getElementById("btnLlmRosterMode");
  if (btnMode) btnMode.onclick = () => {
    LLM_ROSTER_HISTORY = !LLM_ROSTER_HISTORY;
    renderLlmRoster(LLM_ROSTER_CACHE.roster, LLM_ROSTER_CACHE.roster_count, LLM_ROSTER_CACHE.players);
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
  const j = collectJoint();
  j.locked = readJointLocked();
  const body = {
    build: document.getElementById("buildName").value.trim(),
    tb_url: document.getElementById("tbUrl").value.trim(),
    jack: collectHero("jack"),
    lily: collectHero("lily"),
    george: collectHero("george"),
    joint: j,
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
  return openDetailRuns("Joint finetune", runs, "Сначала выбери TB runs и нажми «Нарисовать TB»");
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
    renderServerHost(s);
    applyLabActivity(s.activity || {});
    renderStreamFps((s.activity || {}).stream_fps || {});
    renderObsSsh(s.ssh_sessions || {});
    const streamOn = !!(s.activity && s.activity.stream);
    if (streamOn) schedulePresWorldPoll(true);
    else if (_presWorldTimer) { /* keep last frame; soft refresh less often */ schedulePresWorldPoll(false); }
  } catch (e) {
    document.getElementById("serverStats").textContent = "server_stats: " + (e.message||e);
    document.getElementById("serverPill").textContent = "err";
    document.getElementById("serverPill").className = "pill";
    renderServerHost({ ok:false, host:"lab_comp", metrics:{}, text: String(e.message||e) });
    applyLabActivity({});
    renderStreamFps({});
    renderObsSsh({});
  }
}

function hostMeterHtml(label, valueHtml, pct, barClass) {
  const p = Math.max(0, Math.min(100, Number(pct) || 0));
  return (
    '<div class="host-meter">'
    + '<div class="host-meter-row"><span class="host-meter-label">' + label + '</span>'
    + '<span class="host-meter-val">' + valueHtml + '</span></div>'
    + '<div class="host-bar ' + barClass + '"><i style="width:' + p.toFixed(1) + '%"></i></div>'
    + '</div>'
  );
}

function renderServerHost(s) {
  const title = document.getElementById("serverHostTitle");
  const online = document.getElementById("serverHostOnline");
  const meters = document.getElementById("serverHostMeters");
  if (!meters) return;
  const host = (s && s.host) || "lab_comp";
  if (title) title.textContent = host === "local" ? "lab_comp (local)" : host;
  const lat = s && s.latency_ms != null ? Number(s.latency_ms) : NaN;
  if (online) {
    if (s && s.ok) {
      online.className = "host-online";
      online.textContent = "ONLINE" + (lat === lat ? (" · " + Math.round(lat) + " MS") : "");
    } else if (s && s.pending) {
      online.className = "host-online pending";
      online.textContent = "ОПРОС…";
    } else {
      online.className = "host-online off";
      online.textContent = "OFFLINE";
    }
  }
  const m = (s && s.metrics) || {};
  const parts = [];
  const ram = m.ram || null;
  if (ram) {
    parts.push(hostMeterHtml(
      "RAM",
      (ram.used_gib != null ? ram.used_gib + " GiB / " + ram.total_gib + " GiB · " + ram.pct + "%" : "—"),
      ram.pct,
      "ram"
    ));
  }
  const disk = m.disk || null;
  if (disk) {
    parts.push(hostMeterHtml(
      "DISK",
      "свободно " + disk.free_gib + " GiB · занято " + disk.used_gib + " GiB / " + disk.total_gib + " GiB · " + disk.pct + "%",
      disk.pct,
      "disk"
    ));
  }
  const gpus = Array.isArray(m.gpus) ? m.gpus : [];
  const gpuProcs = Array.isArray(m.gpu_procs) ? m.gpu_procs : [];
  for (const g of gpus) {
    const chips = gpuProcs.map((p) =>
      '<span class="host-chip">' + (p.label || ((p.user || "?") + " " + (p.used_gib != null ? p.used_gib + "G" : ""))) + "</span>"
    ).join("");
    parts.push(
      '<div class="host-gpu">'
      + '<div class="host-gpu-head"><strong>GPU ' + (g.index != null ? g.index : 0) + '</strong>'
      + '<span>' + (g.name || "NVIDIA") + (g.temp != null ? (" · " + g.temp + "°C") : "") + '</span></div>'
      + (chips ? ('<div class="host-chips">' + chips + '</div>') : "")
      + hostMeterHtml(
        "VRAM",
        (g.vram_used_gib != null ? g.vram_used_gib + " GiB / " + g.vram_total_gib + " GiB · " + g.vram_pct + "%" : "—"),
        g.vram_pct,
        "vram"
      )
      + hostMeterHtml("UTIL", (g.util != null ? g.util + "%" : "—"), g.util, "util")
      + '</div>'
    );
  }
  const top = Array.isArray(m.top_cpu) ? m.top_cpu.slice(0, 6) : [];
  if (top.length) {
    parts.push(
      '<div class="host-chips">'
      + top.map((p) =>
        '<span class="host-chip cpu">' + (p.comm || "?") + " " + Math.round(p.cpu || 0) + "%</span>"
      ).join("")
      + '</div>'
    );
  }
  if (!parts.length) {
    meters.innerHTML = '<div class="hint">' + ((s && s.text) ? "нет структурированных метрик — см. сырой вывод" : "загрузка метрик…") + '</div>';
    return;
  }
  meters.innerHTML = parts.join("");
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
  const obsOn = !!a.obs;
  window._labObsOn = obsOn;
  window._labStreamOn = stream;
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
  setCardState("card-obs-stream", stream || obsOn, false);
  // сразу обновить pill/подсказку OBS (не ждать stream_fps)
  renderObsStream(Object.assign({ stream_on: stream }, (a.stream_fps && typeof a.stream_fps === "object") ? a.stream_fps : {}));
  // Preview card keeps its own running state from ssPreviewApplyStatus — don't clear it here.
  const previewLive = !!(document.getElementById("card-ss-preview")
    && document.getElementById("card-ss-preview").classList.contains("running"));

  const tabTrain = document.getElementById("tab-joint-train");
  const tabStream = document.getElementById("tab-joint-stream");
  if (tabTrain) tabTrain.classList.toggle("has-live", joint);
  if (tabStream) tabStream.classList.toggle("has-live", stream);
  const modeTrain = document.getElementById("tab-mode-training");
  if (modeTrain) modeTrain.classList.toggle("has-live", joint || jack || lily || george || validate);
  const modeSs = document.getElementById("tab-mode-streaming");
  if (modeSs) modeSs.classList.toggle("has-live", ss || previewLive);
  const modeObs = document.getElementById("tab-mode-obs");
  if (modeObs) {
    // Красная вкладка только когда эфир (Unity и/или OBS), не из‑за LLM Bot.
    modeObs.classList.toggle("has-live", stream || obsOn);
    modeObs.classList.remove("warn-live");
  }

  setPillLive("pill-jack", jack, false, null);
  setPillLive("pill-lily", lily, false, null);
  setPillLive("pill-george", george, false, null);
  setPillLive("pill-joint", joint, false, "finetune");
  setPillLive("pill-stream", stream, false, "idle");

  applyJointTrainLock(!!joint);

  if (isStreamStopping()) applyStreamButtons("stopping");
  else if (stream) applyStreamButtons("running");
  else if (isStreamStarting()) applyStreamButtons("starting");
  else applyStreamButtons("stopped");

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

let _presWorldTimer = null;
let _presWorldBusy = false;
let _presWorldFrameCache = { start: null, end: null, episode: 0, pending: true };

function schedulePresWorldPoll(live) {
  if (_presWorldTimer) clearInterval(_presWorldTimer);
  const ms = live ? 8000 : 25000;
  pollPresWorld();
  _presWorldTimer = setInterval(pollPresWorld, ms);
}

async function pollPresWorld() {
  if (_presWorldBusy) return;
  const runEl = document.getElementById("streamRunId") || document.getElementById("jointRunId");
  const run_id = ((runEl && runEl.value) || "").trim();
  _presWorldBusy = true;
  try {
    const q = run_id ? ("?run_id=" + encodeURIComponent(run_id)) : "";
    const data = await api("/api/presentation_world" + q);
    renderPresWorld(data);
  } catch (e) {
    const pill = document.getElementById("presWorldPill");
    if (pill) { pill.textContent = "err"; pill.className = "pill"; }
    const meta = document.getElementById("presWorldMeta");
    if (meta) meta.textContent = String(e.message || e);
  } finally {
    _presWorldBusy = false;
  }
}

function _presNum(v, fallback) {
  const n = Number(v);
  return Number.isFinite(n) ? n : fallback;
}

function _presFrameFromLive(data) {
  if (!data || !data.ok) return null;
  return {
    ok: true,
    ts: data.ts || "",
    episode_index: data.episode_index,
    trees: data.trees, sheep: data.sheep, zombies: data.zombies,
    trees_n: data.trees_n, sheep_n: data.sheep_n, zombies_n: data.zombies_n,
    trees_target: data.trees_target, sheep_target: data.sheep_target,
    jack: data.jack, lily: data.lily, george: data.george,
    landmarks: data.landmarks,
  };
}

function renderPresWorld(data) {
  const pill = document.getElementById("presWorldPill");
  const meta = document.getElementById("presWorldMeta");
  const agentsHost = document.getElementById("presWorldAgents");
  if (!data || !data.ok) {
    if (pill) { pill.textContent = "нет данных"; pill.className = "pill"; }
    if (meta) meta.textContent = (data && data.error) ? data.error : "snapshot ещё не появился";
    return;
  }
  if (pill) { pill.textContent = "live"; pill.className = "pill on"; }
  if (meta) {
    const bits = [];
    if (data.run_id) bits.push(data.run_id);
    if (data.source) bits.push(data.source);
    if (data.ts) bits.push(data.ts);
    else if (data.reason) bits.push(data.reason);
    meta.textContent = bits.join(" · ") || "ok";
  }
  const setTxt = (id, v) => { const el = document.getElementById(id); if (el) el.textContent = v; };
  const sheepN = _presNum(data.sheep_n, 0);
  const sheepT = _presNum(data.sheep_target, 0);
  const treeN = _presNum(data.trees_n, 0);
  const treeT = _presNum(data.trees_target, 0);
  const zN = _presNum(data.zombies_n, 0);
  const ep = data.episode || {};
  const lr = data.last_round || {};
  const epIdx = _presNum(data.episode_index, 0)
    || _presNum(ep.index, 0)
    || _presNum(lr.episode, 0);
  setTxt("presKpiEpisode", epIdx > 0 ? String(epIdx) : "—");
  setTxt("presKpiSheep", sheepT ? (sheepN + " / " + sheepT) : String(sheepN));
  setTxt("presKpiTrees", treeT ? (treeN + " / " + treeT) : String(treeN));
  setTxt("presKpiZombies", String(zN));

  const waterShow = _presNum(lr.water, 0) || _presNum(ep.water, 0);
  const woodShow = _presNum(lr.wood, 0) || _presNum(ep.wood, 0);
  const sheepKill = _presNum(lr.sheep_killed, 0) || _presNum(ep.sheep_killed, 0);
  const treesChop = _presNum(lr.trees_chopped, 0) || _presNum(ep.trees_chopped, 0);
  setTxt("presKpiWater", String(waterShow));
  setTxt("presKpiWood", String(woodShow) + (treesChop ? (" · −" + treesChop + " дерев") : ""));
  setTxt("presKpiSheepKill", String(sheepKill));

  const order = [
    { key: "jack", label: "Jack" },
    { key: "lily", label: "Lily" },
    { key: "george", label: "George" },
  ];
  if (agentsHost) {
    agentsHost.innerHTML = order.map(({ key, label }) => {
      const a = data[key] || {};
      if (!a.ok) {
        return '<div class="pres-agent ' + key + '"><div class="name">' + label + '</div><div>нет на карте</div></div>';
      }
      const dead = a.dead ? " dead" : "";
      const wood = (a.wood != null && a.wood >= 0) ? (" · wood " + a.wood) : "";
      return '<div class="pres-agent ' + key + dead + '">'
        + '<div class="name">' + label + (a.dead ? " · DEAD" : "") + "</div>"
        + "<div>hp " + (a.hp != null ? a.hp : "—")
        + " · water " + (a.water != null ? a.water : "—") + wood + "</div>"
        + "<div>xz (" + Number(a.x).toFixed(1) + ", " + Number(a.z).toFixed(1) + ")</div>"
        + "</div>";
    }).join("");
  }

  const frames = data.episode_frames || {};
  let startFr = frames.start || null;
  let endFr = frames.end || null;
  let pending = !!frames.pending;
  let frEp = _presNum(frames.episode, 0);
  if (!startFr || !endFr) {
    const live = _presFrameFromLive(data);
    if (!startFr) startFr = live;
    if (!endFr) endFr = live;
    pending = true;
    frEp = frEp || epIdx;
  }
  _presWorldFrameCache = { start: startFr, end: endFr, episode: frEp, pending: pending };

  const stTitle = document.getElementById("presMapStartTitle");
  const enTitle = document.getElementById("presMapEndTitle");
  if (stTitle) {
    stTitle.textContent = frEp
      ? ("#" + frEp + (startFr && startFr.ts ? (" · " + String(startFr.ts).slice(11, 19)) : ""))
      : "—";
  }
  if (enTitle) {
    enTitle.textContent = pending
      ? ("ещё идёт" + (endFr && endFr.ts ? (" · " + String(endFr.ts).slice(11, 19)) : ""))
      : (frEp
        ? ("#" + frEp + (endFr && endFr.ts ? (" · " + String(endFr.ts).slice(11, 19)) : ""))
        : "—");
  }

  const cam = _presSharedCamera([startFr, endFr, data]);
  drawPresWorldMap("presWorldMapStart", startFr, cam);
  drawPresWorldMap("presWorldMapEnd", endFr, cam);
}

function _presCollectPts(data) {
  const pts = [];
  if (!data) return pts;
  const lm = data.landmarks || {};
  for (const k of ["trees", "sheep", "zombies"]) {
    const arr = Array.isArray(data[k]) ? data[k] : [];
    for (const p of arr) if (p && p.x != null) pts.push(p);
  }
  for (const key of ["jack", "lily", "george"]) {
    const a = data[key];
    if (a && a.ok) pts.push(a);
  }
  if (lm.house && lm.house.x != null) pts.push(lm.house);
  for (const p of (lm.lakes || [])) if (p && p.x != null) pts.push(p);
  for (const p of (lm.stones || [])) if (p && p.x != null) pts.push(p);
  for (const p of (lm.fences || [])) if (p && p.x != null) pts.push(p);
  for (const r of [...(lm.lakes || []), ...(lm.fences || [])]) {
    if (!r || r.x == null) continue;
    const hx = (Number(r.sx) || 0) * 0.5;
    const hz = (Number(r.sz) || 0) * 0.5;
    pts.push({ x: r.x - hx, z: r.z - hz });
    pts.push({ x: r.x + hx, z: r.z + hz });
  }
  return pts;
}

function _presSharedCamera(frames) {
  let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
  for (const fr of frames) {
    for (const p of _presCollectPts(fr)) {
      minX = Math.min(minX, p.x); maxX = Math.max(maxX, p.x);
      minZ = Math.min(minZ, p.z); maxZ = Math.max(maxZ, p.z);
    }
  }
  if (!(minX < maxX) || !(minZ < maxZ)) {
    minX = -10; maxX = 30; minZ = 5; maxZ = 40;
  }
  const pad = 4;
  return { minX: minX - pad, maxX: maxX + pad, minZ: minZ - pad, maxZ: maxZ + pad };
}

function drawPresWorldMap(canvasId, data, cam) {
  const canvas = document.getElementById(canvasId);
  if (!canvas || !canvas.getContext) return;
  const ctx = canvas.getContext("2d");
  const W = canvas.width, H = canvas.height;
  ctx.clearRect(0, 0, W, H);
  ctx.fillStyle = "#0a0c10";
  ctx.fillRect(0, 0, W, H);

  if (!data || !data.ok) {
    ctx.fillStyle = "#6a7384";
    ctx.font = "14px sans-serif";
    ctx.fillText("нет кадра", 16, 28);
    return;
  }

  const lm = data.landmarks || {};
  const pts = _presCollectPts(data);
  if (!pts.length) {
    ctx.fillStyle = "#6a7384";
    ctx.font = "14px sans-serif";
    ctx.fillText("нет точек мира", 16, 28);
    return;
  }
  const minX = cam.minX, maxX = cam.maxX, minZ = cam.minZ, maxZ = cam.maxZ;
  const spanX = Math.max(1, maxX - minX);
  const spanZ = Math.max(1, maxZ - minZ);
  const margin = 16;
  const scale = Math.min((W - margin * 2) / spanX, (H - margin * 2) / spanZ);
  const ox = (W - spanX * scale) / 2;
  const oy = (H - spanZ * scale) / 2;
  const toXY = (x, z) => ({
    x: ox + (x - minX) * scale,
    y: oy + (maxZ - z) * scale,
  });

  ctx.strokeStyle = "rgba(255,255,255,0.05)";
  ctx.lineWidth = 1;
  for (let g = Math.floor(minX); g <= maxX; g += 5) {
    const a = toXY(g, minZ), b = toXY(g, maxZ);
    ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
  }
  for (let g = Math.floor(minZ); g <= maxZ; g += 5) {
    const a = toXY(minX, g), b = toXY(maxX, g);
    ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
  }

  for (const lake of (lm.lakes || [])) {
    if (!lake || lake.x == null) continue;
    const sx = Math.max(2, Number(lake.sx) || 8);
    const sz = Math.max(2, Number(lake.sz) || 6);
    const c = toXY(lake.x, lake.z);
    const rx = Math.max(4, sx * scale * 0.5);
    const ry = Math.max(4, sz * scale * 0.5);
    ctx.fillStyle = "rgba(58,126,200,0.35)";
    ctx.strokeStyle = "rgba(120,180,240,0.7)";
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.ellipse(c.x, c.y, rx, ry, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
    ctx.fillStyle = "#9ec8f0";
    ctx.font = "10px sans-serif";
    ctx.fillText("озеро", c.x - 14, c.y + 3);
  }

  ctx.fillStyle = "rgba(139,105,20,0.75)";
  for (const f of (lm.fences || [])) {
    if (!f || f.x == null) continue;
    const sx = Math.max(0.35, Number(f.sx) || 0.5);
    const sz = Math.max(0.35, Number(f.sz) || 0.5);
    const c = toXY(f.x, f.z);
    const w = Math.max(2, sx * scale);
    const h = Math.max(2, sz * scale);
    ctx.fillRect(c.x - w / 2, c.y - h / 2, w, h);
  }

  ctx.fillStyle = "#9a9aa0";
  for (const s of (lm.stones || [])) {
    if (!s || s.x == null) continue;
    const c = toXY(s.x, s.z);
    ctx.beginPath();
    ctx.arc(c.x, c.y, 3.5, 0, Math.PI * 2);
    ctx.fill();
  }

  if (lm.house && lm.house.x != null) {
    const c = toXY(lm.house.x, lm.house.z);
    ctx.fillStyle = "#c9a227";
    ctx.fillRect(c.x - 6, c.y - 6, 12, 12);
    ctx.strokeStyle = "#f0d878";
    ctx.lineWidth = 1.5;
    ctx.strokeRect(c.x - 6, c.y - 6, 12, 12);
    ctx.fillStyle = "#f5e6a8";
    ctx.font = "10px sans-serif";
    ctx.fillText("дом", c.x - 10, c.y - 9);
  }

  const drawDots = (arr, color, r) => {
    ctx.fillStyle = color;
    for (const p of (arr || [])) {
      if (!p || p.x == null) continue;
      const c = toXY(p.x, p.z);
      ctx.beginPath();
      ctx.arc(c.x, c.y, r, 0, Math.PI * 2);
      ctx.fill();
    }
  };
  drawDots(data.trees, "#4a8f4a", 2.5);
  drawDots(data.sheep, "#e8e0d0", 3);
  drawDots(data.zombies, "#c45a5a", 3.5);

  const agentColors = { jack: "#6aa6ff", lily: "#e070b0", george: "#3ecf8e" };
  for (const key of ["jack", "lily", "george"]) {
    const a = data[key];
    if (!a || !a.ok) continue;
    const c = toXY(a.x, a.z);
    ctx.fillStyle = agentColors[key];
    ctx.beginPath();
    ctx.arc(c.x, c.y, a.dead ? 5 : 7, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = "#0b1220";
    ctx.font = "bold 10px sans-serif";
    ctx.fillText(key[0].toUpperCase(), c.x - 3.5, c.y + 3.5);
  }
}

function renderStreamFps(fps) {
  // Единственное место FPS: «Мир стрима» (meters + KPI + pill).
  const pre = document.getElementById("streamFps");
  const meters = document.getElementById("streamFpsMeters");
  const card = document.getElementById("streamFpsCard");
  const f = fps && typeof fps === "object" ? fps : {};
  const n = Number(f.fps);
  const hitchMs = Number(f.hitch_dt_ms);
  const lagNow = !!f.lag_now || (n === n && n < 15) || (hitchMs === hitchMs && hitchMs >= 800);
  const soft = (n === n && n < 20 && !lagNow);
  if (pre) {
    const lines = [];
    if (n === n) {
      const mark = lagNow ? "  ← НИЗКИЙ FPS" : (soft ? "  ← слабо" : "");
      let line = `fps ≈ ${n.toFixed(1)}`;
      const fmin = Number(f.fps_min), fmax = Number(f.fps_max);
      if (fmin === fmin && fmax === fmax) line += `   min ${fmin.toFixed(0)}   max ${fmax.toFixed(0)}`;
      line += `   step ≈ ${Number(f.step_ms||0).toFixed(0)} ms   steps=${f.steps||"?"}${mark}`;
      lines.push(line);
    }
    if (f.hitches != null) lines.push(`hitches (окно) ${f.hitches}   slow>50ms ${f.slow||0}`);
    if (hitchMs === hitchMs) lines.push(`last hitch ${hitchMs} ms  (step ${f.hitch_step||"?"}, ~${f.hitch_ema_fps||"?"} FPS)`);
    if (f.summary) lines.push(String(f.summary).replace(/^\[stream_onnx\]\s*/, ""));
    if (f.hitch && !hitchMs) lines.push(String(f.hitch).replace(/^\[stream_onnx\]\s*/, ""));
    if (f.age_s != null) lines.push(`обновлено ${f.age_s}s назад · ${f.source||"?"}${f.log ? " · "+f.log : ""}`);
    pre.textContent = lines.length ? lines.join("\n") : "нет данных (стрим не пишет лог)";
  }
  if (card) {
    card.classList.toggle("fps-bad", !!lagNow && n === n);
    card.classList.toggle("fps-warn", !!soft);
  }
  if (meters) {
    if (!(n === n)) {
      meters.innerHTML = '<div class="hint">нет данных (стрим не пишет лог)</div>';
    } else {
      const target = 30;
      const pct = Math.max(0, Math.min(100, (n / target) * 100));
      let barCls = "fps";
      let statusCls = "ok";
      let statusTxt = "OK · нормальный FPS";
      if (lagNow) {
        barCls += " bad";
        statusCls = "bad";
        statusTxt = "НИЗКИЙ FPS · стрим лагает (<15)";
      } else if (soft) {
        barCls += " warn";
        statusCls = "warn";
        statusTxt = "СЛАБЫЙ FPS · лучше проверить (<20)";
      }
      const extra = [];
      if (f.step_ms != null) extra.push("step " + Number(f.step_ms).toFixed(0) + " ms");
      const fmin = Number(f.fps_min), fmax = Number(f.fps_max);
      if (fmin === fmin && fmax === fmax) extra.push("min " + fmin.toFixed(0) + " · max " + fmax.toFixed(0));
      if (hitchMs === hitchMs) extra.push("hitch " + hitchMs + " ms");
      if (f.age_s != null) extra.push(f.age_s + "s назад");
      meters.innerHTML =
        '<div class="host-fps-status ' + statusCls + '">' + statusTxt + "</div>"
        + hostMeterHtml(
          lagNow ? "FPS · НИЗКИЙ" : (soft ? "FPS · слабо" : "FPS"),
          n.toFixed(1) + " / ~" + target + (extra.length ? (" · " + extra.join(" · ")) : ""),
          pct,
          barCls
        );
    }
  }
  renderPresWorldFps(f);
  renderObsStream(f);
}

let _obsStartingUntil = 0;
let _obsStoppingUntil = 0;

function applyObsStreamButtons(state) {
  const btnStart = document.getElementById("btnObsStartStream");
  const btnStop = document.getElementById("btnObsStopStream");
  if (!btnStart || !btnStop) return;
  let st = String(state || "stopped");
  if (Date.now() < _obsStoppingUntil && st !== "stopped") st = "stopping";
  if (Date.now() < _obsStartingUntil && st !== "running" && st !== "stopping") st = "starting";
  if (st === "running") {
    _obsStartingUntil = 0;
    _obsStoppingUntil = 0;
    btnStart.textContent = "● Запущено";
    btnStart.classList.add("live-on");
    btnStart.classList.remove("primary");
    btnStart.disabled = true;
    btnStart.title = "Стрим уже в эфире (Unity + OBS)";
    btnStop.textContent = "⏹ Стоп стрим";
    btnStop.classList.remove("live-on");
    btnStop.disabled = false;
    btnStop.title = "Остановить Presentation + OBS";
  } else if (st === "starting") {
    btnStart.textContent = "● Запускается…";
    btnStart.classList.add("live-on");
    btnStart.classList.remove("primary");
    btnStart.disabled = true;
    btnStart.title = "Идёт запуск эфира";
    btnStop.textContent = "⏹ Стоп стрим";
    btnStop.classList.remove("live-on");
    btnStop.disabled = true;
  } else if (st === "stopping") {
    btnStart.textContent = "● Остановка…";
    btnStart.classList.remove("live-on");
    btnStart.classList.remove("primary");
    btnStart.disabled = true;
    btnStop.textContent = "● Останавливается…";
    btnStop.classList.add("live-on");
    btnStop.disabled = true;
  } else {
    if (st === "stopped") {
      _obsStartingUntil = 0;
      _obsStoppingUntil = 0;
    }
    btnStart.textContent = "● Запись";
    btnStart.classList.remove("live-on");
    btnStart.classList.add("primary");
    btnStart.disabled = false;
    btnStart.title = "Запустить Presentation + OBS в эфир";
    btnStop.textContent = "⏹ Стоп стрим";
    btnStop.classList.remove("live-on");
    btnStop.disabled = false;
  }
}

function renderObsStream(f) {
  const hint = document.getElementById("obsProcHint");
  const card = document.getElementById("card-obs-stream");
  const streamOn = !!(f && f.stream_on) || !!window._labStreamOn;
  const obsOn = !!(window._labObsOn);
  const live = !!(streamOn && obsOn);
  const partial = !live && (streamOn || obsOn);
  if (hint) {
    if (live) hint.textContent = "в эфире · Unity ON · OBS ON";
    else if (partial) {
      hint.textContent = "частично · Unity " + (streamOn ? "ON" : "off")
        + " · OBS " + (obsOn ? "ON" : "off");
    } else hint.textContent = "стрим выключен";
  }
  if (card) card.classList.toggle("running", live || partial);
  if (Date.now() < _obsStoppingUntil) applyObsStreamButtons("stopping");
  else if (Date.now() < _obsStartingUntil && !live) applyObsStreamButtons("starting");
  else if (live) applyObsStreamButtons("running");
  else applyObsStreamButtons("stopped");
}

async function startObsStream() {
  const runId = (document.getElementById("streamRunId")||{}).value
    || (document.getElementById("jointRunId")||{}).value || "";
  if (!runId) { alert("Нужен RUN_ID (вкладка Train → обучение / стрим)"); return; }
  if (!confirm("● Запись / в эфир?\\n\\nPresentation + OBS\\nRUN_ID="+runId)) return;
  flash("hdrMeta", "● запись…");
  _obsStoppingUntil = 0;
  _obsStartingUntil = Date.now() + 120000;
  applyObsStreamButtons("starting");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"start_obs_stream", run_id: runId }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    setTimeout(pollServer, 5000);
    setTimeout(pollServer, 15000);
  } catch (e) {
    _obsStartingUntil = 0;
    applyObsStreamButtons("stopped");
    alert(e.message||e);
  }
}

async function stopObsStream() {
  if (!confirm("⏹ Стоп стрим?\\n\\nГасим Presentation + OBS. Train не трогаем.")) return;
  flash("hdrMeta", "стоп стрим…");
  _obsStartingUntil = 0;
  _obsStoppingUntil = Date.now() + 90000;
  applyObsStreamButtons("stopping");
  try {
    const j = await api("/api/action", {
      method:"POST", headers:{"Content-Type":"application/json"},
      body: JSON.stringify({ action:"stop_obs_stream" }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    setTimeout(pollServer, 3000);
    setTimeout(pollServer, 8000);
  } catch (e) {
    _obsStoppingUntil = 0;
    applyObsStreamButtons("running");
    alert(e.message||e);
  }
}

function renderPresWorldFps(fps) {
  const f = fps && typeof fps === "object" ? fps : {};
  const n = Number(f.fps);
  const fmin = Number(f.fps_min);
  const fmax = Number(f.fps_max);
  const hitchMs = Number(f.hitch_dt_ms);
  const lagNow = !!f.lag_now || (n === n && n < 15) || (hitchMs === hitchMs && hitchMs >= 800);
  const soft = (n === n && n < 20 && !lagNow);
  const pill = document.getElementById("presWorldFpsPill");
  if (!pill) return;
  if (!(n === n)) {
    pill.textContent = "—";
    pill.className = "pill";
    return;
  }
  let ptxt = n.toFixed(0) + " fps";
  if (fmin === fmin && fmax === fmax) ptxt += " · " + fmin.toFixed(0) + "–" + fmax.toFixed(0);
  if (lagNow) {
    pill.textContent = ptxt + " · НИЗКИЙ";
    pill.className = "pill live";
  } else if (soft) {
    pill.textContent = ptxt + " · слабо";
    pill.className = "pill warn";
  } else {
    pill.textContent = ptxt;
    pill.className = "pill on";
  }
}

function renderObsSsh(s) {
  const pre = document.getElementById("obsSshNow");
  const pill = document.getElementById("obsSshPill");
  const hint = document.getElementById("obsSshHint");
  if (!pre || !pill) return;
  s = s || {};
  const local = Number(s.local);
  const ui = Number(s.ui);
  const remote = s.remote == null ? null : Number(s.remote);
  const total = Number(s.total);
  const warnAt = Number(s.warn_at) || 6;
  const badAt = Number(s.bad_at) || 10;
  const level = String(s.level || "ok");
  const lines = [];
  lines.push(`всего ≈ ${total === total ? total : "?"}  (warn≥${warnAt}, bad≥${badAt})`);
  lines.push(`локально ssh→lab: ${local === local ? local : "?"}`);
  lines.push(`активные у этого UI: ${ui === ui ? ui : "?"}`);
  lines.push(`на lab (:22): ${remote == null || !(remote === remote) ? "—" : remote}`);
  if (s.error) lines.push(`err: ${s.error}`);
  pre.textContent = lines.join("\n");
  if (level === "bad") {
    pill.textContent = (total === total ? total : "?") + " ssh";
    pill.className = "pill live";
  } else if (level === "warn") {
    pill.textContent = (total === total ? total : "?") + " ssh";
    pill.className = "pill warn";
  } else {
    pill.textContent = (total === total ? total : "0") + " ssh";
    pill.className = "pill on";
  }
  if (hint) {
    hint.style.color = level === "bad" ? "var(--bad)" : (level === "warn" ? "var(--warn)" : "");
  }
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
    _llmStartingUntil = 0;
    _llmStoppingUntil = Date.now() + 90000;
    applyLlmBotButtons("stopping");
    try {
      const s = await api(LLM_API + "/stop", { method: "POST", headers: {"Content-Type":"application/json"}, body: "{}" });
      renderLlmStatus(s);
      setTimeout(pollLlmBot, 1500);
      setTimeout(pollLlmBot, 5000);
      setTimeout(pollServer, 1200);
      setTimeout(pollServer, 3500);
    } catch (e) {
      _llmStoppingUntil = 0;
      applyLlmBotButtons("running");
      alert(e.message || e);
    }
    return;
  }
  const map = {
    train: { action: "kill_train", ask: "Остановить train на lab (mlagents + headless)? Стрим/validate не трогаем." },
    validate: { action: "kill_validate", ask: "Остановить только validate (mp4)?" },
    stream: { action: "kill_stream", ask: "Остановить только бесконечный стрим Presentation? OBS не гасим." },
    streaming_survival: { action: "stop_streaming_survival", ask: "Остановить Streaming Survival? Train / Presentation onnx не трогаем." },
    obs: { action: "stop_obs_stream", ask: "Остановить OBS + Presentation стрим?" },
  };
  const conf = map[kind];
  if (!conf) { alert("Неизвестный тип: " + kind); return; }
  if (!confirm(conf.ask + (label ? "\n\n" + label : ""))) return;
  flash("hdrMeta", "стоп " + kind + "…");
  if (kind === "train") beginJointStopping();
  if (kind === "stream") {
    _streamStartingUntil = 0;
    _streamStoppingUntil = Date.now() + 90000;
    applyStreamButtons("stopping");
  }
  try {
    const j = await api("/api/action", {
      method: "POST", headers: {"Content-Type":"application/json"},
      body: JSON.stringify({ action: conf.action }),
    });
    LAST_JOB = j.job_id;
    pollJobs();
    setTimeout(pollServer, 1500);
    setTimeout(pollServer, 4000);
    if (kind === "train" || kind === "stream") setTimeout(pollServer, 12000);
  } catch (e) {
    if (kind === "train") {
      _jointStoppingUntil = 0;
      applyJointTrainLock(true);
    }
    if (kind === "stream") {
      _streamStoppingUntil = 0;
      applyStreamButtons("running");
    }
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
  const snap = {
    init_jack: j.init_jack,
    init_lily: j.init_lily,
    init_george: j.init_george,
    run_id: j.run_id,
  };
  await persistJointLocked(snap);
  _jointLockGraceUntil = Date.now() + 60000;
  applyJointTrainLock(true);
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
  _streamStoppingUntil = 0;
  _streamStartingUntil = Date.now() + 120000;
  applyStreamButtons("starting");
  schedulePresWorldPoll(true);
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
    _streamStartingUntil = 0;
    applyStreamButtons("stopped");
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
  document.getElementById("btnJointTrain").onclick = () => trainJoint();
  document.getElementById("btnJointStream").onclick = () => startJointStream();
  document.getElementById("btnKillStream").onclick = () => killStreamOnly();
  const btnRepairSpawn = document.getElementById("btnRepairSpawn");
  if (btnRepairSpawn) btnRepairSpawn.onclick = () => repairStreamSpawn();
  const btnObsStart = document.getElementById("btnObsStartStream");
  if (btnObsStart) btnObsStart.onclick = () => startObsStream();
  const btnObsStop = document.getElementById("btnObsStopStream");
  if (btnObsStop) btnObsStop.onclick = () => stopObsStream();
  const btnKillJoint = document.getElementById("btnKillJointTrain");
  if (btnKillJoint) btnKillJoint.onclick = () => killTrainOnly();
  document.getElementById("btnJointTbTrio").onclick = () => selectJointTbTrio();
  document.getElementById("btnJointTbDraw").onclick = () => drawJointTb();
  document.getElementById("btnJointTbClear").onclick = () => clearJointTb();
  document.getElementById("jointTb").onchange = () => { saveCfg(); };
  document.getElementById("jointRunId").onchange = () => {
    const v = document.getElementById("jointRunId").value;
    const s = document.getElementById("streamRunId");
    if (s) s.value = v;
    // Только выбрать тройку из новой папки — график по кнопке «Нарисовать TB»
    selectJointTbTrio();
    refreshJointWeightStatus();
  };
  document.getElementById("jointRunId").oninput = scheduleJointWeightStatus;
  for (const id of ["jointInitJack", "jointInitLily", "jointInitGeorge"]) {
    const inp = document.getElementById(id);
    if (inp) {
      inp.oninput = scheduleJointWeightStatus;
      inp.onchange = () => { saveCfg(); refreshJointWeightStatus(); };
    }
  }
  document.getElementById("streamRunId").onchange = () => {
    document.getElementById("jointRunId").value = document.getElementById("streamRunId").value;
    saveCfg();
    refreshJointWeightStatus();
  };
  document.getElementById("tab-joint-train").onclick = () => switchJointTab("panel-joint-train");
  document.getElementById("tab-joint-stream").onclick = () => switchJointTab("panel-joint-stream");
  document.getElementById("chart-joint").onclick = () => openJointDetail();
  loadJointChart();
}

function bindModeTabs() {
  const t = document.getElementById("tab-mode-training");
  const s = document.getElementById("tab-mode-streaming");
  const o = document.getElementById("tab-mode-obs");
  if (t) t.onclick = () => switchModeTab("training");
  if (s) s.onclick = () => switchModeTab("streaming");
  if (o) o.onclick = () => switchModeTab("obs");
  const bStart = document.getElementById("btnSsStart");
  const bStop = document.getElementById("btnSsStop");
  if (bStart) bStart.onclick = () => startStreamingSurvival();
  if (bStop) bStop.onclick = () => stopStreamingSurvival();
  bindSsDiagnostics();
  bindSsPreview();
}

let _ssPreviewTimer = null;
let _ssPreviewRunning = false;
let _ssPreviewWantFrame = false; // показывать кадр только после «Показать» / «1 кадр»

function bindSsPreview() {
  const startBtn = document.getElementById("btnSsPreviewStart");
  const stopBtn = document.getElementById("btnSsPreviewStop");
  const onceBtn = document.getElementById("btnSsPreviewOnce");
  if (startBtn) startBtn.onclick = () => ssPreviewStart();
  if (stopBtn) stopBtn.onclick = () => ssPreviewStop();
  if (onceBtn) onceBtn.onclick = () => ssPreviewOnce();
  // статус без показа старого frame.jpg
  ssPreviewPollStatus({ silent: true });
}

function ssPreviewHideFrame() {
  const img = document.getElementById("ssPreviewImg");
  const ph = document.getElementById("ssPreviewPlaceholder");
  if (img) {
    img.style.display = "none";
    img.removeAttribute("src");
  }
  if (ph) ph.style.display = "";
}

function ssPreviewShowFrame() {
  const img = document.getElementById("ssPreviewImg");
  const ph = document.getElementById("ssPreviewPlaceholder");
  if (!img) return;
  img.style.display = "block";
  if (ph) ph.style.display = "none";
  img.src = "/api/ss_preview/frame.jpg?t=" + Date.now();
}

function ssPreviewApplyStatus(st, opts) {
  opts = opts || {};
  _ssPreviewRunning = !!(st && st.running);
  if (opts.wantFrame === true) _ssPreviewWantFrame = true;
  if (opts.wantFrame === false) _ssPreviewWantFrame = false;
  // если live-поток выключен и это не явный once — не тащим старый кадр
  if (!_ssPreviewRunning && opts.silent) _ssPreviewWantFrame = false;

  setCardState("card-ss-preview", _ssPreviewRunning, false);
  const pill = document.getElementById("ssPreviewPill");
  const meta = document.getElementById("ssPreviewMeta");
  if (pill) {
    if (_ssPreviewRunning) {
      pill.textContent = "LIVE";
      pill.className = "pill live";
    } else if (_ssPreviewWantFrame) {
      pill.textContent = "1 кадр";
      pill.className = "pill live-stream";
    } else {
      pill.textContent = "off";
      pill.className = "pill";
    }
  }
  const modeSs = document.getElementById("tab-mode-streaming");
  if (modeSs) {
    const ssLive = !!(document.getElementById("card-ss") && document.getElementById("card-ss").classList.contains("running"));
    modeSs.classList.toggle("has-live", ssLive || _ssPreviewRunning);
  }
  if (meta) {
    const err = (st && st.last_error) ? (" · err: " + st.last_error) : "";
    const age = st && st.last_ok_at
      ? (" · last " + Math.max(0, Math.round(Date.now()/1000 - st.last_ok_at)) + "s ago")
      : "";
    if (_ssPreviewRunning) {
      meta.textContent = "live " + (st.display || ":1") + " · frames=" + ((st && st.frames) || 0) + age + err;
    } else if (_ssPreviewWantFrame) {
      meta.textContent = "показан 1 кадр · frames=" + ((st && st.frames) || 0) + age + err;
    } else {
      meta.textContent = "preview off — кадр скрыт · frames=" + ((st && st.frames) || 0) + err;
    }
  }
  if (_ssPreviewWantFrame && st && st.has_frame) ssPreviewShowFrame();
  else ssPreviewHideFrame();

  if (_ssPreviewRunning && !_ssPreviewTimer) {
    _ssPreviewTimer = setInterval(ssPreviewTick, 1600);
  }
  if (!_ssPreviewRunning && _ssPreviewTimer) {
    clearInterval(_ssPreviewTimer);
    _ssPreviewTimer = null;
  }
}

async function ssPreviewPollStatus(opts) {
  opts = opts || { silent: true };
  try {
    let st = await api("/api/ss_preview/status");
    // После F5 не продолжаем live сами — иначе снова крутит SSH/кадр.
    if (opts.silent && st && st.running) {
      try {
        st = await api("/api/ss_preview/stop", {
          method: "POST",
          headers: {"Content-Type":"application/json"},
          body: "{}",
        });
      } catch (_) {}
      ssPreviewApplyStatus(st, { wantFrame: false, silent: true });
      return;
    }
    ssPreviewApplyStatus(st, opts);
  } catch (_) {}
}

async function ssPreviewTick() {
  try {
    const st = await api("/api/ss_preview/status");
    ssPreviewApplyStatus(st, { wantFrame: true });
  } catch (e) {
    const meta = document.getElementById("ssPreviewMeta");
    if (meta) meta.textContent = "preview poll: " + (e.message || e);
  }
}

async function ssPreviewStart() {
  try {
    _ssPreviewWantFrame = true;
    const st = await api("/api/ss_preview/start", {
      method: "POST",
      headers: {"Content-Type":"application/json"},
      body: "{}",
    });
    ssPreviewApplyStatus(st, { wantFrame: true });
  } catch (e) { alert(e.message || e); }
}

async function ssPreviewStop() {
  try {
    const st = await api("/api/ss_preview/stop", {
      method: "POST",
      headers: {"Content-Type":"application/json"},
      body: "{}",
    });
    ssPreviewApplyStatus(st, { wantFrame: false });
  } catch (e) { alert(e.message || e); }
}

async function ssPreviewOnce() {
  try {
    const st = await api("/api/ss_preview/once", {
      method: "POST",
      headers: {"Content-Type":"application/json"},
      body: "{}",
    });
    ssPreviewApplyStatus(st, { wantFrame: true });
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
  schedulePresWorldPoll(false);
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
            try:
                stats["ssh_sessions"] = ssh_sessions_snapshot(stats.get("activity") or {})
            except Exception as e:
                stats["ssh_sessions"] = {
                    "local": 0,
                    "ui": 0,
                    "remote": None,
                    "total": 0,
                    "warn_at": _SSH_WARN_COUNT,
                    "bad_at": _SSH_BAD_COUNT,
                    "level": "ok",
                    "error": str(e),
                }
            self._json(200, stats)
            return

        if path == "/api/presentation_world":
            cfg = load_cfg()
            qs = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            rid = (qs.get("run_id") or [""])[0].strip()
            if not rid:
                rid = str(((cfg.get("joint") or {}).get("run_id") or "")).strip()
            try:
                if rid:
                    rid = sanitize_run_id(rid)
                host = _ssh_host_cache or resolve_ssh_host_fast() or (cfg.get("ssh_host") or "lab_comp")
                data = fetch_presentation_world(host, rid)
                self._json(200, data)
            except Exception as e:
                self._json(200, {"ok": False, "error": str(e), "run_id": rid})
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

        if path == "/api/joint/run_weights":
            cfg = load_cfg()
            # Не блокировать HTTP на полном resolve — берём кэш / fast probe.
            host = _ssh_host_cache or resolve_ssh_host_fast() or (cfg.get("ssh_host") or "lab_comp")
            if not host:
                self._json(200, {"ok": False, "error": "lab_comp offline", "items": {}})
                return
            try:
                payload = probe_joint_run_weights(
                    host,
                    {
                        "jack": (qs.get("jack") or [""])[0],
                        "lily": (qs.get("lily") or [""])[0],
                        "george": (qs.get("george") or [""])[0],
                        "run": (qs.get("run_id") or [""])[0],
                    },
                )
            except Exception as e:
                payload = {"ok": False, "error": str(e), "items": {}}
            # Всегда 200: UI рисует label/error, не общий catch «не удалось проверить»
            self._json(200, payload)
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
                if path == "/api/llm_bot/resync_roster":
                    self._json(200, LLM_BOT.resync_roster())
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
                elif action == "restart_obs":
                    jid = action_restart_obs(cfg)
                elif action == "start_obs_stream":
                    jid = action_start_obs_stream(cfg, str(body.get("run_id") or ""))
                elif action == "stop_obs_stream":
                    jid = action_stop_obs_stream(cfg)
                elif action == "repair_spawn":
                    force = body.get("force", True)
                    if isinstance(force, str):
                        force = force.strip().lower() not in ("0", "false", "no")
                    jid = action_repair_spawn(
                        cfg, str(body.get("run_id") or ""), force=bool(force)
                    )
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
    # подчистить зомби ssh.exe от прошлого UI до приёма запросов
    try:
        n = cleanup_orphan_batch_ssh(max_age=0.0)
        if n:
            print(f"[train_lab_ui] pruned {n} orphan BatchMode ssh → lab", flush=True)
    except Exception as e:
        print(f"[train_lab_ui] ssh prune: {e}", flush=True)
    _ensure_ssh_reaper()
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
