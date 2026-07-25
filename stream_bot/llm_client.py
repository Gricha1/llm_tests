"""Ollama HTTP client — ответ всегда считается недоверенным."""

from __future__ import annotations

import json
import logging
import re
from typing import Any, Dict, Optional

import requests

log = logging.getLogger("stream_bot.llm")

FALLBACK: Dict[str, Any] = {
    "type": "none",
    "chat_reply": "Не смог разобрать идею. Попробуй сформулировать проще.",
}


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
                log.warning("LLM JSON parse failed; content[:200]=%r", content[:200])
                return dict(FALLBACK)
            return parsed
        except Exception as exc:
            log.warning("LLM request failed: %s", exc)
            return dict(FALLBACK)
