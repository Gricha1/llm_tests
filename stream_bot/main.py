"""Главный цикл: только обращение «бот …» → сразу действие, без голосований."""

from __future__ import annotations

import logging
import signal
import sys
import threading
import time
from typing import Optional

from stream_bot.command_parser import CHAT_TIPS, HELP_TEXT, ParsedKind, parse_message
from stream_bot.config import load_config
from stream_bot.event_log import EventLog
from stream_bot.llm_client import LlmClient
from stream_bot.llm_prompts import SYSTEM_PROMPT, build_user_prompt
from stream_bot.twitch_client import TwitchClient
from stream_bot.unity_client import UnityClient
from stream_bot.user_store import UserStore
from stream_bot.validator import CommandValidator

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
log = logging.getLogger("stream_bot.main")

_FRIENDLY_CMD = {
    "add_zombie": "зомби",
    "food_rain": "еда",
    "night": "ночь",
    "chaos": "хаос",
    "heal_agent": "хил",
    "reset": "сброс",
}


class StreamBot:
    def __init__(self) -> None:
        self.cfg = load_config()
        if not self.cfg.twitch_channel:
            raise SystemExit("TWITCH_CHANNEL не задан в .env")

        self.users = UserStore(self.cfg.db_path)
        self.events = EventLog(self.cfg.db_path)
        self.unity = UnityClient(self.cfg.unity_host, self.cfg.unity_port, self.cfg.unity_transport)
        self.validator = CommandValidator(
            self.cfg.global_command_cooldown_seconds,
            self.cfg.user_command_cooldown_seconds,
        )
        self.llm: Optional[LlmClient] = None
        if self.cfg.use_local_llm:
            self.llm = LlmClient(
                self.cfg.ollama_base_url,
                self.cfg.ollama_model,
                self.cfg.llm_timeout_seconds,
            )

        self.twitch = TwitchClient(
            channel=self.cfg.twitch_channel,
            nick=self.cfg.twitch_bot_nick,
            oauth=self.cfg.twitch_oauth,
            on_message=self.handle_message,
        )
        self._stop = threading.Event()
        self._last_tip = 0.0
        self._tip_index = 0
        self._welcome_at = 0.0
        self._stream_context = "Лес, агенты, зомби. Команды только после слова «бот»."

    def _say(self, text: str) -> None:
        text = (text or "").strip()
        if not text:
            return
        self.twitch.send_chat_message(text)
        self.unity.send_event(text, severity="info")

    def start(self) -> None:
        log.info(
            "Start channel=#%s llm=%s can_send=%s wake=бот unity=%s:%s",
            self.cfg.twitch_channel,
            self.cfg.use_local_llm,
            self.twitch.can_send,
            self.cfg.unity_host,
            self.cfg.unity_port,
        )
        if not self.twitch.can_send:
            log.warning("Нет OAuth — ответы в Twitch не уйдут (can_send=False)")
        self.twitch.start()
        now = time.time()
        self._last_tip = now
        self._welcome_at = now + 8.0
        while not self._stop.is_set():
            try:
                self._maybe_welcome()
                self._maybe_chat_tip()
            except Exception:
                log.exception("tick failed")
            self._stop.wait(1.0)

    def stop(self) -> None:
        self._stop.set()
        self.twitch.stop()
        self.unity.close()

    def handle_message(self, user: str, message: str) -> None:
        self.users.note_message(user)
        parsed = parse_message(message, bot_nick=self.cfg.twitch_bot_nick)
        if parsed.kind == ParsedKind.IGNORE:
            return

        if parsed.kind == ParsedKind.HELP:
            self._say(f"@{user} {HELP_TEXT}")
            return

        if parsed.kind == ParsedKind.PROFILE or parsed.kind == ParsedKind.POINTS:
            p = self.users.get_profile(user)
            self._say(
                f"@{user} {p['points']} очков, lvl {p['level']}, "
                f"команд {p['commands_count']}, событий {p['successful_events_count']}"
            )
            return

        if parsed.kind == ParsedKind.TOP:
            top = self.users.get_top(5)
            if not top:
                self._say(f"@{user} топ пока пуст")
                return
            parts = [f"{i + 1}. {r['username']}={r['points']}" for i, r in enumerate(top)]
            self._say(f"@{user} топ: " + " | ".join(parts))
            return

        if parsed.kind == ParsedKind.STATUS:
            self._say(f"@{user} ок, слушаю. Пиши: бот зомби / бот еда / бот помощь")
            return

        if parsed.kind == ParsedKind.COMMAND and parsed.command:
            self._execute(user, parsed.command, float(parsed.value or 0))
            return

        if parsed.kind == ParsedKind.ASK:
            self._handle_ask(user, parsed.text or "")
            return

        if parsed.kind == ParsedKind.UNKNOWN:
            # попробовать LLM, иначе короткий отказ
            if self.cfg.use_local_llm and self.llm is not None:
                self._handle_llm_as_command(user, parsed.text or "")
            else:
                self._say(f"@{user} не понял. {HELP_TEXT}")
            return

    def _execute(self, user: str, command: str, value: float) -> None:
        # Всегда сразу, без голосования
        vr = self.validator.validate(
            command, value, user, force_requires_vote=False
        )
        if not vr.ok:
            self._say(f"@{user} подожди: {vr.reason}")
            return

        self.validator.mark_used(vr.command, user)
        self.users.increment_commands(user)
        self.events.log("command", username=user, command=vr.command, value=vr.value)
        label = _FRIENDLY_CMD.get(vr.command, vr.command)
        msg = f"@{user} сделал: {label}"
        self.unity.send_command(vr.command, vr.value, user=user, source="chat", message=msg)
        self.unity.send_event(msg, severity="info")
        self._say(msg)

    def _handle_ask(self, user: str, text: str) -> None:
        if self.cfg.use_local_llm and self.llm is not None:
            result = self.llm.generate_json(
                SYSTEM_PROMPT,
                build_user_prompt("ask", user, text or "why", self._stream_context),
            )
            reply = str(result.get("chat_reply") or "").strip()
            if reply:
                self._say(f"@{user} {reply[:250]}")
                return
        self._say(f"@{user} агенты выживают в лесу. Команды: {HELP_TEXT}")

    def _handle_llm_as_command(self, user: str, text: str) -> None:
        result = self.llm.generate_json(
            SYSTEM_PROMPT,
            build_user_prompt(
                "suggest",
                user,
                text,
                self._stream_context
                + " Ответь type=command или chat_reply. Не делай type=poll.",
            ),
        )
        self.events.log("llm_response", username=user, message="bot_wake", raw=result)
        rtype = str(result.get("type", "none")).lower()
        if rtype == "command":
            self._execute(user, str(result.get("command") or ""), float(result.get("value") or 0))
            return
        reply = str(result.get("chat_reply") or "").strip()
        if reply:
            self._say(f"@{user} {reply[:250]}")
        else:
            self._say(f"@{user} не понял. {HELP_TEXT}")

    def _maybe_welcome(self) -> None:
        if not self.cfg.welcome_on_start or self._welcome_at <= 0:
            return
        if time.time() < self._welcome_at:
            return
        self._welcome_at = 0.0
        self._say(f"На связи. {HELP_TEXT}")

    def _maybe_chat_tip(self) -> None:
        interval = self.cfg.chat_tips_interval_seconds
        if interval <= 0:
            return
        if time.time() - self._last_tip < interval:
            return
        self._last_tip = time.time()
        tip = CHAT_TIPS[self._tip_index % len(CHAT_TIPS)]
        self._tip_index += 1
        self._say(tip)


def main() -> None:
    bot = StreamBot()

    def _sig(_signum, _frame) -> None:
        log.info("Stopping...")
        bot.stop()
        sys.exit(0)

    signal.signal(signal.SIGINT, _sig)
    signal.signal(signal.SIGTERM, _sig)
    bot.start()


if __name__ == "__main__":
    main()
