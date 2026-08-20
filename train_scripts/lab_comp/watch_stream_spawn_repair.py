#!/usr/bin/env python3
"""Watch stream_world_snapshot.log and repair empty tree/sheep spawn.

Detects: trees=0/N or sheep=0/N (or << target) for several consecutive SNAPs.
Actions:
  1) write .forest_repair_spawners  → Unity StreamSpawnRepairWatcher ResetSpawners
  2) if still broken after repairs → touch .stream_restart_request (full stream restart)

Usage (on lab_comp):
  python train_scripts/lab_comp/watch_stream_spawn_repair.py --run-id jlg_finetune_2
  python train_scripts/lab_comp/watch_stream_spawn_repair.py --run-id jlg_finetune_2 --once
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import time
from pathlib import Path

_RE_TREES = re.compile(
    r"trees=(?:\[.*?\]|\[\])\s+n=(?P<n>\d+)(?:/(?P<t>\d+))?(?:\(spawnerAlive=(?P<a>\d+)\))?"
)
_RE_SHEEP = re.compile(
    r"sheep=(?:\[.*?\]|\[\])\s+n=(?P<n>\d+)(?:/(?P<t>\d+))?(?:\(spawnerAlive=(?P<a>\d+)\))?"
)
_RE_SNAP = re.compile(r"\[SNAP:")


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def results_dir(run_id: str) -> Path:
    env = (os.environ.get("FOREST_RESULTS_DIR") or "").strip()
    if env:
        base = Path(env)
        if run_id and (base / run_id).is_dir():
            return base / run_id
        if (base / "stream_world_snapshot.log").is_file():
            return base
        if run_id:
            return base / run_id
        return base
    root = repo_root() / "results"
    if run_id:
        return root / run_id
    return root


def find_log(run_id: str) -> Path | None:
    rid = (run_id or "").strip()
    candidates: list[Path] = []
    if rid:
        candidates.append(results_dir(rid) / "stream_world_snapshot.log")
    candidates.append(repo_root() / "results" / "stream_world_snapshot.log")
    # newest under results/*/
    root = repo_root() / "results"
    if root.is_dir():
        for p in sorted(root.glob("*/stream_world_snapshot.log"), key=lambda x: x.stat().st_mtime, reverse=True):
            candidates.append(p)
    for p in candidates:
        if p.is_file():
            return p
    return None


def parse_last_snap_counts(text: str) -> dict:
    """Last SNAP block → trees_n/target, sheep_n/target, spawnerAlive."""
    starts = [m.start() for m in _RE_SNAP.finditer(text)]
    if not starts:
        return {"ok": False, "error": "no SNAP"}
    block = text[starts[-1] :]
    out: dict = {"ok": True, "trees_n": None, "trees_t": None, "trees_alive": None,
                 "sheep_n": None, "sheep_t": None, "sheep_alive": None}
    mt = _RE_TREES.search(block)
    if mt:
        out["trees_n"] = int(mt.group("n"))
        out["trees_t"] = int(mt.group("t") or 0)
        out["trees_alive"] = int(mt.group("a")) if mt.group("a") is not None else out["trees_n"]
    ms = _RE_SHEEP.search(block)
    if ms:
        out["sheep_n"] = int(ms.group("n"))
        out["sheep_t"] = int(ms.group("t") or 0)
        out["sheep_alive"] = int(ms.group("a")) if ms.group("a") is not None else out["sheep_n"]
    # header ts
    head = block.split("\n", 1)[0]
    out["ts"] = head[:23] if len(head) >= 19 else head
    return out


def read_tail(path: Path, nbytes: int = 120_000) -> str:
    with path.open("rb") as f:
        f.seek(0, 2)
        n = f.tell()
        f.seek(max(0, n - nbytes))
        return f.read().decode("utf-8", "replace")


def is_broken(n: int | None, t: int | None, alive: int | None, *, min_ratio: float) -> bool:
    if n is None and alive is None:
        return False
    target = int(t or 0)
    if target <= 0:
        return False
    have = alive if alive is not None else n
    if have is None:
        return False
    need = max(1, int(round(target * min_ratio)))
    return int(have) < need


def write_flag(path: Path, body: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body, encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser(description="Detect empty tree/sheep spawn and repair")
    ap.add_argument("--run-id", default=os.environ.get("RUN_ID", ""))
    ap.add_argument("--once", action="store_true", help="one check then exit")
    ap.add_argument("--interval", type=float, default=20.0, help="seconds between checks")
    ap.add_argument("--bad-streak", type=int, default=3, help="consecutive bad SNAPs before repair")
    ap.add_argument("--min-ratio", type=float, default=0.25, help="alive/target below this → broken")
    ap.add_argument("--repair-cooldown", type=float, default=60.0)
    ap.add_argument("--escalate-after", type=int, default=2, help="failed repairs → stream restart")
    ap.add_argument("--no-restart", action="store_true", help="never touch .stream_restart_request")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    root = repo_root()
    flag = root / ".forest_repair_spawners"
    result = root / ".forest_repair_spawners_result"
    restart_flag = root / ".stream_restart_request"

    bad_streak = 0
    repair_count = 0
    last_repair_t = 0.0
    last_key = ""

    print(f"[spawn_repair] root={root}", flush=True)
    print(f"[spawn_repair] flag={flag}", flush=True)

    while True:
        log = find_log(args.run_id)
        if log is None:
            print("[spawn_repair] no stream_world_snapshot.log yet", flush=True)
        else:
            snap = parse_last_snap_counts(read_tail(log))
            if not snap.get("ok"):
                print(f"[spawn_repair] parse fail: {snap}", flush=True)
            else:
                trees_bad = is_broken(
                    snap.get("trees_n"), snap.get("trees_t"), snap.get("trees_alive"),
                    min_ratio=args.min_ratio,
                )
                sheep_bad = is_broken(
                    snap.get("sheep_n"), snap.get("sheep_t"), snap.get("sheep_alive"),
                    min_ratio=args.min_ratio,
                )
                key = (
                    f"t={snap.get('trees_alive')}/{snap.get('trees_t')} "
                    f"s={snap.get('sheep_alive')}/{snap.get('sheep_t')} "
                    f"ts={snap.get('ts')}"
                )
                if key != last_key:
                    print(f"[spawn_repair] {log.name}: {key} trees_bad={trees_bad} sheep_bad={sheep_bad}", flush=True)
                    last_key = key

                if trees_bad or sheep_bad:
                    bad_streak += 1
                else:
                    bad_streak = 0
                    repair_count = 0

                now = time.time()
                if bad_streak >= args.bad_streak and (now - last_repair_t) >= args.repair_cooldown:
                    reasons = []
                    if trees_bad:
                        reasons.append(
                            f"trees={snap.get('trees_alive')}/{snap.get('trees_t')}"
                        )
                    if sheep_bad:
                        reasons.append(
                            f"sheep={snap.get('sheep_alive')}/{snap.get('sheep_t')}"
                        )
                    body = (
                        f"auto watch_stream_spawn_repair\n"
                        f"utc={time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}\n"
                        f"run_id={args.run_id}\n"
                        f"reason={','.join(reasons)}\n"
                        f"log={log}\n"
                    )
                    if args.dry_run:
                        print(f"[spawn_repair] DRY repair: {reasons}", flush=True)
                    else:
                        write_flag(flag, body)
                        print(f"[spawn_repair] wrote {flag.name}: {reasons}", flush=True)
                        # wait briefly for Unity result
                        time.sleep(8.0)
                        if result.is_file():
                            try:
                                print(f"[spawn_repair] result:\n{result.read_text(encoding='utf-8')[:500]}", flush=True)
                            except OSError:
                                pass
                    last_repair_t = now
                    repair_count += 1
                    bad_streak = 0

                    if (
                        not args.no_restart
                        and not args.dry_run
                        and repair_count >= args.escalate_after
                    ):
                        # Still broken after several repairs → full stream restart
                        # (needs IsAliveTree fix in DLL for trees; sheep often recover earlier).
                        snap2 = parse_last_snap_counts(read_tail(log))
                        still_trees = is_broken(
                            snap2.get("trees_n"), snap2.get("trees_t"), snap2.get("trees_alive"),
                            min_ratio=args.min_ratio,
                        )
                        still_sheep = is_broken(
                            snap2.get("sheep_n"), snap2.get("sheep_t"), snap2.get("sheep_alive"),
                            min_ratio=args.min_ratio,
                        )
                        if still_trees or still_sheep:
                            write_flag(
                                restart_flag,
                                f"utc={time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}\n"
                                f"by=watch_stream_spawn_repair\n"
                                f"reason=spawn_still_broken_after_{repair_count}_repairs\n",
                            )
                            print(
                                f"[spawn_repair] escalate → {restart_flag.name} "
                                f"(trees_bad={still_trees} sheep_bad={still_sheep})",
                                flush=True,
                            )
                            repair_count = 0

        if args.once:
            return 0
        time.sleep(max(5.0, float(args.interval)))


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\n[spawn_repair] stop", flush=True)
        raise SystemExit(0)
