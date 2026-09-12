#!/usr/bin/env python3
"""One-shot: reactivate soft-hidden users inside 7d window; print evidence."""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from stream_bot.streaming_survival_store import StreamingSurvivalStore


def main() -> int:
    db = os.environ.get("STREAM_BOT_DB") or str(ROOT / "stream_bot.sqlite3")
    # lab common path
    candidates = [
        Path(db),
        ROOT / "stream_bot.sqlite3",
        Path.home() / "lab_work_space/forest_survival/stream_bot.sqlite3",
        Path.home() / "lab_work_space/forest_survival/results/stream_bot.sqlite3",
    ]
    path = next((p for p in candidates if p.is_file()), candidates[0])
    ss = StreamingSurvivalStore(str(path))
    info = ss.reactivate_inactivity_within_window()
    info["db_path"] = str(path)
    print(json.dumps(info, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
