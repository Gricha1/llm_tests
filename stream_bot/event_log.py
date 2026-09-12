"""SQLite лог событий (без OAuth)."""

from __future__ import annotations

import json
import sqlite3
import threading
import time
from typing import Any, Dict, Optional


class EventLog:
    def __init__(self, db_path: str) -> None:
        self.db_path = db_path
        self._lock = threading.Lock()
        self._init_db()

    def _connect(self) -> sqlite3.Connection:
        return sqlite3.connect(self.db_path, check_same_thread=False)

    def _init_db(self) -> None:
        with self._lock:
            conn = self._connect()
            try:
                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS events (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp REAL,
                        type TEXT,
                        username TEXT,
                        command TEXT,
                        value REAL,
                        message TEXT,
                        raw_json TEXT
                    )
                    """
                )
                conn.commit()
            finally:
                conn.close()

    def log(
        self,
        event_type: str,
        *,
        username: str = "",
        command: str = "",
        value: float = 0.0,
        message: str = "",
        raw: Optional[Dict[str, Any]] = None,
    ) -> None:
        # Persist stream_mode / episode_id at event time (not reconstructed later).
        payload: Dict[str, Any] = dict(raw) if isinstance(raw, dict) else {}
        if raw is not None and not isinstance(raw, dict):
            payload = {"raw": raw}
        try:
            from stream_bot.stream_context import analytics_context

            ctx = analytics_context()
            if "stream_mode" not in payload:
                payload["stream_mode"] = ctx.get("stream_mode")
            if "episode_id" not in payload and ctx.get("episode_id"):
                payload["episode_id"] = ctx.get("episode_id")
        except Exception:
            payload.setdefault("stream_mode", "autonomous")
        raw_json = ""
        try:
            raw_json = json.dumps(payload, ensure_ascii=False) if payload else ""
        except (TypeError, ValueError):
            raw_json = str(payload)
        with self._lock:
            conn = self._connect()
            try:
                conn.execute(
                    """
                    INSERT INTO events (timestamp, type, username, command, value, message, raw_json)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                    """,
                    (time.time(), event_type, username, command, value, message, raw_json),
                )
                conn.commit()
            finally:
                conn.close()

    def recent(self, limit: int = 20) -> list:
        limit = max(1, min(100, int(limit)))
        with self._lock:
            conn = self._connect()
            try:
                rows = conn.execute(
                    """
                    SELECT timestamp, type, username, command, value, message, raw_json
                    FROM events
                    ORDER BY id DESC
                    LIMIT ?
                    """,
                    (limit,),
                ).fetchall()
            finally:
                conn.close()
        out = []
        for ts, etype, user, cmd, val, msg, raw in rows:
            out.append(
                {
                    "timestamp": ts,
                    "type": etype,
                    "username": user,
                    "command": cmd,
                    "value": val,
                    "message": msg,
                    "raw_json": raw,
                }
            )
        return out
