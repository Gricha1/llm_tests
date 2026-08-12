#!/usr/bin/env python3
"""
Build Streaming Survival live-stress checker statistics, charts, and HTML dashboard.

Does NOT touch Training AI / ML-Agents. Reads live_stress_report.json + trajectories.
"""

from __future__ import annotations

import argparse
import base64
import csv
import json
import math
import re
import statistics
import sys
import textwrap
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
ARTIFACTS = ROOT / "artifacts" / "streaming_survival" / "test_runs"

# Fallback RU labels if validator import fails.
_ACTION_NAMES_RU = {
    "collect_water": "Добывает воду",
    "collect_wood": "Рубит дерево",
    "collect_stone": "Добывает камень",
    "collect_food": "Собирает еду",
    "kill_sheep": "Убивает овечек",
    "build_campfire": "Ставит костёр",
    "go_home": "Идёт к дому",
    "go_to_base": "Идёт к базе",
    "go_to_water": "Идёт к воде",
    "go_to_tree": "Идёт к дереву",
    "go_to_campfire": "Идёт к костру",
    "go_to_sheep": "Идёт к овцам",
    "manual_respawn": "Перезагрузка",
    "walk_circle": "Ходит кругом",
    "circle": "Ходит кругом",
    "walk_forward": "Идёт вперёд",
    "walk_back": "Идёт назад",
    "patrol": "Патрулирует",
    "spin_in_place": "Крутится",
    "idle": "Ждёт у базы",
    "attack_user": "Атакует игрока",
}

try:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt  # noqa: E402

    HAS_MPL = True
except Exception:
    HAS_MPL = False


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


def _load_json(path: Path) -> Any:
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return None


def _load_traj(path: Path) -> List[Dict[str, Any]]:
    if not path.is_file():
        return []
    out: List[Dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if not line.strip():
            continue
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            continue
    return out


def _pos(s: Dict[str, Any]) -> Optional[Tuple[float, float, float]]:
    p = s.get("pos")
    if not isinstance(p, dict):
        return None
    return float(p.get("x", 0)), float(p.get("y", 0)), float(p.get("z", 0))


def _pct(xs: Sequence[float], p: float) -> float:
    if not xs:
        return 0.0
    s = sorted(xs)
    if len(s) == 1:
        return float(s[0])
    k = (len(s) - 1) * (p / 100.0)
    f = math.floor(k)
    c = math.ceil(k)
    if f == c:
        return float(s[int(k)])
    return float(s[f] * (c - k) + s[c] * (k - f))


def _write_csv(path: Path, rows: List[Dict[str, Any]], columns: List[str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=columns, extrasaction="ignore")
        w.writeheader()
        for r in rows:
            w.writerow({k: r.get(k, "") for k in columns})


def _write_json(path: Path, data: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def _plan_actions(plan: Any) -> str:
    if not isinstance(plan, list):
        return ""
    parts = []
    for p in plan:
        if not isinstance(p, dict):
            continue
        a = str(p.get("action") or "")
        c = int(p.get("count") or 1)
        parts.append(f"{a}x{c}" if c > 1 else a)
    return " -> ".join(parts)


def _plan_actions_ru(plan: Any) -> str:
    """Human-readable plan: «Идёт к воде → Рубит дерево ×10»."""
    names = dict(_ACTION_NAMES_RU)
    try:
        from stream_bot.validator import ACTION_NAMES_RU as _vr

        names.update(_vr)
    except Exception:
        pass
    if not isinstance(plan, list):
        return ""
    parts: List[str] = []
    for p in plan:
        if not isinstance(p, dict):
            continue
        a = str(p.get("action") or "").strip()
        if not a:
            continue
        try:
            c = int(p.get("count") or 1)
        except (TypeError, ValueError):
            c = 1
        name = names.get(a, a)
        parts.append(f"{name} ×{c}" if c > 1 else name)
    return " → ".join(parts)


def _unique_task_plan_rows(parser_rows: List[Dict[str, Any]]) -> List[Dict[str, str]]:
    """One row per distinct chat task: задача | команды."""
    out: List[Dict[str, str]] = []
    seen = set()
    for r in parser_rows:
        task = str(r.get("raw_command") or "").strip()
        if not task or task in seen:
            continue
        seen.add(task)
        plan_ru = str(r.get("parsed_actions_ru") or "").strip()
        if not plan_ru:
            # rebuild from json if needed
            try:
                plan = json.loads(r.get("parsed_plan_json") or "[]")
            except Exception:
                plan = []
            plan_ru = _plan_actions_ru(plan) or str(r.get("parsed_actions") or "")
        out.append({"задача": task, "команды": plan_ru})
    return out


def _write_task_plan_table(stats_dir: Path, task_rows: List[Dict[str, str]]) -> None:
    """2-column human table (CSV + HTML) for Diagnostics «open table»."""
    _write_csv(stats_dir / "parser_task_plan.csv", task_rows, ["задача", "команды"])
    rows_html = []
    for r in task_rows:
        rows_html.append(
            "<tr>"
            f"<td class='task'>{_esc(r.get('задача'))}</td>"
            f"<td class='plan'>{_esc(r.get('команды'))}</td>"
            "</tr>"
        )
    html = f"""<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8"/>
<title>Parser: задача → команды</title>
<style>
  body {{ font-family: Segoe UI, system-ui, sans-serif; margin: 24px; background: #f7f7f5; color: #1a1a1a; }}
  h1 {{ font-size: 1.25rem; margin: 0 0 8px; }}
  p {{ color: #555; margin: 0 0 16px; }}
  table {{ border-collapse: collapse; width: 100%; background: #fff; box-shadow: 0 1px 3px rgba(0,0,0,.08); }}
  th, td {{ border: 1px solid #ddd; padding: 10px 12px; vertical-align: top; text-align: left; }}
  th {{ background: #eee; width: 50%; }}
  td.task {{ width: 50%; white-space: pre-wrap; }}
  td.plan {{ width: 50%; font-weight: 600; }}
  tr:nth-child(even) td {{ background: #fafafa; }}
</style>
</head>
<body>
  <h1>Parser Checker — задача → команды</h1>
  <p>Слева текст из чата, справа план действий персонажа (уникальные задачи прогона).</p>
  <table>
    <thead><tr><th>Задача (текст)</th><th>Команды (что выполнится)</th></tr></thead>
    <tbody>
      {''.join(rows_html) if rows_html else '<tr><td colspan="2"><i>empty</i></td></tr>'}
    </tbody>
  </table>
</body>
</html>
"""
    (stats_dir / "parser_task_plan.html").write_text(html, encoding="utf-8")


def _write_target_table(stats_dir: Path, target_rows: List[Dict[str, Any]]) -> None:
    """Readable Target Checker table: задача | итог (дистанции + время)."""
    simple = [{"задача": r.get("задача") or "", "итог": r.get("итог") or ""} for r in target_rows]
    _write_csv(stats_dir / "target_task_result.csv", simple, ["задача", "итог"])
    rows_html = []
    for r in target_rows:
        st = str(r.get("target_status") or "")
        cls = "pass" if st == "PASS" else ("fail" if st == "FAIL" else "")
        rows_html.append(
            "<tr>"
            f"<td class='task'>{_esc(r.get('задача'))}</td>"
            f"<td class='plan {cls}'>{_esc(r.get('итог'))} · <b>{_esc(st)}</b></td>"
            "</tr>"
        )
    html = f"""<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8"/>
<title>Target Checker</title>
<style>
  body {{ font-family: Segoe UI, system-ui, sans-serif; margin: 24px; background: #f7f7f5; color: #1a1a1a; }}
  h1 {{ font-size: 1.25rem; margin: 0 0 8px; }}
  p {{ color: #555; margin: 0 0 16px; max-width: 900px; }}
  table {{ border-collapse: collapse; width: 100%; background: #fff; box-shadow: 0 1px 3px rgba(0,0,0,.08); }}
  th, td {{ border: 1px solid #ddd; padding: 10px 12px; vertical-align: top; text-align: left; }}
  th {{ background: #eee; }}
  td.task {{ width: 38%; white-space: pre-wrap; }}
  td.plan {{ width: 62%; }}
  .pass {{ color: #1b4332; }}
  .fail {{ color: #9b2226; }}
  tr:nth-child(even) td {{ background: #fafafa; }}
</style>
</head>
<body>
  <h1>Target Checker — задача → итог</h1>
  <p>Дистанции и время считаются только по сэмплам с нужным target (без хвоста idle_stand с distance=0 в начале записи).</p>
  <table>
    <thead><tr><th>Задача (текст)</th><th>Итог (цель, дистанции, время)</th></tr></thead>
    <tbody>
      {''.join(rows_html) if rows_html else '<tr><td colspan="2"><i>empty</i></td></tr>'}
    </tbody>
  </table>
</body>
</html>
"""
    (stats_dir / "target_task_result.html").write_text(html, encoding="utf-8")


def _detect_parser_mode(attempt: Dict[str, Any], resp_meta: Dict[str, Any]) -> Tuple[str, bool, str]:
    """Return (mode, fallback_used, fallback_reason)."""
    vr = resp_meta.get("validator_result") or attempt.get("validator_result") or {}
    if isinstance(vr, dict) and vr.get("fallback"):
        return "fallback", True, str(vr.get("reason") or "validator_fallback")
    tag = ""
    for ev in resp_meta.get("events") or attempt.get("events") or []:
        if isinstance(ev, dict) and ev.get("source"):
            tag = str(ev.get("source"))
            break
    tag = tag or str(attempt.get("parser_mode") or "")
    if tag in ("heuristic", "deterministic"):
        return "deterministic", False, ""
    if tag == "llm":
        return "llm", False, ""
    if tag == "fallback":
        return "fallback", True, str(attempt.get("fallback_reason") or "fallback_action")
    plan = attempt.get("parsed_plan") or []
    cmd = str(attempt.get("command") or "")
    if cmd.strip().lower() == "#join":
        return "deterministic", False, ""
    if not plan and cmd.startswith("#do"):
        return "fallback", True, "empty_plan"
    # Heuristic default when unknown
    return "deterministic", False, ""


def _sample_action(s: Dict[str, Any]) -> str:
    return str(s.get("action") or s.get("current_action") or "").lower().strip()


def _filter_target_samples(
    samples: List[Dict[str, Any]],
    *,
    expected_tt: str,
    fam: str,
) -> List[Dict[str, Any]]:
    """Drop pre-command idle leftovers (idle_stand distance=0) that poison metrics.

    Live trajectories often begin with a leftover idle/home target at distance 0
    before the real water/tree/campfire target is assigned — that made
    start/min/time_to_reach all look like 0.
    """
    if not samples:
        return []
    if fam in ("idle", "circle") or not expected_tt:
        # For sequential / unknown: skip only pure idle_stand prefix.
        out: List[Dict[str, Any]] = []
        armed = False
        for s in samples:
            tid = str(s.get("target_id") or "")
            tt = str(s.get("target_type") or "")
            act = _sample_action(s)
            if not armed:
                if tid == "idle_stand" and act in ("", "idle", "none"):
                    continue
                if tt in ("", "home") and act in ("", "idle", "none") and fam not in ("idle", "circle"):
                    continue
                armed = True
            out.append(s)
        return out or samples

    matched = [
        s
        for s in samples
        if str(s.get("target_type") or "") == expected_tt
        and str(s.get("target_id") or "") != "idle_stand"
    ]
    return matched


def _target_reach_metrics(
    samples: List[Dict[str, Any]],
    *,
    expected_tt: str,
    fam: str,
) -> Dict[str, Any]:
    focused = _filter_target_samples(samples, expected_tt=expected_tt, fam=fam)
    dists: List[float] = []
    for s in focused:
        d = s.get("distance_to_target")
        if isinstance(d, (int, float)):
            dists.append(float(d))

    # Actual target = most common among focused samples (not polluted by idle prefix).
    target_types = [str(s.get("target_type") or "") for s in focused if s.get("target_type")]
    actual_tt = Counter(target_types).most_common(1)[0][0] if target_types else ""
    target_ids = [str(s.get("target_id") or "") for s in focused if s.get("target_id")]
    tid = Counter(target_ids).most_common(1)[0][0] if target_ids else ""

    reached = False
    t_reach: Optional[float] = None
    peak = 0.0
    for s in focused:
        evt = str(s.get("event") or "")
        d = s.get("distance_to_target")
        d_f = float(d) if isinstance(d, (int, float)) else None
        if d_f is not None:
            peak = max(peak, d_f)
        rad = float(s.get("interaction_radius") or 2.0)
        inside = s.get("is_inside_interaction_radius") is True or (
            d_f is not None and d_f <= rad + 0.15
        )
        if evt in (
            "reached_target",
            "reached_resource",
            "work_started",
            "resource_added",
            "plan_completed",
        ):
            reached = True
            t_reach = float(s.get("t") or 0)
            break
        # Require a real approach: must have been farther than the interaction radius first.
        # Avoids false time=0 when an early water waypoint is briefly <3.5m then path goes out.
        if inside and peak >= rad + 1.0:
            reached = True
            t_reach = float(s.get("t") or 0)
            break

    # Already glued to target (almost no distance change).
    if not reached and dists and max(dists) <= 3.5 and (max(dists) - min(dists)) < 0.35:
        reached = True
        t_reach = 0.0
    elif not reached and dists and min(dists) < 3.5:
        # Near-target start or short walk — use time of closest sample.
        reached = True
        min_d_val = min(dists)
        for s in focused:
            d = s.get("distance_to_target")
            if isinstance(d, (int, float)) and abs(float(d) - min_d_val) < 1e-6:
                t_reach = float(s.get("t") or 0)
                break

    start_d = dists[0] if dists else None
    min_d = min(dists) if dists else None
    # Final while still on the intended target (last focused sample).
    final_d = dists[-1] if dists else None

    return {
        "distances": dists,
        "start_distance_to_target": start_d,
        "min_distance_to_target": min_d,
        "final_distance_to_target": final_d,
        "reached_target": reached,
        "time_to_reach_target": t_reach,
        "actual_target_type": actual_tt,
        "target_id": tid,
        "focused_samples": len(focused),
    }


def _traj_metrics(samples: List[Dict[str, Any]]) -> Dict[str, Any]:
    jumps: List[float] = []
    speeds: List[float] = []
    clearances: List[float] = []
    below = 0
    missing_g = 0
    stuck = 0
    recovery = 0
    controlled = 0
    illegal_ev = 0
    resource_added = 0
    denied = 0
    credit_dists: List[float] = []
    dists: List[float] = []
    times: List[float] = []
    actions_seq: List[str] = []
    worst_clr = None
    worst_sample: Dict[str, Any] = {}
    prev_pos = None
    prev_t = None
    for s in samples:
        evt = str(s.get("event") or "")
        if evt == "stuck_detected" or evt == "character_stuck":
            stuck += 1
        if "recovery" in evt or evt == "safe_nudge":
            recovery += 1
        if evt in ("controlled_teleport", "join_spawn", "scenario_setup", "round_reset"):
            controlled += 1
        if evt == "illegal_teleport":
            illegal_ev += 1
        if evt == "resource_added":
            resource_added += 1
            d = s.get("distance_to_target")
            if isinstance(d, (int, float)):
                credit_dists.append(float(d))
        if evt in ("resource_guard_denied", "resource_denied"):
            denied += 1
        if "ground_y" not in s and "ground_clearance" not in s and "is_below_ground" not in s:
            if s.get("pos"):
                missing_g += 1
        clr = s.get("ground_clearance")
        if clr is None and s.get("ground_y") is not None and s.get("character_bottom_y") is not None:
            try:
                clr = float(s["character_bottom_y"]) - float(s["ground_y"])
            except (TypeError, ValueError):
                clr = None
        if isinstance(clr, (int, float)):
            clearances.append(float(clr))
            if worst_clr is None or float(clr) < worst_clr:
                worst_clr = float(clr)
                worst_sample = s
        if s.get("is_below_ground") is True:
            below += 1
        d = s.get("distance_to_target")
        if isinstance(d, (int, float)):
            dists.append(float(d))
        t = float(s.get("t") or 0)
        times.append(t)
        act = _sample_action(s)
        if act and (not actions_seq or actions_seq[-1] != act):
            actions_seq.append(act)
        p = _pos(s)
        if p and prev_pos is not None and prev_t is not None:
            dt = max(1e-3, t - prev_t)
            jump = math.hypot(p[0] - prev_pos[0], p[2] - prev_pos[2])
            jumps.append(jump)
            speeds.append(jump / dt)
        if p is not None:
            prev_pos = p
            prev_t = t

    # Legacy fields kept for callers; target_* overwritten per-attempt via _target_reach_metrics.
    reached = any(
        s.get("event")
        in (
            "reached_target",
            "reached_resource",
            "work_started",
            "resource_added",
            "plan_completed",
        )
        for s in samples
    )
    target_types = [str(s.get("target_type") or "") for s in samples if s.get("target_type")]
    actual_tt = Counter(target_types).most_common(1)[0][0] if target_types else ""
    target_ids = [str(s.get("target_id") or "") for s in samples if s.get("target_id")]
    tid = Counter(target_ids).most_common(1)[0][0] if target_ids else ""

    return {
        "total_samples": len(samples),
        "duration_seconds": round(max(times) if times else 0.0, 3),
        "jumps": jumps,
        "speeds": speeds,
        "clearances": clearances,
        "below_ground_samples_count": below,
        "missing_ground_samples_count": missing_g,
        "stuck_events_count": stuck,
        "recovery_events_count": recovery,
        "controlled_teleports_count": controlled,
        "illegal_event_count": illegal_ev,
        "resource_added_count": resource_added,
        "resource_guard_denied_count": denied,
        "credit_distances": credit_dists,
        "distances": dists,
        "reached_target": reached,
        "time_to_reach_target": None,
        "actual_target_type": actual_tt,
        "target_id": tid,
        "action_sequence": actions_seq,
        "worst_sample": worst_sample,
        "samples": samples,
    }


FAMILY_EXPECTED_TARGET = {
    "go_to_water": "water_source",
    "collect_water": "water_source",
    "go_home": "home_interaction_point",
    "go_to_campfire": "campfire_slot",
    "collect_wood": "tree",
    "circle": "home",
    "idle": "home",
}

FAMILY_EXPECTED_DELTA = {
    "collect_water": {"water": 1},
    "collect_wood": {"wood": 1},
}


def build_stats(run_dir: Path) -> Dict[str, Any]:
    stats_dir = run_dir / "stats"
    charts_dir = stats_dir / "charts"
    charts_dir.mkdir(parents=True, exist_ok=True)

    live = _load_json(run_dir / "live_stress_report.json") or {}
    summary = _load_json(run_dir / "summary.json") or {}
    visual = _load_json(run_dir / "visual_verdicts.json") or {}
    attempts = list(live.get("attempts") or [])

    parser_rows: List[Dict[str, Any]] = []
    traj_rows: List[Dict[str, Any]] = []
    ground_rows: List[Dict[str, Any]] = []
    target_rows: List[Dict[str, Any]] = []
    resource_rows: List[Dict[str, Any]] = []
    step_rows: List[Dict[str, Any]] = []
    visual_rows: List[Dict[str, Any]] = []

    xz_by_family: Dict[str, List[Tuple[str, List[Tuple[float, float]]]]] = defaultdict(list)
    all_paths: List[Tuple[str, List[Tuple[float, float]], List[Dict[str, Any]]]] = []

    parser_fail = 0
    traj_fail = 0
    ground_fail = 0
    target_fail = 0
    resource_fail = 0
    visual_fail = 0
    step_fail = 0

    for a in attempts:
        aid = str(a.get("attempt_id") or f"live_{a.get('attempt')}")
        fam = str(a.get("command_family") or "other")
        cmd = str(a.get("command") or "")
        plan = a.get("parsed_plan") or []
        mode, fb, fb_reason = _detect_parser_mode(a, a)
        parsed_actions = _plan_actions(plan)
        parsed_actions_ru = _plan_actions_ru(plan)
        p_status = "PASS"
        if a.get("command_checker") == "FAIL" and "plan" in str(a.get("reason") or "").lower():
            p_status = "FAIL"
        if fb and cmd.startswith("#do") and (not plan or (isinstance(plan, list) and plan and plan[0].get("action") == "idle" and "ничего" not in cmd.lower())):
            # idle fallback on non-idle command
            if "ничего" not in cmd.lower() and cmd.strip() != "#join":
                if not plan or (len(plan) == 1 and str(plan[0].get("action")) == "idle"):
                    p_status = "FAIL"
                    parser_fail += 1
        if a.get("result") == "FAIL" and not plan and cmd.startswith("#do"):
            p_status = "FAIL"
            parser_fail += 1

        parser_rows.append(
            {
                "attempt_id": aid,
                "username": a.get("username"),
                "raw_command": cmd,
                "parser_mode": mode,
                "parsed_plan_json": json.dumps(plan, ensure_ascii=False),
                "parsed_actions": parsed_actions,
                "parsed_actions_ru": parsed_actions_ru,
                "expected_actions": a.get("expected_actions") or "",
                "parser_status": p_status,
                "fallback_used": fb,
                "fallback_reason": fb_reason,
                "parse_time_ms": a.get("parse_time_ms") or "",
                "command_family": fam,
            }
        )

        traj_rel = a.get("trajectory") or f"trajectories/{aid}_trajectory.jsonl"
        traj_path = run_dir / traj_rel
        samples = _load_traj(traj_path)
        uname = str(a.get("username") or "")
        if uname:
            own = [s for s in samples if str(s.get("username") or "") == uname]
            if len(own) >= 2:
                samples = own
        tm = _traj_metrics(samples)
        jumps = tm["jumps"]
        speeds = tm["speeds"]
        clearances = tm["clearances"]

        cont_status = a.get("continuity") or ("PASS" if a.get("result") == "PASS" else "FAIL")
        if a.get("trajectory_checker") == "FAIL":
            traj_fail += 1
        if a.get("ground_checker") == "FAIL" or tm["below_ground_samples_count"] > 0:
            ground_fail += 1

        traj_rows.append(
            {
                "attempt_id": aid,
                "username": a.get("username"),
                "command": cmd,
                "plan": parsed_actions,
                "parsed_plan": plan if isinstance(plan, list) else [],
                "total_samples": tm["total_samples"],
                "duration_seconds": tm["duration_seconds"],
                "max_position_jump": round(max(jumps) if jumps else float(a.get("max_position_jump") or 0), 3),
                "mean_position_jump": round(statistics.mean(jumps), 3) if jumps else 0.0,
                "median_position_jump": round(statistics.median(jumps), 3) if jumps else 0.0,
                "p95_position_jump": round(_pct(jumps, 95), 3) if jumps else 0.0,
                "max_speed": round(max(speeds) if speeds else float(a.get("max_speed") or 0), 3),
                "mean_speed": round(statistics.mean(speeds), 3) if speeds else 0.0,
                "p95_speed": round(_pct(speeds, 95), 3) if speeds else 0.0,
                "illegal_teleports_count": len(a.get("illegal_teleports") or []) + tm["illegal_event_count"],
                "controlled_teleports_count": tm["controlled_teleports_count"],
                "continuity_status": cont_status,
                "stuck_events_count": tm["stuck_events_count"],
                "recovery_events_count": tm["recovery_events_count"],
                "command_family": fam,
            }
        )

        ws = tm["worst_sample"] or {}
        g_status = a.get("ground_checker") or "PASS"
        if tm["below_ground_samples_count"] > 0:
            g_status = "FAIL"
        ground_rows.append(
            {
                "attempt_id": aid,
                "username": a.get("username"),
                "command": cmd,
                "min_ground_clearance": round(
                    min(clearances) if clearances else float(a.get("min_ground_clearance") or 0), 3
                ),
                "mean_ground_clearance": round(statistics.mean(clearances), 3) if clearances else 0.0,
                "max_ground_clearance": round(
                    max(clearances) if clearances else float(a.get("max_ground_clearance") or 0), 3
                ),
                "below_ground_samples_count": tm["below_ground_samples_count"],
                "missing_ground_samples_count": tm["missing_ground_samples_count"],
                "worst_sample_time": ws.get("t"),
                "worst_sample_pos": json.dumps(ws.get("pos") or {}, ensure_ascii=False),
                "worst_ground_y": ws.get("ground_y"),
                "ground_status": g_status,
            }
        )

        expected_tt = FAMILY_EXPECTED_TARGET.get(fam, "")
        # Sequential: infer expected target from last plan step.
        if fam == "sequential" and plan:
            last_act = str((plan[-1] or {}).get("action") or "").lower()
            expected_tt = {
                "collect_water": "water_source",
                "go_to_water": "water_source",
                "collect_wood": "tree",
                "go_home": "home_interaction_point",
                "go_to_campfire": "campfire_slot",
                "build_campfire": "campfire_slot",
                "walk_circle": "home",
                "idle": "home",
            }.get(last_act, expected_tt)
        tgt = _target_reach_metrics(samples, expected_tt=expected_tt, fam=fam)
        t_status = "PASS"
        if expected_tt and tgt["actual_target_type"] and tgt["actual_target_type"] != expected_tt:
            if fam not in ("idle", "circle", "other"):
                t_status = "FAIL"
                target_fail += 1
        if expected_tt and not tgt["reached_target"] and fam not in ("idle", "circle"):
            t_status = "FAIL"
            target_fail += 1

        def _fmt_m(v: Any) -> Any:
            if v is None or v == "":
                return ""
            return round(float(v), 2)

        start_d = _fmt_m(tgt["start_distance_to_target"])
        min_d = _fmt_m(tgt["min_distance_to_target"])
        final_d = _fmt_m(tgt["final_distance_to_target"])
        t_reach = tgt["time_to_reach_target"]
        t_reach_s = round(float(t_reach), 2) if t_reach is not None else ""
        action_ru = _plan_actions_ru(plan) if plan else _ACTION_NAMES_RU.get(
            "walk_circle" if fam == "circle" else fam, fam
        )
        dist_txt = (
            f"старт {start_d}м → мин {min_d}м → финал {final_d}м"
            if start_d != "" or min_d != "" or final_d != ""
            else "дистанции н/д"
        )
        reach_txt = (
            f"дошёл за {t_reach_s}с"
            if tgt["reached_target"] and t_reach_s != ""
            else ("дошёл" if tgt["reached_target"] else "не дошёл")
        )
        tgt_summary = (
            f"{action_ru or fam} → цель {expected_tt or '—'} "
            f"(факт: {tgt['actual_target_type'] or '—'} / {tgt['target_id'] or '—'}); "
            f"{dist_txt}; {reach_txt}"
        )
        target_rows.append(
            {
                "attempt_id": aid,
                "задача": cmd,
                "итог": tgt_summary,
                "action": fam,
                "action_ru": action_ru,
                "expected_target_type": expected_tt,
                "actual_target_type": tgt["actual_target_type"],
                "target_id": tgt["target_id"],
                "start_distance_to_target": start_d,
                "min_distance_to_target": min_d,
                "final_distance_to_target": final_d,
                "reached_target": tgt["reached_target"],
                "time_to_reach_target": t_reach_s,
                "target_status": t_status,
                "focused_samples": tgt["focused_samples"],
            }
        )

        delta = a.get("resource_delta") or {}
        expected_delta = dict(FAMILY_EXPECTED_DELTA.get(fam) or {})
        if fam == "collect_wood" and "5" in cmd:
            expected_delta = {"wood": 5}
        r_status = "PASS"
        for k, need in expected_delta.items():
            if int(delta.get(k, 0) or 0) < int(need):
                r_status = "FAIL"
                resource_fail += 1
                break
        credit_d = tm["credit_distances"]
        resource_rows.append(
            {
                "attempt_id": aid,
                "action": fam,
                "resource_type": ",".join(expected_delta.keys()) if expected_delta else "",
                "resource_before": "",
                "resource_after": json.dumps(delta, ensure_ascii=False),
                "resource_delta": json.dumps(delta, ensure_ascii=False),
                "expected_delta": json.dumps(expected_delta, ensure_ascii=False),
                "resource_guard_pass_count": tm["resource_added_count"],
                "resource_guard_denied_count": tm["resource_guard_denied_count"],
                "credit_distance_to_target": round(statistics.mean(credit_d), 3) if credit_d else "",
                "credit_target_type": tm["actual_target_type"],
                "credit_target_id": tm["target_id"],
                "invalid_resource_completion_count": 0,
                "resource_status": r_status,
                "water_delta": int(delta.get("water", 0) or 0),
                "wood_delta": int(delta.get("wood", 0) or 0),
                "food_delta": int(delta.get("food", 0) or 0),
            }
        )

        # Step order for sequential families
        is_seq = fam == "sequential" or "затем" in cmd.lower() or "потом" in cmd.lower()
        if is_seq or "5 раз" in cmd.lower():
            expected_seq = parsed_actions
            actual_seq = " -> ".join(tm["action_sequence"])
            s_status = "PASS"
            if expected_seq and actual_seq:
                # soft: expected actions appear in order
                exp_acts = [x.split("x")[0] for x in expected_seq.split(" -> ") if x]
                ai = 0
                for act in tm["action_sequence"]:
                    if ai < len(exp_acts) and act == exp_acts[ai]:
                        ai += 1
                if ai < len(exp_acts):
                    s_status = "FAIL"
                    step_fail += 1
            step_rows.append(
                {
                    "attempt_id": aid,
                    "raw_command": cmd,
                    "expected_step_sequence": expected_seq,
                    "actual_step_sequence": actual_seq,
                    "completed_steps": len(tm["action_sequence"]),
                    "failed_step_index": "",
                    "step_order_status": s_status,
                    "step_start_times": "",
                    "step_end_times": "",
                }
            )

        shot = a.get("screenshot") or f"screenshots/{aid}/annotated_final.png"
        v_key = aid
        vv = visual.get(v_key) or visual.get(f"live_{fam}") or {}
        v_verdict = (
            vv.get("verdict")
            if isinstance(vv, dict)
            else (a.get("visual_checker") or "PENDING")
        )
        if v_verdict == "FAIL":
            visual_fail += 1
        visual_rows.append(
            {
                "attempt_id": aid,
                "command": cmd,
                "expected_visual_state": expected_tt or fam,
                "screenshot_path": shot,
                "annotated_screenshot_path": shot,
                "visual_verdict": v_verdict,
                "visual_reason": (vv.get("reason") if isinstance(vv, dict) else "") or "",
            }
        )

        # paths for plots
        path_xz: List[Tuple[float, float]] = []
        for s in samples:
            p = _pos(s)
            if p:
                path_xz.append((p[0], p[2]))
        if path_xz:
            xz_by_family[fam].append((aid, path_xz))
            all_paths.append((aid, path_xz, samples))

    # write tables
    for r in parser_rows:
        r["задача"] = r.get("raw_command") or ""
        r["команды"] = r.get("parsed_actions_ru") or r.get("parsed_actions") or ""
    task_plan_rows = _unique_task_plan_rows(parser_rows)
    _write_task_plan_table(stats_dir, task_plan_rows)
    # Human-first columns; attempt metadata kept for debugging.
    _write_csv(
        stats_dir / "parser_stats.csv",
        parser_rows,
        [
            "задача",
            "команды",
            "attempt_id",
            "username",
            "parser_mode",
            "parser_status",
            "command_family",
            "parsed_actions",
            "fallback_used",
            "fallback_reason",
        ],
    )
    _write_json(stats_dir / "parser_stats.json", parser_rows)

    _write_csv(
        stats_dir / "trajectory_stats.csv",
        traj_rows,
        [
            "attempt_id",
            "username",
            "command",
            "plan",
            "total_samples",
            "duration_seconds",
            "max_position_jump",
            "mean_position_jump",
            "median_position_jump",
            "p95_position_jump",
            "max_speed",
            "mean_speed",
            "p95_speed",
            "illegal_teleports_count",
            "controlled_teleports_count",
            "continuity_status",
            "stuck_events_count",
            "recovery_events_count",
            "command_family",
        ],
    )
    _write_json(stats_dir / "trajectory_stats.json", traj_rows)

    _write_csv(
        stats_dir / "ground_stats.csv",
        ground_rows,
        [
            "attempt_id",
            "username",
            "command",
            "min_ground_clearance",
            "mean_ground_clearance",
            "max_ground_clearance",
            "below_ground_samples_count",
            "missing_ground_samples_count",
            "worst_sample_time",
            "worst_sample_pos",
            "worst_ground_y",
            "ground_status",
        ],
    )
    _write_json(stats_dir / "ground_stats.json", ground_rows)

    _write_csv(
        stats_dir / "target_stats.csv",
        target_rows,
        [
            "задача",
            "итог",
            "attempt_id",
            "action",
            "expected_target_type",
            "actual_target_type",
            "target_id",
            "start_distance_to_target",
            "min_distance_to_target",
            "final_distance_to_target",
            "reached_target",
            "time_to_reach_target",
            "target_status",
        ],
    )
    _write_json(stats_dir / "target_stats.json", target_rows)
    _write_target_table(stats_dir, target_rows)

    _write_csv(
        stats_dir / "resource_guard_stats.csv",
        resource_rows,
        [
            "attempt_id",
            "action",
            "resource_type",
            "resource_before",
            "resource_after",
            "resource_delta",
            "expected_delta",
            "resource_guard_pass_count",
            "resource_guard_denied_count",
            "credit_distance_to_target",
            "credit_target_type",
            "credit_target_id",
            "invalid_resource_completion_count",
            "resource_status",
        ],
    )
    _write_json(stats_dir / "resource_guard_stats.json", resource_rows)

    _write_csv(
        stats_dir / "step_order_stats.csv",
        step_rows,
        [
            "attempt_id",
            "raw_command",
            "expected_step_sequence",
            "actual_step_sequence",
            "completed_steps",
            "failed_step_index",
            "step_order_status",
            "step_start_times",
            "step_end_times",
        ],
    )
    _write_json(stats_dir / "step_order_stats.json", step_rows)

    _write_csv(
        stats_dir / "visual_stats.csv",
        visual_rows,
        [
            "attempt_id",
            "command",
            "expected_visual_state",
            "screenshot_path",
            "annotated_screenshot_path",
            "visual_verdict",
            "visual_reason",
        ],
    )
    _write_json(stats_dir / "visual_stats.json", visual_rows)

    landmarks = _collect_world_landmarks(run_dir, all_paths, traj_rows)
    _write_landmarks_table(stats_dir, landmarks)

    # charts
    chart_files = _render_charts(
        charts_dir,
        parser_rows,
        traj_rows,
        ground_rows,
        target_rows,
        resource_rows,
        step_rows,
        visual_rows,
        xz_by_family,
        all_paths,
        run_dir,
        landmarks,
    )

    # problem_stats
    worst_jump = sorted(traj_rows, key=lambda r: float(r.get("max_position_jump") or 0), reverse=True)
    worst_speed = sorted(traj_rows, key=lambda r: float(r.get("max_speed") or 0), reverse=True)
    worst_ground = sorted(ground_rows, key=lambda r: float(r.get("min_ground_clearance") or 0))
    worst_target = sorted(
        [r for r in target_rows if r.get("final_distance_to_target") != ""],
        key=lambda r: float(r.get("final_distance_to_target") or 0),
        reverse=True,
    )
    problem_stats = {
        "summary_by_checker": {
            "parser_failures": parser_fail,
            "trajectory_failures": traj_fail,
            "ground_failures": ground_fail,
            "target_failures": target_fail,
            "resource_failures": resource_fail,
            "visual_failures": visual_fail,
            "step_order_failures": step_fail,
        },
        "top_suspicious": {
            "largest_jump": worst_jump[:5],
            "highest_speed": worst_speed[:5],
            "lowest_ground_clearance": worst_ground[:5],
            "largest_final_target_distance": worst_target[:5],
            "parser_fallback": [r for r in parser_rows if r.get("fallback_used")][:8],
            "resource_fail": [r for r in resource_rows if r.get("resource_status") == "FAIL"][:8],
        },
        "worst_passing": {
            "jump": next(
                (
                    r
                    for r in worst_jump
                    if float(r.get("max_position_jump") or 0) <= 3.0
                    and r.get("continuity_status") == "PASS"
                ),
                None,
            ),
            "speed": next(
                (r for r in worst_speed if float(r.get("max_speed") or 0) <= 8.0),
                None,
            ),
        },
        "charts": chart_files,
    }
    _write_json(stats_dir / "problem_stats.json", problem_stats)

    attempts_total = int(live.get("attempts_total") or len(attempts))
    attempts_passed = int(live.get("attempts_passed") or 0)
    attempts_failed = int(live.get("attempts_failed") or (attempts_total - attempts_passed))

    required_charts = [
        "parser_command_distribution.png",
        "trajectory_max_jump_per_attempt.png",
        "trajectory_max_speed_per_attempt.png",
        "trajectory_xz_paths_overview.png",
        "ground_min_clearance_per_attempt.png",
        "target_final_distance_per_attempt.png",
        "resource_delta_per_attempt.png",
    ]
    charts_ok = all((charts_dir / c).is_file() for c in required_charts)
    tables_ok = all(
        (stats_dir / f).is_file()
        for f in (
            "parser_task_plan.csv",
            "parser_task_plan.html",
            "parser_stats.csv",
            "trajectory_stats.csv",
            "ground_stats.csv",
            "target_stats.csv",
            "target_task_result.html",
            "resource_guard_stats.csv",
            "visual_stats.csv",
        )
    )
    stats_dashboard_status = "PASS" if charts_ok and tables_ok and attempts_total >= 1 else "FAIL"

    stats_summary = {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "run_dir": str(run_dir),
        "attempts_total": attempts_total,
        "attempts_passed": attempts_passed,
        "attempts_failed": attempts_failed,
        "parser_failures": parser_fail,
        "trajectory_failures": traj_fail,
        "ground_failures": ground_fail,
        "target_failures": target_fail,
        "resource_guard_failures": resource_fail,
        "visual_failures": visual_fail,
        "step_order_failures": step_fail,
        "max_position_jump": max((float(r.get("max_position_jump") or 0) for r in traj_rows), default=0),
        "mean_position_jump": round(
            statistics.mean([float(r.get("mean_position_jump") or 0) for r in traj_rows]) if traj_rows else 0,
            3,
        ),
        "max_speed": max((float(r.get("max_speed") or 0) for r in traj_rows), default=0),
        "min_ground_clearance": min(
            (float(r.get("min_ground_clearance") or 0) for r in ground_rows),
            default=None,
        ),
        "illegal_teleports": sum(int(r.get("illegal_teleports_count") or 0) for r in traj_rows),
        "controlled_teleports": sum(int(r.get("controlled_teleports_count") or 0) for r in traj_rows),
        "stats_dashboard_status": stats_dashboard_status,
        "matplotlib": HAS_MPL,
        "charts": chart_files,
        "live_stress_overall": live.get("overall"),
        "machine_full_runtime_status": summary.get("machine_full_runtime_status")
        or summary.get("full_runtime_status"),
        "live_stress_status": summary.get("live_stress_status") or live.get("overall"),
        "visual_status": summary.get("visual_status"),
    }
    _write_json(stats_dir / "stats_summary.json", stats_summary)

    md = _stats_md(stats_summary, parser_rows, traj_rows, ground_rows, problem_stats)
    (stats_dir / "stats_summary.md").write_text(md, encoding="utf-8")

    html_path = stats_dir / "stats_dashboard.html"
    html_path.write_text(
        _build_dashboard_html(
            run_dir,
            stats_summary,
            parser_rows,
            traj_rows,
            ground_rows,
            target_rows,
            resource_rows,
            step_rows,
            visual_rows,
            problem_stats,
            chart_files,
        ),
        encoding="utf-8",
    )

    # also copy dashboard pointer at run root for convenience
    (run_dir / "stats_dashboard.html").write_text(
        f'<!DOCTYPE html><meta http-equiv="refresh" content="0; url=stats/stats_dashboard.html">'
        f"<a href='stats/stats_dashboard.html'>Open dashboard</a>\n",
        encoding="utf-8",
    )

    return stats_summary


def _bar(path: Path, labels: List[str], values: List[float], title: str, ylabel: str) -> Optional[str]:
    if not HAS_MPL:
        return None
    fig, ax = plt.subplots(figsize=(max(8, len(labels) * 0.35), 4))
    ax.bar(range(len(values)), values, color="#2a6f97")
    ax.set_xticks(range(len(labels)))
    ax.set_xticklabels(labels, rotation=75, ha="right", fontsize=7)
    ax.set_title(title)
    ax.set_ylabel(ylabel)
    ax.grid(axis="y", alpha=0.3)
    fig.tight_layout()
    fig.savefig(path, dpi=120)
    plt.close(fig)
    return path.name


def _hist(path: Path, values: List[float], title: str, xlabel: str, bins: int = 20) -> Optional[str]:
    if not HAS_MPL:
        return None
    fig, ax = plt.subplots(figsize=(7, 4))
    if values:
        ax.hist(values, bins=bins, color="#e76f51", edgecolor="white")
    ax.set_title(title)
    ax.set_xlabel(xlabel)
    ax.grid(axis="y", alpha=0.3)
    fig.tight_layout()
    fig.savefig(path, dpi=120)
    plt.close(fig)
    return path.name


_LANDMARK_STYLE = {
    "water_source": ("deepskyblue", "o", "вода (пруд)"),
    "goal_water": ("cyan", "*", "GoalWater"),
    "home": ("saddlebrown", "s", "дом (здание)"),
    "home_interaction_point": ("peru", "D", "вход дома"),
    "tree": ("forestgreen", "^", "дерево"),
    "campfire_slot": ("orangered", "P", "костёр"),
    "spawn": ("purple", "X", "спавн"),
    "stone": ("gray", "h", "камень"),
    "sheep": ("pink", "v", "овца"),
}

_USER_COLORS = [
    "#1f77b4",
    "#ff7f0e",
    "#2ca02c",
    "#d62728",
    "#9467bd",
    "#8c564b",
    "#e377c2",
    "#7f7f7f",
]


def _mean_xz(points: List[Tuple[float, float]]) -> Tuple[float, float]:
    if not points:
        return 0.0, 0.0
    return (
        sum(p[0] for p in points) / len(points),
        sum(p[1] for p in points) / len(points),
    )


def _put_landmark(
    objects: Dict[str, Dict[str, Any]],
    *,
    oid: str,
    typ: str,
    x: float,
    z: float,
    ix: Optional[float] = None,
    iz: Optional[float] = None,
    source: str,
    label: str = "",
) -> None:
    objects[oid] = {
        "id": oid,
        "type": typ,
        "x": round(float(x), 3),
        "z": round(float(z), 3),
        "interaction_x": round(float(ix if ix is not None else x), 3),
        "interaction_z": round(float(iz if iz is not None else z), 3),
        "source": source,
        "label": label,
    }


def _parse_world_registry_results(path: Path) -> Dict[str, Dict[str, Any]]:
    """Parse world_registry_results.json PASS lines for true object centers."""
    out: Dict[str, Dict[str, Any]] = {}
    data = _load_json(path)
    if not isinstance(data, dict):
        return out
    lines = data.get("lines") or data.get("Lines") or []
    # [PASS] home home_0 found at (-3.3,-5.9,18.9)
    found_re = re.compile(
        r"\[PASS\]\s+(\w+)\s+(\w+)\s+found at\s+\(([-\d.]+),([-\d.]+),([-\d.]+)\)",
        re.I,
    )
    # [PASS] home interaction=(4.2,14.8) center=(-3.3,18.9)
    home_re = re.compile(
        r"home interaction=\(([-\d.]+),([-\d.]+)\) center=\(([-\d.]+),([-\d.]+)\)",
        re.I,
    )
    # [PASS] water water_0 dist_to_home=24.7 pos=(21.1,32.8) interact=(12.0,27.0)
    water_re = re.compile(
        r"water\s+(\w+)\s+dist_to_home=[-\d.]+\s+pos=\(([-\d.]+),([-\d.]+)\)\s+interact=\(([-\d.]+),([-\d.]+)\)",
        re.I,
    )
    # nearest tree ... tree_live at (10.1,-5.7,19.2)
    nearest_re = re.compile(
        r"nearest tree.*?=\s*(\w+)\s+at\s+\(([-\d.]+),([-\d.]+),([-\d.]+)\)",
        re.I,
    )
    type_map = {
        "home": "home",
        "water_source": "water_source",
        "tree": "tree",
        "stone": "stone",
        "sheep": "sheep",
        "campfire_slot": "campfire_slot",
        "spawn": "spawn",
    }
    for line in lines:
        s = str(line)
        m = home_re.search(s)
        if m:
            _put_landmark(
                out,
                oid="home_0",
                typ="home",
                x=float(m.group(3)),
                z=float(m.group(4)),
                ix=float(m.group(1)),
                iz=float(m.group(2)),
                source="world_registry_results",
                label="дом (центр)",
            )
            _put_landmark(
                out,
                oid="home_interaction",
                typ="home_interaction_point",
                x=float(m.group(1)),
                z=float(m.group(2)),
                source="world_registry_results",
                label="дом (вход)",
            )
            continue
        m = water_re.search(s)
        if m:
            _put_landmark(
                out,
                oid=m.group(1),
                typ="water_source",
                x=float(m.group(2)),
                z=float(m.group(3)),
                ix=float(m.group(4)),
                iz=float(m.group(5)),
                source="world_registry_results",
                label="вода (пруд)" if m.group(1) == "water_0" else m.group(1),
            )
            continue
        m = nearest_re.search(s)
        if m:
            _put_landmark(
                out,
                oid=m.group(1),
                typ="tree",
                x=float(m.group(2)),
                z=float(m.group(4)),
                source="world_registry_results",
                label="дерево (nearest)",
            )
            continue
        m = found_re.search(s)
        if m:
            typ_raw, oid, x, _y, z = m.group(1), m.group(2), float(m.group(3)), float(m.group(4)), float(m.group(5))
            typ = type_map.get(typ_raw.lower(), typ_raw.lower())
            label = {
                "home": "дом (центр)",
                "water_source": "вода (пруд)",
                "tree": "дерево",
                "campfire_slot": "костёр",
                "spawn": "спавн",
            }.get(typ, typ)
            # Don't overwrite richer home/water entries.
            if oid in out and typ in ("home", "water_source"):
                continue
            _put_landmark(out, oid=oid, typ=typ, x=x, z=z, source="world_registry_results", label=label)
    return out


def _parse_landmarks_from_unity_log() -> Dict[str, Dict[str, Any]]:
    """Fallback: scrape results/streaming_survival.log for registry lines."""
    out: Dict[str, Dict[str, Any]] = {}
    log = ROOT / "results" / "streaming_survival.log"
    if not log.is_file():
        return out
    try:
        # Tail read — log can be huge.
        data = log.read_bytes()
        text = data[-500_000:].decode("utf-8", errors="ignore")
    except Exception:
        return out
    home_re = re.compile(
        r"\[SSWorldRegistry\] home at \(([-\d.]+),([-\d.]+)\) interaction=\(([-\d.]+),([-\d.]+)\)"
    )
    water_re = re.compile(
        r"\[SSWorldRegistry\] water id=(\w+) name=([^\s]+) pos=\(([-\d.]+),([-\d.]+)\) interact=\(([-\d.]+),([-\d.]+)\)"
    )
    hm = None
    for m in home_re.finditer(text):
        hm = m
    if hm:
        _put_landmark(
            out,
            oid="home_0",
            typ="home",
            x=float(hm.group(1)),
            z=float(hm.group(2)),
            ix=float(hm.group(3)),
            iz=float(hm.group(4)),
            source="streaming_survival.log",
            label="дом (центр)",
        )
        _put_landmark(
            out,
            oid="home_interaction",
            typ="home_interaction_point",
            x=float(hm.group(3)),
            z=float(hm.group(4)),
            source="streaming_survival.log",
            label="дом (вход)",
        )
        # Campfire south of cabin; door slightly east of fire (scene layout).
        _put_landmark(
            out,
            oid="campfire_slot_0",
            typ="campfire_slot",
            x=float(hm.group(1)) - 0.2,
            z=float(hm.group(2)) - 1.7,
            source="streaming_survival.log+offset",
            label="костёр",
        )
    seen_w = set()
    for m in water_re.finditer(text):
        wid = m.group(1)
        if wid in seen_w:
            continue
        seen_w.add(wid)
        _put_landmark(
            out,
            oid=wid,
            typ="water_source",
            x=float(m.group(3)),
            z=float(m.group(4)),
            ix=float(m.group(5)),
            iz=float(m.group(6)),
            source="streaming_survival.log",
            label="вода (пруд)" if wid == "water_0" else f"вода {wid}",
        )
    return out


def _find_registry_json(run_dir: Path) -> Optional[Path]:
    direct = run_dir / "world_registry_results.json"
    if direct.is_file():
        return direct
    base = ARTIFACTS
    if not base.is_dir():
        return None
    # Newest registry dump across test_runs.
    cands = sorted(base.glob("*/world_registry_results.json"), reverse=True)
    return cands[0] if cands else None


def _collect_world_landmarks(
    run_dir: Path,
    all_paths: List[Tuple[str, List[Tuple[float, float]], List[Dict[str, Any]]]],
    traj_rows: List[Dict[str, Any]],
) -> Dict[str, Any]:
    """Authoritative landmarks from Unity registry; trajectories only for spawns.

    Trajectory target_pos is often a waypoint / interaction stand — NOT the house
    mesh or pond body. Using it made «дом» appear right of spawn and «костёр»
    sit on the home entrance.
    """
    objects: Dict[str, Dict[str, Any]] = {}
    fences: List[Dict[str, Any]] = []
    sources: List[str] = []

    wm = _load_json(run_dir / "world_map.json")
    if isinstance(wm, dict) and (wm.get("objects") or wm.get("fences")):
        sources.append("world_map.json")
        for o in wm.get("objects") or []:
            if not isinstance(o, dict):
                continue
            oid = str(o.get("id") or "")
            if not oid:
                continue
            typ = str(o.get("type") or "")
            label = {
                "home": "дом (центр)",
                "water_source": "вода (пруд)",
                "tree": "дерево",
                "campfire_slot": "костёр",
                "spawn": "спавн",
            }.get(typ, typ)
            _put_landmark(
                objects,
                oid=oid,
                typ=typ,
                x=float(o.get("x") or 0),
                z=float(o.get("z") or 0),
                ix=float(o.get("interaction_x") or o.get("x") or 0),
                iz=float(o.get("interaction_z") or o.get("z") or 0),
                source="world_map",
                label=label,
            )
            if typ == "home":
                _put_landmark(
                    objects,
                    oid="home_interaction",
                    typ="home_interaction_point",
                    x=float(o.get("interaction_x") or o.get("x") or 0),
                    z=float(o.get("interaction_z") or o.get("z") or 0),
                    source="world_map",
                    label="дом (вход)",
                )
        for f in wm.get("fences") or []:
            if isinstance(f, dict):
                fences.append(f)
        for cp in wm.get("checkpoints") or []:
            if not isinstance(cp, dict):
                continue
            cid = str(cp.get("id") or "")
            if not cid:
                continue
            _put_landmark(
                objects,
                oid=cid,
                typ="goal_water",
                x=float(cp.get("x") or 0),
                z=float(cp.get("z") or 0),
                source="world_map",
                label=cid,
            )

    reg_path = _find_registry_json(run_dir)
    if reg_path is not None:
        parsed = _parse_world_registry_results(reg_path)
        if parsed:
            sources.append(str(reg_path.name))
            for oid, o in parsed.items():
                # world_map wins; registry fills gaps / corrects trajectory junk
                if oid not in objects or objects[oid].get("source") == "trajectory":
                    objects[oid] = o

    if "home_0" not in objects or objects["home_0"].get("source") in ("trajectory", None):
        from_log = _parse_landmarks_from_unity_log()
        if from_log:
            sources.append("streaming_survival.log")
            for oid, o in from_log.items():
                if oid not in objects or objects[oid].get("source") == "trajectory":
                    objects[oid] = o

    # Trajectory target_pos: ONLY fill missing trees (live chop targets), never home/campfire/water.
    buckets: Dict[str, List[Tuple[float, float]]] = defaultdict(list)
    for _aid, _path, samples in all_paths:
        for s in samples:
            tid = str(s.get("target_id") or "")
            tt = str(s.get("target_type") or "")
            tp = s.get("target_pos")
            if tt != "tree" or not tid or not isinstance(tp, dict):
                continue
            try:
                buckets[tid].append((float(tp.get("x", 0)), float(tp.get("z", 0))))
            except (TypeError, ValueError):
                continue
    for tid, pts in buckets.items():
        if tid in objects:
            continue
        mx, mz = _mean_xz(pts)
        _put_landmark(
            objects,
            oid=tid,
            typ="tree",
            x=mx,
            z=mz,
            source="trajectory_tree_only",
            label="дерево (из траектории)",
        )
        sources.append("trajectories(trees)")

    # One spawn center (flower area). Agents stand on a ring around it — not 5 spawn points.
    spawns: Dict[str, Dict[str, float]] = {}
    by_user_first: Dict[str, Tuple[float, float]] = {}
    for aid, path_xz, samples in all_paths:
        if not path_xz:
            continue
        user = ""
        for s in samples:
            if s.get("username"):
                user = str(s["username"])
                break
        if not user:
            for r in traj_rows:
                if r.get("attempt_id") == aid:
                    user = str(r.get("username") or "")
                    break
        if user and user not in by_user_first:
            by_user_first[user] = path_xz[0]
    for user, (x, z) in by_user_first.items():
        spawns[user] = {"x": round(x, 3), "z": round(z, 3)}

    if "spawn_0" not in objects:
        # Prefer Unity log flowerCenter; else mean of first agent positions (= ring center ≈ spawn).
        cx = cz = None
        log = ROOT / "results" / "streaming_survival.log"
        if log.is_file():
            try:
                text = log.read_bytes()[-300_000:].decode("utf-8", errors="ignore")
                m = None
                for mm in re.finditer(
                    r"followerSpawn\(flowerCenter\)=\((-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+)\)",
                    text,
                ):
                    m = mm
                if m:
                    cx, cz = float(m.group(1)), float(m.group(3))
                    sources.append("streaming_survival.log(spawn)")
            except Exception:
                pass
        if cx is None and by_user_first:
            cx, cz = _mean_xz(list(by_user_first.values()))
            sources.append("spawn_center_from_agents")
        if cx is not None and cz is not None:
            _put_landmark(
                objects,
                oid="spawn_0",
                typ="spawn",
                x=cx,
                z=cz,
                source="spawn_center",
                label="спавн (центр цветов)",
            )

    # Remove accidental per-user spawn_* markers from older stats runs.
    for k in list(objects.keys()):
        if k.startswith("spawn_stress_") or (
            k.startswith("spawn_") and k != "spawn_0" and objects[k].get("type") == "spawn"
        ):
            # keep only spawn_0 on the map
            if k != "spawn_0":
                del objects[k]

    # Fix cabin landmarks to match scene: fire SOUTH of house; door = fire + slight east.
    home = objects.get("home_0")
    fire = objects.get("campfire_slot_0")
    hin = objects.get("home_interaction")
    if home is not None:
        hx, hz = float(home["x"]), float(home["z"])
        want_fx, want_fz = hx - 0.2, hz - 1.7
        want_ix, want_iz = want_fx + 0.9, want_fz + 0.05
        need_fire = fire is None
        if fire is not None:
            fx, fz = float(fire["x"]), float(fire["z"])
            # North of house center, or far from porch band → wrong.
            need_fire = fz >= hz - 0.3 or ((fx - hx) ** 2 + (fz - hz) ** 2) ** 0.5 > 4.0
        if need_fire:
            _put_landmark(
                objects,
                oid="campfire_slot_0",
                typ="campfire_slot",
                x=want_fx,
                z=want_fz,
                source="campfire_south_of_house",
                label="костёр",
            )
            sources.append("campfire_south_of_house")
        need_porch = hin is None
        if hin is not None:
            ix, iz = float(hin["x"]), float(hin["z"])
            dist_home = ((ix - hx) ** 2 + (iz - hz) ** 2) ** 0.5
            dist_fire = ((ix - want_fx) ** 2 + (iz - want_fz) ** 2) ** 0.5
            need_porch = dist_home > 4.0 or dist_fire > 1.5 or iz >= hz - 0.2
        if need_porch:
            _put_landmark(
                objects,
                oid="home_interaction",
                typ="home_interaction_point",
                x=want_ix,
                z=want_iz,
                source="porch_by_campfire",
                label="дом (вход)",
            )
            sources.append("home_entrance_near_campfire")

    return {
        "sources": list(dict.fromkeys(sources)),
        "objects": list(objects.values()),
        "fences": fences,
        "spawns_by_user": spawns,
    }


def _write_landmarks_table(stats_dir: Path, landmarks: Dict[str, Any]) -> None:
    rows = []
    for o in landmarks.get("objects") or []:
        rows.append(
            {
                "подпись": o.get("label") or o.get("type") or "",
                "тип": o.get("type") or "",
                "id": o.get("id") or "",
                "x": o.get("x"),
                "z": o.get("z"),
                "interaction_x": o.get("interaction_x"),
                "interaction_z": o.get("interaction_z"),
                "source": o.get("source") or "",
                "on_map": "yes" if _landmark_relevant_for_map(o) else "no_far",
            }
        )
    for i, f in enumerate(landmarks.get("fences") or []):
        rows.append(
            {
                "тип": "fence",
                "id": f.get("name") or f"fence_{i}",
                "x": f.get("x"),
                "z": f.get("z"),
                "interaction_x": f.get("size_x"),
                "interaction_z": f.get("size_z"),
                "source": "world_map",
            }
        )
    _write_csv(
        stats_dir / "world_landmarks.csv",
        rows,
        ["подпись", "тип", "id", "x", "z", "interaction_x", "interaction_z", "source", "on_map"],
    )
    _write_json(stats_dir / "world_landmarks.json", landmarks)
    body = []
    for r in rows:
        body.append(
            "<tr>"
            f"<td>{_esc(r.get('тип'))}</td>"
            f"<td>{_esc(r.get('id'))}</td>"
            f"<td>{_esc(r.get('x'))}</td>"
            f"<td>{_esc(r.get('z'))}</td>"
            f"<td>{_esc(r.get('interaction_x'))}</td>"
            f"<td>{_esc(r.get('interaction_z'))}</td>"
            f"<td>{_esc(r.get('source'))}</td>"
            "</tr>"
        )
    html = f"""<!DOCTYPE html>
<html lang="ru"><head><meta charset="utf-8"/><title>World landmarks</title>
<style>
body{{font-family:Segoe UI,system-ui,sans-serif;margin:24px;background:#f7f7f5}}
table{{border-collapse:collapse;width:100%;background:#fff}}
th,td{{border:1px solid #ddd;padding:8px;text-align:left}}
th{{background:#eee}}
</style></head><body>
<h1>Координаты мира (XZ)</h1>
<p>Источники: {', '.join(landmarks.get('sources') or []) or '—'}. Для fence колонки interaction_* = size_x/size_z.</p>
<table><thead><tr><th>тип</th><th>id</th><th>x</th><th>z</th><th>interaction_x / size_x</th><th>interaction_z / size_z</th><th>source</th></tr></thead>
<tbody>{''.join(body) if body else '<tr><td colspan="7">empty</td></tr>'}</tbody></table>
</body></html>"""
    (stats_dir / "world_landmarks.html").write_text(html, encoding="utf-8")


def _landmark_relevant_for_map(o: Dict[str, Any]) -> bool:
    """Keep playable-yard landmarks; drop far scenery / duplicate waters."""
    tt = str(o.get("type") or "")
    oid = str(o.get("id") or "")
    try:
        x, z = float(o.get("x") or 0), float(o.get("z") or 0)
    except (TypeError, ValueError):
        return False
    # Core SS playable band (spawn / house / pond).
    if tt in ("home", "home_interaction_point", "campfire_slot", "spawn", "goal_water"):
        return True
    if tt == "water_source":
        # Only the real GoalWater3 pond (water_0). water_1/2 are extra Water*
        # meshes that share the same stand point and clutter the map.
        return oid == "water_0" or oid.endswith("_0") and "water" in oid
    if tt == "tree":
        return -5.0 <= x <= 25.0 and 8.0 <= z <= 30.0
    if tt == "sheep":
        return -5.0 <= x <= 20.0 and 5.0 <= z <= 25.0
    if tt == "stone":
        # Only stones near the yard — registry often has distant rocks at z≈2.
        return -8.0 <= x <= 20.0 and 8.0 <= z <= 28.0
    if oid.startswith("spawn_"):
        return True
    return False


def _fence_rects(
    fences: List[Dict[str, Any]],
) -> List[Tuple[float, float, float, float]]:
    """Draw each fence collider as-is — never merge across gaps.

    Mid-yard sheep→pond opening is real (door_* ends ~z=14.9, next starts ~z=16.5).
    Old merge/join_gap painted that opening shut or too narrow vs the Unity scene.
    """
    seen = set()  # type: set
    rects: List[Tuple[float, float, float, float]] = []
    for f in fences or []:
        if not isinstance(f, dict):
            continue
        try:
            x, z = float(f.get("x") or 0), float(f.get("z") or 0)
            sx = max(0.25, float(f.get("size_x") or 0.3))
            sz = max(0.25, float(f.get("size_z") or 0.3))
        except (TypeError, ValueError):
            continue
        # Duplicate colliders on same plank (door_1a (6)/(8)) — draw once.
        key = (round(x, 2), round(z, 2), round(sx, 2), round(sz, 2))
        if key in seen:
            continue
        seen.add(key)
        rects.append((x - sx / 2, z - sz / 2, sx, sz))
    return rects


def _merged_fence_rects(
    fences: List[Dict[str, Any]],
    *,
    join_gap: float = 1.15,
    thickness: float = 0.75,
) -> List[Tuple[float, float, float, float]]:
    """Deprecated: use _fence_rects. Kept name as alias so callers stay valid."""
    return _fence_rects(fences)


def _plot_landmarks(
    ax,
    landmarks: Dict[str, Any],
    *,
    annotate: bool = True,
    path_points: Optional[Sequence[Tuple[float, float]]] = None,
    fit_axes: bool = True,
) -> List[Any]:
    """Draw world objects/fences; return legend handles."""
    handles = []
    seen_types = set()
    # Prefer stable legend order
    order = [
        "home",
        "home_interaction_point",
        "campfire_slot",
        "tree",
        "water_source",
        "spawn",
        "stone",
        "sheep",
    ]
    objs = [o for o in (landmarks.get("objects") or []) if _landmark_relevant_for_map(o)]
    objs.sort(key=lambda o: order.index(o.get("type")) if o.get("type") in order else 99)
    for o in objs:
        tt = str(o.get("type") or "")
        oid = str(o.get("id") or "")
        # Skip per-user spawn spam on overview legend — still plot them.
        color, marker, default_label = _LANDMARK_STYLE.get(tt, ("black", "o", tt or "obj"))
        label_ru = str(o.get("label") or default_label)
        x, z = float(o.get("x") or 0), float(o.get("z") or 0)
        size = 110 if tt in ("home", "water_source", "campfire_slot") else 80
        h = ax.scatter(
            [x],
            [z],
            c=color,
            marker=marker,
            s=size,
            zorder=6,
            edgecolors="black",
            linewidths=0.5,
            label=default_label if tt not in seen_types and not str(o.get("id", "")).startswith("spawn_stress_") else None,
        )
        if tt not in seen_types and not str(o.get("id", "")).startswith("spawn_stress_"):
            handles.append(h)
            seen_types.add(tt)
        if annotate and not str(o.get("id", "")).startswith("spawn_stress_"):
            # One house story: center = mesh, вход = where go_home walks.
            if tt == "home":
                text = f"дом\nцентр ({x:.1f},{z:.1f})"
            elif tt == "home_interaction_point":
                text = f"вход дома\n(куда идут)\n({x:.1f},{z:.1f})"
            elif tt == "water_source":
                text = f"пруд\n({x:.1f},{z:.1f})"
            else:
                text = f"{label_ru}\n({x:.1f},{z:.1f})"
            ax.annotate(
                text,
                (x, z),
                textcoords="offset points",
                xytext=(6, 6),
                fontsize=8,
                fontweight="bold",
                color=color,
                zorder=7,
            )
        ix, iz = o.get("interaction_x"), o.get("interaction_z")
        if (
            tt == "water_source"
            and oid == "water_0"
            and ix is not None
            and iz is not None
            and (abs(float(ix) - x) > 0.5 or abs(float(iz) - z) > 0.5)
        ):
            ax.scatter(
                [float(ix)],
                [float(iz)],
                c=color,
                marker="x",
                s=70,
                zorder=6,
                linewidths=1.5,
            )
            if annotate:
                ax.annotate(
                    f"берег пруда\n(куда идут)\n({float(ix):.1f},{float(iz):.1f})",
                    (float(ix), float(iz)),
                    textcoords="offset points",
                    xytext=(8, -14),
                    fontsize=7,
                    color=color,
                    zorder=7,
                )
    for x0, z0, w, h in _merged_fence_rects(landmarks.get("fences") or []):
        from matplotlib.patches import Rectangle

        rect = Rectangle(
            (x0, z0),
            w,
            h,
            linewidth=1.0,
            edgecolor="#6b3e1a",
            facecolor="#c4a484",
            alpha=0.75,
            zorder=2,
        )
        ax.add_patch(rect)
    if landmarks.get("fences"):
        from matplotlib.patches import Patch

        handles.append(Patch(facecolor="#c4a484", edgecolor="#8B4513", label="забор"))
    if fit_axes:
        _fit_axes_to_world(ax, landmarks, path_points=path_points)
    return handles


def _fit_axes_to_world(
    ax,
    landmarks: Dict[str, Any],
    path_points: Optional[Sequence[Tuple[float, float]]] = None,
) -> None:
    """Frame yard + agent paths. Ignore far sheep/zombies that pull the camera away."""
    xs: List[float] = []
    zs: List[float] = []
    if path_points:
        for pt in path_points:
            try:
                xs.append(float(pt[0]))
                zs.append(float(pt[1]))
            except (TypeError, ValueError, IndexError):
                continue

    key_types = {
        "home",
        "home_interaction_point",
        "campfire",
        "campfire_slot",
        "tree",
        "water_source",
        "spawn",
        "stone",
    }
    skip_types = {"sheep", "zombie", "flower"}
    obj_pts: List[Tuple[float, float, str]] = []
    for o in landmarks.get("objects") or []:
        if not isinstance(o, dict):
            continue
        try:
            x = float(o.get("x") or 0)
            z = float(o.get("z") or 0)
        except (TypeError, ValueError):
            continue
        t = str(o.get("type") or o.get("kind") or "").lower()
        if t in skip_types:
            continue
        obj_pts.append((x, z, t))

    for x0, z0, w, h in _fence_rects(landmarks.get("fences") or []):
        xs.extend([x0, x0 + w])
        zs.extend([z0, z0 + h])

    core_xs = list(xs)
    core_zs = list(zs)
    for x, z, t in obj_pts:
        if t in key_types:
            core_xs.append(x)
            core_zs.append(z)
            xs.append(x)
            zs.append(z)

    if core_xs and core_zs:
        cx = sorted(core_xs)[len(core_xs) // 2]
        cz = sorted(core_zs)[len(core_zs) // 2]
        # Keep extra landmarks only near the play area / path.
        radius = 40.0
        if path_points and len(path_points) >= 2:
            try:
                px = [float(p[0]) for p in path_points]
                pz = [float(p[1]) for p in path_points]
                span = max(max(px) - min(px), max(pz) - min(pz))
                radius = max(40.0, span * 0.75 + 12.0)
            except Exception:
                pass
        for x, z, t in obj_pts:
            if t in key_types:
                continue
            if (x - cx) * (x - cx) + (z - cz) * (z - cz) <= radius * radius:
                xs.append(x)
                zs.append(z)
    else:
        for x, z, _t in obj_pts:
            xs.append(x)
            zs.append(z)

    if not xs or not zs:
        return
    pad = 2.5
    ax.set_xlim(min(xs) - pad, max(xs) + pad)
    ax.set_ylim(min(zs) - pad, max(zs) + pad)


def _path_username(
    aid: str,
    samples: List[Dict[str, Any]],
    traj_rows: List[Dict[str, Any]],
) -> str:
    for s in samples:
        if s.get("username"):
            return str(s["username"])
    for r in traj_rows:
        if r.get("attempt_id") == aid:
            return str(r.get("username") or "unknown")
    return "unknown"


def _render_xz_charts(
    charts_dir: Path,
    all_paths,
    traj_rows,
    landmarks: Dict[str, Any],
) -> List[str]:
    if not HAS_MPL:
        return []
    out: List[str] = []
    users = sorted({_path_username(a, s, traj_rows) for a, _, s in all_paths})
    user_color = {u: _USER_COLORS[i % len(_USER_COLORS)] for i, u in enumerate(users)}
    n_users = len(users)
    n_paths = len(all_paths)
    busy = n_paths > 6 or n_users > 1

    # --- Overview: color = agent/user (decluttered for 40-attempt runs) ---
    fig, ax = plt.subplots(figsize=(12, 10))
    # Many paths: no per-tree coordinate spam — only icons + fences.
    all_xz: List[Tuple[float, float]] = [pt for _a, path_xz, _s in all_paths for pt in path_xz]
    lm_handles = _plot_landmarks(
        ax, landmarks, annotate=not busy, path_points=all_xz, fit_axes=False
    )
    user_handles = {}
    # Last finish per user → one small nick label (not every attempt).
    last_finish: Dict[str, Tuple[float, float]] = {}
    lw = 1.6 if busy else 3.0
    alpha = 0.55 if busy else 0.9

    for aid, path_xz, samples in all_paths:
        if len(path_xz) < 1:
            continue
        user = _path_username(aid, samples, traj_rows)
        color = user_color.get(user, "#333333")
        xs = [p[0] for p in path_xz]
        zs = [p[1] for p in path_xz]
        span = max(max(xs) - min(xs), max(zs) - min(zs)) if xs else 0.0
        moving = len(path_xz) >= 2 and span >= 0.35

        if moving:
            (line,) = ax.plot(
                xs, zs, color=color, alpha=alpha, linewidth=lw, zorder=8, solid_capstyle="round"
            )
            if not busy:
                ax.plot(xs, zs, color="white", alpha=0.3, linewidth=5.0, zorder=7, solid_capstyle="round")
            if user not in user_handles:
                user_handles[user] = line
        else:
            (line,) = ax.plot([xs[0]], [zs[0]], color=color, alpha=1.0, linewidth=2.0, zorder=8)
            if user not in user_handles:
                user_handles[user] = line

        # Start / finish: single letter only (no big text boxes).
        ax.annotate(
            "S",
            (xs[0], zs[0]),
            ha="center",
            va="center",
            fontsize=8,
            fontweight="bold",
            color="#0a7a0a",
            zorder=12,
            bbox=dict(boxstyle="circle,pad=0.15", fc="white", ec=color, lw=1.0, alpha=0.92),
        )
        ax.annotate(
            "F",
            (xs[-1], zs[-1]),
            ha="center",
            va="center",
            fontsize=8,
            fontweight="bold",
            color="#a00000",
            zorder=12,
            bbox=dict(boxstyle="circle,pad=0.15", fc="white", ec=color, lw=1.0, alpha=0.92),
        )
        last_finish[user] = (xs[-1], zs[-1])

        for s in samples:
            if s.get("event") == "resource_added":
                p = _pos(s)
                if p:
                    ax.scatter([p[0]], [p[2]], c="gold", s=36, marker="*", zorder=10, edgecolors="black", linewidths=0.4)
            if s.get("event") in ("stuck_detected", "character_stuck"):
                p = _pos(s)
                if p:
                    ax.scatter([p[0]], [p[2]], c="black", s=28, marker="x", zorder=10)

    # One compact nick per agent at their last finish.
    for user, (fx, fz) in last_finish.items():
        color = user_color.get(user, "#333333")
        short = user.replace("stress_user_", "u")
        ax.annotate(
            short,
            (fx, fz),
            textcoords="offset points",
            xytext=(6, 6),
            fontsize=7,
            fontweight="bold",
            color="#111",
            zorder=14,
            bbox=dict(boxstyle="round,pad=0.15", fc=color, alpha=0.75, ec="black", lw=0.6),
        )

    _fit_axes_to_world(ax, landmarks, path_points=all_xz)
    ax.set_xlabel("X (м)")
    ax.set_ylabel("Z (м)")
    ax.set_title(
        f"Траектории XZ: {n_paths} попыток, {n_users} агентов\n"
        "цвет линии = агент · S=старт · F=финиш"
    )
    ax.grid(alpha=0.3)
    ax.set_aspect("equal", adjustable="box")
    leg1 = ax.legend(
        handles=list(user_handles.values()),
        labels=[f"{u}" for u in user_handles],
        loc="upper left",
        fontsize=8,
        title="Агенты",
    )
    ax.add_artist(leg1)
    if lm_handles:
        ax.legend(handles=lm_handles, loc="upper right", fontsize=7, title="Мир")
    fig.tight_layout()
    fig.savefig(charts_dir / "trajectory_xz_paths_overview.png", dpi=150)
    plt.close(fig)
    out.append("trajectory_xz_paths_overview.png")

    # --- One panel per agent ---
    if users:
        cols = min(3, len(users))
        rows = math.ceil(len(users) / cols)
        fig, axes = plt.subplots(rows, cols, figsize=(4.8 * cols, 4.2 * rows), squeeze=False)
        for i, user in enumerate(users):
            ax = axes[i // cols][i % cols]
            paths_u = [
                (aid, path_xz, samples)
                for aid, path_xz, samples in all_paths
                if _path_username(aid, samples, traj_rows) == user and len(path_xz) >= 1
            ]
            pts_u = [pt for _a, path_xz, _s in paths_u for pt in path_xz]
            _plot_landmarks(ax, landmarks, annotate=False, path_points=pts_u, fit_axes=False)
            col = user_color.get(user, _USER_COLORS[0])
            for j, (aid, path_xz, samples) in enumerate(paths_u):
                xs = [p[0] for p in path_xz]
                zs = [p[1] for p in path_xz]
                span = max(max(xs) - min(xs), max(zs) - min(zs)) if xs else 0.0
                # Same agent color; slight alpha stack for many attempts.
                if len(path_xz) >= 2 and span >= 0.35:
                    ax.plot(xs, zs, alpha=0.55, linewidth=1.8, color=col, zorder=5)
                ax.annotate(
                    "S",
                    (xs[0], zs[0]),
                    ha="center",
                    va="center",
                    fontsize=7,
                    fontweight="bold",
                    color="#0a7a0a",
                    zorder=6,
                    bbox=dict(boxstyle="circle,pad=0.12", fc="white", ec=col, lw=0.8, alpha=0.9),
                )
                ax.annotate(
                    "F",
                    (xs[-1], zs[-1]),
                    ha="center",
                    va="center",
                    fontsize=7,
                    fontweight="bold",
                    color="#a00000",
                    zorder=6,
                    bbox=dict(boxstyle="circle,pad=0.12", fc="white", ec=col, lw=0.8, alpha=0.9),
                )
            _fit_axes_to_world(ax, landmarks, path_points=pts_u)
            ax.set_title(f"{user} · {len(paths_u)} путей", fontsize=10, fontweight="bold", color=col)
            ax.set_aspect("equal", adjustable="box")
            ax.grid(alpha=0.25)
        for k in range(len(users), rows * cols):
            axes[k // cols][k % cols].axis("off")
        fig.suptitle("Траектории по агентам (S=старт, F=финиш)", fontsize=12)
        fig.tight_layout()
        fig.savefig(charts_dir / "trajectory_xz_by_user.png", dpi=140)
        plt.close(fig)
        out.append("trajectory_xz_by_user.png")

    # --- One panel per attempt, labeled with the task/command ---
    if all_paths:
        from matplotlib.lines import Line2D

        action_colors = {
            "go_to_water": "#1f77b4",
            "collect_water": "#17becf",
            "go_to_campfire": "#ff7f0e",
            "build_campfire": "#ff7f0e",
            "go_home": "#e377c2",
            "collect_wood": "#2ca02c",
            "collect_food": "#98df8a",
            "collect_stone": "#7f7f7f",
            "idle": "#c7c7c7",
        }
        cmd_by_aid: Dict[str, str] = {}
        plan_by_aid: Dict[str, List[Any]] = {}
        for r in traj_rows:
            aid0 = str(r.get("attempt_id") or "")
            if not aid0:
                continue
            if aid0 not in cmd_by_aid:
                cmd_by_aid[aid0] = str(r.get("command") or r.get("raw_command") or "")
            if aid0 not in plan_by_aid:
                pl = r.get("parsed_plan")
                if isinstance(pl, list) and pl:
                    plan_by_aid[aid0] = pl
                else:
                    try:
                        plan_by_aid[aid0] = json.loads(str(r.get("parsed_plan_json") or "[]"))
                    except Exception:
                        plan_by_aid[aid0] = []
        n = len(all_paths)
        cols = 2 if n > 1 else 1
        rows = math.ceil(n / cols)
        fig_w = 6.4 * cols
        fig_h = min(6.0 * rows, 60.0)
        fig, axes = plt.subplots(rows, cols, figsize=(fig_w, fig_h), squeeze=False)
        for i, (aid, path_xz, samples) in enumerate(all_paths):
            ax = axes[i // cols][i % cols]
            _plot_landmarks(ax, landmarks, annotate=False, path_points=path_xz, fit_axes=False)
            user = _path_username(aid, samples, traj_rows)
            col = user_color.get(user, _USER_COLORS[i % len(_USER_COLORS)])
            xs = [p[0] for p in path_xz]
            zs = [p[1] for p in path_xz]
            span = max(max(xs) - min(xs), max(zs) - min(zs)) if xs else 0.0
            segs: List[Tuple[str, List[float], List[float]]] = []
            cur_a = ""
            cur_xs: List[float] = []
            cur_zs: List[float] = []
            for s in samples:
                p = _pos(s)
                if not p:
                    continue
                a = str(s.get("action") or s.get("current_action") or "idle").lower()
                if a != cur_a and cur_xs:
                    segs.append((cur_a, cur_xs, cur_zs))
                    cur_xs = [cur_xs[-1]]
                    cur_zs = [cur_zs[-1]]
                cur_a = a
                cur_xs.append(p[0])
                cur_zs.append(p[2])
            if cur_xs:
                segs.append((cur_a, cur_xs, cur_zs))
            seen_act: set[str] = set()
            if segs:
                for a, sx, sz in segs:
                    if len(sx) < 2:
                        continue
                    ax.plot(
                        sx,
                        sz,
                        color=action_colors.get(a, col),
                        alpha=0.92,
                        linewidth=2.6,
                        zorder=5,
                        solid_capstyle="round",
                    )
                    seen_act.add(a)
            elif len(path_xz) >= 2 and span >= 0.35:
                ax.plot(xs, zs, color=col, alpha=0.9, linewidth=2.4, zorder=5)

            ax.annotate(
                "S", (xs[0], zs[0]), ha="center", va="center", fontsize=8, fontweight="bold",
                color="#0a7a0a", zorder=6,
                bbox=dict(boxstyle="circle,pad=0.12", fc="white", ec=col, lw=0.8, alpha=0.9),
            )
            ax.annotate(
                "F", (xs[-1], zs[-1]), ha="center", va="center", fontsize=8, fontweight="bold",
                color="#a00000", zorder=6,
                bbox=dict(boxstyle="circle,pad=0.12", fc="white", ec=col, lw=0.8, alpha=0.9),
            )
            _fit_axes_to_world(ax, landmarks, path_points=path_xz)

            cmd = cmd_by_aid.get(aid) or ""
            if not cmd:
                for s in samples:
                    if s.get("command"):
                        cmd = str(s.get("command"))
                        break
            plan = plan_by_aid.get(aid) or []
            # Legend = plan steps in order (color ↔ задача). Fallback to seen actions.
            legend_items: List[Tuple[str, str]] = []
            if isinstance(plan, list) and plan:
                for si, step in enumerate(plan, start=1):
                    if not isinstance(step, dict):
                        continue
                    act = str(step.get("action") or "").lower()
                    if not act:
                        continue
                    cnt = int(step.get("count") or step.get("amount") or 1)
                    name = _ACTION_NAMES_RU.get(act, act)
                    label = f"{si}. {name}" + (f" ×{cnt}" if cnt > 1 else "")
                    legend_items.append((act, label))
            if not legend_items:
                for a in segs:
                    act = a[0]
                    if act and all(act != x[0] for x in legend_items):
                        legend_items.append((act, _ACTION_NAMES_RU.get(act, act)))

            handles = [
                Line2D([0], [0], color=action_colors.get(act, col), lw=3.0, label=lab)
                for act, lab in legend_items
            ]
            if handles:
                ax.legend(
                    handles=handles,
                    loc="lower left",
                    fontsize=7.5,
                    title="цвет линии = шаг #do",
                    framealpha=0.95,
                    borderpad=0.4,
                )

            prompt = (cmd or "").strip() or "(нет #do)"
            steps_txt = " → ".join(lab for _a, lab in legend_items) if legend_items else "—"
            title = (
                f"{aid}\n"
                f"Промпт: {textwrap.fill(prompt, width=58)}\n"
                f"Задачи: {textwrap.fill(steps_txt, width=58)}"
            )
            ax.set_title(title, fontsize=7.2, fontweight="bold", loc="left", pad=10)
            ax.set_aspect("equal", adjustable="box")
            ax.grid(alpha=0.25)
        for k in range(n, rows * cols):
            axes[k // cols][k % cols].axis("off")
        fig.suptitle(
            "Траектории по задачам (1 панель = 1 попытка · S=старт · F=финиш)\n"
            "Разный цвет = разный шаг исходного #do (см. легенду и список задач)",
            fontsize=11,
        )
        fig.tight_layout(rect=(0, 0, 1, 0.94), h_pad=2.0)
        fig.savefig(charts_dir / "trajectory_xz_by_attempt.png", dpi=130)
        plt.close(fig)
        out.append("trajectory_xz_by_attempt.png")

    return out


def _render_charts(
    charts_dir: Path,
    parser_rows,
    traj_rows,
    ground_rows,
    target_rows,
    resource_rows,
    step_rows,
    visual_rows,
    xz_by_family,
    all_paths,
    run_dir: Path,
    landmarks: Optional[Dict[str, Any]] = None,
) -> List[str]:
    out: List[str] = []
    if not HAS_MPL:
        (charts_dir / "README_NO_MATPLOTLIB.txt").write_text(
            "matplotlib missing — install matplotlib to generate PNG charts\n",
            encoding="utf-8",
        )
        return out
    if landmarks is None:
        landmarks = _collect_world_landmarks(run_dir, all_paths, traj_rows)

    # parser
    fam_c = Counter(r.get("command_family") or "other" for r in parser_rows)
    n = _bar(
        charts_dir / "parser_command_distribution.png",
        list(fam_c.keys()),
        [float(fam_c[k]) for k in fam_c],
        "Parser: command family distribution",
        "count",
    )
    if n:
        out.append(n)

    act_c: Counter = Counter()
    for r in parser_rows:
        for part in str(r.get("parsed_actions") or "").split(" -> "):
            if part:
                act_c[part.split("x")[0]] += 1
    n = _bar(
        charts_dir / "parser_action_distribution.png",
        list(act_c.keys()) or ["none"],
        [float(act_c[k]) for k in act_c] or [0],
        "Parser: action distribution",
        "count",
    )
    if n:
        out.append(n)

    status_c = Counter(r.get("parser_status") for r in parser_rows)
    mode_c = Counter(r.get("parser_mode") for r in parser_rows)
    n = _bar(
        charts_dir / "parser_success_rate.png",
        list(status_c.keys()) or ["n/a"],
        [float(status_c[k]) for k in status_c] or [0],
        "Parser: PASS/FAIL",
        "count",
    )
    if n:
        out.append(n)
    n = _bar(
        charts_dir / "parser_mode_distribution.png",
        list(mode_c.keys()) or ["n/a"],
        [float(mode_c[k]) for k in mode_c] or [0],
        "Parser: mode distribution",
        "count",
    )
    if n:
        out.append(n)

    labels = [str(r.get("attempt_id", ""))[-12:] for r in traj_rows]
    n = _bar(
        charts_dir / "trajectory_max_jump_per_attempt.png",
        labels,
        [float(r.get("max_position_jump") or 0) for r in traj_rows],
        "Trajectory: max position jump per attempt",
        "meters",
    )
    if n:
        out.append(n)
    n = _bar(
        charts_dir / "trajectory_avg_jump_per_attempt.png",
        labels,
        [float(r.get("mean_position_jump") or 0) for r in traj_rows],
        "Trajectory: mean jump per attempt",
        "meters",
    )
    if n:
        out.append(n)
    n = _bar(
        charts_dir / "trajectory_max_speed_per_attempt.png",
        labels,
        [float(r.get("max_speed") or 0) for r in traj_rows],
        "Trajectory: max speed per attempt",
        "m/s",
    )
    if n:
        out.append(n)
    n = _hist(
        charts_dir / "trajectory_speed_histogram.png",
        [float(r.get("max_speed") or 0) for r in traj_rows],
        "Trajectory: max-speed histogram",
        "max speed",
    )
    if n:
        out.append(n)
    n = _hist(
        charts_dir / "trajectory_jump_histogram.png",
        [float(r.get("max_position_jump") or 0) for r in traj_rows],
        "Trajectory: max-jump histogram",
        "max jump",
    )
    if n:
        out.append(n)

    # XZ overview + per-agent + per-attempt (task-labeled)
    out.extend(_render_xz_charts(charts_dir, all_paths, traj_rows, landmarks))

    # ground
    glabels = [str(r.get("attempt_id", ""))[-12:] for r in ground_rows]
    n = _bar(
        charts_dir / "ground_min_clearance_per_attempt.png",
        glabels,
        [float(r.get("min_ground_clearance") or 0) for r in ground_rows],
        "Ground: min clearance per attempt",
        "meters",
    )
    if n:
        out.append(n)
    n = _hist(
        charts_dir / "ground_clearance_histogram.png",
        [float(r.get("min_ground_clearance") or 0) for r in ground_rows],
        "Ground: min clearance histogram",
        "min clearance",
    )
    if n:
        out.append(n)
    n = _bar(
        charts_dir / "ground_below_samples_per_attempt.png",
        glabels,
        [float(r.get("below_ground_samples_count") or 0) for r in ground_rows],
        "Ground: below-ground samples per attempt",
        "count",
    )
    if n:
        out.append(n)

    # y vs ground_y for 3 worst
    worst3 = sorted(ground_rows, key=lambda r: float(r.get("min_ground_clearance") or 0))[:3]
    if worst3:
        fig, axes = plt.subplots(len(worst3), 1, figsize=(8, 2.8 * len(worst3)), squeeze=False)
        for i, r in enumerate(worst3):
            aid = r["attempt_id"]
            samples = _load_traj(run_dir / "trajectories" / f"{aid}_trajectory.jsonl")
            ts, ys, gys = [], [], []
            for s in samples:
                p = _pos(s)
                if not p:
                    continue
                ts.append(float(s.get("t") or 0))
                ys.append(p[1])
                gys.append(float(s["ground_y"]) if s.get("ground_y") is not None else float("nan"))
            ax = axes[i][0]
            ax.plot(ts, ys, label="character_y")
            ax.plot(ts, gys, label="ground_y")
            ax.set_title(f"{aid} min_clr={r.get('min_ground_clearance')}")
            ax.legend(fontsize=8)
            ax.grid(alpha=0.3)
        fig.tight_layout()
        fig.savefig(charts_dir / "ground_y_vs_character_y_examples.png", dpi=120)
        plt.close(fig)
        out.append("ground_y_vs_character_y_examples.png")

    # target
    tlabels = [str(r.get("attempt_id", ""))[-12:] for r in target_rows]
    finals = [float(r["final_distance_to_target"]) if r.get("final_distance_to_target") != "" else 0 for r in target_rows]
    mins = [float(r["min_distance_to_target"]) if r.get("min_distance_to_target") != "" else 0 for r in target_rows]
    n = _bar(
        charts_dir / "target_final_distance_per_attempt.png",
        tlabels,
        finals,
        "Target: final distance per attempt",
        "meters",
    )
    if n:
        out.append(n)
    n = _bar(
        charts_dir / "target_min_distance_per_attempt.png",
        tlabels,
        mins,
        "Target: min distance per attempt",
        "meters",
    )
    if n:
        out.append(n)
    reach_by = defaultdict(lambda: [0, 0])
    for r in target_rows:
        act = r.get("action") or "other"
        reach_by[act][1] += 1
        if r.get("reached_target"):
            reach_by[act][0] += 1
    n = _bar(
        charts_dir / "target_reached_rate_by_action.png",
        list(reach_by.keys()) or ["n/a"],
        [reach_by[k][0] / max(1, reach_by[k][1]) for k in reach_by] or [0],
        "Target: reached rate by action",
        "rate",
    )
    if n:
        out.append(n)

    # distance curves examples
    examples = [r for r in target_rows if r.get("action") in ("collect_water", "go_home", "collect_wood")][:3]
    if examples:
        fig, axes = plt.subplots(len(examples), 1, figsize=(8, 2.6 * len(examples)), squeeze=False)
        for i, r in enumerate(examples):
            samples = _load_traj(run_dir / "trajectories" / f"{r['attempt_id']}_trajectory.jsonl")
            ts, ds = [], []
            for s in samples:
                if isinstance(s.get("distance_to_target"), (int, float)):
                    ts.append(float(s.get("t") or 0))
                    ds.append(float(s["distance_to_target"]))
            ax = axes[i][0]
            ax.plot(ts, ds)
            ax.set_title(f"{r['attempt_id']} {r.get('action')}")
            ax.set_ylabel("dist")
            ax.grid(alpha=0.3)
        fig.tight_layout()
        fig.savefig(charts_dir / "target_distance_curves_examples.png", dpi=120)
        plt.close(fig)
        out.append("target_distance_curves_examples.png")

    # resource
    rlabels = [str(r.get("attempt_id", ""))[-12:] for r in resource_rows]
    n = _bar(
        charts_dir / "resource_delta_per_attempt.png",
        rlabels,
        [float(r.get("water_delta", 0)) + float(r.get("wood_delta", 0)) for r in resource_rows],
        "Resource: water+wood delta per attempt",
        "delta",
    )
    if n:
        out.append(n)
    credits = [
        float(r["credit_distance_to_target"])
        for r in resource_rows
        if r.get("credit_distance_to_target") not in ("", None)
    ]
    n = _hist(
        charts_dir / "resource_credit_distance_histogram.png",
        credits,
        "Resource: credit distance histogram",
        "distance at credit",
    )
    if n:
        out.append(n)
    n = _bar(
        charts_dir / "resource_guard_denied_per_attempt.png",
        rlabels,
        [float(r.get("resource_guard_denied_count") or 0) for r in resource_rows],
        "ResourceGuard: denied per attempt",
        "count",
    )
    if n:
        out.append(n)

    # step order
    sc = Counter(r.get("step_order_status") for r in step_rows) if step_rows else Counter({"n/a": 0})
    n = _bar(
        charts_dir / "step_order_pass_rate.png",
        list(sc.keys()),
        [float(sc[k]) for k in sc],
        "Step order: PASS/FAIL",
        "count",
    )
    if n:
        out.append(n)
    if step_rows:
        fig, ax = plt.subplots(figsize=(9, max(3, 0.45 * len(step_rows) + 1)))
        for i, r in enumerate(step_rows):
            acts = [x for x in str(r.get("actual_step_sequence") or "").split(" -> ") if x]
            for j, act in enumerate(acts[:8]):
                ax.barh(i, 1, left=j, label=act if i == 0 else None)
                ax.text(j + 0.05, i, act[:14], va="center", fontsize=7)
        ax.set_yticks(range(len(step_rows)))
        ax.set_yticklabels([str(r.get("attempt_id"))[-14:] for r in step_rows], fontsize=7)
        ax.set_title("Step order timelines (actual action sequence)")
        fig.tight_layout()
        fig.savefig(charts_dir / "step_order_timeline_examples.png", dpi=120)
        plt.close(fig)
        out.append("step_order_timeline_examples.png")
    else:
        fig, ax = plt.subplots(figsize=(6, 2))
        ax.text(0.5, 0.5, "No sequential attempts in this run", ha="center")
        ax.axis("off")
        fig.savefig(charts_dir / "step_order_timeline_examples.png", dpi=100)
        plt.close(fig)
        out.append("step_order_timeline_examples.png")

    # visual verdicts
    vc = Counter(r.get("visual_verdict") for r in visual_rows)
    n = _bar(
        charts_dir / "visual_verdicts_summary.png",
        list(vc.keys()) or ["n/a"],
        [float(vc[k]) for k in vc] or [0],
        "Visual: verdict summary",
        "count",
    )
    if n:
        out.append(n)

    return out


def _stats_md(summary, parser_rows, traj_rows, ground_rows, problem_stats) -> str:
    lines = [
        "# Streaming Survival Test Stats",
        "",
        f"- run_dir: `{summary.get('run_dir')}`",
        f"- attempts_total: {summary.get('attempts_total')}",
        f"- attempts_passed: {summary.get('attempts_passed')}",
        f"- attempts_failed: {summary.get('attempts_failed')}",
        f"- stats_dashboard_status: **{summary.get('stats_dashboard_status')}**",
        f"- max_position_jump: {summary.get('max_position_jump')}",
        f"- mean_position_jump: {summary.get('mean_position_jump')}",
        f"- max_speed: {summary.get('max_speed')}",
        f"- min_ground_clearance: {summary.get('min_ground_clearance')}",
        f"- parser_failures: {summary.get('parser_failures')}",
        f"- trajectory_failures: {summary.get('trajectory_failures')}",
        f"- ground_failures: {summary.get('ground_failures')}",
        f"- target_failures: {summary.get('target_failures')}",
        f"- resource_guard_failures: {summary.get('resource_guard_failures')}",
        f"- visual_failures: {summary.get('visual_failures')}",
        "",
        "## Parser (задача → команды)",
        "",
        "| Задача (текст) | Команды (что выполнится) |",
        "|----------------|--------------------------|",
    ]
    seen_tasks = set()
    for r in parser_rows:
        task = str(r.get("задача") or r.get("raw_command") or "").strip()
        if not task or task in seen_tasks:
            continue
        seen_tasks.add(task)
        plan = r.get("команды") or r.get("parsed_actions_ru") or r.get("parsed_actions") or ""
        lines.append(f"| `{task}` | {plan} |")
    lines += ["", "## Top suspicious", ""]
    for k, rows in (problem_stats.get("top_suspicious") or {}).items():
        lines.append(f"### {k}")
        if not rows:
            lines.append("_none_")
            continue
        for r in rows[:5]:
            if isinstance(r, dict):
                lines.append(f"- `{r.get('attempt_id')}`: {json.dumps({kk: r.get(kk) for kk in list(r)[:6]}, ensure_ascii=False)}")
        lines.append("")
    return "\n".join(lines) + "\n"


def _img_tag(charts_dir_rel: str, name: str, run_dir: Path) -> str:
    p = run_dir / "stats" / "charts" / name
    if not p.is_file():
        return f"<p class='miss'>missing {name}</p>"
    b64 = base64.b64encode(p.read_bytes()).decode("ascii")
    return f"<img alt='{name}' src='data:image/png;base64,{b64}' style='max-width:100%;height:auto;border:1px solid #ddd;margin:8px 0'/>"


def _table_html(rows: List[Dict[str, Any]], cols: List[str], limit: int = 60) -> str:
    if not rows:
        return "<p><i>empty</i></p>"
    h = "<table><thead><tr>" + "".join(f"<th>{c}</th>" for c in cols) + "</tr></thead><tbody>"
    for r in rows[:limit]:
        h += "<tr>" + "".join(f"<td>{_esc(r.get(c))}</td>" for c in cols) + "</tr>"
    h += "</tbody></table>"
    if len(rows) > limit:
        h += f"<p><i>showing {limit}/{len(rows)}</i></p>"
    return h


def _esc(v: Any) -> str:
    s = "" if v is None else str(v)
    return (
        s.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
        .replace('"', "&quot;")
    )


def _build_dashboard_html(
    run_dir: Path,
    summary: Dict[str, Any],
    parser_rows,
    traj_rows,
    ground_rows,
    target_rows,
    resource_rows,
    step_rows,
    visual_rows,
    problem_stats,
    chart_files,
) -> str:
    def img(name: str) -> str:
        return _img_tag("charts", name, run_dir)

    thumbs: List[str] = []
    # Prefer embedding every shot in screenshots/<attempt_id>/ so Visual Checker
    # is readable without opening files by path.
    seen_files = set()
    for r in visual_rows:
        aid = str(r.get("attempt_id") or "")
        candidates: List[Path] = []
        rel = r.get("annotated_screenshot_path") or r.get("screenshot_path") or ""
        if rel:
            candidates.append(run_dir / str(rel))
        shot_dir = run_dir / "screenshots" / aid
        if shot_dir.is_dir():
            for name in ("000_start.png", "000_start.jpg", "001_mid.png", "001_mid.jpg",
                         "002_final.png", "002_final.jpg", "annotated_final.png", "annotated_final.jpg"):
                candidates.append(shot_dir / name)
            for p in sorted(shot_dir.glob("*.png")) + sorted(shot_dir.glob("*.jpg")):
                candidates.append(p)
        for p in candidates:
            try:
                key = str(p.resolve())
            except Exception:
                key = str(p)
            if key in seen_files or not p.is_file():
                continue
            if p.stat().st_size < 400 or p.stat().st_size > 4_000_000:
                continue
            seen_files.add(key)
            raw = p.read_bytes()
            # Detect jpeg vs png
            mime = "image/jpeg" if raw[:2] == b"\xff\xd8" else "image/png"
            b64 = base64.b64encode(raw).decode("ascii")
            label = p.name
            if aid:
                label = f"{aid} · {p.name}"
            verdict = r.get("visual_verdict") or ""
            cmd = r.get("command") or ""
            thumbs.append(
                "<figure style='display:inline-block;width:320px;margin:8px;vertical-align:top;"
                "background:#f8f9fa;border:1px solid #ddd;border-radius:8px;padding:6px'>"
                f"<img src='data:{mime};base64,{b64}' "
                "style='width:100%;height:auto;border-radius:4px;background:#000'/>"
                f"<figcaption style='font-size:12px;margin-top:6px;line-height:1.35'>"
                f"<b>{_esc(label)}</b><br/>{_esc(cmd)}<br/>"
                f"verdict: {_esc(verdict) or '—'}</figcaption></figure>"
            )

    visual_body = (
        "".join(thumbs)
        if thumbs
        else (
            "<p><b>Скринов нет.</b> Кадры с lab DISPLAY=:1 не попали в run "
            "(Unity не на :1, или захват дал пустой кадр). "
            "Перезапусти SS через start_streaming_survival (DISPLAY=:1) и Live Stress 1.</p>"
        )
    )

    css = """
    body{font-family:Segoe UI,system-ui,sans-serif;margin:0;background:#f6f7f9;color:#1a1a1a}
    header{background:#1b4332;color:#fff;padding:18px 24px}
    main{padding:18px 24px;max-width:1200px;margin:0 auto}
    section{background:#fff;border-radius:10px;padding:16px 18px;margin:14px 0;box-shadow:0 1px 3px rgba(0,0,0,.08)}
    h1{margin:0;font-size:22px} h2{margin-top:0;font-size:18px;color:#1b4332}
    .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:10px}
    .kpi{background:#edf2f4;border-radius:8px;padding:10px}
    .kpi b{display:block;font-size:20px}
    table{border-collapse:collapse;width:100%;font-size:12px}
    th,td{border:1px solid #ddd;padding:4px 6px;text-align:left;vertical-align:top}
    th{background:#e9ecef}
    .pass{color:#2d6a4f;font-weight:700}.fail{color:#9b2226;font-weight:700}
    """
    s = summary
    status_cls = "pass" if s.get("stats_dashboard_status") == "PASS" else "fail"
    return f"""<!DOCTYPE html>
<html lang="ru"><head><meta charset="utf-8"/>
<title>SS Stats Dashboard</title><style>{css}</style></head>
<body>
<header>
  <h1>Streaming Survival — Checker Statistics</h1>
  <div>run: {_esc(s.get('run_dir'))}</div>
  <div>dashboard: <span class="{status_cls}">{_esc(s.get('stats_dashboard_status'))}</span>
   · live: {_esc(s.get('live_stress_status'))}
   · visual: {_esc(s.get('visual_status'))}</div>
</header>
<main>
<section>
  <h2>1. Summary</h2>
  <div class="grid">
    <div class="kpi">attempts<b>{s.get('attempts_total')}</b></div>
    <div class="kpi">passed<b>{s.get('attempts_passed')}</b></div>
    <div class="kpi">failed<b>{s.get('attempts_failed')}</b></div>
    <div class="kpi">max jump<b>{s.get('max_position_jump')}</b></div>
    <div class="kpi">max speed<b>{s.get('max_speed')}</b></div>
    <div class="kpi">min ground clr<b>{s.get('min_ground_clearance')}</b></div>
    <div class="kpi">parser fail<b>{s.get('parser_failures')}</b></div>
    <div class="kpi">traj fail<b>{s.get('trajectory_failures')}</b></div>
    <div class="kpi">ground fail<b>{s.get('ground_failures')}</b></div>
    <div class="kpi">target fail<b>{s.get('target_failures')}</b></div>
    <div class="kpi">resource fail<b>{s.get('resource_guard_failures')}</b></div>
    <div class="kpi">visual fail<b>{s.get('visual_failures')}</b></div>
  </div>
</section>

<section>
  <h2>2. Parser Checker</h2>
  {img('parser_command_distribution.png')}
  {img('parser_action_distribution.png')}
  {img('parser_success_rate.png')}
  {img('parser_mode_distribution.png')}
  <p>Уникальные задачи прогона (текст чата → план действий):</p>
  {_table_html(_unique_task_plan_rows(parser_rows), ['задача', 'команды'])}
  <p><a href="parser_task_plan.html">Открыть таблицу задача → команды</a>
     · <a href="parser_task_plan.csv">CSV</a>
     · <a href="parser_stats.csv">полный CSV по attempt</a></p>
</section>

<section>
  <h2>3. Trajectory Continuity Checker</h2>
  {img('trajectory_max_jump_per_attempt.png')}
  {img('trajectory_avg_jump_per_attempt.png')}
  {img('trajectory_max_speed_per_attempt.png')}
  {img('trajectory_speed_histogram.png')}
  {img('trajectory_jump_histogram.png')}
  {img('trajectory_xz_paths_overview.png')}
  {img('trajectory_xz_by_attempt.png')}
  {img('trajectory_xz_by_user.png')}
  <p>Цвет линии = агент (stress_user_XX). <b>S</b> = старт попытки, <b>F</b> = финиш.
  Карта «по задачам» — отдельный сверху-вид на каждую попытку с текстом команды.
  Подписи мира: дом / вода / дерево / костёр / спавн.
  Таблица координат: <a href="world_landmarks.html">world_landmarks.html</a></p>
  <p>Top jumps:</p>
  {_table_html((problem_stats.get('top_suspicious') or {}).get('largest_jump') or [], ['attempt_id','max_position_jump','max_speed','continuity_status','command'])}
</section>

<section>
  <h2>4. Ground / Y Checker</h2>
  {img('ground_min_clearance_per_attempt.png')}
  {img('ground_clearance_histogram.png')}
  {img('ground_below_samples_per_attempt.png')}
  {img('ground_y_vs_character_y_examples.png')}
  {_table_html(ground_rows, ['attempt_id','min_ground_clearance','below_ground_samples_count','ground_status','command'])}
</section>

<section>
  <h2>5. Target Checker</h2>
  {img('target_final_distance_per_attempt.png')}
  {img('target_min_distance_per_attempt.png')}
  {img('target_reached_rate_by_action.png')}
  {img('target_distance_curves_examples.png')}
  {_table_html(target_rows, ['задача', 'итог', 'target_status', 'start_distance_to_target', 'min_distance_to_target', 'final_distance_to_target', 'time_to_reach_target'])}
  <p><a href="target_task_result.html">Открыть таблицу задача → итог</a>
     · <a href="target_task_result.csv">CSV</a>
     · <a href="target_stats.csv">полный CSV</a></p>
</section>

<section>
  <h2>6. ResourceGuard Checker</h2>
  {img('resource_delta_per_attempt.png')}
  {img('resource_credit_distance_histogram.png')}
  {img('resource_guard_denied_per_attempt.png')}
  {_table_html(resource_rows, ['attempt_id','action','resource_delta','expected_delta','credit_distance_to_target','resource_guard_denied_count','resource_status'])}
</section>

<section>
  <h2>7. Step Order Checker</h2>
  {img('step_order_pass_rate.png')}
  {img('step_order_timeline_examples.png')}
  {_table_html(step_rows, ['attempt_id','raw_command','expected_step_sequence','actual_step_sequence','step_order_status'])}
</section>

<section>
  <h2>8. Visual Checker</h2>
  <p style="font-size:13px;opacity:.85">Скрины с lab DISPLAY=:1 (start / mid / final) для каждой попытки.</p>
  {img('visual_verdicts_summary.png')}
  {_table_html(visual_rows, ['attempt_id','command','expected_visual_state','visual_verdict','annotated_screenshot_path'])}
  <div style="margin-top:10px">{visual_body}</div>
</section>

<section>
  <h2>9. Problem Finder</h2>
  <pre style="white-space:pre-wrap;font-size:12px">{_esc(json.dumps(problem_stats.get('summary_by_checker'), indent=2))}</pre>
  <p>Full report: <code>problem_finder_report.md</code> / <code>stats/problem_stats.json</code></p>
</section>
</main>
</body></html>
"""


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run-dir", default="")
    args = ap.parse_args()
    run = Path(args.run_dir) if args.run_dir else _latest_run()
    if run is None or not run.is_dir():
        print("FAIL: no run dir")
        return 1
    if not (run / "live_stress_report.json").is_file():
        print(f"FAIL: no live_stress_report.json in {run}")
        return 1
    summary = build_stats(run)
    print(json.dumps({k: summary.get(k) for k in (
        "stats_dashboard_status",
        "attempts_total",
        "attempts_passed",
        "attempts_failed",
        "parser_failures",
        "trajectory_failures",
        "ground_failures",
        "max_position_jump",
        "min_ground_clearance",
        "run_dir",
    )}, indent=2))
    print(str(run / "stats" / "stats_dashboard.html"))
    return 0 if summary.get("stats_dashboard_status") == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
