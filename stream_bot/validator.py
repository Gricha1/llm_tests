"""Whitelist + cooldown — единственный путь команд в Unity."""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Any, Dict, Optional, Tuple


ALLOWED_COMMANDS: Dict[str, Dict[str, Any]] = {
    "add_zombie": {
        "min": 1,
        "max": 5,
        "global_cooldown": 120,
        "user_cooldown": 300,
        "requires_vote": False,
    },
    "food_rain": {
        "min": 1,
        "max": 1,
        "global_cooldown": 120,
        "user_cooldown": 300,
        "requires_vote": False,
    },
    "night": {
        "min": 10,
        "max": 120,
        "global_cooldown": 300,
        "user_cooldown": 600,
        "requires_vote": False,
    },
    "chaos": {
        "min": 10,
        "max": 60,
        "global_cooldown": 600,
        "user_cooldown": 900,
        "requires_vote": False,
    },
    "reset_vote": {
        "min": 1,
        "max": 1,
        "global_cooldown": 600,
        "user_cooldown": 900,
        "requires_vote": False,
    },
    "reset": {
        "min": 0,
        "max": 0,
        "global_cooldown": 600,
        "user_cooldown": 900,
        "requires_vote": False,
    },
    "tree_reward": {
        "min": -2,
        "max": 5,
        "global_cooldown": 300,
        "user_cooldown": 600,
        "requires_vote": False,
    },
    "zombie_speed": {
        "min": 0.5,
        "max": 2.0,
        "global_cooldown": 300,
        "user_cooldown": 600,
        "requires_vote": False,
    },
    "heal_agent": {
        "min": 1,
        "max": 50,
        "global_cooldown": 180,
        "user_cooldown": 300,
        "requires_vote": False,
    },
    "do_nothing": {
        "min": 0,
        "max": 0,
        "global_cooldown": 0,
        "user_cooldown": 0,
        "requires_vote": False,
    },
}


@dataclass
class ValidationResult:
    ok: bool
    command: str
    value: float
    reason: str
    requires_vote: bool
    cooldown_left: float = 0.0


class CommandValidator:
    def __init__(
        self,
        global_fallback_cooldown: int = 30,
        user_fallback_cooldown: int = 120,
    ) -> None:
        self.global_fallback_cooldown = global_fallback_cooldown
        self.user_fallback_cooldown = user_fallback_cooldown
        self._global_last: Dict[str, float] = {}
        self._user_last: Dict[Tuple[str, str], float] = {}

    def validate(
        self,
        command: str,
        value: float,
        username: str,
        *,
        skip_cooldown: bool = False,
        force_requires_vote: Optional[bool] = None,
    ) -> ValidationResult:
        cmd = (command or "").strip().lower()
        spec = ALLOWED_COMMANDS.get(cmd)
        if spec is None:
            return ValidationResult(
                ok=False,
                command=cmd,
                value=0.0,
                reason=f"команда не в whitelist: {cmd}",
                requires_vote=False,
            )

        lo = float(spec["min"])
        hi = float(spec["max"])
        try:
            num = float(value)
        except (TypeError, ValueError):
            num = lo
        clamped = max(lo, min(hi, num))

        requires_vote = bool(spec["requires_vote"])
        if force_requires_vote is not None:
            requires_vote = force_requires_vote

        if skip_cooldown or cmd == "do_nothing":
            return ValidationResult(
                ok=True,
                command=cmd,
                value=clamped,
                reason="ok",
                requires_vote=requires_vote,
            )

        now = time.time()
        g_cd = int(spec.get("global_cooldown") or self.global_fallback_cooldown)
        u_cd = int(spec.get("user_cooldown") or self.user_fallback_cooldown)

        g_last = self._global_last.get(cmd, 0.0)
        g_left = g_cd - (now - g_last)
        if g_cd > 0 and g_left > 0:
            return ValidationResult(
                ok=False,
                command=cmd,
                value=clamped,
                reason=f"global cooldown: осталось {int(g_left)} сек",
                requires_vote=requires_vote,
                cooldown_left=g_left,
            )

        u_key = (username.lower(), cmd)
        u_last = self._user_last.get(u_key, 0.0)
        u_left = u_cd - (now - u_last)
        if u_cd > 0 and u_left > 0:
            return ValidationResult(
                ok=False,
                command=cmd,
                value=clamped,
                reason=f"user cooldown: осталось {int(u_left)} сек",
                requires_vote=requires_vote,
                cooldown_left=u_left,
            )

        return ValidationResult(
            ok=True,
            command=cmd,
            value=clamped,
            reason="ok",
            requires_vote=requires_vote,
        )

    def mark_used(self, command: str, username: str) -> None:
        cmd = command.lower()
        now = time.time()
        self._global_last[cmd] = now
        self._user_last[(username.lower(), cmd)] = now

    def validate_poll_option(self, command: str, value: float) -> ValidationResult:
        """Для опций poll — без cooldown (cooldown при исполнении победителя)."""
        return self.validate(command, value, username="poll", skip_cooldown=True)
