"""SQLite: активные игроки Streaming Survival."""

from __future__ import annotations

import sqlite3
import threading
import time
from typing import Any, Dict, List, Optional


class StreamingSurvivalStore:
    def __init__(self, db_path: str) -> None:
        self.db_path = db_path
        self._lock = threading.Lock()
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
                    CREATE TABLE IF NOT EXISTS streaming_survival_users (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        username TEXT UNIQUE NOT NULL,
                        twitch_user_id TEXT,
                        joined_at REAL,
                        last_seen_at REAL,
                        is_active INTEGER DEFAULT 1,
                        last_action TEXT DEFAULT 'idle',
                        last_action_name TEXT DEFAULT 'Ждёт у базы',
                        last_action_changed_at REAL DEFAULT 0,
                        total_rounds_participated INTEGER DEFAULT 0,
                        total_water_collected INTEGER DEFAULT 0,
                        total_wood_collected INTEGER DEFAULT 0,
                        total_food_collected INTEGER DEFAULT 0,
                        total_sheep_killed INTEGER DEFAULT 0,
                        total_campfires_built INTEGER DEFAULT 0
                    )
                    """
                )
                conn.commit()
            finally:
                conn.close()

    def get_user(self, username: str) -> Optional[Dict[str, Any]]:
        u = username.lower().strip()
        with self._lock:
            conn = self._connect()
            try:
                row = conn.execute(
                    "SELECT * FROM streaming_survival_users WHERE username=?", (u,)
                ).fetchone()
                return dict(row) if row else None
            finally:
                conn.close()

    def has_joined(self, username: str) -> bool:
        row = self.get_user(username)
        return bool(row and int(row.get("is_active") or 0) == 1)

    def deactivate_all(self) -> int:
        """Сброс сессии: никто не в игре до нового #join."""
        with self._lock:
            conn = self._connect()
            try:
                cur = conn.execute(
                    """
                    UPDATE streaming_survival_users
                    SET is_active=0, last_action='idle', last_action_name='Ждёт у базы'
                    WHERE is_active=1
                    """
                )
                conn.commit()
                return int(cur.rowcount or 0)
            finally:
                conn.close()

    def join(self, username: str, twitch_user_id: str = "") -> bool:
        """True если пользователь только что стал активным (новый join)."""
        u = username.lower().strip()
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                row = conn.execute(
                    "SELECT id, is_active FROM streaming_survival_users WHERE username=?",
                    (u,),
                ).fetchone()
                if row is None:
                    conn.execute(
                        """
                        INSERT INTO streaming_survival_users (
                            username, twitch_user_id, joined_at, last_seen_at, is_active,
                            last_action, last_action_name, last_action_changed_at
                        ) VALUES (?, ?, ?, ?, 1, 'idle', 'Ждёт у базы', 0)
                        """,
                        (u, twitch_user_id or None, now, now),
                    )
                    conn.commit()
                    return True
                was_active = int(row["is_active"] or 0) == 1
                # Re-activating after exit (or fresh session): reset to idle so #join
                # never resumes collect_wood/collect_water without a new #do.
                if was_active:
                    conn.execute(
                        """
                        UPDATE streaming_survival_users
                        SET is_active=1, last_seen_at=?,
                            twitch_user_id=COALESCE(?, twitch_user_id)
                        WHERE username=?
                        """,
                        (now, twitch_user_id or None, u),
                    )
                else:
                    conn.execute(
                        """
                        UPDATE streaming_survival_users
                        SET is_active=1, last_seen_at=?,
                            twitch_user_id=COALESCE(?, twitch_user_id),
                            last_action='idle', last_action_name='Ждёт у базы',
                            last_action_changed_at=0
                        WHERE username=?
                        """,
                        (now, twitch_user_id or None, u),
                    )
                conn.commit()
                # уже был активен → «уже в игре»; иначе свежий вход
                return not was_active
            finally:
                conn.close()

    def leave(self, username: str) -> bool:
        """True если был активен и вышел."""
        u = username.lower().strip()
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                row = conn.execute(
                    "SELECT is_active FROM streaming_survival_users WHERE username=?",
                    (u,),
                ).fetchone()
                if row is None or not int(row["is_active"] or 0):
                    return False
                conn.execute(
                    """
                    UPDATE streaming_survival_users
                    SET is_active=0, last_seen_at=?, last_action='idle',
                        last_action_name='Ждёт у базы'
                    WHERE username=?
                    """,
                    (now, u),
                )
                conn.commit()
                return True
            finally:
                conn.close()

    def set_action(
        self,
        username: str,
        action: str,
        action_name: str,
        *,
        touch_changed_at: bool = True,
    ) -> Dict[str, Any]:
        u = username.lower().strip()
        now = time.time()
        self.join(u)
        with self._lock:
            conn = self._connect()
            try:
                # Join default idle must not start the #do cooldown (changed_at=0).
                changed_at = now if touch_changed_at else 0.0
                conn.execute(
                    """
                    UPDATE streaming_survival_users
                    SET last_action=?, last_action_name=?, last_action_changed_at=?,
                        last_seen_at=?, is_active=1
                    WHERE username=?
                    """,
                    (action, action_name, changed_at, now, u),
                )
                conn.commit()
                row = conn.execute(
                    "SELECT * FROM streaming_survival_users WHERE username=?", (u,)
                ).fetchone()
                return dict(row) if row else {}
            finally:
                conn.close()

    def seconds_since_action_change(self, username: str) -> float:
        row = self.get_user(username)
        if not row:
            return 1e9
        return max(0.0, time.time() - float(row.get("last_action_changed_at") or 0))

    def list_active(self) -> List[Dict[str, Any]]:
        with self._lock:
            conn = self._connect()
            try:
                rows = conn.execute(
                    """
                    SELECT username, last_action AS action, last_action_name AS action_name
                    FROM streaming_survival_users
                    WHERE is_active=1
                    ORDER BY joined_at ASC
                    """
                ).fetchall()
                return [dict(r) for r in rows]
            finally:
                conn.close()

    def bump_stat(self, username: str, field: str, amount: int = 1) -> None:
        allowed = {
            "total_water_collected",
            "total_wood_collected",
            "total_food_collected",
            "total_sheep_killed",
            "total_campfires_built",
            "total_rounds_participated",
        }
        if field not in allowed:
            return
        u = username.lower().strip()
        with self._lock:
            conn = self._connect()
            try:
                conn.execute(
                    f"UPDATE streaming_survival_users SET {field}={field}+? WHERE username=?",
                    (amount, u),
                )
                conn.commit()
            finally:
                conn.close()
