"""Настройки из .env — токены только из окружения, не из кода."""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

from dotenv import load_dotenv

_ROOT = Path(__file__).resolve().parent.parent
load_dotenv(_ROOT / ".env")
load_dotenv(Path(__file__).resolve().parent / ".env")
load_dotenv()


def _bool(name: str, default: bool = False) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in ("1", "true", "yes", "on")


def _int(name: str, default: int) -> int:
    raw = os.getenv(name)
    if raw is None or raw.strip() == "":
        return default
    return int(raw)


def _float(name: str, default: float) -> float:
    raw = os.getenv(name)
    if raw is None or raw.strip() == "":
        return default
    return float(raw)


@dataclass(frozen=True)
class Config:
    twitch_bot_nick: str
    twitch_oauth: str
    twitch_channel: str

    unity_host: str
    unity_port: int
    unity_transport: str

    use_local_llm: bool
    ollama_base_url: str
    ollama_model: str
    llm_timeout_seconds: float

    vote_duration_seconds: int
    auto_poll_interval_seconds: int
    empty_poll_policy: str  # "do_nothing" | "random_safe"
    chat_tips_interval_seconds: int
    welcome_on_start: bool

    global_command_cooldown_seconds: int
    user_command_cooldown_seconds: int

    db_path: str

    @property
    def can_send_chat(self) -> bool:
        return bool(self.twitch_oauth.strip()) and bool(self.twitch_bot_nick.strip())


def load_config() -> Config:
    db = os.getenv("DB_PATH", "stream_bot.sqlite3").strip()
    if not os.path.isabs(db):
        db = str(_ROOT / db)

    return Config(
        twitch_bot_nick=os.getenv("TWITCH_BOT_NICK", "").strip(),
        twitch_oauth=os.getenv("TWITCH_OAUTH", "").strip(),
        twitch_channel=os.getenv("TWITCH_CHANNEL", "").strip().lstrip("#").lower(),
        unity_host=os.getenv("UNITY_HOST", "127.0.0.1").strip(),
        unity_port=_int("UNITY_PORT", 5055),
        unity_transport=os.getenv("UNITY_TRANSPORT", "udp").strip().lower(),
        use_local_llm=_bool("USE_LOCAL_LLM", True),
        ollama_base_url=os.getenv("OLLAMA_BASE_URL", "http://localhost:11434").rstrip("/"),
        ollama_model=os.getenv("OLLAMA_MODEL", "qwen3:4b"),
        llm_timeout_seconds=_float("LLM_TIMEOUT_SECONDS", 20.0),
        vote_duration_seconds=_int("VOTE_DURATION_SECONDS", 60),
        auto_poll_interval_seconds=_int("AUTO_POLL_INTERVAL_SECONDS", 600),
        empty_poll_policy=os.getenv("EMPTY_POLL_POLICY", "do_nothing").strip().lower(),
        chat_tips_interval_seconds=_int("CHAT_TIPS_INTERVAL_SECONDS", 180),
        welcome_on_start=_bool("WELCOME_ON_START", True),
        global_command_cooldown_seconds=_int("GLOBAL_COMMAND_COOLDOWN_SECONDS", 30),
        user_command_cooldown_seconds=_int("USER_COMMAND_COOLDOWN_SECONDS", 120),
        db_path=db,
    )
