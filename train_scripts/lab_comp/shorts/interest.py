"""Episode interest evaluation for Shorts capture (lab-side)."""

from __future__ import annotations

import json
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Set


ALWAYS_INTERESTING = frozenset(
    {
        "AI_OPENED_NEXT_LOCATION",
        "ALL_DEFENDERS_DIED",
        "BOSS_DESTROYED_WALL",
        "VIEWER_BECAME_SOLDIER",
        "VIEWER_BECAME_BUILDER",
    }
)

# Map Unity JSONL event names → interest types
EVENT_MAP = {
    "boss_spawn": "BOSS_SPAWNED",
    "boss_defeated": "BOSS_DESTROYED_WALL",  # treat defeat as wall-clearing climax proxy if no wall event
    "episode_wipe": "ALL_DEFENDERS_DIED",
    "phase_announce": None,  # inspected by detail
    "skin_soldier": "VIEWER_BECAME_SOLDIER",
    "skin_builder": "VIEWER_BECAME_BUILDER",
    "apocalypse_x5": "APOCALYPSE_X5",
    "next_location": "AI_OPENED_NEXT_LOCATION",
    "last_second_survival": "LAST_SECOND_SURVIVAL",
}


@dataclass
class EpisodeInterestTracker:
    episode_id: str
    started_at: float
    ended_at: float = 0.0
    max_episode_progress: float = 0.0
    max_apocalypse_progress: float = 0.0
    events: List[Dict[str, Any]] = field(default_factory=list)
    save_reasons: List[str] = field(default_factory=list)
    manual: bool = False

    def note_progress(self, progress: float) -> None:
        p = float(progress or 0.0)
        if p > self.max_apocalypse_progress:
            self.max_apocalypse_progress = p
        if p > self.max_episode_progress:
            self.max_episode_progress = p

    def note_event(self, event_type: str, detail: str = "", progress: float = 0.0) -> None:
        et = (event_type or "").strip().upper()
        if not et:
            return
        self.note_progress(progress)
        self.events.append(
            {
                "type": et,
                "detail": detail or "",
                "progress": float(progress or 0.0),
                "ts": time.time(),
            }
        )

    def evaluate(self, novelty: "NoveltyState") -> Dict[str, Any]:
        reasons: List[str] = []
        if self.manual:
            reasons.append("manual_capture")

        seen_types: Set[str] = set()
        for ev in self.events:
            et = ev["type"]
            prog = float(ev.get("progress") or 0.0)
            if et in ALWAYS_INTERESTING:
                reasons.append(f"always:{et}")
            if et not in novelty.seen_event_types:
                reasons.append(f"first_occurrence:{et}")
                novelty.seen_event_types.add(et)
            best = novelty.best_progress_at_event.get(et, -1.0)
            if prog > best + 1e-6:
                reasons.append(f"further_than_history:{et}@{prog:.3f}>={best:.3f}")
                novelty.best_progress_at_event[et] = prog
            seen_types.add(et)

        if self.max_apocalypse_progress > novelty.best_episode_progress_ever + 1e-6:
            reasons.append(
                f"new_max_progress:{self.max_apocalypse_progress:.3f}>"
                f"{novelty.best_episode_progress_ever:.3f}"
            )
            novelty.best_episode_progress_ever = self.max_apocalypse_progress

        # de-dupe reasons keep order
        uniq: List[str] = []
        for r in reasons:
            if r not in uniq:
                uniq.append(r)
        self.save_reasons = uniq
        keep = bool(uniq)
        score = float(len(uniq)) + self.max_apocalypse_progress
        return {
            "keep": keep,
            "interest_score": score,
            "save_reasons": uniq,
            "event_types": sorted(seen_types),
        }


@dataclass
class NoveltyState:
    best_episode_progress_ever: float = 0.0
    best_progress_at_event: Dict[str, float] = field(default_factory=dict)
    seen_event_types: Set[str] = field(default_factory=set)

    def to_json(self) -> Dict[str, Any]:
        return {
            "best_episode_progress_ever": self.best_episode_progress_ever,
            "best_progress_at_event": dict(self.best_progress_at_event),
            "seen_event_types": sorted(self.seen_event_types),
        }

    @classmethod
    def from_json(cls, data: Dict[str, Any] | None) -> "NoveltyState":
        if not data:
            return cls()
        return cls(
            best_episode_progress_ever=float(data.get("best_episode_progress_ever") or 0.0),
            best_progress_at_event={
                str(k): float(v) for k, v in (data.get("best_progress_at_event") or {}).items()
            },
            seen_event_types=set(data.get("seen_event_types") or []),
        )

    def save(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.to_json(), ensure_ascii=False, indent=2), encoding="utf-8")

    @classmethod
    def load(cls, path: Path) -> "NoveltyState":
        if not path.is_file():
            return cls()
        try:
            return cls.from_json(json.loads(path.read_text(encoding="utf-8")))
        except (OSError, json.JSONDecodeError, TypeError, ValueError):
            return cls()


def map_unity_event(event_name: str, detail: str = "") -> Optional[str]:
    name = (event_name or "").strip().lower()
    if name in EVENT_MAP:
        mapped = EVENT_MAP[name]
        if mapped:
            return mapped
    if name == "phase_announce":
        d = (detail or "").lower()
        if "босс" in d or "boss" in d:
            return "BOSS_SPAWNED"
        if "ещё сильнее" in d or "x5" in d or "элита" in d:
            return "APOCALYPSE_X5"
    if name == "ss_skin" or name == "skin_change":
        d = (detail or "").lower()
        if "soldier" in d:
            return "VIEWER_BECAME_SOLDIER"
        if "builder" in d:
            return "VIEWER_BECAME_BUILDER"
    return None
