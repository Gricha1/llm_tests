"""LLM Bot на lab_comp через SSH (не на локальном ПК).

Train Lab UI → SSH lab → stream_bot FastAPI :8765 → Unity UDP на lab.

Важно: status()/start()/local_chat не должны блокировать HTTP UI.
"""

from __future__ import annotations

import json
import threading
import time
from collections import deque
from typing import Any, Callable, Deque, Dict, List, Optional

OLLAMA_MODEL_DEFAULT = "qwen3:4b"
REMOTE_BOT_HTTP = "http://127.0.0.1:8765"


class LabBotManager:
    def __init__(
        self,
        *,
        ssh_run: Callable[..., Any],
        resolve_ssh_host: Callable[..., str],
        load_cfg: Callable[[], dict],
        remote_dir: str,
        sh_quote: Callable[[str], str],
    ) -> None:
        self._ssh_run = ssh_run
        self._resolve = resolve_ssh_host
        self._load_cfg = load_cfg
        self._remote_dir = remote_dir
        self._q = sh_quote
        # RLock: иначе _log/_snapshot внутри with self._lock → вечный deadlock UI
        self._lock = threading.RLock()
        self.bot_state = "stopped"
        self.listen_stream = False
        self.python_deps = "unknown"
        self.ollama = "unknown"
        self.model = "unknown"
        self.bot_http_ready = False
        self.last_error: Optional[str] = None
        self.roster: List[Dict[str, Any]] = []
        self.roster_count = 0
        self.players: List[Dict[str, Any]] = []
        self.players_count = 0
        self._logs: Deque[str] = deque(maxlen=500)
        self._chat: List[Dict[str, str]] = []
        self._host: Optional[str] = None
        self._start_thread: Optional[threading.Thread] = None
        self._probe_thread: Optional[threading.Thread] = None
        self._probe_stop = threading.Event()
        self._last_probe = 0.0
        self._ensure_probe_loop()

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

    def _host_now(self) -> str:
        cfg = self._load_cfg()
        # предпочитаем уже известный хост — без долгого resolve в горячем пути
        if self._host:
            return self._host
        host = self._resolve(cfg.get("ssh_host") or "lab_comp")
        self._host = host
        return host

    def _ssh(self, remote: str, timeout: float = 120, **kwargs: Any) -> Any:
        host = self._host or self._host_now()
        return self._ssh_run(host, remote, timeout=timeout, **kwargs)

    def _parse_setup_markers(self, text: str) -> None:
        low = text or ""
        if "python_deps=ready" in low:
            self.python_deps = "ready"
        if "ollama=running" in low:
            self.ollama = "running"
        if "ollama=missing" in low:
            self.ollama = "missing"
        if "model=ready" in low:
            self.model = "ready"
        if "model=pulling" in low:
            self.model = "pulling"

    def _snapshot(self) -> Dict[str, Any]:
        # hardcoded — не зависеть от PYTHONPATH / пустого []
        acts = [
            {"action": "collect_water", "hint": "добывай воду"},
            {"action": "collect_wood", "hint": "руби дерево"},
            {"action": "collect_food", "hint": "собирай еду"},
            {"action": "kill_sheep", "hint": "убивай овечек"},
            {"action": "build_campfire", "hint": "поставь костёр"},
            {"action": "go_home", "hint": "иди к дому"},
            {"action": "idle", "hint": "жди"},
        ]
        try:
            from stream_bot.command_parser import AVAILABLE_ACTIONS
            if AVAILABLE_ACTIONS:
                acts = [{"action": a, "hint": h} for a, h in AVAILABLE_ACTIONS]
        except Exception:
            pass
        with self._lock:
            err = self.last_error
            # Transient SSH probe noise must not override a healthy bot status in UI.
            if (
                self.bot_http_ready
                and self.bot_state == "running"
                and err
                and str(err).startswith("ssh status:")
            ):
                err = None
            return {
                "bot_state": self.bot_state,
                "listen_stream": self.listen_stream,
                "python_deps": self.python_deps,
                "ollama": self.ollama,
                "model": self.model,
                "bot_http_ready": self.bot_http_ready,
                "last_error": err,
                "ollama_model": OLLAMA_MODEL_DEFAULT,
                "host": self._host or "lab_comp",
                "available_actions": acts,
                "roster": list(self.roster),
                "roster_count": int(self.roster_count),
                "players": list(self.players),
                "players_count": int(self.players_count),
                "logs": list(self._logs)[-120:],
                "chat": list(self._chat)[-80:],
            }

    def is_live(self) -> bool:
        with self._lock:
            return self.bot_state in ("running", "starting") or bool(self.bot_http_ready)

    def _ensure_probe_loop(self) -> None:
        if self._probe_thread and self._probe_thread.is_alive():
            return

        def loop() -> None:
            while not self._probe_stop.is_set():
                try:
                    with self._lock:
                        st = self.bot_state
                        ready = self.bot_http_ready
                        last = self._last_probe
                    # Healthy bot: rare keepalive only (don't fight stats SSH every 12s).
                    if ready and st == "running":
                        if time.time() - last >= 60.0:
                            self._probe_once()
                    elif st in ("running", "starting") or ready:
                        self._probe_once()
                    elif time.time() - last > 30:
                        self._probe_once()
                except Exception:
                    pass
                self._probe_stop.wait(12.0)

        self._probe_thread = threading.Thread(target=loop, name="lab-llm-bot-probe", daemon=True)
        self._probe_thread.start()

    def _probe_once(self) -> None:
        self._last_probe = time.time()
        with self._lock:
            already_ready = bool(self.bot_http_ready and self.bot_state == "running")
        try:
            r = self._ssh(
                f"curl -s -m 2 {REMOTE_BOT_HTTP}/health 2>/dev/null || echo down",
                timeout=10,
                gate_timeout=1.0,
                skip_if_busy=True,
            )
            ready = '"ok"' in ((r.stdout or "") + (r.stderr or ""))
            with self._lock:
                prev = self.bot_state
                self.bot_http_ready = ready
                if ready:
                    # не перебивать явную остановку probe'ом
                    if prev != "stopping":
                        self.bot_state = "running"
                    if self.last_error and str(self.last_error).startswith("ssh status:"):
                        self.last_error = None
            if ready and prev != "running" and prev != "stopping":
                try:
                    st = self._curl_json("GET", "/status", None, timeout=5)
                    if isinstance(st, dict) and "listen_stream" in st:
                        with self._lock:
                            self.listen_stream = bool(st.get("listen_stream"))
                except Exception:
                    pass
            if ready:
                with self._lock:
                    if self.python_deps in ("unknown", "installing"):
                        self.python_deps = "ready"
                    if self.ollama in ("unknown", "checking", "missing"):
                        self.ollama = "running"
                    if self.model in ("unknown", "pulling"):
                        self.model = "ready"
                try:
                    st = self._curl_json("GET", "/status", None, timeout=5)
                    if isinstance(st, dict):
                        with self._lock:
                            if "listen_stream" in st:
                                self.listen_stream = bool(st.get("listen_stream"))
                            self.roster = list(st.get("roster") or st.get("active_users") or [])
                            self.roster_count = int(
                                st.get("roster_count") if st.get("roster_count") is not None
                                else len(self.roster)
                            )
                            self.players = list(st.get("players") or self.roster)
                            self.players_count = int(
                                st.get("players_count") if st.get("players_count") is not None
                                else len(self.players)
                            )
                except Exception:
                    pass
                # Log tail is optional; never block health on it.
                try:
                    self._pull_remote_log_tail()
                except Exception:
                    pass
        except TimeoutError:
            # Gate busy / skip — keep previous ready state, never paint Last error red.
            return
        except Exception as e:
            if already_ready:
                # Bot was fine; transient SSH blip stays in logs only.
                self._log(f"ssh probe soft-fail (ignored): {e}")
                return
            msg = f"ssh status: {e}"
            with self._lock:
                changed = self.last_error != msg
                if changed:
                    self.last_error = msg
            if changed:
                self._log(msg)

    def status(self) -> Dict[str, Any]:
        """Мгновенный кэш — без SSH."""
        self._ensure_probe_loop()
        return self._snapshot()

    def clear_chat(self) -> Dict[str, Any]:
        with self._lock:
            self._chat.clear()
        return {"ok": True, "chat": []}

    def resync_roster(self) -> Dict[str, Any]:
        try:
            data = self._curl_json("POST", "/roster/resync", {}, timeout=30)
            if isinstance(data, dict):
                with self._lock:
                    self.roster = list(data.get("roster") or [])
                    self.roster_count = int(data.get("roster_count") or len(self.roster))
            self._log(f"roster resync → Unity ({self.roster_count} players)")
            self._chat_add("system", f"roster resync → Unity ({self.roster_count})")
            return {"ok": True, "status": self._snapshot(), "bot": data}
        except Exception as e:
            self.last_error = str(e)
            self._log(f"roster resync error: {e}")
            return {"ok": False, "error": str(e), "status": self._snapshot()}

    def start(self) -> Dict[str, Any]:
        with self._lock:
            if self.bot_state == "starting":
                return self._snapshot()
            if self._start_thread and self._start_thread.is_alive():
                self.bot_state = "starting"
                return self._snapshot()
            self.bot_state = "starting"
            self.last_error = None
            self.python_deps = "installing"
            self.ollama = "checking"
            self.model = "unknown"
        self._log("Start Bot on lab_comp…")
        self._chat_add("system", "starting on lab_comp…")
        t = threading.Thread(target=self._start_worker, name="lab-llm-bot-start", daemon=True)
        self._start_thread = t
        t.start()
        return self._snapshot()

    def _start_worker(self) -> None:
        try:
            host = self._host_now()
            self._log(f"ssh host={host}")
            remote = (
                f"cd {self._remote_dir} && "
                f"bash train_scripts/lab_comp/llm_bot_lab.bash start"
            )
            r = self._ssh(remote, timeout=900)
            out = (r.stdout or "") + (r.stderr or "")
            for line in out.splitlines():
                if line.strip():
                    self._log(line)
            self._parse_setup_markers(out)
            if r.returncode != 0:
                raise RuntimeError(f"lab start exit={r.returncode}")
            with self._lock:
                self.bot_state = "running"
                self.bot_http_ready = True
                if self.python_deps != "ready":
                    self.python_deps = "ready"
            try:
                st = self._curl_json("GET", "/status", None, timeout=10)
                if isinstance(st, dict) and "listen_stream" in st:
                    with self._lock:
                        self.listen_stream = bool(st.get("listen_stream"))
            except Exception:
                pass
            mode = "Twitch stream" if self.listen_stream else "Local debug"
            self._chat_add("system", f"bot running on {host} · {mode}")
            self._log(f"Start Bot OK (lab_comp) · {mode}")
            self._probe_once()
        except Exception as e:
            with self._lock:
                self.bot_state = "error"
                self.bot_http_ready = False
                self.last_error = str(e)
            self._log(f"Start Bot FAILED: {e}")
            self._chat_add("system", f"error: {e}")

    def stop(self) -> Dict[str, Any]:
        with self._lock:
            if self.bot_state == "stopping":
                return self._snapshot()
            self.bot_state = "stopping"
            self.bot_http_ready = False
        self._log("Stop Bot on lab_comp…")
        self._chat_add("system", "stopping on lab…")

        def worker() -> None:
            try:
                host = self._host_now()
                r = self._ssh(
                    f"cd {self._remote_dir} && bash train_scripts/lab_comp/llm_bot_lab.bash stop",
                    timeout=60,
                )
                out = (r.stdout or "") + (r.stderr or "")
                for line in out.splitlines():
                    if line.strip():
                        self._log(line)
                with self._lock:
                    self.bot_state = "stopped"
                    self.bot_http_ready = False
                self._chat_add("system", "bot stopped on lab")
                self._log("Stop Bot OK (lab_comp)")
            except Exception as e:
                with self._lock:
                    self.last_error = str(e)
                    # даже при ошибке SSH считаем остановку завершённой локально
                    self.bot_state = "stopped"
                    self.bot_http_ready = False
                self._log(f"stop error: {e}")
                self._chat_add("system", f"stop error: {e}")

        threading.Thread(target=worker, name="lab-llm-bot-stop", daemon=True).start()
        return self._snapshot()

    def set_mode(self, listen_stream: bool) -> Dict[str, Any]:
        try:
            data = self._curl_json("POST", "/mode", {"listen_stream": bool(listen_stream)}, timeout=30)
            with self._lock:
                self.listen_stream = bool(listen_stream)
            mode = "Twitch stream" if listen_stream else "Local debug"
            self._log(f"mode → {mode}")
            self._chat_add("system", f"Mode: {mode}")
            return {"ok": True, "listen_stream": self.listen_stream, "bot": data, "status": self._snapshot()}
        except Exception as e:
            self.last_error = str(e)
            self._log(f"mode error: {e}")
            return {"ok": False, "error": str(e), "status": self._snapshot()}

    def local_chat(self, username: str, message: str) -> Dict[str, Any]:
        """Не блокирует UI: сообщение сразу в чат, ответ с lab — в фоне."""
        user = (username or "debug_user").strip() or "debug_user"
        msg = (message or "").strip()
        if msg.startswith("#"):
            self._chat_add(user, msg)
        self._log(f"local_chat → lab {user}: {msg[:160]}")
        if self.bot_state != "running" and not self.bot_http_ready:
            err = "бот не запущен — нажми Start Bot"
            self.last_error = err
            self._log(err)
            if msg.startswith("#"):
                self._chat_add("bot", err)
            return {"ok": False, "error": err, "status": self._snapshot()}

        def worker() -> None:
            try:
                data = self._curl_json(
                    "POST",
                    "/local_chat",
                    {"username": user, "message": msg},
                    timeout=45,
                )
                reply = str((data or {}).get("chat_reply") or "").strip()
                if reply:
                    self._chat_add("bot", reply)
                else:
                    self._chat_add("bot", "(пустой ответ бота)")
            except Exception as e:
                with self._lock:
                    self.last_error = str(e)
                self._log(f"local_chat error: {e}")
                if "curl exit 7" in str(e):
                    tip = "бот на lab не отвечает (HTTP :8765) — Start Bot"
                    with self._lock:
                        self.last_error = tip
                    if msg.startswith("#"):
                        self._chat_add("bot", tip)
                elif msg.startswith("#"):
                    self._chat_add("bot", f"ошибка: {e}")

        threading.Thread(target=worker, name="lab-llm-local-chat", daemon=True).start()
        return {"ok": True, "accepted": True, "status": self._snapshot()}

    def _curl_json(self, method: str, path: str, body: Optional[dict], timeout: float = 60) -> Any:
        url = REMOTE_BOT_HTTP + path
        curl_m = max(5, int(timeout))
        ssh_t = float(timeout) + 15.0
        if method == "GET":
            remote = f"curl -s -m {curl_m} {self._q(url)}"
        else:
            payload = json.dumps(body or {})
            remote = (
                f"curl -s -m {curl_m} -X {method} {self._q(url)} "
                f"-H 'Content-Type: application/json' "
                f"--data-binary {self._q(payload)}"
            )
        r = self._ssh(remote, timeout=ssh_t, gate_timeout=min(20.0, ssh_t))
        raw = ((r.stdout or "") + (r.stderr or "")).strip()
        if r.returncode != 0:
            raise RuntimeError(f"curl exit {r.returncode}: {raw[:400]}")
        if not raw:
            return {}
        try:
            return json.loads(raw)
        except json.JSONDecodeError as e:
            raise RuntimeError(f"bad JSON from lab: {raw[:300]}") from e

    def _pull_remote_log_tail(self) -> None:
        try:
            r = self._ssh(
                f"tail -n 30 {self._remote_dir}/results/stream_bot.log 2>/dev/null || true",
                timeout=12,
                gate_timeout=1.5,
                skip_if_busy=True,
            )
            text = r.stdout or ""
            for line in text.splitlines()[-15:]:
                line = line.rstrip()
                if not line:
                    continue
                with self._lock:
                    skip = bool(self._logs and self._logs[-1].endswith(line))
                if skip:
                    continue
                self._log("[lab] " + line)
        except Exception:
            pass
