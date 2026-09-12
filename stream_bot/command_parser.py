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
    SKINS = "skins"
    I_WOLF = "i_wolf"
    I_SOLDIER = "i_soldier"
    I_HUMAN = "i_human"
    I_BUILDER = "i_builder"
    IGNORE = "ignore"
    NEED_JOIN = "need_join"
    UNKNOWN_CMD = "unknown_cmd"


@dataclass
class ParsedMessage:
    kind: ParsedKind
    raw: str
    text: Optional[str] = None


_HASH_RE = re.compile(r"^\s*#([a-zA-Zа-яА-ЯёЁ0-9_]+)(?:\s+(.*))?$", re.S)
_DO_ALIASES = frozenset({"do", "behavior", "behaviour"})
_EXIT_ALIASES = frozenset({"exit", "leave", "quit", "leave_game", "delete"})
# Порог для #i_wolf: total_sheep_killed (еда с овец).
WOLF_SHEEP_REQUIREMENT = 30_000
# Порог для #i_soldier: total_zombies_killed.
SOLDIER_ZOMBIE_REQUIREMENT = 5_000
# Порог для #i_builder: дерево + вода.
BUILDER_WOOD_REQUIREMENT = 50_000
BUILDER_WATER_REQUIREMENT = 50_000
WOLF_GATHER_ACTIONS = frozenset(
    {
        # Только добыча/работа — approach к воде/базе/дому/костру разрешён.
        "collect_water",
        "collect_wood",
        "collect_stone",
        "collect_food",
        "kill_sheep",
        "build_campfire",
        "build_walls",
        "go_to_tree",
        "go_to_sheep",
    }
)
# Солдат/волк: нельзя добывать и ходить к деревьям/овцам; можно вода/база/дом/костёр + kill_zombie.
COMBAT_SKIN_GATHER_ACTIONS = WOLF_GATHER_ACTIONS
# Строитель: нельзя добывать/бить зомби/идти к деревьям·овцам; можно build_walls + navigation.
BUILDER_FORBIDDEN_ACTIONS = frozenset(
    {
        "collect_water",
        "collect_wood",
        "collect_stone",
        "collect_food",
        "kill_sheep",
        "build_campfire",
        "kill_zombie",
        "attack_user",
        "go_to_tree",
        "go_to_sheep",
    }
)


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
    if cmd in ("skins", "skin", "скины", "скин"):
        return ParsedMessage(ParsedKind.SKINS, text, text=arg)
    if cmd in _EXIT_ALIASES:
        return ParsedMessage(ParsedKind.EXIT, text, text=arg)
    if cmd in ("i_wolf", "iwolf"):
        if not has_joined:
            return ParsedMessage(ParsedKind.NEED_JOIN, text, text=arg)
        return ParsedMessage(ParsedKind.I_WOLF, text, text=arg)
    if cmd in ("i_soldier", "isoldier", "i_gun", "igun", "soldier"):
        if not has_joined:
            return ParsedMessage(ParsedKind.NEED_JOIN, text, text=arg)
        return ParsedMessage(ParsedKind.I_SOLDIER, text, text=arg)
    if cmd in ("i_human", "ihuman", "i_jack", "ihumanoid"):
        if not has_joined:
            return ParsedMessage(ParsedKind.NEED_JOIN, text, text=arg)
        return ParsedMessage(ParsedKind.I_HUMAN, text, text=arg)
    if cmd in ("i_builder", "ibuilder", "builder", "строитель"):
        if not has_joined:
            return ParsedMessage(ParsedKind.NEED_JOIN, text, text=arg)
        return ParsedMessage(ParsedKind.I_BUILDER, text, text=arg)
    if cmd in _DO_ALIASES:
        if not has_joined:
            return ParsedMessage(ParsedKind.NEED_JOIN, text, text=arg)
        return ParsedMessage(ParsedKind.DO, text, text=arg)
    return ParsedMessage(ParsedKind.UNKNOWN_CMD, text, text=cmd)


JOIN_PROMPT = "Пиши #join, чтобы войти в игру"
JOIN_OK = "Ты добавлен в игру. Напиши #do добывай воду, чтобы задать действие."
JOIN_ALREADY = "Ты уже в игре. Напиши #do <действие>, чтобы сменить действие."
EXIT_OK = "Ты вышел из игры (статистика сохранена). Чтобы вернуться — #join."
EXIT_NOT_IN = "Ты не в игре. Пиши #join, чтобы войти."
WOLF_OK = "Аууу. Ты теперь волк — #do бей зомби."
WOLF_NEED_SHEEP = (
    "Волк доступен после 30000 еды. Сейчас у тебя {sheep}."
)
WOLF_NEED_JOIN = "Сначала #join, потом #i_wolf."
SOLDIER_OK = "Солдат с автоматом! #do бей зомби — стреляет пулями (есть КД)."
SOLDIER_NEED_ZOMBIES = (
    "Солдат доступен после 5000 убитых зомби. Сейчас у тебя {zombies}."
)
SOLDIER_NEED_JOIN = "Сначала #join, потом #i_soldier."
HUMAN_OK = "Снова человек — можно добывать воду / дерево / еду."
HUMAN_NEED_JOIN = "Сначала #join, потом #i_human."
BUILDER_OK = "Строитель! #do ставь стены — строит баррикады на Front_1..4."
BUILDER_NEED_JOIN = "Сначала #join, потом #i_builder."
BUILDER_NEED_RESOURCES = (
    "Строитель доступен после 50000 дерева и 50000 воды. "
    "Сейчас у тебя дерево {wood}, вода {water}."
)
BUILDER_FORBIDDEN_DENIED = (
    "Строитель не добывает ресурсы. Можно: #do ставь стены · #do к базе · #do к воде. "
    "Вернуться: #i_human"
)
WOLF_GATHER_DENIED = (
    "В облике волка нельзя добывать ресурсы. "
    "Можно: #do бей зомби · #do иди к воде · #do к базе · #do к костру. "
    "Вернуться: #i_human"
)
SOLDIER_GATHER_DENIED = (
    "В облике солдата нельзя добывать ресурсы. "
    "Можно: #do бей зомби · #do иди к воде · #do к базе · #do к костру. "
    "Вернуться: #i_human"
)
# Одна строка — Twitch часто режет многострочные PRIVMSG до первого \n.
SKINS_HELP = (
    "Скины: #i_human (вода/дерево/еда); "
    "#i_builder — строитель (стены), нужно 50000 дерева и 50000 воды; "
    "#i_soldier — автомат, с 5000 убитых зомби; "
    "#i_wolf — ближний бой, с 30000 еды. Панель слева снизу на экране."
)
FOLLOWER_ONLY = (
    "Только фолловеры могут добавлять персонажей. Нажми Follow и попробуй ещё раз."
)
DO_EMPTY = "Напиши: #do <действие> (например: #do добывай воду)"
_UNKNOWN_CMD_TYPOS = {
    "jon": "join",
    "jion": "join",
    "jin": "join",
    "joim": "join",
    "jojn": "join",
    "stat": "stats",
    "statsa": "stats",
    "doo": "do",
    "dos": "do",
    "exi": "exit",
    "ext": "exit",
    "iwolf": "i_wolf",
    "isoldier": "i_soldier",
    "ihuman": "i_human",
    "ibuilder": "i_builder",
}


def unknown_command_reply(cmd: str) -> str:
    c = (cmd or "").strip().lower()
    hint = _UNKNOWN_CMD_TYPOS.get(c)
    if hint:
        return f"Нет команды #{c}. Ты имел в виду #{hint}? {HELP_TEXT}"
    return f"Нет такой команды #{c}. {HELP_TEXT}"


UNKNOWN_DO_HINT = (
    "Не понял команду. Попробуй: #do добывай воду · #do руби дерево · "
    "#do убивай овечек · #do бей зомби · #do сделай костер · #do ставь стены · #skins"
)

UNKNOWN_DO_HINT_BY_SKIN = {
    "builder": (
        "Не понял команду. Строитель: #do ставь стены · #do ставь барикады · "
        "#do к базе · #do к воде · #skins"
    ),
    "wolf": (
        "Не понял команду. Волк: #do бей зомби · #do иди к воде · "
        "#do к базе · #do к костру · #skins"
    ),
    "soldier": (
        "Не понял команду. Солдат: #do бей зомби · #do иди к воде · "
        "#do к базе · #do к костру · #skins"
    ),
    "human": UNKNOWN_DO_HINT,
}


def unknown_do_hint(skin: str = "human") -> str:
    s = (skin or "human").strip().lower()
    if s not in UNKNOWN_DO_HINT_BY_SKIN:
        s = "human"
    return UNKNOWN_DO_HINT_BY_SKIN[s]
HELP_TEXT = (
    "#join — войти | #do <действие> — поведение | #stats — статистика | "
    "#skins — доступные скины | #exit — выйти"
)
CHAT_TIPS = (
    "Пиши #join, чтобы войти в игру",
    "Пиши #do добывай воду / руби дерево / ставь стены / убивай овечек",
    "Пиши #skins — посмотреть доступные скины",
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
    ("kill_zombie", "бей / атакуй зомби (волк или солдат)"),
    ("build_walls", "ставь стены / баррикады (строитель)"),
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
