"""FFmpeg-based stream capture (independent of OBS Twitch broadcast)."""

from __future__ import annotations

import os
import shutil
import signal
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Optional


@dataclass
class RecorderHandle:
    proc: subprocess.Popen
    path: Path
    started_at: float
    episode_id: str


def resolve_display() -> str:
    return os.environ.get("SHORTS_DISPLAY") or os.environ.get("DISPLAY") or ":0"


def resolve_xauthority() -> Optional[str]:
    if os.environ.get("XAUTHORITY"):
        return os.environ["XAUTHORITY"]
    candidates = []
    try:
        uid = os.getuid()  # type: ignore[attr-defined]
        candidates.append(f"/run/user/{uid}/gdm/Xauthority")
    except AttributeError:
        pass
    candidates.append(str(Path.home() / ".Xauthority"))
    for p in candidates:
        if Path(p).is_file():
            return p
    return None


def start_ffmpeg_record(out_path: Path, *, episode_id: str, width: int = 1280, height: int = 720, fps: int = 30) -> RecorderHandle:
    """Record X11 display. Does NOT touch OBS streaming."""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    if out_path.exists():
        out_path.unlink()
    display = resolve_display()
    # x11grab; audio optional (may fail without pulse) — video-only for reliability
    cmd = [
        "ffmpeg",
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
        "-f",
        "x11grab",
        "-video_size",
        f"{width}x{height}",
        "-framerate",
        str(fps),
        "-i",
        display,
        "-c:v",
        "libx264",
        "-preset",
        "veryfast",
        "-pix_fmt",
        "yuv420p",
        "-an",
        str(out_path),
    ]
    # Prefer ffmpeg over OBS StartRecord: OBS is already `--startstreaming` to Twitch;
    # recording via obs-websocket is not reliably available and must stay independent.
    env = os.environ.copy()
    env["DISPLAY"] = display
    xa = resolve_xauthority()
    if xa:
        env["XAUTHORITY"] = xa
    proc = subprocess.Popen(cmd, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    time.sleep(0.4)
    if proc.poll() is not None:
        err = (proc.stderr.read() if proc.stderr else b"").decode("utf-8", "replace")
        raise RuntimeError(f"ffmpeg failed to start: {err[:500]}")
    return RecorderHandle(proc=proc, path=out_path, started_at=time.time(), episode_id=episode_id)


def stop_recorder(handle: Optional[RecorderHandle], *, timeout: float = 8.0) -> dict:
    if handle is None:
        return {"ok": False, "error": "no_recorder"}
    proc = handle.proc
    if proc.poll() is None:
        try:
            proc.send_signal(signal.SIGINT)
        except OSError:
            proc.terminate()
        try:
            proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=3)
    dur = max(0.0, time.time() - handle.started_at)
    size = handle.path.stat().st_size if handle.path.is_file() else 0
    return {
        "ok": size > 1000,
        "path": str(handle.path),
        "duration_sec": round(dur, 2),
        "size_bytes": size,
        "episode_id": handle.episode_id,
    }


def cleanup_temp_dir(temp_dir: Path, *, max_age_sec: float = 6 * 3600) -> int:
    if not temp_dir.is_dir():
        return 0
    now = time.time()
    n = 0
    for p in temp_dir.glob("*.mp4"):
        try:
            if now - p.stat().st_mtime > max_age_sec:
                p.unlink()
                n += 1
        except OSError:
            pass
    return n


def which_ffmpeg() -> Optional[str]:
    return shutil.which("ffmpeg")
