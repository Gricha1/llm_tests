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

    twitch_client_id: str
    twitch_user_access_token: str
    twitch_broadcaster_id: str
    twitch_moderator_id: str
    local_debug_bypass_follower_check: bool
    follower_cache_seconds: int
    watchtime_tick_seconds: int
    do_cooldown_seconds: int
    streaming_survival_stage_seconds: int

    unity_host: str
    unity_port: int
    unity_transport: str

    use_local_llm: bool
    ollama_base_url: str
    ollama_model: str
    llm_timeout_seconds: float

    bot_http_host: str
    bot_http_port: int
    listen_stream_on_start: bool

    vote_duration_seconds: int
    auto_poll_interval_seconds: int
    empty_poll_policy: str
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
        twitch_client_id=os.getenv("TWITCH_CLIENT_ID", "").strip(),
        twitch_user_access_token=os.getenv("TWITCH_USER_ACCESS_TOKEN", "").strip(),
        twitch_broadcaster_id=os.getenv("TWITCH_BROADCASTER_ID", "").strip(),
        twitch_moderator_id=os.getenv("TWITCH_MODERATOR_ID", "").strip(),
        local_debug_bypass_follower_check=_bool("LOCAL_DEBUG_BYPASS_FOLLOWER_CHECK", True),
        follower_cache_seconds=_int("FOLLOWER_CACHE_SECONDS", 600),
        watchtime_tick_seconds=_int("WATCHTIME_TICK_SECONDS", 60),
        do_cooldown_seconds=_int("DO_COOLDOWN_SECONDS", 10),
        streaming_survival_stage_seconds=_int("STREAMING_SURVIVAL_STAGE_SECONDS", 180),
        unity_host=os.getenv("UNITY_HOST", "127.0.0.1").strip(),
        unity_port=_int("UNITY_PORT", 5055),
        unity_transport=os.getenv("UNITY_TRANSPORT", "udp").strip().lower(),
        use_local_llm=_bool("USE_LOCAL_LLM", True),
        ollama_base_url=os.getenv("OLLAMA_BASE_URL", "http://localhost:11434").rstrip("/"),
        ollama_model=os.getenv("OLLAMA_MODEL", "qwen3:4b"),
        llm_timeout_seconds=_float("LLM_TIMEOUT_SECONDS", 20.0),
        bot_http_host=os.getenv("BOT_HTTP_HOST", "127.0.0.1").strip(),
        bot_http_port=_int("BOT_HTTP_PORT", 8765),
        listen_stream_on_start=_bool("LISTEN_STREAM_ON_START", False),
        vote_duration_seconds=_int("VOTE_DURATION_SECONDS", 60),
        auto_poll_interval_seconds=_int("AUTO_POLL_INTERVAL_SECONDS", 600),
        empty_poll_policy=os.getenv("EMPTY_POLL_POLICY", "do_nothing").strip().lower(),
        chat_tips_interval_seconds=_int("CHAT_TIPS_INTERVAL_SECONDS", 180),
        welcome_on_start=_bool("WELCOME_ON_START", True),
        global_command_cooldown_seconds=_int("GLOBAL_COMMAND_COOLDOWN_SECONDS", 30),
        user_command_cooldown_seconds=_int("USER_COMMAND_COOLDOWN_SECONDS", 120),
        db_path=db,
    )
