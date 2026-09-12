"""SQLite: активные игроки Streaming Survival."""

from __future__ import annotations

import re
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
                        total_zombies_killed INTEGER DEFAULT 0,
                        total_campfires_built INTEGER DEFAULT 0,
                        total_barricades_built INTEGER DEFAULT 0
                    )
                    """
                )
                self._ensure_columns(conn)
                conn.commit()
            finally:
                conn.close()

    @staticmethod
    def _ensure_columns(conn: sqlite3.Connection) -> None:
        cols = {row[1] for row in conn.execute("PRAGMA table_info(streaming_survival_users)")}
        if "join_source" not in cols:
            conn.execute(
                "ALTER TABLE streaming_survival_users ADD COLUMN join_source TEXT DEFAULT 'chat'"
            )
        if "total_survival_seconds" not in cols:
            conn.execute(
                "ALTER TABLE streaming_survival_users ADD COLUMN total_survival_seconds REAL DEFAULT 0"
            )
        if "session_joined_at" not in cols:
            conn.execute(
                "ALTER TABLE streaming_survival_users ADD COLUMN session_joined_at REAL DEFAULT 0"
            )
        if "total_zombies_killed" not in cols:
            conn.execute(
                "ALTER TABLE streaming_survival_users ADD COLUMN total_zombies_killed INTEGER DEFAULT 0"
            )
        if "skin" not in cols:
            conn.execute(
                "ALTER TABLE streaming_survival_users ADD COLUMN skin TEXT DEFAULT 'human'"
            )
        if "total_barricades_built" not in cols:
            conn.execute(
                "ALTER TABLE streaming_survival_users ADD COLUMN total_barricades_built INTEGER DEFAULT 0"
            )
        if "hide_reason" not in cols:
            conn.execute(
                "ALTER TABLE streaming_survival_users ADD COLUMN hide_reason TEXT DEFAULT ''"
            )
        if "first_join_stream_mode" not in cols:
            conn.execute(
                "ALTER TABLE streaming_survival_users ADD COLUMN first_join_stream_mode TEXT DEFAULT ''"
            )
        if "first_action_stream_mode" not in cols:
            conn.execute(
                "ALTER TABLE streaming_survival_users ADD COLUMN first_action_stream_mode TEXT DEFAULT ''"
            )

    _STAT_FIELDS = {
        "water": "total_water_collected",
        "food": "total_food_collected",
        "sheep": "total_sheep_killed",
        "zombie": "total_zombies_killed",
        "campfire": "total_campfires_built",
        "barricade": "total_barricades_built",
        "wood": "total_wood_collected",
        "round": "total_rounds_participated",
    }

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

    def join(self, username: str, twitch_user_id: str = "", join_source: str = "chat") -> bool:
        """True если пользователь только что стал активным (новый join)."""
        u = username.lower().strip()
        src = (join_source or "chat").strip().lower() or "chat"
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
                            last_action, last_action_name, last_action_changed_at, join_source,
                            session_joined_at
                        ) VALUES (?, ?, ?, ?, 1, 'idle', 'Ждёт у базы', 0, ?, ?)
                        """,
                        (u, twitch_user_id or None, now, now, src, now),
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
                            twitch_user_id=COALESCE(?, twitch_user_id),
                            join_source=?, hide_reason=''
                        WHERE username=?
                        """,
                        (now, twitch_user_id or None, src, u),
                    )
                else:
                    conn.execute(
                        """
                        UPDATE streaming_survival_users
                        SET is_active=1, last_seen_at=?, session_joined_at=?,
                            twitch_user_id=COALESCE(?, twitch_user_id),
                            last_action='idle', last_action_name='Ждёт у базы',
                            last_action_changed_at=0, join_source=?, hide_reason=''
                        WHERE username=?
                        """,
                        (now, now, twitch_user_id or None, src, u),
                    )
                conn.commit()
                # уже был активен → «уже в игре»; иначе свежий вход
                return not was_active
            finally:
                conn.close()

    def leave(self, username: str) -> bool:
        """True если был активен и вышел. Строка в БД НЕ удаляется — только is_active=0."""
        u = username.lower().strip()
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                row = conn.execute(
                    "SELECT is_active, session_joined_at, total_survival_seconds FROM streaming_survival_users WHERE username=?",
                    (u,),
                ).fetchone()
                if row is None or not int(row["is_active"] or 0):
                    return False
                session_start = float(row["session_joined_at"] or 0)
                if session_start <= 0:
                    session_start = now
                lived = max(0.0, now - session_start)
                total = float(row["total_survival_seconds"] or 0) + lived
                conn.execute(
                    """
                    UPDATE streaming_survival_users
                    SET is_active=0, last_seen_at=?, last_action='idle',
                        last_action_name='Ждёт у базы',
                        total_survival_seconds=?, session_joined_at=0,
                        hide_reason='exit'
                    WHERE username=?
                    """,
                    (now, total, u),
                )
                conn.commit()
                return True
            finally:
                conn.close()

    # Soft-hide из мира (строка в БД остаётся). TEST: 7 дней (было 48ч).
    # Tue/Fri hosted loop ≈72ч — 48ч прятал персонажа до следующего стрима.
    INACTIVE_HIDE_SECONDS = 7 * 24 * 3600
    INACTIVE_HIDE_MARKER = "INACTIVE_HIDE_7D_V1"

    def deactivate_inactive(
        self, max_idle_seconds: Optional[float] = None
    ) -> List[str]:
        """Снять is_active у тех, кто давно без активности. Статистика в БД сохраняется.

        Возвращает список username, которых только что убрали из игры.
        """
        idle = float(
            self.INACTIVE_HIDE_SECONDS
            if max_idle_seconds is None
            else max_idle_seconds
        )
        if idle <= 0:
            return []
        cutoff = time.time() - idle
        with self._lock:
            conn = self._connect()
            try:
                rows = conn.execute(
                    """
                    SELECT username, session_joined_at, total_survival_seconds,
                           last_seen_at, last_action_changed_at
                    FROM streaming_survival_users
                    WHERE is_active=1
                    """
                ).fetchall()
                removed: List[str] = []
                now = time.time()
                for row in rows:
                    last_seen = float(row["last_seen_at"] or 0)
                    last_act = float(row["last_action_changed_at"] or 0)
                    session = float(row["session_joined_at"] or 0)
                    last_activity = max(last_seen, last_act, session)
                    if last_activity <= 0 or last_activity >= cutoff:
                        continue
                    u = str(row["username"] or "").strip()
                    if not u:
                        continue
                    session_start = session if session > 0 else last_activity
                    lived = max(0.0, now - session_start)
                    total = float(row["total_survival_seconds"] or 0) + lived
                    conn.execute(
                        """
                        UPDATE streaming_survival_users
                        SET is_active=0,
                            total_survival_seconds=?, session_joined_at=0,
                            hide_reason='inactivity'
                        WHERE username=? AND is_active=1
                        """,
                        (total, u),
                    )
                    removed.append(u)
                if removed:
                    conn.commit()
                return removed
            finally:
                conn.close()

    def reactivate_inactivity_within_window(
        self, window_seconds: Optional[float] = None
    ) -> Dict[str, Any]:
        """Re-activate soft-hidden users whose last activity is still inside the hide window.

        Explicit #exit (hide_reason='exit') are never restored.
        Historical rows without hide_reason are treated as inactivity unless last leave was exit.
        """
        window = float(
            self.INACTIVE_HIDE_SECONDS if window_seconds is None else window_seconds
        )
        cutoff = time.time() - window
        eligible: List[str] = []
        reactivated: List[str] = []
        with self._lock:
            conn = self._connect()
            try:
                rows = conn.execute(
                    """
                    SELECT username, hide_reason, last_seen_at, last_action_changed_at,
                           session_joined_at, is_active
                    FROM streaming_survival_users
                    WHERE is_active=0
                    """
                ).fetchall()
                for row in rows:
                    u = str(row["username"] or "").strip()
                    if not u:
                        continue
                    reason = str(row["hide_reason"] or "").strip().lower()
                    if reason == "exit":
                        continue
                    # Only inactivity (explicit or legacy empty).
                    if reason and reason not in ("inactivity", "inactive", "soft_hide"):
                        continue
                    last_activity = max(
                        float(row["last_seen_at"] or 0),
                        float(row["last_action_changed_at"] or 0),
                        float(row["session_joined_at"] or 0),
                    )
                    if last_activity < cutoff:
                        continue
                    eligible.append(u)
                    # Keep last_action as-is (do not force idle). Soft-hide must not
                    # wipe what the follower was doing — they resume after reactivation.
                    conn.execute(
                        """
                        UPDATE streaming_survival_users
                        SET is_active=1, hide_reason=''
                        WHERE username=? AND is_active=0
                          AND COALESCE(hide_reason,'') != 'exit'
                        """,
                        (u,),
                    )
                    reactivated.append(u)
                if reactivated:
                    conn.commit()
            finally:
                conn.close()
        # If historical soft-hide already wiped actions to idle, recover from events.
        restored = self.restore_actions_from_events(reactivated)
        out = {
            "eligible_for_reactivation_count": len(eligible),
            "actually_reactivated_count": len(reactivated),
            "usernames": reactivated,
            "restored_actions": restored,
            "window_seconds": window,
            "marker": self.INACTIVE_HIDE_MARKER,
        }
        return out

    def restore_actions_from_events(self, usernames: Optional[List[str]] = None) -> Dict[str, str]:
        """For active idle users, restore last non-idle ss_action from event log."""
        from stream_bot.validator import ACTION_NAMES_RU

        restored_map: Dict[str, str] = {}
        with self._lock:
            conn = self._connect()
            try:
                if usernames:
                    rows = []
                    for name in usernames:
                        r = conn.execute(
                            "SELECT username, last_action FROM streaming_survival_users WHERE username=? AND is_active=1",
                            (name.lower().strip(),),
                        ).fetchone()
                        if r:
                            rows.append(r)
                else:
                    rows = conn.execute(
                        """
                        SELECT username, last_action FROM streaming_survival_users
                        WHERE is_active=1 AND COALESCE(last_action,'idle') IN ('','idle')
                        """
                    ).fetchall()
                for row in rows:
                    u = str(row["username"] or "").strip()
                    if not u:
                        continue
                    act = str(row["last_action"] or "idle").strip().lower()
                    if act and act != "idle":
                        continue
                    ev = conn.execute(
                        """
                        SELECT message FROM events
                        WHERE username=? AND type='ss_action'
                          AND message IS NOT NULL AND TRIM(message) != ''
                          AND LOWER(TRIM(message)) != 'idle'
                        ORDER BY id DESC LIMIT 1
                        """,
                        (u,),
                    ).fetchone()
                    if not ev:
                        continue
                    action = str(ev["message"] or "").strip().lower()
                    if not action or action == "idle":
                        continue
                    action_name = ACTION_NAMES_RU.get(action) or action
                    conn.execute(
                        """
                        UPDATE streaming_survival_users
                        SET last_action=?, last_action_name=?
                        WHERE username=? AND is_active=1
                        """,
                        (action, action_name, u),
                    )
                    restored_map[u] = action
                if restored_map:
                    conn.commit()
            finally:
                conn.close()
        return restored_map

    def touch_activity(self, username: str) -> bool:
        """Обновить last_seen_at. Не реактивирует вышедших (#exit / 48ч)."""
        u = username.lower().strip()
        if not u:
            return False
        now = time.time()
        with self._lock:
            conn = self._connect()
            try:
                cur = conn.execute(
                    "UPDATE streaming_survival_users SET last_seen_at=? WHERE username=?",
                    (now, u),
                )
                conn.commit()
                return int(cur.rowcount or 0) > 0
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
        existing = self.get_user(u)
        src = (existing or {}).get("join_source") or "chat"
        self.join(u, join_source=str(src))
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

    _CHAT_ROSTER_WHERE = "is_active=1 AND (join_source='chat' OR join_source IS NULL)"

    def list_active(self) -> List[Dict[str, Any]]:
        with self._lock:
            conn = self._connect()
            try:
                rows = conn.execute(
                    f"""
                    SELECT username, last_action AS action, last_action_name AS action_name,
                           COALESCE(skin, 'human') AS skin
                    FROM streaming_survival_users
                    WHERE {self._CHAT_ROSTER_WHERE}
                    ORDER BY joined_at ASC
                    """
                ).fetchall()
                return [dict(r) for r in rows]
            finally:
                conn.close()

    def list_roster(self) -> List[Dict[str, Any]]:
        """Активные игроки из Twitch-чата (#join, не #exit)."""
        return self.list_players(active_only=True, chat_only=True)

    _TEST_NAME_RE = re.compile(
        r"^(debug_user|viewer(_\d+)?|ui_smoke|testjoin|joincheck|twitch_join_test|"
        r"crash_test|jack_model_test|"
        r"(stress_user|u|t|ck|ep|coord|pond|e2e|wfix)\w*)$",
        re.I,
    )

    def list_players(
        self,
        *,
        active_only: bool = False,
        chat_only: bool = True,
        hide_test: bool = False,
    ) -> List[Dict[str, Any]]:
        """Игроки с датами и статистикой (для UI / #stats history)."""
        where = ["1=1"]
        if active_only:
            where.append("is_active=1")
        if chat_only:
            where.append("(join_source='chat' OR join_source IS NULL)")
        sql = f"""
            SELECT username, twitch_user_id, joined_at, last_seen_at,
                   session_joined_at, last_action AS action, last_action_name AS action_name,
                   last_action_changed_at, is_active, join_source,
                   total_water_collected, total_wood_collected, total_food_collected,
                   total_sheep_killed, total_zombies_killed, total_campfires_built,
                   total_barricades_built,
                   total_rounds_participated,
                   total_survival_seconds
            FROM streaming_survival_users
            WHERE {' AND '.join(where)}
            ORDER BY is_active DESC, joined_at ASC
        """
        with self._lock:
            conn = self._connect()
            try:
                rows = conn.execute(sql).fetchall()
                out: List[Dict[str, Any]] = []
                for row in rows:
                    d = dict(row)
                    name = str(d.get("username") or "")
                    if hide_test and self._TEST_NAME_RE.match(name):
                        continue
                    d["is_active"] = bool(int(d.get("is_active") or 0))
                    d["survival_seconds"] = self.survival_seconds(d)
                    out.append(d)
                return out
            finally:
                conn.close()

    def deactivate_local_debug(self) -> int:
        with self._lock:
            conn = self._connect()
            try:
                cur = conn.execute(
                    """
                    UPDATE streaming_survival_users
                    SET is_active=0, last_action='idle', last_action_name='Ждёт у базы'
                    WHERE is_active=1 AND join_source='local_debug'
                    """
                )
                conn.commit()
                return int(cur.rowcount or 0)
            finally:
                conn.close()

    def deactivate_usernames(self, usernames: List[str]) -> int:
        names = [u.lower().strip() for u in usernames if (u or "").strip()]
        if not names:
            return 0
        with self._lock:
            conn = self._connect()
            try:
                n = 0
                for u in names:
                    cur = conn.execute(
                        """
                        UPDATE streaming_survival_users
                        SET is_active=0, last_action='idle', last_action_name='Ждёт у базы'
                        WHERE username=? AND is_active=1
                        """,
                        (u,),
                    )
                    n += int(cur.rowcount or 0)
                conn.commit()
                return n
            finally:
                conn.close()

    def bump_stat(self, username: str, field: str, amount: int = 1) -> None:
        allowed = {
            "total_water_collected",
            "total_wood_collected",
            "total_food_collected",
            "total_sheep_killed",
            "total_zombies_killed",
            "total_campfires_built",
            "total_barricades_built",
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

    def record_stat(self, username: str, stat: str, amount: int = 1) -> None:
        field = self._STAT_FIELDS.get((stat or "").strip().lower())
        if not field:
            return
        u = username.lower().strip()
        if not u or not self.get_user(u):
            return
        self.bump_stat(u, field, max(1, int(amount)))

    def survival_seconds(self, row: Dict[str, Any]) -> float:
        total = float(row.get("total_survival_seconds") or 0)
        if int(row.get("is_active") or 0) != 1:
            return total
        session_start = float(row.get("session_joined_at") or 0)
        if session_start <= 0:
            session_start = float(row.get("joined_at") or 0)
        if session_start <= 0:
            return total
        return total + max(0.0, time.time() - session_start)

    def list_top_by_survival_time(
        self,
        *,
        limit: int = 5,
        chat_only: bool = True,
        hide_test: bool = True,
    ) -> List[Dict[str, Any]]:
        """Топ-N по суммарному времени в игре (включая неактивных)."""
        limit = max(1, min(int(limit or 5), 20))
        players = self.list_players(
            active_only=False, chat_only=chat_only, hide_test=hide_test
        )
        players.sort(
            key=lambda p: float(p.get("survival_seconds") or 0), reverse=True
        )
        out: List[Dict[str, Any]] = []
        for row in players:
            secs = float(row.get("survival_seconds") or 0)
            if secs <= 0:
                continue
            out.append(
                {
                    "rank": len(out) + 1,
                    "username": row.get("username") or "",
                    "survival_seconds": secs,
                    "survival_label": self.format_duration(secs),
                    "is_active": bool(row.get("is_active")),
                }
            )
            if len(out) >= limit:
                break
        return out

    def list_top_by_zombies(
        self,
        *,
        limit: int = 5,
        chat_only: bool = True,
        hide_test: bool = True,
    ) -> List[Dict[str, Any]]:
        """Топ-N по убийствам зомби (включая неактивных)."""
        limit = max(1, min(int(limit or 5), 20))
        players = self.list_players(
            active_only=False, chat_only=chat_only, hide_test=hide_test
        )
        players.sort(
            key=lambda p: int(p.get("total_zombies_killed") or 0), reverse=True
        )
        out: List[Dict[str, Any]] = []
        for row in players:
            kills = int(row.get("total_zombies_killed") or 0)
            if kills <= 0:
                continue
            label = f"{kills} зомби"
            out.append(
                {
                    "rank": len(out) + 1,
                    "username": row.get("username") or "",
                    "zombies_killed": kills,
                    "score_label": label,
                    # HUD/старый парсер читает survival_label как подпись строки.
                    "survival_label": label,
                    "is_active": bool(row.get("is_active")),
                }
            )
            if len(out) >= limit:
                break
        return out

    @staticmethod
    def format_duration(seconds: float) -> str:
        s = max(0, int(seconds))
        if s < 60:
            return f"{s} сек"
        if s < 3600:
            m = s // 60
            return f"{m} мин"
        h = s // 3600
        m = (s % 3600) // 60
        if m:
            return f"{h} ч {m} мин"
        return f"{h} ч"

    def get_skin(self, username: str) -> str:
        row = self.get_user(username)
        if not row:
            return "human"
        skin = (row.get("skin") or "human").strip().lower()
        return skin if skin in ("human", "wolf", "soldier", "builder") else "human"

    def set_skin(self, username: str, skin: str) -> None:
        u = username.lower().strip()
        skin = (skin or "human").strip().lower()
        if skin not in ("human", "wolf", "soldier", "builder"):
            skin = "human"
        with self._lock:
            conn = self._connect()
            try:
                conn.execute(
                    "UPDATE streaming_survival_users SET skin=?, last_seen_at=? WHERE username=?",
                    (skin, time.time(), u),
                )
                conn.commit()
            finally:
                conn.close()

    def sheep_killed(self, username: str) -> int:
        row = self.get_user(username)
        if not row:
            return 0
        return int(row.get("total_sheep_killed") or 0)

    def zombies_killed(self, username: str) -> int:
        row = self.get_user(username)
        if not row:
            return 0
        return int(row.get("total_zombies_killed") or 0)

    def wood_collected(self, username: str) -> int:
        row = self.get_user(username)
        if not row:
            return 0
        return int(row.get("total_wood_collected") or 0)

    def water_collected(self, username: str) -> int:
        row = self.get_user(username)
        if not row:
            return 0
        return int(row.get("total_water_collected") or 0)

    def format_next_class_hint(self, username: str) -> str:
        """Nearest progression goal (centralized resolver). Shown on #join / #stats only."""
        from stream_bot.progression_goals import format_goal_message

        row = self.get_user(username)
        return format_goal_message(row)

    def note_first_join_stream_mode(self, username: str, mode: str) -> None:
        u = username.lower().strip()
        m = (mode or "").strip().lower()
        if not u or not m:
            return
        with self._lock:
            conn = self._connect()
            try:
                row = conn.execute(
                    "SELECT first_join_stream_mode FROM streaming_survival_users WHERE username=?",
                    (u,),
                ).fetchone()
                if row is None:
                    return
                if str(row["first_join_stream_mode"] or "").strip():
                    return
                conn.execute(
                    "UPDATE streaming_survival_users SET first_join_stream_mode=? WHERE username=?",
                    (m, u),
                )
                conn.commit()
            finally:
                conn.close()

    def note_first_action_stream_mode(self, username: str, mode: str) -> None:
        u = username.lower().strip()
        m = (mode or "").strip().lower()
        if not u or not m:
            return
        with self._lock:
            conn = self._connect()
            try:
                row = conn.execute(
                    "SELECT first_action_stream_mode FROM streaming_survival_users WHERE username=?",
                    (u,),
                ).fetchone()
                if row is None:
                    return
                if str(row["first_action_stream_mode"] or "").strip():
                    return
                conn.execute(
                    "UPDATE streaming_survival_users SET first_action_stream_mode=? WHERE username=?",
                    (m, u),
                )
                conn.commit()
            finally:
                conn.close()

    def format_stats_reply(self, username: str) -> str:
        row = self.get_user(username)
        if not row:
            return "Ты ещё не играл. Пиши #join, чтобы войти."
        name = row.get("username") or username
        alive = self.format_duration(self.survival_seconds(row))
        water = int(row.get("total_water_collected") or 0)
        wood = int(row.get("total_wood_collected") or 0)
        # Еда = мясо с овцы: в чате одна метрика «овцы» (Unity пишет только sheep).
        sheep = int(row.get("total_sheep_killed") or 0)
        zombies = int(row.get("total_zombies_killed") or 0)
        fires = int(row.get("total_campfires_built") or 0)
        barricades = int(row.get("total_barricades_built") or 0)
        in_game = " (в игре)" if int(row.get("is_active") or 0) == 1 else ""
        next_class = self.format_next_class_hint(username)
        return (
            f"@{name}{in_game}: в игре {alive} · вода {water} · дерево {wood} · "
            f"овцы {sheep} · зомби {zombies} · костры {fires} · баррикады {barricades}. "
            f"{next_class}"
        )
