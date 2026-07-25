"""SQLite: зрители и очки."""

from __future__ import annotations

import sqlite3
import threading
import time
from typing import Any, Dict, List, Optional


class UserStore:
    def __init__(self, db_path: str) -> None:
        self.db_path = db_path
        self._lock = threading.Lock()
        self._msg_point_last: Dict[str, float] = {}
        self._init_db()

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self.db_path, check_same_thread=False)
        conn.row_factory = sqlite3.Row
        return conn

    def _init_db(self) -> None:
        with self._lock:
            conn = self._connect()
            try:
                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS users (
                        username TEXT PRIMARY KEY,
                        points INTEGER DEFAULT 0,
                        level INTEGER DEFAULT 1,
                        messages_count INTEGER DEFAULT 0,
                        votes_count INTEGER DEFAULT 0,
                        commands_count INTEGER DEFAULT 0,
                        llm_suggestions_count INTEGER DEFAULT 0,
                        successful_events_count INTEGER DEFAULT 0,
                        created_at REAL,
                        updated_at REAL
                    )
                    """
                )
                conn.commit()
            finally:
                conn.close()

    def get_or_create_user(self, username: str) -> Dict[str, Any]:
        u = username.lower().strip()
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                row = conn.execute("SELECT * FROM users WHERE username=?", (u,)).fetchone()
                if row is None:
                    conn.execute(
                        """
                        INSERT INTO users (
                            username, points, level, messages_count, votes_count,
                            commands_count, llm_suggestions_count, successful_events_count,
                            created_at, updated_at
                        ) VALUES (?, 0, 1, 0, 0, 0, 0, 0, ?, ?)
                        """,
                        (u, now, now),
                    )
                    conn.commit()
                    row = conn.execute("SELECT * FROM users WHERE username=?", (u,)).fetchone()
                return dict(row)
            finally:
                conn.close()

    def _bump(self, username: str, field: str, points: int = 0) -> None:
        u = username.lower().strip()
        self.get_or_create_user(u)
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                if points:
                    conn.execute(
                        f"UPDATE users SET {field}={field}+1, points=points+?, updated_at=? WHERE username=?",
                        (points, now, u),
                    )
                else:
                    conn.execute(
                        f"UPDATE users SET {field}={field}+1, updated_at=? WHERE username=?",
                        (now, u),
                    )
                # level = 1 + points // 50
                conn.execute(
                    "UPDATE users SET level=1+(points/50), updated_at=? WHERE username=?",
                    (now, u),
                )
                conn.commit()
            finally:
                conn.close()

    def add_points(self, username: str, points: int) -> None:
        u = username.lower().strip()
        self.get_or_create_user(u)
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                conn.execute(
                    "UPDATE users SET points=points+?, level=1+((points+?)/50), updated_at=? WHERE username=?",
                    (points, points, now, u),
                )
                conn.commit()
            finally:
                conn.close()

    def note_message(self, username: str) -> None:
        """+1 message; +1 point не чаще раза в 60 сек."""
        u = username.lower().strip()
        self.get_or_create_user(u)
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                conn.execute(
                    "UPDATE users SET messages_count=messages_count+1, updated_at=? WHERE username=?",
                    (now, u),
                )
                last = self._msg_point_last.get(u, 0.0)
                if now - last >= 60.0:
                    conn.execute(
                        "UPDATE users SET points=points+1, level=1+((points+1)/50), updated_at=? WHERE username=?",
                        (now, u),
                    )
                    self._msg_point_last[u] = now
                conn.commit()
            finally:
                conn.close()

    def increment_votes(self, username: str) -> None:
        self._bump(username, "votes_count", points=3)

    def increment_commands(self, username: str) -> None:
        self._bump(username, "commands_count", points=0)

    def increment_llm_suggestions(self, username: str) -> None:
        self._bump(username, "llm_suggestions_count", points=5)

    def increment_successful_events(self, username: str) -> None:
        self._bump(username, "successful_events_count", points=10)

    def get_profile(self, username: str) -> Dict[str, Any]:
        return self.get_or_create_user(username)

    def get_top(self, limit: int = 10) -> List[Dict[str, Any]]:
        with self._lock:
            conn = self._connect()
            try:
                rows = conn.execute(
                    "SELECT * FROM users ORDER BY points DESC, votes_count DESC LIMIT ?",
                    (limit,),
                ).fetchall()
                return [dict(r) for r in rows]
            finally:
                conn.close()
