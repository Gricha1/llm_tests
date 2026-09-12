#!/usr/bin/env python3
"""Examples of progression goal messages for 4 skins."""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from stream_bot.progression_goals import format_goal_message, resolve_goal


def main() -> None:
    samples = {
        "human": {
            "skin": "human",
            "total_sheep_killed": 12000,
            "total_wood_collected": 5000,
            "total_water_collected": 5000,
            "total_zombies_killed": 0,
            "total_barricades_built": 0,
        },
        "wolf": {
            "skin": "wolf",
            "total_sheep_killed": 30000,
            "total_zombies_killed": 2000,
            "total_wood_collected": 0,
            "total_water_collected": 0,
            "total_barricades_built": 0,
        },
        "soldier": {
            "skin": "soldier",
            "total_zombies_killed": 45000,
            "total_sheep_killed": 30000,
            "total_wood_collected": 0,
            "total_water_collected": 0,
            "total_barricades_built": 0,
        },
        "builder": {
            "skin": "builder",
            "total_wood_collected": 50000,
            "total_water_collected": 50000,
            "total_barricades_built": 2500,
            "total_sheep_killed": 0,
            "total_zombies_killed": 0,
        },
    }
    for name, row in samples.items():
        g = resolve_goal(row)
        print("=" * 40, name, "→", g.target_role)
        print(format_goal_message(row))
        print()


if __name__ == "__main__":
    main()
