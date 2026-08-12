#!/usr/bin/env python3
"""Analyze Streaming Survival test failures → problem_finder_report.md."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any, Dict, List, Optional

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts" / "streaming_survival" / "test_runs"


def _latest_run() -> Optional[Path]:
    latest = ARTIFACTS / "LATEST"
    if latest.is_file():
        p = Path(latest.read_text(encoding="utf-8").strip())
        if p.is_dir():
            return p
    if not ARTIFACTS.is_dir():
        return None
    runs = sorted([d for d in ARTIFACTS.iterdir() if d.is_dir()], reverse=True)
    return runs[0] if runs else None


def _load(path: Path) -> Any:
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return None


def _suspect(sid: str, reason: str, target: str) -> Dict[str, str]:
    r = (reason or "").lower()
    files = [
        "Assets/Twitch/StreamingSurvival/StreamingSurvivalPlayer.cs",
        "stream_bot/validator.py",
    ]
    hyp = "Unknown — inspect trajectory and screenshots."
    if "ground" in r or "below ground" in r or "clearance" in r:
        hyp = (
            "Character fell below sampled ground_y — likely disabled ground/terrain "
            "collider, CharacterController/SnapToGround bug, or water corridor over-disable."
        )
        files = [
            "Assets/Twitch/StreamingSurvival/StreamingSurvivalPlayer.cs",
            "Assets/Twitch/StreamingSurvival/StreamingSurvivalTrajectoryRecorder.cs",
            "Assets/Twitch/StreamingSurvival/StreamingSurvivalWorldRegistry.cs",
            "Assets/Twitch/StreamCommandReceiver.cs",
        ]
    elif "plan" in r or "heuristic" in r:
        hyp = "Parser/DSL mismatch — sequential split or go_to vs collect detection."
        files = ["stream_bot/validator.py", "stream_bot/llm_prompts.py"]
    elif "water" in sid or "water" in r or target == "water_source":
        hyp = "Water target/registry or route may point away from visible pond."
        files = [
            "Assets/Twitch/StreamingSurvival/StreamingSurvivalWorldRegistry.cs",
            "Assets/Twitch/StreamingSurvival/StreamingSurvivalPlayer.cs",
        ]
    elif "teleport" in r or "jump" in r:
        hyp = "Illegal/controlled teleport or stuck recovery snap."
        files = [
            "Assets/Twitch/StreamingSurvival/StreamingSurvivalPlayer.cs",
            "stream_bot/ss_trajectory_checker.py",
        ]
    elif "resource" in r or "guard" in r:
        hyp = "ResourceGuard / wrong place credit."
        files = ["Assets/Twitch/StreamingSurvival/StreamingSurvivalResourceGuard.cs"]
    elif "stuck" in r:
        hyp = "No progress to target; path blocked."
        files = ["Assets/Twitch/StreamingSurvival/StreamingSurvivalPlayer.cs"]
    elif "idle" in sid or "join" in sid:
        hyp = "Join resumed last_action instead of idle."
        files = [
            "stream_bot/main.py",
            "stream_bot/streaming_survival_store.py",
            "Assets/Twitch/StreamCommandReceiver.cs",
        ]
    return {"hypothesis": hyp, "likely_files": ", ".join(files)}


def analyze(run_dir: Path) -> str:
    summary = _load(run_dir / "summary.json") or {}
    scenarios = _load(run_dir / "scenario_results.json") or {}
    parser = _load(run_dir / "parser_results.json") or {}
    visual = _load(run_dir / "visual_verdicts.json") or {}
    live = _load(run_dir / "live_stress_report.json") or {}
    stats = _load(run_dir / "stats" / "stats_summary.json") or {}
    problem_stats = _load(run_dir / "stats" / "problem_stats.json") or {}
    if not stats and (run_dir / "live_stress_report.json").is_file():
        try:
            from scripts.generate_streaming_survival_test_stats import build_stats

            stats = build_stats(run_dir)
            problem_stats = _load(run_dir / "stats" / "problem_stats.json") or {}
        except Exception:
            pass

    lines: List[str] = []
    lines.append(f"# Streaming Survival Problem Finder")
    lines.append("")
    lines.append(f"Run: `{run_dir}`")
    lines.append("")
    lines.append("## Summary")
    lines.append("")
    lines.append(f"- overall: **{summary.get('overall')}**")
    lines.append(f"- machine_full_runtime_status: **{summary.get('machine_full_runtime_status') or summary.get('full_runtime_status')}**")
    lines.append(f"- ground_status: **{summary.get('ground_status', 'UNKNOWN')}**")
    lines.append(f"- live_stress_status: **{summary.get('live_stress_status', 'NOT_RUN')}**")
    lines.append(f"- visual_status: **{summary.get('visual_status', 'PENDING')}**")
    lines.append(f"- stats_dashboard_status: **{summary.get('stats_dashboard_status') or stats.get('stats_dashboard_status', 'NOT_RUN')}**")
    lines.append(f"- overall_qa_status: **{summary.get('overall_qa_status', 'UNKNOWN')}**")
    lines.append(f"- illegal_teleports: {summary.get('illegal_teleports')}")
    lines.append(f"- max_position_jump: {summary.get('max_position_jump') or stats.get('max_position_jump')}")
    lines.append(f"- min_ground_clearance: {summary.get('min_ground_clearance') or stats.get('min_ground_clearance')}")
    lines.append(f"- ground_failures: {summary.get('ground_failures') or stats.get('ground_failures')}")
    lines.append("")

    by_checker = problem_stats.get("summary_by_checker") or {
        "parser_failures": stats.get("parser_failures"),
        "trajectory_failures": stats.get("trajectory_failures"),
        "ground_failures": stats.get("ground_failures"),
        "target_failures": stats.get("target_failures"),
        "resource_failures": stats.get("resource_guard_failures"),
        "visual_failures": stats.get("visual_failures"),
    }
    lines.append("## Summary by checker")
    lines.append("")
    for k, v in by_checker.items():
        lines.append(f"- {k}: {v}")
    lines.append("")
    lines.append(f"- stats dashboard: `{(run_dir / 'stats' / 'stats_dashboard.html')}`")
    lines.append("")

    top = problem_stats.get("top_suspicious") or {}
    if top:
        lines.append("## Top suspicious attempts")
        lines.append("")
        for title, rows in top.items():
            lines.append(f"### {title}")
            if not rows:
                lines.append("_none_")
            else:
                for r in rows[:5]:
                    if isinstance(r, dict):
                        lines.append(
                            f"- `{r.get('attempt_id')}` cmd=`{r.get('command') or r.get('raw_command')}` "
                            f"jump={r.get('max_position_jump')} speed={r.get('max_speed')} "
                            f"clr={r.get('min_ground_clearance')} status={r.get('continuity_status') or r.get('resource_status') or r.get('parser_status')}"
                        )
            lines.append("")
    worst_pass = problem_stats.get("worst_passing") or {}
    if worst_pass.get("jump"):
        wp = worst_pass["jump"]
        lines.append(
            f"- Worst passing jump: `{wp.get('attempt_id')}` "
            f"max_jump={wp.get('max_position_jump')} under threshold=3.0"
        )
        lines.append("")

    failed: List[Dict[str, Any]] = []
    for e in (scenarios.get("scenarios") or scenarios.get("results") or []):
        if str(e.get("result") or e.get("overall") or "").upper() == "FAIL":
            failed.append(e)
    for e in parser.get("failed") or []:
        failed.append({"id": e.get("id"), "reason": e.get("reason"), "source": "parser"})
    for e in live.get("failed_attempts") or []:
        failed.append(
            {
                "id": e.get("attempt_id") or f"live_{e.get('attempt')}",
                "reason": e.get("reason"),
                "ground_checker": e.get("ground_checker"),
                "source": "live_stress",
                "pos": (e.get("final_state") or {}).get("pos"),
            }
        )

    # Dedicated ground section
    ground_hits: List[Dict[str, Any]] = []
    for e in failed:
        r = str(e.get("reason") or "").lower()
        if "ground" in r or str(e.get("ground_checker", "")).upper() == "FAIL":
            ground_hits.append(e)
    for a in live.get("attempts") or []:
        for sample in a.get("below_ground_samples") or []:
            ground_hits.append(sample)

    if ground_hits:
        lines.append("## Ground / Y failures")
        lines.append("")
        for g in ground_hits[:20]:
            lines.append(f"### `{g.get('id') or g.get('event') or 'ground'}`")
            lines.append("")
            lines.append(f"- t: {g.get('t')}")
            lines.append(f"- pos: {g.get('pos')}")
            lines.append(f"- ground_y: {g.get('ground_y')}")
            lines.append(f"- clearance: {g.get('clearance') or g.get('min_ground_clearance')}")
            lines.append(f"- collider: {g.get('ground_collider_name')}")
            lines.append(f"- action: {g.get('action') or g.get('current_action')}")
            lines.append(f"- reason: {g.get('reason')}")
            sus = _suspect(str(g.get("id") or ""), str(g.get("reason") or "ground"), "")
            lines.append(f"- likely cause: {sus['hypothesis']}")
            lines.append(f"- suggested files: {sus['likely_files']}")
            lines.append("")
        lines.append("")

    if not failed and summary.get("overall") == "PASS" and summary.get("visual_status") != "FAIL":
        lines.append("## Failed scenarios")
        lines.append("")
        lines.append("None (machine checks look clean).")
    else:
        lines.append("## Failed scenarios")
        lines.append("")
        if not failed:
            lines.append("_No scenario FAIL entries, but overall is not PASS — check summary.reason._")
            lines.append(f"reason: {summary.get('reason')}")
        for e in failed:
            sid = str(e.get("id") or "?")
            reason = str(e.get("reason") or e.get("notes") or "")
            target = str(e.get("target_type") or e.get("expected_target_type") or "")
            sus = _suspect(sid, reason, target)
            shot = run_dir / "screenshots" / sid / "annotated_final.png"
            lines.append(f"### `{sid}`")
            lines.append("")
            lines.append(f"- checkers: command={e.get('command_checker')} trajectory={e.get('trajectory')} "
                         f"target={e.get('target')} resource={e.get('resource')} "
                         f"ground={e.get('ground_checker')} "
                         f"step_order={e.get('step_order_checker')} visual={e.get('visual_checker')}")
            lines.append(f"- reason: {reason}")
            lines.append(f"- max_position_jump: {e.get('max_position_jump')}")
            lines.append(f"- min_ground_clearance: {e.get('min_ground_clearance')}")
            lines.append(f"- target_type: {target}")
            lines.append(f"- evidence traj: `trajectories/{sid}_trajectory.jsonl`")
            if shot.is_file():
                lines.append(f"- screenshot: `{shot.relative_to(run_dir)}`")
            lines.append(f"- hypothesis: {sus['hypothesis']}")
            lines.append(f"- likely files: {sus['likely_files']}")
            lines.append(f"- suggested fix: inspect first failing event in trajectory; "
                         f"confirm WorldRegistry target vs visual pond/home/campfire; "
                         f"if ground FAIL check OpenWaterApproachCorridor / SnapToGround.")
            lines.append("")

    if isinstance(visual, dict) and visual:
        lines.append("## Visual verdicts")
        lines.append("")
        for k, v in visual.items():
            if isinstance(v, dict):
                lines.append(f"- `{k}`: **{v.get('verdict')}** — {v.get('reason')}")
        lines.append("")

    # Persist problem_stats even if stats generator already wrote one
    if not problem_stats:
        problem_stats = {"summary_by_checker": by_checker, "failed_count": len(failed)}
    (run_dir / "stats").mkdir(parents=True, exist_ok=True)
    (run_dir / "stats" / "problem_stats.json").write_text(
        json.dumps(problem_stats, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    out = run_dir / "problem_finder_report.md"
    out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return str(out)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run-dir", type=str, default="")
    args = ap.parse_args()
    run = Path(args.run_dir) if args.run_dir else _latest_run()
    if run is None or not run.is_dir():
        print("FAIL: no run dir")
        return 1
    path = analyze(run)
    print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
