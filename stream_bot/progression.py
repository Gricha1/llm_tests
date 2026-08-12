"""Уровни, XP и разблокировка species по watchtime."""

from __future__ import annotations

from typing import Dict, List, Set, Tuple

# Level N открывается при total_watch_minutes >= THRESHOLD[N]
LEVEL_MINUTES = {
    1: 0,
    2: 30,
    3: 90,
    4: 180,
    5: 360,
}

SPECIES_BY_LEVEL: Dict[int, List[str]] = {
    1: ["sheep", "rabbit"],
    2: ["fox", "dog"],
    3: ["wolf"],
    4: ["bear"],
    5: ["sheep", "rabbit", "fox", "dog", "wolf", "bear"],  # legendary = любой + glow flag
}

SPECIES_RU = {
    "sheep": "овечка",
    "rabbit": "кролик",
    "fox": "лиса",
    "dog": "собака",
    "wolf": "волк",
    "bear": "медведь",
}


def level_from_watch_minutes(minutes: float) -> int:
    m = max(0.0, float(minutes))
    level = 1
    for lv in sorted(LEVEL_MINUTES.keys()):
        if m >= LEVEL_MINUTES[lv]:
            level = lv
    return level


def level_from_watch_seconds(seconds: float) -> int:
    return level_from_watch_minutes(float(seconds) / 60.0)


def xp_for_minute() -> int:
    return 1


def unlocked_species(level: int) -> Set[str]:
    out: Set[str] = set()
    for lv, specs in SPECIES_BY_LEVEL.items():
        if lv <= max(1, int(level)) and lv < 5:
            out.update(specs)
        elif lv == 5 and max(1, int(level)) >= 5:
            out.update(specs)
    if not out:
        out = {"sheep", "rabbit"}
    return out


def clamp_species_for_level(
    species: str,
    level: int,
    unlocked: Set[str] | None = None,
) -> Tuple[str, str]:
    """(species, soft_note). note пустой если ок."""
    sp = (species or "sheep").strip().lower()
    unlock = unlocked if unlocked is not None else unlocked_species(level)
    if sp in unlock:
        return sp, ""
    # мягкая замена
    fallback = "sheep" if "sheep" in unlock else next(iter(sorted(unlock)))
    need_lv = 1
    for lv, specs in SPECIES_BY_LEVEL.items():
        if sp in specs:
            need_lv = lv
            break
    ru = SPECIES_RU.get(sp, sp)
    note = f"{ru.capitalize()} откроется на Level {need_lv}. Пока создал {SPECIES_RU.get(fallback, fallback)}."
    return fallback, note


def unlocked_species_csv(level: int) -> str:
    return ",".join(sorted(unlocked_species(level)))
