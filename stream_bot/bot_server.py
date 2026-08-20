"""FastAPI control plane для Unity / local debug."""

from __future__ import annotations

import logging
import threading
from typing import Any, Dict, Optional

from fastapi import FastAPI
from pydantic import BaseModel, Field
import uvicorn

log = logging.getLogger("stream_bot.api")

_bot = None  # type: ignore
_server: Optional[uvicorn.Server] = None


class ModeBody(BaseModel):
    listen_stream: bool


class LocalChatBody(BaseModel):
    username: str = Field(default="debug_user")
    message: str = Field(default="")


class StatsEventBody(BaseModel):
    username: str = Field(default="")
    stat: str = Field(default="")
    amount: int = Field(default=1, ge=1)


def create_app(bot) -> FastAPI:
    app = FastAPI(title="Forest Stream Bot", version="0.2")

    @app.get("/health")
    def health() -> Dict[str, Any]:
        return {"ok": True}

    @app.get("/status")
    def status() -> Dict[str, Any]:
        return bot.status()

    @app.post("/mode")
    def mode(body: ModeBody) -> Dict[str, Any]:
        return bot.set_listen_stream(body.listen_stream)

    @app.post("/session/reset")
    def session_reset() -> Dict[str, Any]:
        """QA / live-stress: clear all joined users (empty world until #join)."""
        return bot.reset_session()

    @app.get("/roster")
    def roster() -> Dict[str, Any]:
        roster_list = bot.ss.list_roster()
        return {
            "ok": True,
            "roster": roster_list,
            "roster_count": len(roster_list),
            "roster_json": str(bot._roster_json),
        }

    @app.get("/players")
    def players(active_only: bool = False) -> Dict[str, Any]:
        """История входов + статистика (вода/еда/дерево/…)."""
        return bot.list_players(active_only=bool(active_only))

    @app.post("/roster/resync")
    def roster_resync() -> Dict[str, Any]:
        """Повторно заспавнить всех из roster в Unity."""
        return bot.resync_roster()

    @app.post("/roster/prune")
    def roster_prune() -> Dict[str, Any]:
        """Убрать debug/test пользователей; на сцене только Twitch chat."""
        return bot.prune_roster()

    @app.post("/stats/event")
    def stats_event(body: StatsEventBody) -> Dict[str, Any]:
        user = (body.username or "").strip()
        stat = (body.stat or "").strip()
        if not user or not stat:
            return {"ok": False, "error": "username and stat required"}
        bot.ss.record_stat(user, stat, body.amount)
        return {"ok": True}

    @app.post("/local_chat")
    def local_chat(body: LocalChatBody) -> Dict[str, Any]:
        user = (body.username or "debug_user").strip() or "debug_user"
        msg = body.message or ""
        result = bot.process_message(user, msg, source="local_debug")
        return result

    @app.post("/shutdown")
    def shutdown() -> Dict[str, Any]:
        log.info("shutdown requested")
        try:
            bot.stop()
        except Exception:
            log.exception("bot.stop failed")

        def _exit() -> None:
            import time

            time.sleep(0.3)
            if _server is not None:
                _server.should_exit = True

        threading.Thread(target=_exit, daemon=True).start()
        return {"ok": True}

    return app


def run_server(bot) -> None:
    global _bot, _server
    _bot = bot
    cfg = bot.cfg
    app = create_app(bot)
    config = uvicorn.Config(
        app,
        host=cfg.bot_http_host,
        port=cfg.bot_http_port,
        log_level="info",
        access_log=False,
    )
    _server = uvicorn.Server(config)
    log.info("HTTP control http://%s:%s", cfg.bot_http_host, cfg.bot_http_port)
    _server.run()
