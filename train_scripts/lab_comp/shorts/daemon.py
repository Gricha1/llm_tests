"""Lab-side Shorts capture daemon.

Watches Unity obs_stream_events.jsonl, records episodes via ffmpeg (not OBS stream),
evaluates interest, keeps/syncs metadata for local UI.

Usage:
  python -m train_scripts.lab_comp.shorts.daemon
  # or:
  bash train_scripts/lab_comp/shorts/run_shorts_daemon.bash
"""

from __future__ import annotations

import json
import os
import sys
import threading
import time
import uuid
from pathlib import Path
from typing import Any, Dict, Optional

# Allow `python daemon.py` without package install.
_HERE = Path(__file__).resolve().parent
if str(_HERE) not in sys.path:
    sys.path.insert(0, str(_HERE))
_ROOT = _HERE.parents[2]
if str(_ROOT) not in sys.path:
    sys.path.insert(0, str(_ROOT))

try:
    from interest import EpisodeInterestTracker, NoveltyState, map_unity_event
    from recorder import cleanup_temp_dir, start_ffmpeg_record, stop_recorder, which_ffmpeg
except ImportError:
    from .interest import EpisodeInterestTracker, NoveltyState, map_unity_event
    from .recorder import cleanup_temp_dir, start_ffmpeg_record, stop_recorder, which_ffmpeg

ROOT = Path(os.environ.get("FOREST_ROOT") or Path(__file__).resolve().parents[3])
RUN_ID = os.environ.get("RUN_ID") or "jlg_finetune_2"
RESULTS = Path(os.environ.get("FOREST_RESULTS_DIR") or (ROOT / "results" / RUN_ID))
SHORTS_ROOT = Path(os.environ.get("SHORTS_ROOT") or (ROOT / "results" / "shorts"))
TEMP_DIR = SHORTS_ROOT / "temp"
SAVED_DIR = SHORTS_ROOT / "saved"
STATE_PATH = SHORTS_ROOT / "daemon_state.json"
NOVELTY_PATH = SHORTS_ROOT / "novelty_state.json"
INDEX_PATH = SHORTS_ROOT / "index.json"
EVENTS_CANDIDATES = [
    RESULTS / "obs_stream_events.jsonl",
    Path.home() / ".config/unity3d/DefaultCompany/forest_survival/obs_events/obs_stream_events.jsonl",
]


class ShortsDaemon:
    def __init__(self) -> None:
        SHORTS_ROOT.mkdir(parents=True, exist_ok=True)
        TEMP_DIR.mkdir(parents=True, exist_ok=True)
        SAVED_DIR.mkdir(parents=True, exist_ok=True)
        cleanup_temp_dir(TEMP_DIR)
        self.status = "IDLE"
        self.collection_on = False
        self.manual_next = False
        self.error = ""
        self.current_episode: Optional[str] = None
        self.tracker: Optional[EpisodeInterestTracker] = None
        self.recorder = None
        self.detected_events: list[str] = []
        self.recording_started_at = 0.0
        self.novelty = NoveltyState.load(NOVELTY_PATH)
        self._lock = threading.Lock()
        self._load_flags()

    def _load_flags(self) -> None:
        cfg = SHORTS_ROOT / "collection_on"
        self.collection_on = cfg.is_file() and cfg.read_text(encoding="utf-8").strip() in ("1", "true", "ON", "on")
        self.manual_next = (SHORTS_ROOT / "manual_next_episode.flag").is_file()

    def set_collection(self, on: bool) -> None:
        self.collection_on = bool(on)
        p = SHORTS_ROOT / "collection_on"
        if on:
            p.write_text("1\n", encoding="utf-8")
        elif p.exists():
            p.unlink()
        self._persist_state()

    def request_manual_episode(self) -> Dict[str, Any]:
        (SHORTS_ROOT / "manual_next_episode.flag").write_text("1\n", encoding="utf-8")
        self.manual_next = True
        if self.status == "RECORDING":
            # already in episode — wait for next
            self.status = "WAITING_EPISODE"
            msg = "WAITING_FOR_NEXT_EPISODE"
        else:
            self.status = "WAITING_EPISODE"
            msg = "ARMED_FOR_NEXT_EPISODE"
        self._persist_state()
        return {"ok": True, "message": msg, "status": self.status}

    def snapshot(self) -> Dict[str, Any]:
        dur = 0.0
        if self.recording_started_at > 0 and self.status == "RECORDING":
            dur = time.time() - self.recording_started_at
        return {
            "ok": True,
            "collection_on": self.collection_on,
            "manual_next": self.manual_next,
            "status": self.status,
            "current_episode": self.current_episode or "",
            "recording_duration_sec": round(dur, 1),
            "detected_interesting_events": list(self.detected_events),
            "error": self.error,
            "ffmpeg": which_ffmpeg() or "",
            "shorts_root": str(SHORTS_ROOT),
            "saved_count": len(list(SAVED_DIR.glob("*.mp4"))),
        }

    def _persist_state(self) -> None:
        STATE_PATH.write_text(json.dumps(self.snapshot(), ensure_ascii=False, indent=2), encoding="utf-8")

    def _find_events_file(self) -> Optional[Path]:
        for p in EVENTS_CANDIDATES:
            if p.is_file():
                return p
        # create primary so Unity can write when FOREST_RESULTS_DIR set
        primary = EVENTS_CANDIDATES[0]
        primary.parent.mkdir(parents=True, exist_ok=True)
        primary.touch(exist_ok=True)
        return primary

    def begin_episode(self, episode_id: str | None = None, *, force_manual: bool = False) -> None:
        with self._lock:
            if self.status == "RECORDING":
                return
            self._load_flags()
            manual = force_manual or self.manual_next
            if not self.collection_on and not manual:
                self.status = "IDLE"
                self.current_episode = episode_id or ""
                self._persist_state()
                return
            eid = episode_id or f"ep_{int(time.time())}_{uuid.uuid4().hex[:6]}"
            self.current_episode = eid
            self.tracker = EpisodeInterestTracker(episode_id=eid, started_at=time.time(), manual=manual)
            self.detected_events = []
            self.error = ""
            temp = TEMP_DIR / f"{eid}.mp4"
            try:
                self.status = "RECORDING"
                self.recorder = start_ffmpeg_record(temp, episode_id=eid)
                self.recording_started_at = time.time()
            except Exception as e:
                self.status = "ERROR"
                self.error = str(e)
                self.recorder = None
            self._persist_state()

    def on_event(self, event_name: str, detail: str = "", progress: float = 0.0) -> None:
        mapped = map_unity_event(event_name, detail)
        with self._lock:
            if self.tracker is None:
                # auto-start episode on first meaningful event if collection/manual armed
                if self.collection_on or self.manual_next:
                    pass
                else:
                    return
            if self.tracker is None and (self.collection_on or self.manual_next):
                # start lazily
                self.begin_episode()
            if self.tracker is None:
                return
            self.tracker.note_progress(progress)
            if mapped:
                self.tracker.note_event(mapped, detail=detail, progress=progress)
                if mapped not in self.detected_events:
                    self.detected_events.append(mapped)
            # episode boundaries
            if event_name == "episode_start":
                if self.status != "RECORDING":
                    self.begin_episode(detail or None)
            if event_name == "episode_wipe" or event_name == "episode_end":
                self._end_episode_locked()
            self._persist_state()

    def end_episode(self) -> Dict[str, Any]:
        with self._lock:
            return self._end_episode_locked()

    def _end_episode_locked(self) -> Dict[str, Any]:
        if self.status not in ("RECORDING", "PROCESSING", "WAITING_EPISODE"):
            if self.tracker is None:
                return {"ok": True, "kept": False, "reason": "no_active_episode"}
        self.status = "PROCESSING"
        self._persist_state()
        stop_info = stop_recorder(self.recorder)
        self.recorder = None
        self.recording_started_at = 0.0
        tracker = self.tracker
        self.tracker = None
        if tracker is None:
            self.status = "IDLE"
            self._persist_state()
            return {"ok": False, "error": "no_tracker"}

        tracker.ended_at = time.time()
        # if manual flag was set for this episode, clear it after use
        if tracker.manual:
            flag = SHORTS_ROOT / "manual_next_episode.flag"
            if flag.exists():
                flag.unlink()
            self.manual_next = False

        verdict = tracker.evaluate(self.novelty)
        self.novelty.save(NOVELTY_PATH)
        temp_path = Path(stop_info.get("path") or (TEMP_DIR / f"{tracker.episode_id}.mp4"))
        kept = False
        meta: Dict[str, Any] = {}
        if verdict["keep"] and stop_info.get("ok"):
            self.status = "SYNCING"
            self._persist_state()
            dest = SAVED_DIR / f"{tracker.episode_id}.mp4"
            try:
                if dest.exists():
                    dest.unlink()
                temp_path.replace(dest)
            except OSError:
                # copy fallback
                import shutil

                shutil.copy2(temp_path, dest)
                try:
                    temp_path.unlink()
                except OSError:
                    pass
            meta = {
                "video_id": tracker.episode_id,
                "episode_id": tracker.episode_id,
                "created_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "duration_sec": stop_info.get("duration_sec"),
                "size_bytes": dest.stat().st_size if dest.is_file() else 0,
                "source_mode": "manual" if tracker.manual else "smart",
                "interest_score": verdict["interest_score"],
                "save_reasons": verdict["save_reasons"],
                "remote_path": str(dest),
                "local_path": "",
                "max_apocalypse_progress": tracker.max_apocalypse_progress,
                "events": tracker.events,
            }
            meta_path = SAVED_DIR / f"{tracker.episode_id}.json"
            meta_path.write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
            self._update_index(meta)
            kept = True
        else:
            # boring or failed → delete temp
            try:
                if temp_path.is_file():
                    temp_path.unlink()
            except OSError:
                pass
            meta = {
                "kept": False,
                "save_reasons": verdict.get("save_reasons") or [],
                "stop": stop_info,
            }

        self.status = "WAITING_EPISODE" if self.manual_next else ("IDLE" if not self.collection_on else "WAITING_EPISODE")
        if self.collection_on and not self.manual_next:
            self.status = "WAITING_EPISODE"
        self.detected_events = []
        self.current_episode = ""
        self._persist_state()
        return {"ok": True, "kept": kept, "meta": meta, "verdict": verdict}

    def _update_index(self, meta: Dict[str, Any]) -> None:
        idx: list = []
        if INDEX_PATH.is_file():
            try:
                idx = json.loads(INDEX_PATH.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                idx = []
        idx = [x for x in idx if x.get("video_id") != meta.get("video_id")]
        idx.insert(0, meta)
        INDEX_PATH.write_text(json.dumps(idx, ensure_ascii=False, indent=2), encoding="utf-8")

    def follow_events(self) -> None:
        path = self._find_events_file()
        assert path is not None
        # start waiting if collection on
        if self.collection_on or self.manual_next:
            self.status = "WAITING_EPISODE"
        self._persist_state()
        # Also synthesize episode boundaries periodically from wipe events only
        with path.open("r", encoding="utf-8", errors="replace") as f:
            f.seek(0, os.SEEK_END)
            while True:
                line = f.readline()
                if not line:
                    time.sleep(0.35)
                    self._load_flags()
                    self._persist_state()
                    continue
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                ev = str(obj.get("event") or "")
                detail = str(obj.get("detail") or "")
                progress = float(obj.get("apocalypse_progress") or 0.0)
                # Treat wipe as end of previous + start of next
                if ev == "episode_wipe":
                    if self.status == "RECORDING":
                        self.on_event("episode_wipe", detail, progress)
                    # after wipe, new episode begins
                    if self.collection_on or self.manual_next:
                        self.begin_episode()
                    continue
                if ev == "episode_start":
                    self.begin_episode(detail or None)
                    continue
                if self.status != "RECORDING" and (self.collection_on or self.manual_next):
                    # start recording on first gameplay event of interest window
                    if ev in ("boss_spawn", "phase_announce", "boss_defeated"):
                        self.begin_episode()
                self.on_event(ev, detail, progress)


def main() -> None:
    d = ShortsDaemon()
    d.follow_events()


if __name__ == "__main__":
    main()
