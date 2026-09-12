"""Ollama HTTP client — ответ всегда считается недоверенным."""

from __future__ import annotations

import json
import logging
import os
import re
import threading
import time
from pathlib import Path
from typing import Any, Dict, Optional

import requests

log = logging.getLogger("stream_bot.llm")

FALLBACK: Dict[str, Any] = {
    "type": "none",
    "chat_reply": "Не смог разобрать идею. Попробуй сформулировать проще.",
}

# Process-level LLM timing (bot process — NOT Unity main thread).
_llm_lock = threading.Lock()
_llm_stats: Dict[str, Any] = {
    "requests": 0,
    "errors": 0,
    "timeouts": 0,
    "parse_fail": 0,
    "last_wall_ms": 0.0,
    "last_parse_ms": 0.0,
    "max_wall_ms": 0.0,
    "sum_wall_ms": 0.0,
    "last_status": "idle",
    "last_ts": 0.0,
    "main_thread_block_ms": 0.0,  # always 0: LLM runs in stream_bot, not Unity
    "queue_depth": 0,
    "bot_alive": True,
}
_llm_last_flush = 0.0


def _llm_results_dirs() -> list[Path]:
    dirs: list[Path] = []
    env = os.environ.get("FOREST_RESULTS_DIR")
    if env:
        dirs.append(Path(env))
    # Always also write repo results/ and run subdir so UI finds the file.
    root = Path("results")
    dirs.append(root)
    dirs.append(root / "jlg_finetune_2")
    # de-dupe preserving order
    out: list[Path] = []
    seen = set()
    for d in dirs:
        key = str(d.resolve()) if d.exists() or True else str(d)
        try:
            key = str(d)
        except Exception:
            key = str(d)
        if key in seen:
            continue
        seen.add(key)
        out.append(d)
    return out


def _flush_llm_stats(force: bool = False) -> None:
    global _llm_last_flush
    now = time.time()
    if not force and (now - _llm_last_flush) < 1.0:
        return
    _llm_last_flush = now
    with _llm_lock:
        payload = dict(_llm_stats)
    payload["ts"] = now
    payload["iso"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(now))
    payload["process"] = "stream_bot"
    payload["note"] = "LLM runs in bot process; main_thread_block_ms always 0 for Unity"
    text = json.dumps(payload, ensure_ascii=False)
    for root in _llm_results_dirs():
        try:
            root.mkdir(parents=True, exist_ok=True)
            tmp = root / "llm_perf_live.json.tmp"
            dest = root / "llm_perf_live.json"
            tmp.write_text(text, encoding="utf-8")
            tmp.replace(dest)
        except OSError:
            continue


def llm_heartbeat(queue_depth: int = 0) -> None:
    """Periodic write so UI sees bot alive even without LLM calls."""
    with _llm_lock:
        _llm_stats["queue_depth"] = int(queue_depth)
        if _llm_stats.get("last_status") in (None, "", "idle"):
            _llm_stats["last_status"] = "idle"
        _llm_stats["last_ts"] = time.time()
    _flush_llm_stats(force=True)


def llm_perf_snapshot() -> Dict[str, Any]:
    with _llm_lock:
        return dict(_llm_stats)


def _extract_json_object(text: str) -> Optional[dict]:
    text = (text or "").strip()
    if not text:
        return None
    try:
        obj = json.loads(text)
        if isinstance(obj, dict):
            return obj
    except json.JSONDecodeError:
        pass

    # Убрать ```json fences
    fenced = re.search(r"```(?:json)?\s*(\{.*?\})\s*```", text, re.S | re.I)
    if fenced:
        try:
            obj = json.loads(fenced.group(1))
            if isinstance(obj, dict):
                return obj
        except json.JSONDecodeError:
            pass

    start = text.find("{")
    end = text.rfind("}")
    if start >= 0 and end > start:
        try:
            obj = json.loads(text[start : end + 1])
            if isinstance(obj, dict):
                return obj
        except json.JSONDecodeError:
            pass
    return None


class LlmClient:
    def __init__(self, base_url: str, model: str, timeout_seconds: float = 20.0) -> None:
        self.base_url = base_url.rstrip("/")
        self.model = model
        self.timeout_seconds = timeout_seconds

    def generate_json(self, system_prompt: str, user_prompt: str) -> dict:
        url = f"{self.base_url}/api/chat"
        payload = {
            "model": self.model,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt},
            ],
            "stream": False,
            "options": {"temperature": 0.2},
        }
        t0 = time.perf_counter()
        status = "ok"
        try:
            resp = requests.post(url, json=payload, timeout=self.timeout_seconds)
            resp.raise_for_status()
            data = resp.json()
            content = ""
            if isinstance(data, dict):
                message = data.get("message") or {}
                content = message.get("content") or data.get("response") or ""
            parsed = _extract_json_object(content)
            if parsed is None:
                status = "parse_fail"
                log.warning("LLM JSON parse failed; content[:200]=%r", content[:200])
                parsed = dict(FALLBACK)
            return parsed
        except requests.Timeout:
            status = "timeout"
            log.warning("LLM request timeout")
            return dict(FALLBACK)
        except Exception as exc:
            status = "error"
            log.warning("LLM request failed: %s", exc)
            return dict(FALLBACK)
        finally:
            wall_ms = (time.perf_counter() - t0) * 1000.0
            with _llm_lock:
                _llm_stats["requests"] += 1
                _llm_stats["last_wall_ms"] = round(wall_ms, 1)
                _llm_stats["sum_wall_ms"] = round(
                    float(_llm_stats["sum_wall_ms"]) + wall_ms, 1
                )
                _llm_stats["max_wall_ms"] = max(
                    float(_llm_stats["max_wall_ms"]), wall_ms
                )
                _llm_stats["last_status"] = status
                _llm_stats["last_ts"] = time.time()
                if status == "timeout":
                    _llm_stats["timeouts"] += 1
                elif status == "error":
                    _llm_stats["errors"] += 1
                elif status == "parse_fail":
                    _llm_stats["parse_fail"] += 1
                # Unity does not block on this HTTP call (UDP commands only).
                _llm_stats["main_thread_block_ms"] = 0.0
            _flush_llm_stats()
