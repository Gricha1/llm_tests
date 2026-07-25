"""Парсер: активируется только с обращения «бот …»."""

from __future__ import annotations

import re
from dataclasses import dataclass
from enum import Enum
from typing import Optional, Tuple


class ParsedKind(str, Enum):
    HELP = "help"
    PROFILE = "profile"
    POINTS = "points"
    TOP = "top"
    STATUS = "status"
    COMMAND = "command"
    ASK = "ask"
    UNKNOWN = "unknown"
    IGNORE = "ignore"


@dataclass
class ParsedMessage:
    kind: ParsedKind
    raw: str
    command: Optional[str] = None
    value: Optional[float] = None
    text: Optional[str] = None


# тело после «бот»
_WAKE_RE = re.compile(r"^\s*бот[\s,.:!\-]+(.*)$", re.I | re.S)
_WAKE_ONLY_RE = re.compile(r"^\s*бот\s*$", re.I)

_NATURAL_COMMANDS: Tuple[Tuple[re.Pattern[str], str, float], ...] = (
    (re.compile(r"(зомб|zombie)", re.I), "add_zombie", 2.0),
    (re.compile(r"(еда|овц|food|sheep|корм)", re.I), "food_rain", 1.0),
    (re.compile(r"(ночь|night|темн)", re.I), "night", 60.0),
    (re.compile(r"(хаос|chaos|безумие)", re.I), "chaos", 30.0),
    (re.compile(r"(хил|heal|вылеч|лечен)", re.I), "heal_agent", 15.0),
    (re.compile(r"(сброс|reset|заново)", re.I), "reset", 0.0),
)

_HELP_RE = re.compile(r"(помог|команд|что\s+умеешь|help)", re.I)
_PROFILE_RE = re.compile(r"(профиль|очк|points|profile)", re.I)
_TOP_RE = re.compile(r"(^\s*топ\b|\btop\b)", re.I)
_STATUS_RE = re.compile(r"(статус|status)", re.I)
_ASK_RE = re.compile(r"(почему|зачем|why|что\s+происходит)", re.I)


def parse_message(message: str, bot_nick: str = "") -> ParsedMessage:
    text = (message or "").strip()
    if not text or text.lstrip().startswith("#"):
        return ParsedMessage(ParsedKind.IGNORE, text)

    lower = text.lower()
    nick = (bot_nick or "").lstrip("@").lower()

    body = ""
    # @nick … тоже считаем обращением к боту
    if nick and lower.startswith(f"@{nick}"):
        body = re.sub(rf"^@{re.escape(nick)}\s*", "", text, flags=re.I).strip()
    elif _WAKE_ONLY_RE.match(text):
        return ParsedMessage(ParsedKind.HELP, text)
    else:
        m = _WAKE_RE.match(text)
        if not m:
            return ParsedMessage(ParsedKind.IGNORE, text)
        body = (m.group(1) or "").strip()

    if not body:
        return ParsedMessage(ParsedKind.HELP, text)

    return parse_bot_body(body, raw=text)


def parse_bot_body(body: str, raw: str = "") -> ParsedMessage:
    """Разбор текста после слова «бот»."""
    raw = raw or body
    if _HELP_RE.search(body):
        return ParsedMessage(ParsedKind.HELP, raw, text=body)
    if _TOP_RE.search(body):
        return ParsedMessage(ParsedKind.TOP, raw, text=body)
    if _PROFILE_RE.search(body):
        return ParsedMessage(ParsedKind.PROFILE, raw, text=body)
    if _STATUS_RE.search(body):
        return ParsedMessage(ParsedKind.STATUS, raw, text=body)
    if _ASK_RE.search(body):
        return ParsedMessage(ParsedKind.ASK, raw, text=body)

    for pattern, cmd, val in _NATURAL_COMMANDS:
        if pattern.search(body):
            num = re.search(r"\b([1-9]|10)\b", body)
            use_val = float(num.group(1)) if num and cmd == "add_zombie" else val
            if cmd == "night" and num:
                use_val = float(max(10, min(120, int(num.group(1)))))
            return ParsedMessage(ParsedKind.COMMAND, raw, command=cmd, value=use_val, text=body)

    # не распознали — всё равно UNKNOWN, чтобы бот ответил
    return ParsedMessage(ParsedKind.UNKNOWN, raw, text=body)


HELP_TEXT = (
    "Пиши: бот зомби | бот еда | бот ночь | бот хаос | бот хил | бот сброс | бот помощь"
)

CHAT_TIPS = (
    "бот зомби — добавить зомби",
    "бот еда — еда рядом",
    "бот ночь — ночь",
    "бот помощь — список команд",
)
