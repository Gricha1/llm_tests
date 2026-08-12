#!/usr/bin/env python3
"""Export Streaming Survival core files + latest test report for review."""

from __future__ import annotations

import argparse
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, List

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts" / "streaming_survival" / "test_runs"
OUT_DIR = ROOT / "artifacts" / "streaming_survival" / "exports"

INCLUDE_GLOBS = [
    "stream_bot/*.py",
    "stream_bot/tests/*.py",
    "streaming_survival_tests/scenarios.json",
    "streaming_survival_tests/command_catalog.json",
    "streaming_survival_tests/llm_command_suite.json",
    "scripts/run_streaming_survival_checks.py",
    "scripts/run_streaming_survival_live_stress_test.py",
    "scripts/generate_streaming_survival_test_stats.py",
    "scripts/test_streaming_survival_e2e.py",
    "scripts/export_streaming_survival_core_zip.py",
    "scripts/analyze_streaming_survival_failures.py",
    "scripts/generate_ss_visual_check_pack.py",
    "README_STREAMING_SURVIVAL.md",
    "Assets/Twitch/StreamingSurvival/*.cs",
    "Assets/Twitch/StreamCommandReceiver.cs",
    "train_scripts/lab_comp/train_lab_ui.py",
]

UNITY_CORE = [
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalWorldRegistry.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalWorldRegistrySelfTest.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalScenarioRunner.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalScenarioChecker.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalTrajectoryRecorder.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalResourceGuard.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalPlayer.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalController.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalHud.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalPlayersTable.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalCommandCheckers.cs",
    "Assets/Twitch/StreamingSurvival/StreamingSurvivalCommandRuntimeTester.cs",
    "Assets/Twitch/StreamCommandReceiver.cs",
]


def _iter_files() -> Iterable[Path]:
    seen = set()
    for g in INCLUDE_GLOBS:
        for p in ROOT.glob(g):
            if p.is_file() and p not in seen:
                seen.add(p)
                yield p
    for rel in UNITY_CORE:
        p = ROOT / rel
        if p.is_file() and p not in seen:
            seen.add(p)
            yield p


def _run_is_full_pass(run: Path) -> bool:
    summary = run / "summary.json"
    if not summary.is_file():
        return False
    try:
        import json

        data = json.loads(summary.read_text(encoding="utf-8"))
    except Exception:
        return False
    return (
        data.get("full_runtime_status") == "PASS"
        and data.get("overall") == "PASS"
        and (run / "world_registry_results.json").is_file()
        and (run / "scenario_results.json").is_file()
        and (run / "trajectories").is_dir()
    )


def _has_stats(run: Path) -> bool:
    return (run / "stats" / "stats_dashboard.html").is_file() and (
        run / "live_stress_report.json"
    ).is_file()


def _latest_run():
    if not ARTIFACTS.is_dir():
        return None
    runs = sorted([d for d in ARTIFACTS.iterdir() if d.is_dir()], reverse=True)
    # Prefer full-runtime PASS that also has live-stress stats dashboard.
    for run in runs:
        if _run_is_full_pass(run) and _has_stats(run):
            return run
    latest = ARTIFACTS / "LATEST"
    if latest.is_file():
        p = Path(latest.read_text(encoding="utf-8").strip())
        if p.is_dir() and _has_stats(p):
            return p
    for run in runs:
        if _has_stats(run):
            return run
    for run in runs:
        if _run_is_full_pass(run):
            return run
    if latest.is_file():
        p = Path(latest.read_text(encoding="utf-8").strip())
        if p.is_dir():
            return p
    return runs[0] if runs else None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="")
    ap.add_argument("--run-dir", default="")
    args = ap.parse_args()
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    ts = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    out = Path(args.out) if args.out else OUT_DIR / f"streaming_survival_core_{ts}.zip"

    files: List[Path] = list(_iter_files())
    run = Path(args.run_dir) if args.run_dir else _latest_run()
    if run is not None:
        run = Path(run)
    extra: List[Path] = []
    if run:
        for name in (
            "summary.json",
            "parser_results.json",
            "world_registry_results.json",
            "scenario_results.json",
            "live_stress_report.json",
            "live_stress_report.md",
            "visual_verdicts.json",
            "visual_check_tasks.json",
            "problem_finder_report.md",
            "e2e_results.json",
            "stats_dashboard.html",
        ):
            p = run / name
            if p.is_file():
                extra.append(p)
        stats = run / "stats"
        if stats.is_dir():
            for p in stats.rglob("*"):
                if p.is_file() and p.stat().st_size < 8_000_000:
                    extra.append(p)
        traj = run / "trajectories"
        if traj.is_dir():
            for p in traj.glob("*.jsonl"):
                if p.stat().st_size < 2_000_000:
                    extra.append(p)
        shots = run / "screenshots"
        if shots.is_dir():
            for p in shots.rglob("*"):
                if p.is_file() and p.stat().st_size < 5_000_000:
                    extra.append(p)

    with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for p in files:
            z.write(p, p.relative_to(ROOT).as_posix())
        for p in extra:
            arc = Path("latest_test_report") / p.relative_to(run if run else p.parent)
            z.write(p, arc.as_posix())

    print(str(out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
