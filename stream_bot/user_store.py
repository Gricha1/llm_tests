"""SQLite: users / characters / watchtime / follower cache."""

from __future__ import annotations

import json
import sqlite3
import threading
import time
from typing import Any, Dict, List, Optional

from stream_bot.progression import level_from_watch_seconds, xp_for_minute


class UserStore:
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
                # CREATE TABLE только для новой БД; старую мигрируем ALTER ниже.
                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS users (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        twitch_user_id TEXT,
                        username TEXT UNIQUE NOT NULL,
                        is_follower INTEGER DEFAULT 0,
                        first_seen_at REAL,
                        last_seen_at REAL,
                        total_watch_seconds INTEGER DEFAULT 0,
                        current_session_seconds INTEGER DEFAULT 0,
                        level INTEGER DEFAULT 1,
                        xp INTEGER DEFAULT 0,
                        selected_species TEXT DEFAULT 'sheep',
                        selected_skin TEXT DEFAULT 'default',
                        active_character_id INTEGER,
                        joined INTEGER DEFAULT 0,
                        points INTEGER DEFAULT 0,
                        messages_count INTEGER DEFAULT 0,
                        votes_count INTEGER DEFAULT 0,
                        commands_count INTEGER DEFAULT 0,
                        llm_suggestions_count INTEGER DEFAULT 0,
                        successful_events_count INTEGER DEFAULT 0,
                        follower_checked_at REAL DEFAULT 0,
                        created_at REAL,
                        updated_at REAL
                    )
                    """
                )
                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS characters (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        twitch_user_id TEXT,
                        username TEXT NOT NULL,
                        species TEXT DEFAULT 'sheep',
                        skin TEXT DEFAULT 'default',
                        level INTEGER DEFAULT 1,
                        xp INTEGER DEFAULT 0,
                        created_at REAL,
                        last_spawned_at REAL,
                        is_active INTEGER DEFAULT 1,
                        behavior_json TEXT,
                        behavior_name TEXT,
                        survival_seconds INTEGER DEFAULT 0,
                        deaths INTEGER DEFAULT 0,
                        last_death_reason TEXT
                    )
                    """
                )
                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS watch_sessions (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        twitch_user_id TEXT,
                        username TEXT NOT NULL,
                        stream_started_at REAL,
                        first_seen_at REAL,
                        last_seen_at REAL,
                        accumulated_seconds INTEGER DEFAULT 0
                    )
                    """
                )
                # миграция старых колонок (до индексов)
                cols = {r[1] for r in conn.execute("PRAGMA table_info(users)").fetchall()}
                migrations = {
                    "joined": "INTEGER DEFAULT 0",
                    "twitch_user_id": "TEXT",
                    "is_follower": "INTEGER DEFAULT 0",
                    "first_seen_at": "REAL",
                    "last_seen_at": "REAL",
                    "total_watch_seconds": "INTEGER DEFAULT 0",
                    "current_session_seconds": "INTEGER DEFAULT 0",
                    "xp": "INTEGER DEFAULT 0",
                    "selected_species": "TEXT DEFAULT 'sheep'",
                    "selected_skin": "TEXT DEFAULT 'default'",
                    "active_character_id": "INTEGER",
                    "follower_checked_at": "REAL DEFAULT 0",
                    "points": "INTEGER DEFAULT 0",
                    "messages_count": "INTEGER DEFAULT 0",
                    "votes_count": "INTEGER DEFAULT 0",
                    "commands_count": "INTEGER DEFAULT 0",
                    "llm_suggestions_count": "INTEGER DEFAULT 0",
                    "successful_events_count": "INTEGER DEFAULT 0",
                    "created_at": "REAL",
                    "updated_at": "REAL",
                    "level": "INTEGER DEFAULT 1",
                }
                for col, decl in migrations.items():
                    if col not in cols:
                        conn.execute(f"ALTER TABLE users ADD COLUMN {col} {decl}")

                # старые БД: username был PRIMARY KEY без id — ок, индексы по новым колонкам
                conn.execute("CREATE INDEX IF NOT EXISTS idx_users_username ON users(username)")
                conn.execute(
                    "CREATE INDEX IF NOT EXISTS idx_users_twitch_id ON users(twitch_user_id)"
                )
                conn.execute(
                    "CREATE INDEX IF NOT EXISTS idx_chars_username ON characters(username)"
                )
                conn.commit()
            finally:
                conn.close()

    def get_or_create_user(self, username: str, twitch_user_id: str = "") -> Dict[str, Any]:
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
                            username, twitch_user_id, first_seen_at, last_seen_at,
                            created_at, updated_at, level, xp, selected_species
                        ) VALUES (?, ?, ?, ?, ?, ?, 1, 0, 'sheep')
                        """,
                        (u, twitch_user_id or None, now, now, now, now),
                    )
                    conn.commit()
                    row = conn.execute("SELECT * FROM users WHERE username=?", (u,)).fetchone()
                elif twitch_user_id and not row["twitch_user_id"]:
                    conn.execute(
                        "UPDATE users SET twitch_user_id=?, updated_at=? WHERE username=?",
                        (twitch_user_id, now, u),
                    )
                    conn.commit()
                    row = conn.execute("SELECT * FROM users WHERE username=?", (u,)).fetchone()
                return dict(row)
            finally:
                conn.close()

    def note_message(self, username: str) -> None:
        u = username.lower().strip()
        self.get_or_create_user(u)
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                conn.execute(
                    """
                    UPDATE users SET messages_count=COALESCE(messages_count,0)+1,
                    last_seen_at=?, updated_at=? WHERE username=?
                    """,
                    (now, now, u),
                )
                conn.commit()
            finally:
                conn.close()

    def get_profile(self, username: str) -> Dict[str, Any]:
        return self.get_or_create_user(username)

    def has_joined(self, username: str) -> bool:
        row = self.get_or_create_user(username)
        return bool(row.get("joined"))

    def get_cached_follower(
        self, username: str, *, max_age_sec: float = 600.0
    ) -> Optional[bool]:
        row = self.get_or_create_user(username)
        checked = float(row.get("follower_checked_at") or 0)
        if checked <= 0 or time.time() - checked > max_age_sec:
            return None
        return bool(row.get("is_follower"))

    def set_follower_status(
        self, username: str, is_follower: bool, twitch_user_id: str = ""
    ) -> None:
        u = username.lower().strip()
        self.get_or_create_user(u, twitch_user_id)
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                if twitch_user_id:
                    conn.execute(
                        """
                        UPDATE users SET is_follower=?, follower_checked_at=?,
                        twitch_user_id=?, updated_at=? WHERE username=?
                        """,
                        (1 if is_follower else 0, now, twitch_user_id, now, u),
                    )
                else:
                    conn.execute(
                        """
                        UPDATE users SET is_follower=?, follower_checked_at=?, updated_at=?
                        WHERE username=?
                        """,
                        (1 if is_follower else 0, now, now, u),
                    )
                conn.commit()
            finally:
                conn.close()

    def mark_joined(self, username: str) -> bool:
        """True если только что добавили."""
        u = username.lower().strip()
        user = self.get_or_create_user(u)
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                already = bool(user.get("joined"))
                if already:
                    # обновить last_spawned у активного персонажа
                    conn.execute(
                        """
                        UPDATE characters SET last_spawned_at=?, is_active=1
                        WHERE username=? AND is_active=1
                        """,
                        (now, u),
                    )
                    conn.commit()
                    return False

                conn.execute(
                    "UPDATE users SET joined=1, updated_at=?, last_seen_at=? WHERE username=?",
                    (now, now, u),
                )
                # создать character если нет
                row = conn.execute(
                    "SELECT id FROM characters WHERE username=? AND is_active=1 ORDER BY id DESC LIMIT 1",
                    (u,),
                ).fetchone()
                if row is None:
                    cur = conn.execute(
                        """
                        INSERT INTO characters (
                            twitch_user_id, username, species, skin, level, xp,
                            created_at, last_spawned_at, is_active, behavior_name, behavior_json
                        ) VALUES (?, ?, 'sheep', 'default', ?, 0, ?, ?, 1, 'Спокойная овечка', ?)
                        """,
                        (
                            user.get("twitch_user_id"),
                            u,
                            int(user.get("level") or 1),
                            now,
                            now,
                            json.dumps(
                                {
                                    "type": "character_behavior",
                                    "species": "sheep",
                                    "rules": [
                                        {
                                            "priority": 50,
                                            "condition": {"type": "always"},
                                            "action": {
                                                "type": "wander",
                                                "target": "spawn_point",
                                                "speed": 1.0,
                                            },
                                        }
                                    ],
                                },
                                ensure_ascii=False,
                            ),
                        ),
                    )
                    char_id = int(cur.lastrowid)
                    conn.execute(
                        "UPDATE users SET active_character_id=?, selected_species='sheep' WHERE username=?",
                        (char_id, u),
                    )
                conn.commit()
                return True
            finally:
                conn.close()

    def mark_unjoined(self, username: str) -> bool:
        u = username.lower().strip()
        self.get_or_create_user(u)
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                row = conn.execute("SELECT joined FROM users WHERE username=?", (u,)).fetchone()
                was = bool(row["joined"]) if row is not None else False
                if not was:
                    return False
                conn.execute(
                    "UPDATE users SET joined=0, updated_at=? WHERE username=?",
                    (now, u),
                )
                conn.execute(
                    "UPDATE characters SET is_active=0 WHERE username=? AND is_active=1",
                    (u,),
                )
                conn.commit()
                return True
            finally:
                conn.close()

    def save_character_behavior(
        self,
        username: str,
        *,
        species: str,
        behavior_name: str,
        behavior_json: Dict[str, Any],
        level: int,
    ) -> None:
        u = username.lower().strip()
        self.get_or_create_user(u)
        now = time.time()
        payload = json.dumps(behavior_json, ensure_ascii=False)
        with self._lock:
            conn = self._connect()
            try:
                row = conn.execute(
                    "SELECT id FROM characters WHERE username=? AND is_active=1 ORDER BY id DESC LIMIT 1",
                    (u,),
                ).fetchone()
                if row is None:
                    cur = conn.execute(
                        """
                        INSERT INTO characters (
                            username, species, skin, level, created_at, last_spawned_at,
                            is_active, behavior_json, behavior_name
                        ) VALUES (?, ?, 'default', ?, ?, ?, 1, ?, ?)
                        """,
                        (u, species, level, now, now, payload, behavior_name),
                    )
                    char_id = int(cur.lastrowid)
                else:
                    char_id = int(row["id"])
                    conn.execute(
                        """
                        UPDATE characters SET species=?, level=?, behavior_json=?,
                        behavior_name=?, last_spawned_at=?, is_active=1 WHERE id=?
                        """,
                        (species, level, payload, behavior_name, now, char_id),
                    )
                conn.execute(
                    """
                    UPDATE users SET joined=1, selected_species=?, level=?,
                    active_character_id=?, updated_at=? WHERE username=?
                    """,
                    (species, level, char_id, now, u),
                )
                conn.commit()
            finally:
                conn.close()

    def add_watch_tick(
        self,
        username: str,
        *,
        seconds: int = 60,
        twitch_user_id: str = "",
        is_follower: bool = True,
    ) -> Dict[str, Any]:
        """+seconds watchtime и +XP за каждую полную минуту."""
        u = username.lower().strip()
        self.get_or_create_user(u, twitch_user_id)
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                row = conn.execute("SELECT * FROM users WHERE username=?", (u,)).fetchone()
                total = int(row["total_watch_seconds"] or 0) + int(seconds)
                session = int(row["current_session_seconds"] or 0) + int(seconds)
                xp = int(row["xp"] or 0) + xp_for_minute() * max(1, int(seconds) // 60)
                level = level_from_watch_seconds(total)
                conn.execute(
                    """
                    UPDATE users SET total_watch_seconds=?, current_session_seconds=?,
                    xp=?, level=?, is_follower=?, last_seen_at=?, updated_at=?,
                    twitch_user_id=COALESCE(?, twitch_user_id)
                    WHERE username=?
                    """,
                    (
                        total,
                        session,
                        xp,
                        level,
                        1 if is_follower else 0,
                        now,
                        now,
                        twitch_user_id or None,
                        u,
                    ),
                )
                # watch session
                sess = conn.execute(
                    """
                    SELECT id FROM watch_sessions WHERE username=?
                    ORDER BY id DESC LIMIT 1
                    """,
                    (u,),
                ).fetchone()
                if sess is None:
                    conn.execute(
                        """
                        INSERT INTO watch_sessions (
                            twitch_user_id, username, stream_started_at,
                            first_seen_at, last_seen_at, accumulated_seconds
                        ) VALUES (?, ?, ?, ?, ?, ?)
                        """,
                        (twitch_user_id or None, u, now, now, now, int(seconds)),
                    )
                else:
                    conn.execute(
                        """
                        UPDATE watch_sessions SET last_seen_at=?,
                        accumulated_seconds=accumulated_seconds+?,
                        twitch_user_id=COALESCE(?, twitch_user_id)
                        WHERE id=?
                        """,
                        (now, int(seconds), twitch_user_id or None, int(sess["id"])),
                    )
                conn.execute(
                    "UPDATE characters SET level=?, xp=? WHERE username=? AND is_active=1",
                    (level, xp, u),
                )
                conn.commit()
                return {
                    "username": u,
                    "total_watch_seconds": total,
                    "xp": xp,
                    "level": level,
                }
            finally:
                conn.close()

    def get_top(self, limit: int = 10) -> List[Dict[str, Any]]:
        with self._lock:
            conn = self._connect()
            try:
                rows = conn.execute(
                    """
                    SELECT * FROM users
                    ORDER BY total_watch_seconds DESC, xp DESC LIMIT ?
                    """,
                    (limit,),
                ).fetchall()
                return [dict(r) for r in rows]
            finally:
                conn.close()

    # legacy stubs used by vote_manager
    def add_points(self, username: str, points: int) -> None:
        u = username.lower().strip()
        self.get_or_create_user(u)
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                conn.execute(
                    "UPDATE users SET points=COALESCE(points,0)+?, updated_at=? WHERE username=?",
                    (points, now, u),
                )
                conn.commit()
            finally:
                conn.close()

    def increment_votes(self, username: str) -> None:
        self.add_points(username, 0)

    def increment_commands(self, username: str) -> None:
        pass

    def increment_llm_suggestions(self, username: str) -> None:
        pass

    def increment_successful_events(self, username: str) -> None:
        pass
