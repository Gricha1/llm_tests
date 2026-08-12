"""Stream bot: Streaming Survival — #join / #do → whitelist action → Unity."""

from __future__ import annotations

import logging
import os
import threading
import time
from collections import deque
from typing import Any, Deque, Dict, List, Optional

from stream_bot.command_parser import (
    AVAILABLE_ACTIONS,
    CHAT_TIPS,
    DO_EMPTY,
    EXIT_NOT_IN,
    EXIT_OK,
    FOLLOWER_ONLY,
    HELP_TEXT,
    JOIN_ALREADY,
    JOIN_OK,
    JOIN_PROMPT,
    ParsedKind,
    available_actions_text,
    cooldown_reply,
    parse_message,
)
from stream_bot.config import load_config
from stream_bot.event_log import EventLog
from stream_bot.llm_client import LlmClient
from stream_bot.llm_prompts import SYSTEM_PROMPT, build_user_prompt
from stream_bot.streaming_survival_store import StreamingSurvivalStore
from stream_bot.twitch_api import TwitchApiClient
from stream_bot.twitch_client import TwitchClient
from stream_bot.unity_client import UnityClient
from stream_bot.user_store import UserStore
from stream_bot.validator import (
    CommandValidator,
    fallback_action,
    heuristic_action,
    validate_streaming_survival_action,
)
from stream_bot.vote_manager import VoteManager

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
log = logging.getLogger("stream_bot.main")


class StreamBot:
    def __init__(self) -> None:
        self.cfg = load_config()
        self.listen_stream = bool(self.cfg.listen_stream_on_start)
        self.running = False
        self.last_error: Optional[str] = None
        self._memory_events: Deque[Dict[str, Any]] = deque(maxlen=40)

        self.users = UserStore(self.cfg.db_path)  # follower cache / legacy
        self.ss = StreamingSurvivalStore(self.cfg.db_path)
        self.events = EventLog(self.cfg.db_path)
        self.unity = UnityClient(self.cfg.unity_host, self.cfg.unity_port, self.cfg.unity_transport)
        self.validator = CommandValidator()
        self.llm: Optional[LlmClient] = None
        if self.cfg.use_local_llm:
            self.llm = LlmClient(
                self.cfg.ollama_base_url,
                self.cfg.ollama_model,
                self.cfg.llm_timeout_seconds,
            )

        self.twitch_api = TwitchApiClient(
            self.cfg.twitch_client_id,
            self.cfg.twitch_user_access_token,
            self.cfg.twitch_broadcaster_id,
            self.cfg.twitch_moderator_id,
        )
        self.twitch = TwitchClient(
            channel=self.cfg.twitch_channel or "offline",
            nick=self.cfg.twitch_bot_nick,
            oauth=self.cfg.twitch_oauth,
            on_message=self._on_twitch_message,
        )
        self.votes = VoteManager(
            unity=self.unity,
            event_log=self.events,
            validator=self.validator,
            user_store=self.users,
            send_chat=self._twitch_say,
            empty_poll_policy=self.cfg.empty_poll_policy,
        )

        self._stop = threading.Event()
        self._tick_thread: Optional[threading.Thread] = None
        self._last_tip = 0.0
        self._tip_index = 0
        self._welcome_at = 0.0
        self._last_watch_tick = 0.0
        self._stream_context = (
            "Streaming Survival: фолловеры #join, через #do одно действие из whitelist."
        )
        self.do_cooldown_seconds = int(getattr(self.cfg, "do_cooldown_seconds", 10) or 10)

    def status(self) -> Dict[str, Any]:
        return {
            "running": self.running,
            "listen_stream": self.listen_stream,
            "twitch_connected": bool(self.twitch.connected) if self.listen_stream else False,
            "twitch_api_configured": self.twitch_api.configured,
            "llm_enabled": bool(self.cfg.use_local_llm and self.llm is not None),
            "ollama_model": self.cfg.ollama_model,
            "last_error": self.last_error,
            "mode": "streaming_survival",
            "active_users": self.ss.list_active(),
            "available_actions": [
                {"action": a, "hint": h} for a, h in AVAILABLE_ACTIONS
            ],
            "active_poll": None,
            "last_events": list(self._memory_events)[:20],
        }

    def set_listen_stream(self, enabled: bool) -> Dict[str, Any]:
        self.listen_stream = bool(enabled)
        self._push_event("mode", message="Twitch" if self.listen_stream else "Local debug")
        if self.listen_stream and self.cfg.twitch_channel:
            if self.twitch._thread is None or not self.twitch._thread.is_alive():
                self.twitch.start()
        return {"ok": True, "listen_stream": self.listen_stream}

    def reset_session(self) -> Dict[str, Any]:
        """Empty world session: nobody joined until the next #join.

        Used by live-stress / QA so leftover stress_user bodies and bot
        'already in game' state cannot poison a fresh run.
        """
        cleared = self.ss.deactivate_all()
        self._send_users_sync()
        log.info("session reset: deactivated %s users", cleared)
        return {
            "ok": True,
            "cleared": int(cleared or 0),
            "active_users": self.ss.list_active(),
        }

    def start_background(self) -> None:
        if self.running:
            return
        self.running = True
        self._stop.clear()
        if self.cfg.twitch_channel and self.listen_stream:
            self.twitch.start()
        now = time.time()
        self._last_tip = now
        self._last_watch_tick = now
        self._welcome_at = now + 8.0 if self.listen_stream else 0.0
        self._tick_thread = threading.Thread(target=self._tick_loop, name="bot-tick", daemon=True)
        self._tick_thread.start()
        self._push_event("start", message="streaming survival bot")
        # новая сессия: никто не в игре до #join
        cleared = self.ss.deactivate_all()
        if cleared:
            log.info("cleared %s leftover active users", cleared)
        self._send_users_sync()

    def stop(self) -> None:
        self._stop.set()
        self.running = False
        try:
            self.twitch.stop()
        except Exception:
            log.exception("twitch stop")
        try:
            self.unity.close()
        except Exception:
            log.exception("unity close")

    def _tick_loop(self) -> None:
        while not self._stop.is_set():
            try:
                if self.listen_stream:
                    self._maybe_welcome()
                    self._maybe_chat_tip()
                    self._maybe_watchtime_tick()
            except Exception:
                log.exception("tick failed")
                self.last_error = "tick failed"
            self._stop.wait(1.0)

    def _on_twitch_message(self, user: str, message: str) -> None:
        if not self.listen_stream:
            return
        try:
            self.process_message(user, message, source="chat")
        except Exception as exc:
            self.last_error = str(exc)
            log.exception("twitch handle failed")

    def _twitch_say(self, text: str) -> None:
        if self.listen_stream:
            self.twitch.send_chat_message(text)

    def _reply(
        self, user: str, text: str, source: str, out: Dict[str, Any], *, severity: str = "info"
    ) -> None:
        text = (text or "").strip()
        if not text:
            return
        out["chat_reply"] = text
        if source != "local_debug":
            mention = f"@{user} {text}" if user and not text.startswith("@") else text
            self._twitch_say(mention)
        self.unity.send_event(
            text if source == "local_debug" else f"{user}: {text}", severity=severity
        )
        out.setdefault("events", []).append(
            {"type": "stream_event", "message": text, "severity": severity}
        )

    def _push_event(self, etype: str, message: str = "", **kwargs: Any) -> None:
        self._memory_events.appendleft(
            {"type": etype, "message": message, "timestamp": time.time(), **kwargs}
        )

    def _check_follower(self, user: str, source: str, out: Dict[str, Any]) -> bool:
        if source == "local_debug" and self.cfg.local_debug_bypass_follower_check:
            out["follower_check"] = "bypassed_local_debug"
            return True
        if not self.listen_stream and source == "local_debug":
            out["follower_check"] = "bypassed_local_debug"
            return True

        cached = self.users.get_cached_follower(
            user, max_age_sec=float(self.cfg.follower_cache_seconds)
        )
        if cached is not None:
            out["follower_check"] = "cache_hit"
            if not cached:
                self._reply(user, FOLLOWER_ONLY, source, out)
            return cached

        if not self.twitch_api.configured:
            msg = "Twitch Helix не настроен"
            log.warning(msg)
            self.last_error = msg
            out["follower_check"] = "api_not_configured"
            self._reply(user, FOLLOWER_ONLY, source, out)
            return False

        try:
            uid = self.twitch_api.get_user_id_by_login(user)
            if not uid:
                self.users.set_follower_status(user, False)
                self._reply(user, FOLLOWER_ONLY, source, out)
                return False
            ok = self.twitch_api.is_follower(uid)
            self.users.set_follower_status(user, ok, twitch_user_id=uid)
            out["follower_check"] = "api"
            if not ok:
                self._reply(user, FOLLOWER_ONLY, source, out)
            return ok
        except Exception as exc:
            self.last_error = f"follower check failed: {exc}"
            log.warning("%s", self.last_error)
            self._reply(user, "Сейчас не могу проверить Follow. Попробуй чуть позже.", source, out)
            return False

    def _send_users_sync(self) -> None:
        users = self.ss.list_active()
        self.unity.send_payload(
            {"type": "streaming_survival_users_sync", "users": users}
        )

    def process_message(self, user: str, message: str, source: str = "chat") -> Dict[str, Any]:
        out: Dict[str, Any] = {
            "ok": True,
            "chat_reply": "",
            "events": [],
            "unity_commands": [],
            "raw_input": message,
            "parsed_json": None,
            "validator_result": None,
            "sent_to_unity": False,
        }
        user = (user or "viewer").strip() or "viewer"
        message = message or ""
        log.info("[%s] %s: %s", source, user, message[:200])
        self.events.log("chat_in", username=user, message=message, raw={"source": source})
        self._push_event("chat_in", message=message, username=user, source=source)

        try:
            self.users.note_message(user)
            joined = self.ss.has_joined(user)
            parsed = parse_message(message, has_joined=joined)

            if parsed.kind == ParsedKind.IGNORE:
                return out
            if parsed.kind == ParsedKind.NEED_JOIN:
                self._reply(user, JOIN_PROMPT, source, out)
                return out
            if parsed.kind == ParsedKind.JOIN:
                if not self._check_follower(user, source, out):
                    out["ok"] = False
                    return out
                self._handle_join(user, source, out)
                return out
            if parsed.kind == ParsedKind.EXIT:
                self._handle_exit(user, source, out)
                return out
            if parsed.kind == ParsedKind.DO:
                if not self._check_follower(user, source, out):
                    out["ok"] = False
                    return out
                text = (parsed.text or "").strip()
                if not text:
                    self._reply(user, DO_EMPTY, source, out)
                    return out
                self._handle_do(user, text, source, out)
                return out
            return out
        except Exception as exc:
            self.last_error = str(exc)
            log.exception("process_message failed")
            out["ok"] = False
            out["chat_reply"] = f"ошибка бота: {exc}"
            return out

    def _handle_join(self, user: str, source: str, out: Dict[str, Any]) -> None:
        first = self.ss.join(user)
        payload = {"type": "streaming_survival_user_joined", "username": user}
        self.unity.send_payload(payload)
        out["unity_commands"].append(payload)
        out["sent_to_unity"] = True
        # Fresh #join always starts idle unless explicit persist is enabled.
        # STREAMING_SURVIVAL_NEW_USER_DEFAULT_ACTION=idle (default).
        # STREAMING_SURVIVAL_PERSIST_LAST_ACTION=1 keeps last_action on re-join.
        default_action = (
            os.environ.get("STREAMING_SURVIVAL_NEW_USER_DEFAULT_ACTION", "idle")
            or "idle"
        ).strip().lower()
        if not default_action:
            default_action = "idle"
        persist = (
            os.environ.get("STREAMING_SURVIVAL_PERSIST_LAST_ACTION", "")
            .strip()
            .lower()
            in ("1", "true", "yes")
        )
        row = self.ss.get_user(user) or {}
        if persist and not first:
            action = (row.get("last_action") or default_action).strip().lower()
            action_name = row.get("last_action_name") or "Ждёт у базы"
        else:
            action = default_action
            action_name = "Ждёт у базы" if action == "idle" else (row.get("last_action_name") or action)
            # Default idle after #join must not arm the 10s #do cooldown.
            self.ss.set_action(user, action, action_name, touch_changed_at=False)
        action_payload = {
            "type": "streaming_survival_action",
            "username": user,
            "action": action,
            "action_name": action_name,
        }
        self.unity.send_payload(action_payload)
        out["unity_commands"].append(action_payload)
        self._send_users_sync()
        if first:
            self._reply(user, JOIN_OK, source, out)
            self.events.log("ss_join", username=user, message="joined")
        else:
            self._reply(user, JOIN_ALREADY, source, out)

    def _handle_exit(self, user: str, source: str, out: Dict[str, Any]) -> None:
        left = self.ss.leave(user)
        if not left:
            self._reply(user, EXIT_NOT_IN, source, out)
            return
        payload = {"type": "streaming_survival_user_left", "username": user}
        self.unity.send_payload(payload)
        out["unity_commands"].append(payload)
        out["sent_to_unity"] = True
        self._send_users_sync()
        self._reply(user, EXIT_OK, source, out)
        self.events.log("ss_leave", username=user, message="left")

    def _handle_do(self, user: str, text: str, source: str, out: Dict[str, Any]) -> None:
        if not self.ss.has_joined(user):
            self._reply(user, JOIN_PROMPT, source, out)
            return

        elapsed = self.ss.seconds_since_action_change(user)
        # last_action_changed_at=0 после join → elapsed огромный → #do сразу ок
        left = self.do_cooldown_seconds - elapsed
        if 0 < left <= self.do_cooldown_seconds:
            self._reply(user, cooldown_reply(int(left) + 1), source, out)
            return

        raw: Optional[Dict[str, Any]] = heuristic_action(text, user)
        tag = "heuristic"
        if raw is None and self.cfg.use_local_llm and self.llm is not None:
            try:
                raw = self.llm.generate_json(
                    SYSTEM_PROMPT,
                    build_user_prompt("do", user, text, self._stream_context),
                )
                tag = "llm"
            except Exception as exc:
                self.last_error = str(exc)
                log.warning("LLM failed: %s", exc)
                raw = None
        if raw is None:
            raw = fallback_action(user)
            tag = "fallback"

        out["parsed_json"] = raw
        vr = validate_streaming_survival_action(raw, username=user)
        out["validator_result"] = {"ok": vr.ok, "reason": vr.reason}
        if not vr.ok or not vr.payload:
            payload = fallback_action(user)
            out["validator_result"] = {"ok": False, "reason": vr.reason, "fallback": True}
        else:
            payload = vr.payload

        self.ss.set_action(user, payload["action"], payload["action_name"])
        self.unity.send_payload(payload)
        out["unity_commands"].append(payload)
        out["sent_to_unity"] = True
        out["system_summary"] = f"action={payload['action']} name={payload['action_name']}"
        self._send_users_sync()
        self.events.log("ss_action", username=user, message=payload["action"], raw=payload)
        self._push_event("ss_action", message=payload["action"], username=user, source=tag)
        self._reply(user, str(payload.get("chat_reply") or ""), source, out)
        if source == "local_debug":
            out.setdefault("events", []).append(
                {"type": "system", "message": out["system_summary"]}
            )
            out["events"].append({"type": "system", "message": "sent to Unity"})

    def _maybe_welcome(self) -> None:
        if not self.cfg.welcome_on_start or self._welcome_at <= 0:
            return
        if time.time() < self._welcome_at:
            return
        self._welcome_at = 0.0
        self._twitch_say(f"На связи. {HELP_TEXT}")

    def _maybe_chat_tip(self) -> None:
        interval = self.cfg.chat_tips_interval_seconds
        if interval <= 0 or not self.listen_stream:
            return
        if time.time() - self._last_tip < interval:
            return
        self._last_tip = time.time()
        tip = CHAT_TIPS[self._tip_index % len(CHAT_TIPS)]
        self._tip_index += 1
        self._twitch_say(tip)

    def _maybe_watchtime_tick(self) -> None:
        tick = max(10, int(self.cfg.watchtime_tick_seconds))
        now = time.time()
        if now - self._last_watch_tick < tick:
            return
        self._last_watch_tick = now
        if not self.twitch_api.configured:
            return
        try:
            chatters = self.twitch_api.get_chatters()
        except Exception as exc:
            self.last_error = f"chatters failed: {exc}"
            return
        for ch in chatters:
            login = (ch.user_login or ch.user_name or "").strip().lower()
            if not login:
                continue
            try:
                cached = self.users.get_cached_follower(
                    login, max_age_sec=float(self.cfg.follower_cache_seconds)
                )
                is_f = cached
                if is_f is None and ch.user_id:
                    try:
                        is_f = self.twitch_api.is_follower(ch.user_id)
                        self.users.set_follower_status(login, is_f, twitch_user_id=ch.user_id)
                    except Exception:
                        continue
                if not is_f:
                    continue
                self.users.add_watch_tick(
                    login, seconds=tick, twitch_user_id=ch.user_id, is_follower=True
                )
            except Exception:
                log.exception("watch tick %s", login)


def main() -> None:
    from stream_bot.bot_server import run_server

    bot = StreamBot()
    bot.start_background()
    run_server(bot)


if __name__ == "__main__":
    main()
