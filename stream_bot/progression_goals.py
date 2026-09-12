"""Centralized progression goal resolver for #join / #stats."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, List, Optional

from stream_bot.command_parser import (
    BUILDER_WATER_REQUIREMENT,
    BUILDER_WOOD_REQUIREMENT,
    SOLDIER_ZOMBIE_REQUIREMENT,
    WOLF_SHEEP_REQUIREMENT,
)

# Future goals (commands not shipped yet — do not advertise fake #i_*)
ROBOT_ZOMBIE_REQUIREMENT = 150_000
BUILDER_UPGRADE_BARRICADES = 10_000
HAS_I_ROBOT_COMMAND = False
HAS_I_BUILDER_UPGRADE_COMMAND = False


def progress_bar(pct: float, width: int = 10) -> str:
    pct = max(0.0, min(100.0, float(pct)))
    filled = int(round(width * pct / 100.0))
    filled = max(0, min(width, filled))
    return "🟩" * filled + "⬜" * (width - filled)


@dataclass
class GoalResult:
    target_role: str
    label: str
    command_hint: str  # may be empty when unlock command not shipped yet
    progress_percent: float
    remaining_parts: List[str]  # resource lines like "баррикады 5609 / 10000"
    is_future: bool
    source_skin: str

    def format_message(self) -> str:
        """Single-line for Twitch: PRIVMSG is truncated at the first \\n."""
        bar = progress_bar(self.progress_percent)
        pct = int(round(self.progress_percent))
        cmd = f" {self.command_hint}" if self.command_hint else ""
        head = f"🎯 Ближайшая цель: {self.label}{cmd}"
        if self.remaining_parts:
            rem = " · ".join(self.remaining_parts)
        elif self.command_hint:
            rem = "Готово — можно брать класс!"
        else:
            rem = "Готово"
        return f"{head} · {bar} {pct}% · {rem}"


def _pct(have: float, need: float) -> float:
    if need <= 0:
        return 100.0
    return max(0.0, min(100.0, 100.0 * float(have) / float(need)))


def _have_need(label: str, have: int, need: int) -> str:
    return f"{label} {int(have)} / {int(need)}"


def _human_goals(row: Dict[str, Any]) -> GoalResult:
    """Nearest of Wolf vs Builder only (explicit product rule)."""
    sheep = int(row.get("total_sheep_killed") or 0)
    wood = int(row.get("total_wood_collected") or 0)
    water = int(row.get("total_water_collected") or 0)
    wolf_pct = _pct(sheep, WOLF_SHEEP_REQUIREMENT)
    builder_pct = min(_pct(wood, BUILDER_WOOD_REQUIREMENT), _pct(water, BUILDER_WATER_REQUIREMENT))

    wolf = GoalResult(
        target_role="wolf",
        label="🐺 Wolf",
        command_hint="#i_wolf",
        progress_percent=wolf_pct,
        remaining_parts=[_have_need("еда", sheep, WOLF_SHEEP_REQUIREMENT)],
        is_future=False,
        source_skin="human",
    )
    builder = GoalResult(
        target_role="builder",
        label="🧱 Builder",
        command_hint="#i_builder",
        progress_percent=builder_pct,
        remaining_parts=[
            _have_need("дерево", wood, BUILDER_WOOD_REQUIREMENT),
            _have_need("вода", water, BUILDER_WATER_REQUIREMENT),
        ],
        is_future=False,
        source_skin="human",
    )
    # Highest progress = nearest. Tie → Wolf.
    if builder_pct > wolf_pct:
        return builder
    return wolf


def resolve_goal(row: Optional[Dict[str, Any]]) -> GoalResult:
    if not row:
        return GoalResult(
            target_role="wolf",
            label="🐺 Wolf",
            command_hint="#i_wolf",
            progress_percent=0.0,
            remaining_parts=[_have_need("еда", 0, WOLF_SHEEP_REQUIREMENT)],
            is_future=False,
            source_skin="human",
        )
    skin = (row.get("skin") or "human").strip().lower()
    if skin not in ("human", "wolf", "soldier", "builder"):
        skin = "human"

    if skin == "human":
        return _human_goals(row)

    if skin == "wolf":
        zombies = int(row.get("total_zombies_killed") or 0)
        pct = _pct(zombies, SOLDIER_ZOMBIE_REQUIREMENT)
        return GoalResult(
            target_role="soldier",
            label="⚔️ Soldier",
            command_hint="#i_soldier",
            progress_percent=pct,
            remaining_parts=[_have_need("зомби", zombies, SOLDIER_ZOMBIE_REQUIREMENT)],
            is_future=False,
            source_skin="wolf",
        )

    if skin == "soldier":
        zombies = int(row.get("total_zombies_killed") or 0)
        pct = _pct(zombies, ROBOT_ZOMBIE_REQUIREMENT)
        return GoalResult(
            target_role="robot",
            label="Robot",
            command_hint="" if not HAS_I_ROBOT_COMMAND else "#i_robot",
            progress_percent=pct,
            remaining_parts=[_have_need("зомби", zombies, ROBOT_ZOMBIE_REQUIREMENT)],
            is_future=not HAS_I_ROBOT_COMMAND,
            source_skin="soldier",
        )

    # builder
    barricades = int(row.get("total_barricades_built") or 0)
    pct = _pct(barricades, BUILDER_UPGRADE_BARRICADES)
    return GoalResult(
        target_role="builder_upgrade",
        label="Builder Upgrade",
        command_hint="" if not HAS_I_BUILDER_UPGRADE_COMMAND else "#i_builder_upgrade",
        progress_percent=pct,
        remaining_parts=[_have_need("баррикады", barricades, BUILDER_UPGRADE_BARRICADES)],
        is_future=not HAS_I_BUILDER_UPGRADE_COMMAND,
        source_skin="builder",
    )


def format_goal_message(row: Optional[Dict[str, Any]]) -> str:
    return resolve_goal(row).format_message()


def goal_analytics_payload(row: Optional[Dict[str, Any]], *, goal_source: str) -> Dict[str, Any]:
    g = resolve_goal(row)
    return {
        "goal_shown": True,
        "target_role": g.target_role,
        "goal_progress_percent": round(g.progress_percent, 2),
        "goal_source": goal_source,
        "goal_is_future": g.is_future,
        "goal_label": g.label,
    }
