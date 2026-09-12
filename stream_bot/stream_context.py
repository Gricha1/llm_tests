"""Stream mode + episode context persisted into analytics events."""

from __future__ import annotations

import os
import threading
from pathlib import Path
from typing import Optional

_lock = threading.Lock()
_mode: Optional[str] = None
_episode_id: str = ""

VALID = frozenset({"autonomous", "hosted", "test"})


def _candidates() -> list[Path]:
    out: list[Path] = []
    env = os.environ.get("FOREST_STREAM_MODE_FILE")
    if env:
        out.append(Path(env))
    root = os.environ.get("FOREST_ROOT")
    if root:
        out.append(Path(root) / "results" / "stream_mode.txt")
    # lab default relative to repo when bot cwd is forest_survival
    out.append(Path("results/stream_mode.txt"))
    out.append(Path.home() / "lab_work_space/forest_survival/results/stream_mode.txt")
    return out


def get_stream_mode() -> str:
    global _mode
    with _lock:
        # Prefer env override for one-shot export tools; UI writes file for live.
        env_mode = (os.environ.get("FOREST_STREAM_MODE") or "").strip().lower()
        if env_mode in VALID:
            return env_mode
        for p in _candidates():
            try:
                if p.is_file():
                    m = p.read_text(encoding="utf-8").strip().lower()
                    if m in VALID:
                        _mode = m
                        return m
            except OSError:
                continue
        if _mode in VALID:
            return _mode  # type: ignore[return-value]
        return "autonomous"


def set_stream_mode(mode: str) -> str:
    global _mode
    mode = (mode or "").strip().lower()
    if mode not in VALID:
        raise ValueError(mode)
    with _lock:
        _mode = mode
        for p in _candidates()[:2]:
            try:
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_text(mode + "\n", encoding="utf-8")
                break
            except OSError:
                continue
    return mode


def get_episode_id() -> str:
    with _lock:
        return _episode_id


def set_episode_id(episode_id: str) -> None:
    global _episode_id
    with _lock:
        _episode_id = (episode_id or "").strip()


def analytics_context() -> dict:
    return {
        "stream_mode": get_stream_mode(),
        "episode_id": get_episode_id() or None,
    }
