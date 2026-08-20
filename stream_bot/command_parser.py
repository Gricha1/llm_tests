"""Публичные команды Streaming Survival: #join, #do, #exit."""

from __future__ import annotations

import re
from dataclasses import dataclass
from enum import Enum
from typing import Optional


class ParsedKind(str, Enum):
    JOIN = "join"
    DO = "do"
    EXIT = "exit"
    STATS = "stats"
    IGNORE = "ignore"
    NEED_JOIN = "need_join"


@dataclass
class ParsedMessage:
    kind: ParsedKind
    raw: str
    text: Optional[str] = None


_HASH_RE = re.compile(r"^\s*#([a-zA-Zа-яА-ЯёЁ0-9_]+)(?:\s+(.*))?$", re.S)
_DO_ALIASES = frozenset({"do", "behavior", "behaviour"})
_EXIT_ALIASES = frozenset({"exit", "leave", "quit", "leave_game", "delete"})


def parse_message(message: str, *, has_joined: bool = False) -> ParsedMessage:
    text = (message or "").strip()
    if not text:
        return ParsedMessage(ParsedKind.IGNORE, text)
    m = _HASH_RE.match(text)
    if not m:
        return ParsedMessage(ParsedKind.IGNORE, text)
    cmd = (m.group(1) or "").lower()
    arg = (m.group(2) or "").strip()
    if cmd == "join":
        return ParsedMessage(ParsedKind.JOIN, text, text=arg)
    if cmd == "stats":
        return ParsedMessage(ParsedKind.STATS, text, text=arg)
    if cmd in _EXIT_ALIASES:
        return ParsedMessage(ParsedKind.EXIT, text, text=arg)
    if cmd in _DO_ALIASES:
        if not has_joined:
            return ParsedMessage(ParsedKind.NEED_JOIN, text, text=arg)
        return ParsedMessage(ParsedKind.DO, text, text=arg)
    return ParsedMessage(ParsedKind.IGNORE, text)


JOIN_PROMPT = "Пиши #join, чтобы войти в игру"
JOIN_OK = "Ты добавлен в игру. Напиши #do добывай воду, чтобы задать действие."
JOIN_ALREADY = "Ты уже в игре. Напиши #do <действие>, чтобы сменить действие."
EXIT_OK = "Ты вышел из игры. Чтобы вернуться — #join."
EXIT_NOT_IN = "Ты не в игре. Пиши #join, чтобы войти."
FOLLOWER_ONLY = (
    "Только фолловеры могут добавлять персонажей. Нажми Follow и попробуй ещё раз."
)
DO_EMPTY = "Напиши: #do <действие> (например: #do добывай воду)"
UNKNOWN_DO_HINT = (
    "Не понял команду. Попробуй: #do добывай воду · #do руби дерево · "
    "#do убивай овечек · #do сделай костер"
)
HELP_TEXT = "#join — войти | #do <действие> — поведение | #stats — статистика | #exit — выйти"
CHAT_TIPS = (
    "Пиши #join, чтобы войти в игру",
    "Пиши #do добывай воду / руби дерево / убивай овечек",
    "Пиши #stats — твоя статистика",
    "Пиши #exit, чтобы выйти из игры",
)

# Публичный список для UI / тестов / подсказок
AVAILABLE_ACTIONS = (
    ("go_to_water", "иди к воде (без добычи)"),
    ("go_to_campfire", "иди к костру (без постройки)"),
    ("go_to_tree", "иди к дереву"),
    ("go_to_sheep", "иди к овцам"),
    ("go_home", "иди к дому / к базе"),
    ("collect_water", "добывай воду / принеси воды"),
    ("collect_wood", "руби дерево / дрова"),
    ("collect_stone", "добывай камень"),
    ("collect_food", "собирай еду"),
    ("kill_sheep", "убивай овечек"),
    ("build_campfire", "поставь костёр"),
    ("manual_respawn", "перезагрузи персонажа"),
    ("walk_circle", "ходи кругом / по кругу"),
    ("walk_forward", "иди вперёд"),
    ("walk_back", "иди назад"),
    ("patrol", "вперёд и назад"),
    ("spin_in_place", "крутись на месте"),
    ("idle", "жди / гуляй у базы / кружись"),
    ("attack_user", "атакуй игрока"),
)


def available_actions_text() -> str:
    lines = ["Доступные #do действия:"]
    for action, tip in AVAILABLE_ACTIONS:
        lines.append(f"  • {action} — {tip}")
    return "\n".join(lines)


def cooldown_reply(seconds_left: int) -> str:
    n = max(1, int(seconds_left))
    return f"Подожди {n} сек. перед сменой действия."
