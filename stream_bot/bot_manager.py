"""BotManager: setup + lifecycle stream_bot для Train Lab UI (localhost:8877).

Не для Unity. Запускает venv/pip/ollama/model и subprocess `python -m stream_bot.main`.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from collections import deque
from pathlib import Path
from typing import Any, Deque, Dict, List, Optional

ROOT = Path(__file__).resolve().parent.parent
BOT_DIR = Path(__file__).resolve().parent
ENV_PATH = BOT_DIR / ".env"
ENV_EXAMPLE = BOT_DIR / ".env.example"
VENV_DIR = BOT_DIR / ".venv"
DEPS_DIR = BOT_DIR / ".pydeps"
REQ_PATH = BOT_DIR / "requirements.txt"
BOT_HTTP = os.environ.get("FOREST_BOT_HTTP", "http://127.0.0.1:8765").rstrip("/")
OLLAMA_MODEL = os.environ.get("OLLAMA_MODEL", "qwen3:4b")


def _venv_python() -> Path:
    if os.name == "nt":
        return VENV_DIR / "Scripts" / "python.exe"
    return VENV_DIR / "bin" / "python"


def _find_windows_python() -> Optional[str]:
    """Из WSL: нормальный Python с pip на Windows."""
    candidates = [
        shutil.which("python.exe"),
        "/mnt/c/Users/User/AppData/Local/Programs/Python/Python312/python.exe",
        "/mnt/c/Users/User/AppData/Local/Programs/Python/Python311/python.exe",
        "/mnt/c/Users/User/AppData/Local/Programs/Python/Python310/python.exe",
    ]
    # любой Python*/python.exe под Local/Programs/Python
    prog = Path("/mnt/c/Users/User/AppData/Local/Programs/Python")
    if prog.is_dir():
        for p in sorted(prog.glob("Python*/python.exe"), reverse=True):
            candidates.append(str(p))
    seen = set()
    for c in candidates:
        if not c or c in seen:
            continue
        seen.add(c)
        if not Path(c).exists():
            continue
        try:
            subprocess.check_output(
                [c, "-c", "import pip, sys; print(sys.executable)"],
                stderr=subprocess.STDOUT,
                timeout=15,
            )
            return c
        except Exception:
            continue
    return None


def _python_has_pip(py: str) -> bool:
    try:
        subprocess.check_output([py, "-m", "pip", "--version"], stderr=subprocess.STDOUT, timeout=15)
        return True
    except Exception:
        return False


class BotManager:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self.bot_state = "stopped"  # stopped|starting|running|error
        self.listen_stream = False
        self.python_deps = "unknown"  # unknown|installing|ready|error
        self.ollama = "unknown"  # unknown|checking|running|missing|error
        self.model = "unknown"  # missing|pulling|ready|error|unknown
        self.bot_http_ready = False
        self.last_error: Optional[str] = None
        self._logs: Deque[str] = deque(maxlen=400)
        self._chat: List[Dict[str, str]] = []
        self._proc: Optional[subprocess.Popen] = None
        self._reader: Optional[threading.Thread] = None
        self._start_thread: Optional[threading.Thread] = None
        self._python: str = sys.executable  # venv or system
        self._use_target_deps: bool = False  # pip --target .pydeps

    def _log(self, line: str) -> None:
        ts = time.strftime("%H:%M:%S")
        msg = f"[{ts}] {line}"
        with self._lock:
            self._logs.append(msg)

    def _chat_add(self, role: str, text: str) -> None:
        with self._lock:
            self._chat.append({"role": role, "text": text, "ts": time.strftime("%H:%M:%S")})
            if len(self._chat) > 200:
                self._chat = self._chat[-200:]

    def status(self) -> Dict[str, Any]:
        with self._lock:
            # refresh http ready
            ready = self._probe_health_unlocked()
            self.bot_http_ready = ready
            if ready and self.bot_state in ("starting", "stopped"):
                self.bot_state = "running"
            if self._proc is not None and self._proc.poll() is not None and self.bot_state == "running":
                if not ready:
                    self.bot_state = "error"
                    self.last_error = f"bot process exited code={self._proc.returncode}"
            return {
                "bot_state": self.bot_state,
                "listen_stream": self.listen_stream,
                "python_deps": self.python_deps,
                "ollama": self.ollama,
                "model": self.model,
                "bot_http_ready": self.bot_http_ready,
                "last_error": self.last_error,
                "ollama_model": OLLAMA_MODEL,
                "logs": list(self._logs)[-120:],
                "chat": list(self._chat)[-80:],
            }

    def clear_chat(self) -> Dict[str, Any]:
        with self._lock:
            self._chat.clear()
        return {"ok": True, "chat": []}

    def start(self) -> Dict[str, Any]:
        with self._lock:
            if self.bot_state == "starting":
                return self.status()
            if self.bot_state == "running" and self._probe_health_unlocked():
                return self.status()
            self.bot_state = "starting"
            self.last_error = None
        self._log("Start Bot requested")
        t = threading.Thread(target=self._start_worker, name="llm-bot-start", daemon=True)
        self._start_thread = t
        t.start()
        return self.status()

    def stop(self) -> Dict[str, Any]:
        self._log("Stop Bot requested")
        # graceful
        try:
            self._http_json("POST", "/shutdown", {}, timeout=5)
            self._log("POST /shutdown ok")
            time.sleep(0.8)
        except Exception as e:
            self._log(f"shutdown HTTP: {e}")
        with self._lock:
            proc = self._proc
            self._proc = None
        if proc is not None and proc.poll() is None:
            try:
                proc.terminate()
                try:
                    proc.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    proc.kill()
                self._log("subprocess killed")
            except Exception as e:
                self._log(f"kill failed: {e}")
        with self._lock:
            self.bot_state = "stopped"
            self.bot_http_ready = False
        self._chat_add("system", "bot stopped")
        return self.status()

    def set_mode(self, listen_stream: bool) -> Dict[str, Any]:
        try:
            data = self._http_json("POST", "/mode", {"listen_stream": bool(listen_stream)}, timeout=10)
            with self._lock:
                self.listen_stream = bool(listen_stream)
            mode = "Twitch stream" if listen_stream else "Local debug"
            self._log(f"mode → {mode}")
            self._chat_add("system", f"Mode: {mode}")
            return {"ok": True, "listen_stream": self.listen_stream, "bot": data, "status": self.status()}
        except Exception as e:
            self.last_error = str(e)
            self._log(f"mode error: {e}")
            return {"ok": False, "error": str(e), "status": self.status()}

    def local_chat(self, username: str, message: str) -> Dict[str, Any]:
        user = (username or "debug_user").strip() or "debug_user"
        msg = message or ""
        self._chat_add(user, msg)
        self._log(f"local_chat {user}: {msg[:160]}")
        try:
            data = self._http_json(
                "POST",
                "/local_chat",
                {"username": user, "message": msg},
                timeout=120,
            )
            reply = str((data or {}).get("chat_reply") or "").strip()
            if reply:
                self._chat_add("bot", reply)
            events = (data or {}).get("events") or []
            for ev in events:
                m = ev.get("message") if isinstance(ev, dict) else None
                if m and m != reply:
                    self._chat_add("system", str(m))
            cmds = (data or {}).get("unity_commands") or []
            for c in cmds:
                if isinstance(c, dict) and c.get("type") == "streaming_survival_action":
                    self._chat_add(
                        "system",
                        f"action → Unity: {c.get('action')} / {c.get('action_name')}",
                    )
                elif isinstance(c, dict) and c.get("type") == "streaming_survival_user_joined":
                    self._chat_add("system", f"join → Unity: {c.get('username')}")
                elif isinstance(c, dict) and c.get("type") == "streaming_survival_user_left":
                    self._chat_add("system", f"leave → Unity: {c.get('username')}")
                elif isinstance(c, dict) and c.get("type") == "streaming_survival_users_sync":
                    n = len(c.get("users") or [])
                    self._chat_add("system", f"users_sync → Unity ({n})")
                elif isinstance(c, dict) and c.get("type") == "stream_command":
                    self._chat_add("system", f"stream_command → Unity: {c.get('command')}")
            summary = (data or {}).get("system_summary")
            if summary:
                self._chat_add("system", str(summary))
            if (data or {}).get("sent_to_unity"):
                self._chat_add("system", "sent to Unity")
            if not (data or {}).get("ok", True):
                err = reply or "local_chat failed"
                self.last_error = err
                self._chat_add("system", f"error: {err}")
            return {"ok": True, "result": data, "status": self.status()}
        except Exception as e:
            self.last_error = str(e)
            self._log(f"local_chat error: {e}")
            self._chat_add("system", f"error: {e}")
            return {"ok": False, "error": str(e), "status": self.status()}

    # ── start pipeline ──────────────────────────────────────────────

    def _start_worker(self) -> None:
        try:
            self._ensure_env()
            self._ensure_venv()
            self._pip_install()
            self._check_ollama()
            self._ensure_model()
            self._spawn_bot()
            if not self._wait_health(timeout=45):
                raise RuntimeError("stream_bot не ответил на /health за 45с")
            with self._lock:
                self.bot_state = "running"
                self.bot_http_ready = True
                self.listen_stream = False
            # force local debug on start
            try:
                self._http_json("POST", "/mode", {"listen_stream": False}, timeout=5)
            except Exception:
                pass
            self._chat_add("system", "bot running · Local debug")
            self._log("Start Bot OK")
        except Exception as e:
            self.last_error = str(e)
            with self._lock:
                self.bot_state = "error"
            self._log(f"Start Bot FAILED: {e}")
            self._chat_add("system", f"error: {e}")

    def _ensure_env(self) -> None:
        if ENV_PATH.exists():
            self._log(f".env exists: {ENV_PATH}")
            return
        if ENV_EXAMPLE.exists():
            ENV_PATH.write_text(ENV_EXAMPLE.read_text(encoding="utf-8"), encoding="utf-8")
            self._log(f"created .env from .env.example")
        else:
            ENV_PATH.write_text(
                "\n".join(
                    [
                        "TWITCH_BOT_NICK=",
                        "TWITCH_OAUTH=",
                        "TWITCH_CHANNEL=",
                        "UNITY_HOST=127.0.0.1",
                        "UNITY_PORT=5055",
                        "USE_LOCAL_LLM=true",
                        f"OLLAMA_MODEL={OLLAMA_MODEL}",
                        "BOT_HTTP_HOST=127.0.0.1",
                        "BOT_HTTP_PORT=8765",
                        "LISTEN_STREAM_ON_START=false",
                        "",
                    ]
                ),
                encoding="utf-8",
            )
            self._log("created minimal .env")

    def _ensure_venv(self) -> None:
        """Создать venv; при отсутствии python3-venv/pip — Windows Python или .pydeps."""
        py = _venv_python()
        if py.exists() and os.access(py, os.X_OK) and _python_has_pip(str(py)):
            self._python = str(py)
            self._use_target_deps = False
            self._log(f"venv ready: {py}")
            return

        if py.exists():
            self._log("venv broken (no pip) — removing")
            shutil.rmtree(VENV_DIR, ignore_errors=True)

        self._log(f"creating venv {VENV_DIR}")
        proc = subprocess.run(
            [sys.executable, "-m", "venv", str(VENV_DIR)],
            cwd=str(ROOT),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        if proc.returncode == 0 and _venv_python().exists() and _python_has_pip(str(_venv_python())):
            self._python = str(_venv_python())
            self._use_target_deps = False
            self._log(f"venv ok: {self._python}")
            return

        err = ((proc.stdout or "") + (proc.stderr or "")).strip()
        self._log(f"venv failed (exit {proc.returncode}): {err[:500]}")
        shutil.rmtree(VENV_DIR, ignore_errors=True)

        # Prefer Windows Python (has pip) when Train Lab UI runs under WSL
        win_py = _find_windows_python()
        if win_py:
            self._python = win_py
            self._use_target_deps = False
            # отдельный venv рядом через Windows python
            win_venv = BOT_DIR / ".venv_win"
            win_py_out = win_venv / "Scripts" / "python.exe"
            if win_py_out.exists() and _python_has_pip(str(win_py_out)):
                self._python = str(win_py_out)
                self._log(f"using Windows venv: {self._python}")
                return
            self._log(f"creating Windows venv via {win_py}")
            p2 = subprocess.run(
                [win_py, "-m", "venv", str(win_venv)],
                cwd=str(ROOT),
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
            )
            if p2.returncode == 0 and win_py_out.exists() and _python_has_pip(str(win_py_out)):
                self._python = str(win_py_out)
                self._log(f"Windows venv ok: {self._python}")
                return
            self._log(f"Windows venv failed — using Windows python directly: {win_py}")
            self._python = win_py
            self._use_target_deps = False
            return

        # Последний шанс: system python + --target (нужен pip)
        self._python = sys.executable
        self._use_target_deps = True
        DEPS_DIR.mkdir(parents=True, exist_ok=True)
        if not _python_has_pip(self._python):
            self._bootstrap_pip_target()
        self._log(
            f"fallback: system python {self._python} + pip --target {DEPS_DIR} "
            "(лучше: sudo apt install python3-venv python3-pip)"
        )

    def _bootstrap_pip_target(self) -> None:
        """Скачать get-pip.py и поставить pip в .pydeps без sudo."""
        import urllib.request as ur

        get_pip = BOT_DIR / "_get_pip.py"
        self._log("downloading get-pip.py …")
        ur.urlretrieve("https://bootstrap.pypa.io/get-pip.py", str(get_pip))
        self._run_logged(
            [self._python, str(get_pip), "--target", str(DEPS_DIR)],
            cwd=ROOT,
        )
        # чтобы `python -m pip` видел pip из .pydeps
        env_pip = str(DEPS_DIR)
        self._log(f"get-pip into {env_pip} done")

    def _pip_install(self) -> None:
        with self._lock:
            self.python_deps = "installing"
        py = self._python
        self._log("pip install -r stream_bot/requirements.txt …")
        if self._use_target_deps:
            cmd = [
                py, "-m", "pip", "install", "--upgrade",
                "-r", str(REQ_PATH),
                "--target", str(DEPS_DIR),
            ]
        else:
            cmd = [py, "-m", "pip", "install", "-r", str(REQ_PATH)]
        try:
            self._run_logged(cmd, cwd=ROOT)
            with self._lock:
                self.python_deps = "ready"
            self._log("pip deps ready")
        except Exception as e:
            # иногда pip модуль тоже отсутствует у system python
            if "No module named pip" in str(e) or "No module named 'pip'" in str(e):
                self._log("pip missing — trying ensurepip / get-pip fallback…")
                try:
                    self._run_logged([py, "-m", "ensurepip", "--upgrade"], cwd=ROOT)
                except Exception as e2:
                    self._log(f"ensurepip failed: {e2}")
                try:
                    self._run_logged(cmd, cwd=ROOT)
                    with self._lock:
                        self.python_deps = "ready"
                    self._log("pip deps ready (after ensurepip)")
                    return
                except Exception as e3:
                    with self._lock:
                        self.python_deps = "error"
                    raise RuntimeError(f"pip install failed: {e3}") from e3
            with self._lock:
                self.python_deps = "error"
            raise RuntimeError(f"pip install failed: {e}") from e

    def _bot_env(self) -> Dict[str, str]:
        env = os.environ.copy()
        env["LISTEN_STREAM_ON_START"] = "false"
        env["USE_LOCAL_LLM"] = env.get("USE_LOCAL_LLM", "true")
        env["BOT_HTTP_HOST"] = "127.0.0.1"
        env["BOT_HTTP_PORT"] = "8765"
        env["OLLAMA_MODEL"] = OLLAMA_MODEL
        # PYTHONPATH: repo root + optional .pydeps
        parts = [str(ROOT)]
        if self._use_target_deps and DEPS_DIR.exists():
            parts.insert(0, str(DEPS_DIR))
        prev = env.get("PYTHONPATH", "")
        env["PYTHONPATH"] = os.pathsep.join(parts + ([prev] if prev else []))
        return env

    def _spawn_bot(self) -> None:
        # stop previous
        if self._proc is not None and self._proc.poll() is None:
            try:
                self._proc.terminate()
            except Exception:
                pass
        py = self._python
        env = self._bot_env()
        self._log(f"spawn: {py} -m stream_bot.main")
        self._log(f"PYTHONPATH={env.get('PYTHONPATH')}")
        proc = subprocess.Popen(
            [py, "-m", "stream_bot.main"],
            cwd=str(ROOT),
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        with self._lock:
            self._proc = proc
        self._reader = threading.Thread(target=self._read_stdout, args=(proc,), daemon=True)
        self._reader.start()

    def _check_ollama(self) -> None:
        with self._lock:
            self.ollama = "checking"
        ollama = shutil.which("ollama") or shutil.which("ollama.exe")
        if not ollama:
            with self._lock:
                self.ollama = "missing"
            # optional winget on Windows
            if os.name == "nt" and shutil.which("winget"):
                self._log("Ollama not found — trying winget install Ollama.Ollama …")
                try:
                    self._run_logged(
                        ["winget", "install", "-e", "--id", "Ollama.Ollama", "--accept-package-agreements", "--accept-source-agreements"],
                        cwd=ROOT,
                    )
                    ollama = shutil.which("ollama") or shutil.which("ollama.exe")
                except Exception as e:
                    self._log(f"winget install failed: {e}")
            if not ollama:
                with self._lock:
                    self.ollama = "missing"
                raise RuntimeError(
                    "Ollama is not installed. Install Ollama from https://ollama.com "
                    "or enable manual fallback (USE_LOCAL_LLM=false in stream_bot/.env)."
                )
        try:
            out = subprocess.check_output([ollama, "--version"], text=True, stderr=subprocess.STDOUT, timeout=15)
            self._log(f"ollama: {out.strip()}")
            with self._lock:
                self.ollama = "running"
        except Exception as e:
            with self._lock:
                self.ollama = "error"
            raise RuntimeError(f"ollama --version failed: {e}") from e

    def _ensure_model(self) -> None:
        ollama = shutil.which("ollama") or shutil.which("ollama.exe")
        if not ollama:
            with self._lock:
                self.model = "error"
            raise RuntimeError("ollama binary missing")
        try:
            listed = subprocess.check_output([ollama, "list"], text=True, stderr=subprocess.STDOUT, timeout=30)
        except Exception as e:
            with self._lock:
                self.model = "error"
            raise RuntimeError(f"ollama list failed: {e}") from e
        self._log("ollama list:\n" + listed.strip())
        # строка вида "qwen3:4b    …"
        if any(ln.split()[0] == OLLAMA_MODEL for ln in listed.splitlines() if ln.strip() and not ln.lower().startswith("name")):
            with self._lock:
                self.model = "ready"
            self._log(f"model ready: {OLLAMA_MODEL}")
            return

        with self._lock:
            self.model = "pulling"
        self._log(f"ollama pull {OLLAMA_MODEL} …")
        try:
            self._run_logged([ollama, "pull", OLLAMA_MODEL], cwd=ROOT)
            with self._lock:
                self.model = "ready"
            self._log(f"model pulled: {OLLAMA_MODEL}")
        except Exception as e:
            with self._lock:
                self.model = "error"
            raise RuntimeError(f"ollama pull failed: {e}") from e

    def _read_stdout(self, proc: subprocess.Popen) -> None:
        assert proc.stdout is not None
        for line in proc.stdout:
            line = line.rstrip("\n")
            if line:
                self._log(line)

    def _wait_health(self, timeout: float = 45) -> bool:
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self._probe_health_unlocked():
                return True
            if self._proc is not None and self._proc.poll() is not None:
                return False
            time.sleep(0.5)
        return False

    def _probe_health_unlocked(self) -> bool:
        try:
            with urllib.request.urlopen(BOT_HTTP + "/health", timeout=2) as r:
                return r.status == 200
        except Exception:
            return False

    def _http_json(self, method: str, path: str, body: Optional[dict], timeout: float = 30) -> Any:
        data = None
        headers = {}
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(BOT_HTTP + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                raw = r.read().decode("utf-8", errors="replace")
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"HTTP {e.code} {path}: {raw[:300]}") from e

    def _run_logged(self, cmd: List[str], cwd: Path) -> None:
        self._log("$ " + " ".join(cmd))
        p = subprocess.Popen(
            cmd,
            cwd=str(cwd),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        assert p.stdout is not None
        for line in p.stdout:
            line = line.rstrip("\n")
            if line:
                self._log(line)
        code = p.wait()
        if code != 0:
            raise RuntimeError(f"exit {code}: {' '.join(cmd)}")


# singleton for train_lab_ui
MANAGER = BotManager()
