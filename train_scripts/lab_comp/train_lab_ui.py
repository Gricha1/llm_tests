#!/usr/bin/env python3
"""Локальный UI: билд+синк и обучение Jack/Lily/George на lab_comp.

Запуск (из корня репо, WSL):
  python3 train_scripts/lab_comp/train_lab_ui.py
  → http://127.0.0.1:8877
  (порт не 8765 — тот под другой UI; свой: FOREST_UI_PORT=....)
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

HOST = os.environ.get("FOREST_UI_HOST", "0.0.0.0")
PORT = int(os.environ.get("FOREST_UI_PORT", "8877"))
ROOT = Path(__file__).resolve().parents[2]
CFG_PATH = ROOT / ".train_lab_ui.json"
LOG_DIR = ROOT / ".train_lab_ui" / "logs"
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

# lab_comp в ssh-config часто = только ZeroTier; второй путь — 192.168.194.7.
SSH_FALLBACK_HOSTS = (
    "lab_comp",
    "reedgern@10.43.71.7",
    "reedgern@192.168.194.7",
    "lab_comp_local",
    "reedgern@192.168.50.18",
)


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
            [bin_, "-o", "BatchMode=yes", "-o", f"ConnectTimeout={connect_timeout}", host, "true"],
            capture_output=True,
            timeout=connect_timeout + 6,
        )
        return r.returncode == 0
    except Exception:
        return False


def clear_ssh_host_cache() -> None:
    global _ssh_host_cache
    _ssh_host_cache = None


def resolve_ssh_host(preferred: str = "lab_comp", *, force: bool = False) -> str:
    """Пробует alias + оба IP (ZT / 194). Не кэширует мёртвый хост."""
    global _ssh_host_cache
    if _ssh_host_cache and not force:
        return _ssh_host_cache
    for bin_ in _ssh_bins():
        for host in ssh_candidates(preferred):
            if _ssh_probe(bin_, host):
                _ssh_host_cache = host
                os.environ["FOREST_UI_SSH_BIN"] = bin_
                return host
    # Не кэшируем preferred при полном фейле — иначе второй IP больше не пробуется.
    raise RuntimeError(
        "SSH: не достучались ни до одного адреса lab_comp "
        f"({', '.join(ssh_candidates(preferred))})"
    )


def ssh_bin() -> str:
    return os.environ.get("FOREST_UI_SSH_BIN") or ("ssh.exe" if shutil.which("ssh.exe") else "ssh")


def scp_bin() -> str:
    return os.environ.get("FOREST_UI_SCP_BIN") or ("scp.exe" if shutil.which("scp.exe") else "scp")


def _is_ssh_connect_error(r: subprocess.CompletedProcess | None, exc: BaseException | None = None) -> bool:
    text = ""
    if r is not None:
        text = f"{r.stderr or ''}{r.stdout or ''}".lower()
    if exc is not None:
        text += f" {exc}".lower()
    keys = (
        "connection timed out",
        "connection refused",
        "no route to host",
        "network is unreachable",
        "could not resolve hostname",
        "connection reset by peer",
        "connection closed by remote host",
    )
    return any(k in text for k in keys)


def ssh_run(
    host: str | None,
    remote_cmd: str,
    timeout: float | None = 120,
    *,
    preferred: str | None = None,
) -> subprocess.CompletedProcess:
    """SSH с failover: при timeout/refusal пробует остальные IP lab_comp."""
    global _ssh_host_cache
    preferred = preferred or "lab_comp"
    try:
        preferred = (load_cfg().get("ssh_host") or preferred)
    except Exception:
        pass
    hosts: list[str] = []
    if host:
        hosts.append(host)
    for h in ssh_candidates(preferred):
        if h not in hosts:
            hosts.append(h)

    last: subprocess.CompletedProcess | None = None
    last_exc: BaseException | None = None
    tried: list[str] = []
    for h in hosts:
        tried.append(h)
        try:
            r = subprocess.run(
                [ssh_bin(), "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", h, remote_cmd],
                capture_output=True,
                text=True,
                timeout=timeout,
                encoding="utf-8",
                errors="replace",
            )
            last = r
            if r.returncode == 0:
                _ssh_host_cache = h
                return r
            if not _is_ssh_connect_error(r):
                # Команда на сервере упала — хост живой, не крутим IP.
                _ssh_host_cache = h
                return r
            clear_ssh_host_cache()
        except Exception as e:
            last_exc = e
            if not _is_ssh_connect_error(None, e):
                raise
            clear_ssh_host_cache()
            continue

    err = (last.stderr or last.stdout or "") if last else str(last_exc or "ssh failed")
    raise RuntimeError(f"[ssh tried {', '.join(tried)}] {err.strip()}")


def sh_quote(s: str) -> str:
    return "'" + s.replace("'", "'\"'\"'") + "'"


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


def fetch_lab_activity(host: str | None = None) -> dict[str, Any]:
    """Что сейчас крутится на lab: train/validate/stream (для подсветки карточек)."""
    # Важно: в remote-скрипте нельзя писать маркер целиком (_ui + _val_), иначе
    # bash -c сам матчится как «validate» и карточка мигает ложно.
    remote = r"""
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
    # tail логов UI-слотов и сам ssh-probe activity
    if "forest_ui_fui_" in cl and ("tail" in cl or "tmux" in cl):
        return True
    if "os.listdir(\"/proc\")" in c or "fetch_lab_activity" in c:
        return True
    return False

def train_yaml(name):
    # Только живой mlagents-learn + yaml (не tail/fui_* и не --inference).
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
    # Один процесс mlagents на троих. Не матчить tail /tmp/forest_ui_fui_joint_*.log
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

# Маркер без цельной строки в исходнике (иначе self-match через ssh cmdline).
ui_val = "_" + "ui_val_"
validate = any(
    (not is_ui_noise(c)) and (("validate_ui_one.bash" in c) or ("forestValidate" in c) or (ui_val in c))
    for c in cmds
)
stream = any(
    (not is_ui_noise(c)) and (("forestStreamOnly" in c) or ("stream_onnx_infer.py" in c))
    for c in cmds
)
joint = is_joint_train()
# Solo: только живой mlagents + свой yaml (fui_* tail больше не считаем train).
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

print(json.dumps({
    "jack": bool(jack),
    "lily": bool(lily),
    "george": bool(george),
    "joint": bool(joint),
    "validate": bool(validate),
    "stream": bool(stream),
    "tasks": tasks,
}))
PY
"""
    empty = {
        "jack": False,
        "lily": False,
        "george": False,
        "joint": False,
        "validate": False,
        "stream": False,
        "tasks": [],
    }
    try:
        r = ssh_run(host, remote, timeout=35)
        text = (r.stdout or "").strip().splitlines()
        raw = text[-1] if text else ""
        data = json.loads(raw) if raw.startswith("{") else empty
        for k in empty:
            if k == "tasks":
                data["tasks"] = list(data.get("tasks") or [])
            else:
                data[k] = bool(data.get(k))
        data["ok"] = True
        return data
    except Exception as e:
        out = dict(empty)
        out["ok"] = False
        out["error"] = str(e)
        return out


def fetch_server_stats(host: str | None = None) -> dict[str, Any]:
    """RAM + nvidia-smi на lab_comp (короткий ssh, failover по IP)."""
    remote = r"""
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
"""
    try:
        r = ssh_run(host, remote, timeout=40)
        used = _ssh_host_cache or host or "?"
        text = ((r.stdout or "") + (r.stderr or "")).strip()
        out = {"ok": r.returncode == 0 and bool(text), "text": text or "(пусто)", "host": used}
        out["activity"] = fetch_lab_activity(used)
        return out
    except Exception as e:
        clear_ssh_host_cache()
        return {
            "ok": False,
            "text": f"[ssh] {e}",
            "host": host or "?",
            "activity": fetch_lab_activity(host),
        }


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
<title>Forest Lab Train UI</title>
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
</style>
</head>
<body>
<header>
  <h1>Forest Lab Train</h1>
  <span class="meta" id="hdrMeta">старт…</span>
  <span class="meta">UI локальный · train/stats только через ssh lab_comp</span>
  <div style="margin-left:auto" class="btns">
    <button id="btnRefreshTb">Обновить TB</button>
    <button class="danger" id="btnKill" title="Только train: mlagents + headless Unity (стрим/validate не трогает)">⏹ Стоп train</button>
  </div>
</header>
<main>
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
    <section class="card">
      <h2>Сервер <code>lab_comp</code> (только SSH) <span class="pill" id="serverPill">—</span></h2>
      <p class="hint" id="serverHostHint">RAM / nvidia-smi снимаются по ssh, не с твоего ПК.</p>
      <pre class="server-pre" id="serverStats">загрузка RAM / nvidia-smi с lab_comp…</pre>
      <h2 style="margin-top:10px;font-size:14px">Сейчас на lab <span class="pill" id="labTasksPill">—</span></h2>
      <div class="lab-tasks" id="labTasks"><div class="lab-tasks-empty">нет активных задач</div></div>
      <p class="hint">× гасит только этот тип процесса (train / validate / stream). OBS не трогает.</p>
      <h2 style="margin-top:8px">Лог действий <span class="pill" id="jobPill">—</span></h2>
      <div class="field">
        <label>Активная задача</label>
        <select id="jobSelect"></select>
      </div>
      <div class="log" id="jobLog">нет задач</div>
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
  const r = await fetch(path, opts);
  const j = await r.json();
  if (!r.ok) throw new Error(j.error || r.statusText);
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
  for (const btn of document.querySelectorAll(".subtab")) {
    const on = btn.dataset.panel === panelId;
    btn.classList.toggle("active", on);
  }
  for (const panel of document.querySelectorAll(".subpanel")) {
    panel.classList.toggle("active", panel.id === panelId);
  }
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
    const tried = (s.tried || []).join(" → ");
    document.getElementById("serverHostHint").textContent =
      s.ok
        ? `host=${host} · данные только по SSH (не локальный ПК)`
        : `ssh fail · пробовали: ${tried || host}`;
    document.getElementById("serverStats").textContent =
      `[ssh ${host}]\n` + (s.text || "(пусто)");
    document.getElementById("serverPill").textContent = s.ok ? "ssh ok" : "ssh err";
    document.getElementById("serverPill").className = "pill" + (s.ok ? " on" : "");
    applyLabActivity(s.activity || {});
  } catch (e) {
    document.getElementById("serverStats").textContent = "server_stats (ssh lab_comp): " + (e.message||e);
    document.getElementById("serverPill").textContent = "err";
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
  const jack = !!a.jack, lily = !!a.lily, george = !!a.george;
  const joint = !!a.joint, validate = !!a.validate, stream = !!a.stream;
  setCardState("card-jack", jack, false);
  setCardState("card-lily", lily, false);
  setCardState("card-george", george, false);
  setCardState("card-validate", validate, false);
  // Обёртка с вкладками: красный = train, синий = stream.
  setCardState("card-joint-wrap", joint, stream && !joint);
  if (joint && stream) setCardState("card-joint-wrap", true, true);

  const tabTrain = document.getElementById("tab-joint-train");
  const tabStream = document.getElementById("tab-joint-stream");
  if (tabTrain) tabTrain.classList.toggle("has-live", joint);
  if (tabStream) tabStream.classList.toggle("has-live", stream);

  setPillLive("pill-jack", jack, false, null);
  setPillLive("pill-lily", lily, false, null);
  setPillLive("pill-george", george, false, null);
  setPillLive("pill-joint", joint, false, "finetune");
  setPillLive("pill-stream", stream, false, "idle");

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
  const map = {
    train: { action: "kill_train", ask: "Остановить train на lab (mlagents + headless)? Стрим/validate не трогаем." },
    validate: { action: "kill_validate", ask: "Остановить только validate (mp4)?" },
    stream: { action: "kill_stream", ask: "Остановить только бесконечный стрим Presentation? OBS не гасим." },
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
        return json.loads(self.rfile.read(n).decode("utf-8"))

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

        if path == "/api/config":
            self._json(200, {"config": load_cfg()})
            return

        if path == "/api/server_stats":
            cfg = load_cfg()
            # Всегда remote lab_comp — никогда не читать free/nvidia-smi с машины UI.
            # ssh_run сам перебирает ZT + 194 при timeout.
            try:
                host = resolve_ssh_host(cfg.get("ssh_host") or "lab_comp")
            except Exception:
                host = None
            stats = fetch_server_stats(host)
            stats["note"] = "stats via ssh only; not local PC"
            stats["tried"] = ssh_candidates(cfg.get("ssh_host") or "lab_comp")
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
    # SSH resolve в фоне — иначе старт UI ждёт таймауты.
    threading.Thread(
        target=lambda: resolve_ssh_host(load_cfg().get("ssh_host") or "lab_comp"),
        daemon=True,
    ).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n[train_lab_ui] stop UI only — remote nohup trains keep running", flush=True)


if __name__ == "__main__":
    main()
