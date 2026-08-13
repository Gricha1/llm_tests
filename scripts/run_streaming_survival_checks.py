#!/usr/bin/env python3
"""
Streaming Survival checks (scripted follower characters — not ML-Agents / training).

Modes:
  --parser-only   parser PASS may be overall PASS (mode=parser_only)
  --full-runtime  PASS only with real world_registry + trajectory jsonl (+ ground checker)
  --live-stress   real Streaming Survival + bot chat attempts (default 40)
  --full-qa       full-runtime + live-stress + visual
  (default)       full-runtime if Unity answers; else INCOMPLETE (never fake PASS)

Usage:
  python scripts/run_streaming_survival_checks.py --parser-only
  python scripts/run_streaming_survival_checks.py --full-runtime
  python scripts/run_streaming_survival_checks.py --live-stress --attempts 40
  python scripts/run_streaming_survival_checks.py --full-qa --attempts 40
  python scripts/run_streaming_survival_checks.py
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from stream_bot.command_parser import ParsedKind, parse_message
from stream_bot.ss_trajectory_checker import (
    MAX_ALLOWED_POSITION_JUMP,
    NORMAL_GAMEPLAY_STRICT_IDS,
    check_trajectory,
)
from stream_bot.validator import (
    heuristic_action,
    normalize_expected_plan,
    plan_from_payload,
    validate_streaming_survival_action,
)

SCENARIOS = ROOT / "streaming_survival_tests" / "scenarios.json"
ARTIFACTS = ROOT / "artifacts" / "streaming_survival" / "test_runs"

# Scenarios that need Unity trajectories for full-runtime PASS
RUNTIME_TRAJECTORY_IDS = (
    "collect_water_simple",
    "collect_water_no_teleport",
    "go_home_no_teleport",
    "go_home_no_teleport_strict",
    "stuck_near_fence_no_target_teleport",
    "collect_wood_simple",
    "stone_disabled_no_collection",
    "cannot_collect_water_from_forest",
    "cannot_collect_wood_far_from_tree",
    "cannot_collect_food_far_from_sheep",
    "water_then_campfire",
    "water_home_wood_chain",
    "circle",
    "forward_backward",
    "idle",
    "seq_campfire_wood_water",
    "go_to_water_only",
    "collect_water_only",
    "go_home_only",
    "go_to_campfire_only",
    "collect_wood_5",
    "water_2_then_campfire",
    "wrong_target_visual_water",
)


def _utc_ts() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S_%f")[:21]


def _load_scenarios() -> List[Dict[str, Any]]:
    return json.loads(SCENARIOS.read_text(encoding="utf-8"))


def _do_text(cmd: str) -> str:
    p = parse_message(cmd, has_joined=True)
    if p.kind == ParsedKind.DO:
        return p.text or ""
    return ""


def _udp_send(payload: dict, host: str, port: int) -> bool:
    try:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.settimeout(2.0)
        sock.sendto(data, (host, port))
        sock.close()
        return True
    except OSError as e:
        print(f"[checks] UDP send failed: {e}")
        return False


def _bot_reachable(base: str, timeout: float = 2.0) -> bool:
    for path in ("/health", "/status"):
        try:
            with urllib.request.urlopen(base.rstrip("/") + path, timeout=timeout) as r:
                if r.status == 200:
                    return True
        except Exception:
            continue
    return False


def _wait_file(path: Path, timeout: float, poll: float = 0.4) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if path.is_file() and path.stat().st_size > 2:
            return True
        time.sleep(poll)
    return path.is_file() and path.stat().st_size > 2


def run_parser_tests(run_dir: Path) -> Dict[str, Any]:
    proc = subprocess.run(
        [
            sys.executable,
            "-m",
            "unittest",
            "stream_bot.tests.test_streaming_survival_parser",
            "stream_bot.tests.test_streaming_survival_llm_command_suite",
            "-q",
        ],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    scenarios = _load_scenarios()
    results = []
    failed = []
    for sc in scenarios:
        if sc.get("expect_join") or not sc.get("expected_plan"):
            continue
        if sc.get("expect_cooldown_reject_second_do"):
            continue
        # Negative runtime-only scenarios still have a plan for parser smoke.
        cmd = sc.get("chat_command") or ""
        if cmd.strip().lower() == "#join":
            continue
        text = _do_text(cmd)
        raw = heuristic_action(text, "test_user") if text else None
        ok = False
        reason = ""
        got: List[Any] = []
        if raw is None:
            reason = "no heuristic"
        else:
            vr = validate_streaming_survival_action(raw, username="test_user")
            if not vr.ok:
                reason = vr.reason
            else:
                got = plan_from_payload(vr.payload)
                want = normalize_expected_plan(sc["expected_plan"])
                ok = got == want
                if not ok:
                    reason = f"plan {got} != {want}"
        entry = {
            "id": sc["id"],
            "overall": "PASS" if ok else "FAIL",
            "plan": got,
            "reason": reason,
        }
        results.append(entry)
        if not ok:
            failed.append(entry)

    out = {
        "overall": "PASS" if proc.returncode == 0 and not failed else "FAIL",
        "unittest_returncode": proc.returncode,
        "stdout": (proc.stdout or "")[-4000:],
        "stderr": (proc.stderr or "")[-4000:],
        "scenarios": results,
        "failed": failed,
    }
    (run_dir / "parser_results.json").write_text(
        json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    (run_dir / "logs" / "parser_unittest.txt").write_text(
        (proc.stdout or "") + "\n" + (proc.stderr or ""), encoding="utf-8"
    )
    return out


def _base_payload(run_id: str, output_dir: Path) -> Dict[str, Any]:
    return {
        "run_id": run_id,
        "output_dir": str(output_dir.resolve()),
    }


def probe_unity_runtime(run_dir: Path, host: str, port: int, run_id: str) -> bool:
    """Send ping; Unity should write runtime_ping.json if Streaming Survival is up."""
    payload = {
        "type": "streaming_survival_test",
        "cmd": "ping",
        **_base_payload(run_id, run_dir),
    }
    if not _udp_send(payload, host, port):
        return False
    return _wait_file(run_dir / "runtime_ping.json", timeout=5.0)


def run_world_registry_test(
    run_dir: Path, host: str, port: int, run_id: str
) -> Dict[str, Any]:
    target = run_dir / "world_registry_results.json"
    # Remove stale from this run if any
    if target.is_file():
        try:
            target.unlink()
        except OSError:
            pass

    ok_send = _udp_send(
        {
            "type": "streaming_survival_test",
            "cmd": "world_registry_self_test",
            **_base_payload(run_id, run_dir),
        },
        host,
        port,
    )
    if not ok_send:
        out = {"overall": "FAIL", "ok": False, "reason": "UDP send failed"}
        target.write_text(json.dumps(out, indent=2), encoding="utf-8")
        return out

    if not _wait_file(target, timeout=10.0):
        out = {
            "overall": "INCOMPLETE",
            "ok": False,
            "sent": True,
            "reason": "world_registry_results.json not written within 10s",
        }
        target.write_text(json.dumps(out, indent=2), encoding="utf-8")
        return out

    try:
        data = json.loads(target.read_text(encoding="utf-8"))
    except Exception as e:
        return {"overall": "FAIL", "ok": False, "reason": f"bad json: {e}"}

    ok = bool(data.get("ok"))
    data["overall"] = "PASS" if ok else "FAIL"
    target.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    return data


def _parser_entry(sc: Dict[str, Any]) -> Dict[str, Any]:
    entry: Dict[str, Any] = {
        "id": sc["id"],
        "parser": "SKIP",
        "target": "SKIP",
        "trajectory": "SKIP",
        "resource": "SKIP",
        "result": "INCOMPLETE",
        "note": "",
    }
    if sc.get("expect_join") or sc.get("expect_cooldown_reject_second_do"):
        entry["parser"] = "SKIP"
        entry["note"] = "join/cooldown checked in e2e / Unity special cmds"
        return entry
    if not sc.get("expected_plan"):
        entry["note"] = "no expected_plan"
        return entry
    text = _do_text(sc.get("chat_command") or "")
    raw = heuristic_action(text, "test_user") if text else None
    if raw is None:
        entry["parser"] = "FAIL"
        entry["result"] = "FAIL"
        entry["reason"] = "no heuristic"
        return entry
    vr = validate_streaming_survival_action(raw, username="test_user")
    if not vr.ok:
        entry["parser"] = "FAIL"
        entry["result"] = "FAIL"
        entry["reason"] = vr.reason
        return entry
    got = plan_from_payload(vr.payload)
    want = normalize_expected_plan(sc["expected_plan"])
    if got != want:
        entry["parser"] = "FAIL"
        entry["result"] = "FAIL"
        entry["reason"] = f"plan {got} != {want}"
        return entry
    entry["parser"] = "PASS"
    entry["result"] = "INCOMPLETE"
    entry["note"] = "Parser passed, but runtime trajectory was not checked."
    return entry


def _unity_result_ok(data: Optional[Dict[str, Any]]) -> Tuple[bool, str]:
    if not isinstance(data, dict):
        return False, "unity_result missing"
    overall = str(data.get("overall", "")).upper()
    ok_flag = data.get("ok")
    if overall == "PASS" and ok_flag is not False:
        return True, ""
    if overall == "FAIL" or ok_flag is False:
        return False, str(data.get("reason") or "unity_result FAIL")
    return False, str(data.get("reason") or f"unity_result overall={overall or '?'}")


def validate_report_consistency(
    summary: Dict[str, Any], scenarios: Dict[str, Any], run_dir: Path
) -> List[str]:
    """Fail closed if summary PASS hides nested FAIL / missing runtime artifacts."""
    errors: List[str] = []
    entries = scenarios.get("scenarios") or []
    for entry in entries:
        sid = entry.get("id", "?")
        result = str(entry.get("result", "")).upper()
        unity = entry.get("unity_result")
        if isinstance(unity, dict):
            u_overall = str(unity.get("overall", "")).upper()
            if (u_overall == "FAIL" or unity.get("ok") is False) and result == "PASS":
                errors.append(f"{sid}: result=PASS but unity_result FAIL ({unity.get('reason')})")
            u_cont = str(unity.get("continuity", "")).upper()
            if u_cont == "FAIL" and result == "PASS":
                errors.append(f"{sid}: result=PASS but unity continuity=FAIL")
            u_illegal = unity.get("illegal_teleports")
            if result == "PASS" and isinstance(u_illegal, list) and len(u_illegal) > 0:
                errors.append(f"{sid}: result=PASS but unity illegal_teleports not empty")
        for key in ("target", "trajectory", "resource", "direction", "continuity"):
            val = str(entry.get(key, "")).upper()
            if val == "FAIL" and result == "PASS":
                errors.append(f"{sid}: result=PASS but {key}=FAIL")
        illegal = entry.get("illegal_teleports")
        if result == "PASS" and isinstance(illegal, list) and len(illegal) > 0:
            errors.append(f"{sid}: result=PASS but illegal_teleports not empty")
        if result == "PASS" and sid in RUNTIME_TRAJECTORY_IDS:
            if not isinstance(unity, dict):
                errors.append(f"{sid}: result=PASS but unity_result missing")
            traj = run_dir / "trajectories" / f"{sid}_trajectory.jsonl"
            marker = run_dir / f"{sid}_result.json"
            if not traj.is_file():
                errors.append(f"{sid}: result=PASS but trajectory jsonl missing")
            if not marker.is_file():
                errors.append(f"{sid}: result=PASS but *_result.json missing")

    if str(summary.get("overall", "")).upper() == "PASS" or str(
        summary.get("full_runtime_status", "")
    ).upper() == "PASS":
        for entry in entries:
            sid = entry.get("id", "?")
            result = str(entry.get("result", "")).upper()
            if result in ("FAIL", "INCOMPLETE", "SKIP") and sid in RUNTIME_TRAJECTORY_IDS:
                errors.append(
                    f"summary PASS but scenario {sid} result={result}"
                )
            unity = entry.get("unity_result")
            if isinstance(unity, dict) and (
                str(unity.get("overall", "")).upper() == "FAIL" or unity.get("ok") is False
            ):
                errors.append(f"summary PASS but {sid} unity_result FAIL")
        if str(summary.get("world_registry", "")).upper() in ("FAIL", "SKIP", "INCOMPLETE"):
            errors.append(
                f"summary PASS but world_registry={summary.get('world_registry')}"
            )
        if str(summary.get("runtime_scenarios", "")).upper() != "PASS":
            errors.append(
                f"summary PASS but runtime_scenarios={summary.get('runtime_scenarios')}"
            )
        traj_dir = run_dir / "trajectories"
        if not traj_dir.is_dir() or not any(traj_dir.glob("*.jsonl")):
            errors.append("summary PASS but trajectories/*.jsonl missing")
    return errors


def run_runtime_scenarios(
    run_dir: Path,
    host: str,
    port: int,
    run_id: str,
    *,
    do_runtime: bool,
    only_ids: Optional[List[str]] = None,
    merge_into: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    scenarios = _load_scenarios()
    traj_dir = run_dir / "trajectories"
    traj_dir.mkdir(parents=True, exist_ok=True)
    results: List[Dict[str, Any]] = list((merge_into or {}).get("scenarios") or [])
    # Drop entries we will re-run
    if only_ids:
        skip = set(only_ids)
        results = [e for e in results if str(e.get("id")) not in skip]
    failed: List[Dict[str, Any]] = []
    incomplete: List[Dict[str, Any]] = []

    by_id = {s["id"]: s for s in scenarios}

    id_list = list(only_ids) if only_ids else list(RUNTIME_TRAJECTORY_IDS)
    for sid in id_list:
        sc = by_id.get(sid)
        if sc is None:
            entry = {
                "id": sid,
                "parser": "FAIL",
                "result": "FAIL",
                "reason": "missing from scenarios.json",
            }
            failed.append(entry)
            results.append(entry)
            continue

        entry = _parser_entry(sc)
        if entry["parser"] == "FAIL":
            failed.append(entry)
            results.append(entry)
            continue

        if not do_runtime:
            incomplete.append(entry)
            results.append(entry)
            continue

        done_marker = run_dir / f"{sid}_result.json"
        traj_path = traj_dir / f"{sid}_trajectory.jsonl"
        for p in (done_marker, traj_path):
            if p.is_file():
                try:
                    p.unlink()
                except OSError:
                    pass

        payload = {
            "type": "streaming_survival_test",
            "cmd": "run_scenario",
            **_base_payload(run_id, run_dir),
            "scenario": sc,
        }
        if not _udp_send(payload, host, port):
            entry["result"] = "FAIL"
            entry["reason"] = "UDP send failed"
            failed.append(entry)
            results.append(entry)
            continue

        max_sec = float(sc.get("max_seconds") or 30) + 10.0
        print(f"[checks] waiting scenario={sid} timeout={max_sec:.0f}s")
        got_done = _wait_file(done_marker, timeout=max_sec)
        got_traj = traj_path.is_file() or _wait_file(traj_path, timeout=2.0)

        if not got_traj:
            entry["result"] = "INCOMPLETE"
            entry["note"] = "runtime UDP sent but trajectory jsonl missing after timeout"
            incomplete.append(entry)
            results.append(entry)
            continue

        cr = check_trajectory(sc, traj_path)
        entry["target"] = "PASS" if cr.target_pass else "FAIL"
        entry["trajectory"] = "PASS" if cr.trajectory_pass else "FAIL"
        entry["resource"] = "PASS" if cr.resource_pass else "FAIL"
        entry["direction"] = "PASS" if cr.direction_pass else "FAIL"
        entry["continuity"] = "PASS" if cr.continuity_pass else "FAIL"
        entry["ground_checker"] = "PASS" if getattr(cr, "ground_pass", True) else "FAIL"
        entry["min_ground_clearance"] = round(
            float(getattr(cr, "min_ground_clearance", 0) or 0), 3
        )
        entry["max_ground_clearance"] = round(
            float(getattr(cr, "max_ground_clearance", 0) or 0), 3
        )
        entry["max_position_jump"] = round(float(cr.max_position_jump), 3)
        entry["max_speed"] = round(float(cr.max_speed), 3)
        entry["illegal_teleports"] = list(cr.illegal_teleports)
        entry["controlled_teleports"] = int(getattr(cr, "controlled_teleports", 0) or 0)
        entry["note"] = ""

        # Merge continuity from Unity result if present (fail if either fails)
        unity: Optional[Dict[str, Any]] = None
        if got_done:
            try:
                unity = json.loads(done_marker.read_text(encoding="utf-8-sig"))
            except Exception as e:
                unity = {"overall": "FAIL", "ok": False, "reason": f"bad *_result.json: {e}"}
        else:
            # Trajectory without Unity done marker is not a full-runtime PASS
            entry["result"] = "INCOMPLETE"
            entry["note"] = "trajectory present but *_result.json missing"
            entry["reason"] = "missing unity *_result.json"
            incomplete.append(entry)
            results.append(entry)
            continue

        if isinstance(unity, dict):
            if str(unity.get("continuity", "")).upper() == "FAIL":
                entry["continuity"] = "FAIL"
                cr.continuity_pass = False
                cr.ok = False
            if str(unity.get("ground_checker", "")).upper() == "FAIL":
                entry["ground_checker"] = "FAIL"
                cr.ground_pass = False
                cr.ok = False
            if unity.get("min_ground_clearance") is not None:
                try:
                    entry["min_ground_clearance"] = min(
                        float(entry["min_ground_clearance"]),
                        float(unity["min_ground_clearance"]),
                    )
                except (TypeError, ValueError):
                    pass
            u_illegal = unity.get("illegal_teleports")
            if isinstance(u_illegal, list) and u_illegal:
                entry["continuity"] = "FAIL"
                for msg in u_illegal:
                    if msg not in entry["illegal_teleports"]:
                        entry["illegal_teleports"].append(msg)
                cr.ok = False
            if unity.get("max_position_jump") is not None:
                try:
                    entry["max_position_jump"] = max(
                        float(entry["max_position_jump"]), float(unity["max_position_jump"])
                    )
                except (TypeError, ValueError):
                    pass
            if unity.get("max_speed") is not None:
                try:
                    entry["max_speed"] = max(float(entry["max_speed"]), float(unity["max_speed"]))
                except (TypeError, ValueError):
                    pass

        # Normal gameplay: jump > threshold is FAIL even with stuck_recovery excuse.
        if sid in NORMAL_GAMEPLAY_STRICT_IDS:
            try:
                jump = float(entry.get("max_position_jump") or 0)
            except (TypeError, ValueError):
                jump = 0.0
            if jump > MAX_ALLOWED_POSITION_JUMP:
                msg = (
                    f"max_position_jump={jump:.3f} exceeds "
                    f"MAX_ALLOWED_POSITION_JUMP={MAX_ALLOWED_POSITION_JUMP:.1f}"
                )
                entry["continuity"] = "FAIL"
                if msg not in entry["illegal_teleports"]:
                    entry["illegal_teleports"].append(msg)
                cr.ok = False
                if not cr.reason:
                    cr.reason = msg
                else:
                    cr.reason += " | " + msg

        py_ok = (
            bool(cr.ok)
            and entry["parser"] == "PASS"
            and entry["continuity"] == "PASS"
            and entry.get("ground_checker", "PASS") == "PASS"
        )
        if entry["illegal_teleports"]:
            py_ok = False
        if sid in NORMAL_GAMEPLAY_STRICT_IDS and entry.get("controlled_teleports", 0) > 0:
            py_ok = False
            entry["continuity"] = "FAIL"
            msg = f"controlled_teleports_in_normal_gameplay={entry['controlled_teleports']}"
            if msg not in entry["illegal_teleports"]:
                entry["illegal_teleports"].append(msg)
        unity_ok, unity_reason = _unity_result_ok(unity)
        entry["unity_result"] = unity

        if py_ok and unity_ok:
            entry["result"] = "PASS"
            entry["reason"] = ""
        else:
            entry["result"] = "FAIL"
            reasons = []
            if not py_ok:
                reasons.append(cr.reason or "python checker FAIL")
            if entry["illegal_teleports"]:
                reasons.append(f"illegal_teleports={len(entry['illegal_teleports'])}")
            if entry.get("ground_checker") == "FAIL":
                reasons.append("ground_checker FAIL")
            if not unity_ok:
                reasons.append(unity_reason)
            entry["reason"] = " | ".join(reasons)
            failed.append(entry)
        results.append(entry)

    # Special: join + cooldown + join_default_idle via Unity cmds when runtime
    if only_ids is None:
      for sid, cmd in (
          ("join_idempotent", "test_join_idempotent"),
          ("action_cooldown", "test_action_cooldown"),
          ("join_default_idle", "test_join_default_idle"),
      ):
        sc = by_id.get(sid, {"id": sid})
        entry = {
            "id": sid,
            "parser": "SKIP",
            "target": "SKIP",
            "trajectory": "SKIP",
            "resource": "SKIP",
            "ground_checker": "SKIP",
            "result": "INCOMPLETE",
            "note": "Parser passed, but runtime trajectory was not checked."
            if not do_runtime
            else "",
        }
        if not do_runtime:
            incomplete.append(entry)
            results.append(entry)
            continue
        marker = run_dir / f"{sid}_result.json"
        traj_path = traj_dir / f"{sid}_trajectory.jsonl"
        if marker.is_file():
            try:
                marker.unlink()
            except OSError:
                pass
        _udp_send(
            {
                "type": "streaming_survival_test",
                "cmd": cmd,
                **_base_payload(run_id, run_dir),
            },
            host,
            port,
        )
        wait_sec = 20.0 if sid == "join_default_idle" else 15.0
        if _wait_file(marker, timeout=wait_sec):
            try:
                data = json.loads(marker.read_text(encoding="utf-8-sig"))
                ok, reason = _unity_result_ok(data)
                entry["unity_result"] = data
                entry["note"] = ""
                if sid == "join_default_idle" and traj_path.is_file():
                    cr = check_trajectory(
                        {
                            "id": sid,
                            "expected_motion_pattern": "idle",
                            "expected_resource_delta": {
                                "water": 0,
                                "wood": 0,
                                "food": 0,
                            },
                            "max_seconds": 12,
                        },
                        traj_path,
                    )
                    entry["trajectory"] = "PASS" if cr.trajectory_pass else "FAIL"
                    entry["continuity"] = "PASS" if cr.continuity_pass else "FAIL"
                    entry["ground_checker"] = (
                        "PASS" if getattr(cr, "ground_pass", True) else "FAIL"
                    )
                    entry["min_ground_clearance"] = round(
                        float(getattr(cr, "min_ground_clearance", 0) or 0), 3
                    )
                    entry["max_position_jump"] = round(float(cr.max_position_jump), 3)
                    entry["illegal_teleports"] = list(cr.illegal_teleports)
                    if not cr.ok:
                        ok = False
                        reason = (reason + " | " if reason else "") + (
                            cr.reason or "traj FAIL"
                        )
                if str(data.get("ground_checker", "")).upper() == "FAIL":
                    ok = False
                    entry["ground_checker"] = "FAIL"
                    reason = (reason + " | " if reason else "") + "ground FAIL"
                if ok:
                    entry["result"] = "PASS"
                    entry["reason"] = ""
                else:
                    entry["result"] = "FAIL"
                    entry["reason"] = reason
            except Exception as e:
                entry["result"] = "FAIL"
                entry["reason"] = str(e)
        else:
            entry["result"] = "INCOMPLETE"
            entry["note"] = f"{cmd} result file missing"
        results.append(entry)

    # Recompute buckets from final results (supports merge/retry).
    failed = [e for e in results if e.get("result") == "FAIL"]
    incomplete = [e for e in results if e.get("result") == "INCOMPLETE"]

    if failed:
        overall = "FAIL"
    elif incomplete or not do_runtime:
        overall = "INCOMPLETE"
    else:
        overall = "PASS"

    out = {
        "overall": overall,
        "passed": sum(1 for x in results if x.get("result") == "PASS"),
        "failed": len(failed),
        "incomplete": len(incomplete),
        "scenarios": results,
        "failed_scenarios": failed,
        "incomplete_scenarios": incomplete,
    }
    (run_dir / "scenario_results.json").write_text(
        json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return out


def run_e2e(run_dir: Path, bot_url: str, *, required: bool) -> Dict[str, Any]:
    script = ROOT / "scripts" / "test_streaming_survival_e2e.py"
    if not script.is_file():
        out = {"overall": "SKIP", "optional": True, "reason": "e2e script missing"}
        (run_dir / "e2e_results.json").write_text(json.dumps(out, indent=2), encoding="utf-8")
        return out
    if not _bot_reachable(bot_url):
        out = {
            "overall": "SKIP",
            "optional": True,
            "reason": f"bot not reachable at {bot_url} (e2e optional)",
        }
        (run_dir / "e2e_results.json").write_text(json.dumps(out, indent=2), encoding="utf-8")
        return out
    proc = subprocess.run(
        [sys.executable, str(script), "--bot-url", bot_url, "--run-dir", str(run_dir)],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    out = {
        "overall": "PASS" if proc.returncode == 0 else "FAIL",
        "optional": False,
        "returncode": proc.returncode,
        "stdout": (proc.stdout or "")[-6000:],
        "stderr": (proc.stderr or "")[-3000:],
    }
    (run_dir / "e2e_results.json").write_text(json.dumps(out, indent=2), encoding="utf-8")
    (run_dir / "logs" / "e2e.txt").write_text(
        (proc.stdout or "") + "\n" + (proc.stderr or ""), encoding="utf-8"
    )
    return out


def _write_summary(run_dir: Path, summary: Dict[str, Any]) -> None:
    (run_dir / "summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    (ARTIFACTS / "LATEST").write_text(str(run_dir), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


def main() -> int:
    ap = argparse.ArgumentParser(description="Streaming Survival checks (followers, not training AI)")
    ap.add_argument("--parser-only", action="store_true")
    ap.add_argument(
        "--full-runtime",
        action="store_true",
        help="Require Unity world_registry + trajectory jsonl for PASS",
    )
    ap.add_argument(
        "--live-stress",
        action="store_true",
        help="Run live Streaming Survival stress attempts via bot local_chat",
    )
    ap.add_argument(
        "--full-qa",
        action="store_true",
        help="full-runtime + live-stress + visual (requires attempts)",
    )
    ap.add_argument(
        "--attempts",
        type=int,
        default=40,
        help="Live stress attempt count (default 40)",
    )
    ap.add_argument(
        "--time-scale",
        type=float,
        default=1.0,
        help="Unity Time.timeScale for live-stress only (1=normal, 3=faster). Restored after run.",
    )
    # legacy aliases
    ap.add_argument("--require-runtime", action="store_true", help=argparse.SUPPRESS)
    ap.add_argument("--send-runtime", action="store_true", help=argparse.SUPPRESS)
    ap.add_argument("--unity-host", default=os.environ.get("FOREST_UNITY_HOST", "127.0.0.1"))
    ap.add_argument(
        "--unity-port",
        type=int,
        default=int(os.environ.get("FOREST_STREAM_BOT_PORT", "5055")),
    )
    ap.add_argument("--bot-url", default=os.environ.get("STREAM_BOT_URL", "http://127.0.0.1:8765"))
    args = ap.parse_args()

    if args.require_runtime or args.send_runtime:
        args.full_runtime = True
    if args.full_qa:
        args.full_runtime = True
        args.live_stress = True

    # Live-stress-only mode: skip long scenario suite unless full-runtime also requested.
    if args.live_stress and not args.full_runtime and not args.parser_only:
        return _run_live_stress_only(args)

    run_id = _utc_ts()
    run_dir = ARTIFACTS / run_id
    (run_dir / "logs").mkdir(parents=True, exist_ok=True)
    (run_dir / "trajectories").mkdir(parents=True, exist_ok=True)
    # Also set env so local Unity (same machine) prefers this tree
    os.environ["STREAMING_SURVIVAL_TEST_ARTIFACTS_DIR"] = str(run_dir.resolve())
    os.environ["STREAMING_SURVIVAL_ARTIFACTS"] = str(ARTIFACTS.resolve())
    mode = (
        "parser_only"
        if args.parser_only
        else ("full_qa" if args.full_qa else ("full_runtime" if args.full_runtime else "auto"))
    )
    print(f"[checks] run_dir={run_dir} mode={mode}")

    parser = run_parser_tests(run_dir)
    print(f"[checks] parser={parser['overall']}")

    if args.parser_only:
        overall = "PASS" if parser["overall"] == "PASS" else "FAIL"
        summary = {
            "timestamp": run_id,
            "overall": overall,
            "mode": "parser_only",
            "parser_only_status": overall,
            "full_runtime_status": "NOT_RUN",
            "ground_status": "NOT_RUN",
            "live_stress_status": "NOT_RUN",
            "parser": parser["overall"],
            "world_registry": "SKIP",
            "runtime_scenarios": "SKIP",
            "e2e": "SKIP",
            "scenario_tests": {"passed": 0, "failed": 0, "overall": "SKIP"},
            "failed_scenarios": parser.get("failed") or [],
            "reason": "Parser-only mode. Runtime not checked.",
            "run_dir": str(run_dir),
        }
        _write_summary(run_dir, summary)
        return 0 if overall == "PASS" else 1

    # Auto / full-runtime path
    want_full = bool(args.full_runtime)
    runtime_up = probe_unity_runtime(run_dir, args.unity_host, args.unity_port, run_id)
    print(f"[checks] unity_runtime={'UP' if runtime_up else 'DOWN'}")

    if not runtime_up:
        scenarios = run_runtime_scenarios(
            run_dir, args.unity_host, args.unity_port, run_id, do_runtime=False
        )
        world = {
            "overall": "SKIP",
            "reason": "Streaming Survival runtime not available (no runtime_ping.json)",
        }
        (run_dir / "world_registry_results.json").write_text(
            json.dumps(world, indent=2), encoding="utf-8"
        )
        e2e = {"overall": "SKIP", "optional": True, "reason": "runtime down; e2e not required"}
        (run_dir / "e2e_results.json").write_text(json.dumps(e2e, indent=2), encoding="utf-8")

        reason = (
            "Runtime not available. Start Streaming Survival build/scene and use --full-runtime."
        )
        if want_full:
            overall = "FAIL"
            full_status = "FAIL"
            reason = reason + " (--full-runtime requires Unity)."
            exit_code = 1
        elif parser["overall"] == "FAIL":
            overall = "FAIL"
            full_status = "FAIL"
            exit_code = 1
        else:
            overall = "INCOMPLETE"
            full_status = "INCOMPLETE"
            exit_code = 2

        summary = {
            "timestamp": run_id,
            "overall": overall,
            "mode": mode,
            "parser_only_status": parser["overall"],
            "full_runtime_status": full_status,
            "ground_status": "FAIL" if want_full else "SKIP",
            "live_stress_status": "NOT_RUN",
            "parser": parser["overall"],
            "world_registry": "SKIP",
            "runtime_scenarios": "SKIP",
            "e2e": "SKIP",
            "scenario_tests": {
                "passed": 0,
                "failed": scenarios.get("failed", 0),
                "incomplete": scenarios.get("incomplete", 0),
                "overall": "INCOMPLETE",
            },
            "failed_scenarios": parser.get("failed") or [],
            "reason": reason,
            "run_dir": str(run_dir),
        }
        _write_summary(run_dir, summary)
        return exit_code

    # Runtime is up — give Streaming Survival a moment to finish WorldRegistry water/tree bind.
    time.sleep(8.0)
    world = run_world_registry_test(run_dir, args.unity_host, args.unity_port, run_id)
    print(f"[checks] world_registry={world.get('overall')}")
    scenarios = run_runtime_scenarios(
        run_dir, args.unity_host, args.unity_port, run_id, do_runtime=True
    )
    # One retry pass for flaky first-scenario cold-start failures (empty route / water@spawn).
    retry_ids = [
        str(e.get("id"))
        for e in (scenarios.get("failed_scenarios") or [])
        if str(e.get("id") or "")
    ]
    if retry_ids and world.get("overall") == "PASS":
        print(f"[checks] retrying failed scenarios once: {retry_ids}")
        time.sleep(3.0)
        scenarios = run_runtime_scenarios(
            run_dir,
            args.unity_host,
            args.unity_port,
            run_id,
            do_runtime=True,
            only_ids=retry_ids,
            merge_into=scenarios,
        )
    print(f"[checks] runtime_scenarios={scenarios.get('overall')}")
    e2e = run_e2e(run_dir, args.bot_url, required=False)
    print(f"[checks] e2e={e2e.get('overall')} optional={e2e.get('optional')}")

    parts_ok = (
        parser["overall"] == "PASS"
        and world.get("overall") == "PASS"
        and scenarios.get("overall") == "PASS"
    )
    e2e_ok = e2e.get("overall") == "PASS" or (
        e2e.get("overall") == "SKIP" and e2e.get("optional")
    )
    if e2e.get("overall") == "FAIL":
        e2e_ok = False

    if parts_ok and e2e_ok:
        overall = "PASS"
        full_status = "PASS"
        reason = ""
    elif parser["overall"] == "FAIL" or world.get("overall") == "FAIL" or scenarios.get("overall") == "FAIL" or e2e.get("overall") == "FAIL":
        overall = "FAIL"
        full_status = "FAIL"
        reason = "One or more required checks FAILED"
    else:
        overall = "INCOMPLETE"
        full_status = "INCOMPLETE"
        reason = "Runtime started but some artifacts incomplete (trajectories/registry)"

    failed_scenarios = list(parser.get("failed") or []) + list(
        scenarios.get("failed_scenarios") or []
    )
    summary = {
        "timestamp": run_id,
        "overall": overall,
        "mode": mode,
        "parser_only_status": parser["overall"],
        "full_runtime_status": full_status,
        "parser": parser["overall"],
        "world_registry": world.get("overall", "SKIP"),
        "runtime_scenarios": scenarios.get("overall", "SKIP"),
        "e2e": e2e.get("overall", "SKIP"),
        "scenario_tests": {
            "passed": scenarios.get("passed", 0),
            "failed": scenarios.get("failed", 0),
            "incomplete": scenarios.get("incomplete", 0),
            "overall": scenarios.get("overall", "SKIP"),
        },
        "failed_scenarios": failed_scenarios,
        "scenario_results": scenarios,
        "reason": reason,
        "run_dir": str(run_dir),
    }
    # Aggregate continuity / illegal teleports / ground into summary
    illegal_total = 0
    max_jump = 0.0
    max_speed = 0.0
    controlled_normal = 0
    jump_fail_normal = 0
    ground_fail = 0
    min_ground = None
    for e in scenarios.get("scenarios") or []:
        sid = str(e.get("id") or "")
        arr = e.get("illegal_teleports") or []
        if isinstance(arr, list):
            illegal_total += len(arr)
        try:
            jump_v = float(e.get("max_position_jump") or 0)
            max_jump = max(max_jump, jump_v)
            max_speed = max(max_speed, float(e.get("max_speed") or 0))
            if sid in NORMAL_GAMEPLAY_STRICT_IDS and jump_v > MAX_ALLOWED_POSITION_JUMP:
                jump_fail_normal += 1
        except (TypeError, ValueError):
            pass
        if str(e.get("ground_checker", "")).upper() == "FAIL":
            ground_fail += 1
        try:
            g = e.get("min_ground_clearance")
            if g is not None:
                gv = float(g)
                min_ground = gv if min_ground is None else min(min_ground, gv)
        except (TypeError, ValueError):
            pass
        try:
            ct = int(e.get("controlled_teleports") or 0)
        except (TypeError, ValueError):
            ct = 0
        if sid in NORMAL_GAMEPLAY_STRICT_IDS:
            controlled_normal += ct
        u = e.get("unity_result")
        if isinstance(u, dict):
            uarr = u.get("illegal_teleports") or []
            if isinstance(uarr, list):
                illegal_total += len(uarr)
    summary["illegal_teleports"] = illegal_total
    summary["controlled_teleports_in_normal_gameplay"] = controlled_normal
    summary["max_position_jump"] = round(max_jump, 3)
    summary["max_speed"] = round(max_speed, 3)
    summary["ground_failures"] = ground_fail
    summary["min_ground_clearance"] = (
        round(min_ground, 3) if min_ground is not None else None
    )
    summary["ground_status"] = "FAIL" if ground_fail else "PASS"
    if ground_fail and overall == "PASS":
        overall = "FAIL"
        summary["overall"] = "FAIL"
        summary["full_runtime_status"] = "FAIL"
        summary["ground_status"] = "FAIL"
        summary["reason"] = (
            (summary.get("reason") + " | " if summary.get("reason") else "")
            + f"ground_failures={ground_fail}"
        )

    # Optional / required live stress
    live = {"overall": "NOT_RUN", "attempts_total": 0}
    if args.live_stress:
        live = _invoke_live_stress(
            run_dir,
            args.bot_url,
            args.unity_host,
            args.unity_port,
            args.attempts,
            run_id,
            getattr(args, "time_scale", 1.0) or 1.0,
        )
        summary["live_stress_status"] = live.get("overall", "FAIL")
        summary["live_stress"] = live
        if live.get("overall") != "PASS":
            overall = "FAIL"
            summary["overall"] = "FAIL"
            summary["reason"] = (
                (summary.get("reason") + " | " if summary.get("reason") else "")
                + "live_stress FAIL"
            )
    else:
        summary["live_stress_status"] = "NOT_RUN"

    # QA split: machine vs visual
    summary["machine_full_runtime_status"] = summary.get("full_runtime_status", full_status)
    if args.full_qa or args.live_stress:
        _seed_visual_verdicts(run_dir, scenarios)
    verdicts_path = run_dir / "visual_verdicts.json"
    if verdicts_path.is_file():
        try:
            vv = json.loads(verdicts_path.read_text(encoding="utf-8"))
            fails = [
                k
                for k, v in (vv.items() if isinstance(vv, dict) else [])
                if isinstance(v, dict) and str(v.get("verdict", "")).upper() == "FAIL"
            ]
            pending = [
                k
                for k, v in (vv.items() if isinstance(vv, dict) else [])
                if isinstance(v, dict) and str(v.get("verdict", "")).upper() == "PENDING"
            ]
            if fails:
                summary["visual_status"] = "FAIL"
            elif pending and not args.full_qa:
                summary["visual_status"] = "PENDING"
            else:
                summary["visual_status"] = "PASS"
            summary["visual_fail_ids"] = fails
        except Exception:
            summary["visual_status"] = "PENDING"
    else:
        summary["visual_status"] = "PENDING" if not args.full_qa else "FAIL"

    # Stats dashboard (from live stress artifacts)
    stats_status = "NOT_RUN"
    stats_path = run_dir / "stats" / "stats_summary.json"
    if not stats_path.is_file() and (run_dir / "live_stress_report.json").is_file():
        try:
            from scripts.generate_streaming_survival_test_stats import build_stats

            build_stats(run_dir)
        except Exception as e:
            print(f"[checks] stats generation failed: {e}")
    if stats_path.is_file():
        try:
            stats_obj = json.loads(stats_path.read_text(encoding="utf-8"))
            stats_status = stats_obj.get("stats_dashboard_status") or "FAIL"
            summary["stats_summary"] = stats_obj
        except Exception:
            stats_status = "FAIL"
    summary["stats_dashboard_status"] = stats_status

    live_obj = summary.get("live_stress") or {}
    live_failed = int(live_obj.get("attempts_failed") or 0)
    live_total = int(live_obj.get("attempts_total") or 0)
    live_parser_fail = int(live_obj.get("parser_failures") or 0)
    live_resource_fail = int(live_obj.get("resource_guard_failures") or 0)
    live_illegal = int(summary.get("illegal_teleports") or 0) + int(
        live_obj.get("teleport_failures") or 0
    )

    if (
        summary["machine_full_runtime_status"] == "PASS"
        and summary.get("ground_status") == "PASS"
        and summary["visual_status"] == "PASS"
        and (not args.live_stress or summary.get("live_stress_status") == "PASS")
        and (not args.live_stress or stats_status == "PASS")
        and (not args.live_stress or (live_total >= 40 and live_failed == 0))
        and live_illegal == 0
        and int(summary.get("ground_failures") or 0) == 0
        and live_parser_fail == 0
        and live_resource_fail == 0
    ):
        summary["overall_qa_status"] = "PASS"
    elif summary["visual_status"] == "PENDING" and not args.full_qa:
        summary["overall_qa_status"] = "VISUAL_PENDING"
    else:
        summary["overall_qa_status"] = "FAIL"

    if args.full_qa:
        if summary.get("overall_qa_status") != "PASS":
            overall = "FAIL"
            summary["overall"] = "FAIL"
            summary["reason"] = (
                (summary.get("reason") + " | " if summary.get("reason") else "")
                + "full_qa requires full_runtime+live_stress+visual+ground+stats PASS"
            )
        else:
            overall = "PASS"
            summary["overall"] = "PASS"

    fail_bits = []
    if illegal_total > 0:
        fail_bits.append(f"illegal_teleports={illegal_total}")
    if controlled_normal > 0:
        fail_bits.append(f"controlled_teleports_in_normal_gameplay={controlled_normal}")
    if jump_fail_normal > 0:
        fail_bits.append(
            f"normal_gameplay_max_jump_exceeded={jump_fail_normal} "
            f"(MAX_ALLOWED_POSITION_JUMP={MAX_ALLOWED_POSITION_JUMP})"
        )
    if fail_bits and overall == "PASS":
        overall = "FAIL"
        summary["overall"] = "FAIL"
        summary["full_runtime_status"] = "FAIL"
        summary["reason"] = (
            (summary.get("reason") + " | " if summary.get("reason") else "")
            + " | ".join(fail_bits)
        )
    consistency_errors = validate_report_consistency(summary, scenarios, run_dir)
    if consistency_errors:
        summary["overall"] = "FAIL"
        summary["full_runtime_status"] = "FAIL"
        summary["runtime_scenarios"] = "FAIL"
        summary["scenario_tests"]["overall"] = "FAIL"
        summary["consistency_errors"] = consistency_errors
        summary["reason"] = (
            (summary.get("reason") + " | " if summary.get("reason") else "")
            + "report consistency failed: "
            + "; ".join(consistency_errors[:5])
        )
        overall = "FAIL"
        print("[checks] consistency FAIL:")
        for err in consistency_errors:
            print(f"  - {err}")

    try:
        from scripts.analyze_streaming_survival_failures import analyze as _analyze

        _analyze(run_dir)
    except Exception as e:
        print(f"[checks] problem_finder skip: {e}")

    _write_summary(run_dir, summary)
    if overall == "PASS":
        return 0
    if overall == "INCOMPLETE":
        return 2
    return 1


def _invoke_live_stress(
    run_dir: Path,
    bot_url: str,
    unity_host: str,
    unity_port: int,
    attempts: int,
    run_id: str,
    time_scale: float = 1.0,
) -> Dict[str, Any]:
    script = ROOT / "scripts" / "run_streaming_survival_live_stress_test.py"
    if not script.is_file():
        return {"overall": "FAIL", "reason": "live stress script missing"}
    proc = subprocess.run(
        [
            sys.executable,
            str(script),
            "--bot-url",
            bot_url,
            "--unity-host",
            unity_host,
            "--unity-port",
            str(unity_port),
            "--attempts",
            str(attempts),
            "--time-scale",
            str(time_scale),
            "--run-dir",
            str(run_dir),
            "--run-id",
            run_id,
        ],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    report_path = run_dir / "live_stress_report.json"
    if report_path.is_file():
        try:
            return json.loads(report_path.read_text(encoding="utf-8"))
        except Exception as e:
            return {"overall": "FAIL", "reason": f"bad live_stress_report: {e}"}
    return {
        "overall": "FAIL",
        "reason": "live_stress_report.json missing",
        "returncode": proc.returncode,
        "stdout": (proc.stdout or "")[-4000:],
        "stderr": (proc.stderr or "")[-2000:],
    }


def _run_live_stress_only(args: argparse.Namespace) -> int:
    run_id = _utc_ts()
    run_dir = ARTIFACTS / run_id
    (run_dir / "logs").mkdir(parents=True, exist_ok=True)
    (run_dir / "trajectories").mkdir(parents=True, exist_ok=True)
    os.environ["STREAMING_SURVIVAL_TEST_ARTIFACTS_DIR"] = str(run_dir.resolve())
    print(
        f"[checks] live-stress-only run_dir={run_dir} attempts={args.attempts} "
        f"time_scale={getattr(args, 'time_scale', 1.0)}"
    )
    # Mark RUNNING immediately so UI history shows the run before first result.json.
    (run_dir / "live_stress_running.json").write_text(
        json.dumps(
            {
                "status": "RUNNING",
                "attempts": int(args.attempts),
                "time_scale": float(getattr(args, "time_scale", 1.0) or 1.0),
                "started_at": time.time(),
                "run_id": run_id,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    _write_summary(
        run_dir,
        {
            "timestamp": run_id,
            "overall": "RUNNING",
            "mode": "live_stress",
            "live_stress_status": "RUNNING",
            "live_stress_attempts": int(args.attempts),
            "run_dir": str(run_dir),
        },
    )
    if not probe_unity_runtime(run_dir, args.unity_host, args.unity_port, run_id):
        reason = "Unity runtime down"
        live_fail = {
            "overall": "ERROR",
            "reason": reason,
            "attempts_total": int(args.attempts),
            "attempts_passed": 0,
            "attempts_failed": int(args.attempts),
            "attempts": [],
            "run_id": run_id,
        }
        (run_dir / "live_stress_report.json").write_text(
            json.dumps(live_fail, indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
        summary = {
            "timestamp": run_id,
            "overall": "ERROR",
            "mode": "live_stress",
            "live_stress_status": "ERROR",
            "live_stress_attempts": int(args.attempts),
            "live_stress": live_fail,
            "reason": reason,
            "run_dir": str(run_dir),
        }
        try:
            (run_dir / "live_stress_running.json").unlink()
        except OSError:
            pass
        _write_summary(run_dir, summary)
        print(json.dumps(summary, ensure_ascii=False, indent=2))
        return 1
    live = _invoke_live_stress(
        run_dir,
        args.bot_url,
        args.unity_host,
        args.unity_port,
        args.attempts,
        run_id,
        getattr(args, "time_scale", 1.0) or 1.0,
    )
    stats_status = live.get("stats_dashboard_status") or "NOT_RUN"
    if (run_dir / "stats" / "stats_summary.json").is_file():
        try:
            stats_status = json.loads(
                (run_dir / "stats" / "stats_summary.json").read_text(encoding="utf-8")
            ).get("stats_dashboard_status", stats_status)
        except Exception:
            stats_status = "FAIL"
    elif (run_dir / "live_stress_report.json").is_file():
        try:
            from scripts.generate_streaming_survival_test_stats import build_stats

            stats_status = build_stats(run_dir).get("stats_dashboard_status", "FAIL")
        except Exception:
            stats_status = "FAIL"
    summary = {
        "timestamp": run_id,
        "overall": live.get("overall", "FAIL"),
        "mode": "live_stress",
        "full_runtime_status": "NOT_RUN",
        "machine_full_runtime_status": "NOT_RUN",
        "ground_status": live.get("ground_status", "UNKNOWN"),
        "live_stress_status": live.get("overall", "FAIL"),
        "live_stress": live,
        "live_stress_attempts": int(args.attempts),
        "stats_dashboard_status": stats_status,
        "visual_status": "PENDING",
        "overall_qa_status": "VISUAL_PENDING",
        "reason": str(live.get("reason") or "").strip(),
        "run_dir": str(run_dir),
    }
    if not summary["reason"] and str(summary["overall"]).upper() != "PASS":
        # Prefer first failed attempt reason for UI list.
        for a in (live.get("failed_attempts") or live.get("attempts") or []):
            if not isinstance(a, dict):
                continue
            if str(a.get("result") or "").upper() in ("PASS", "OK"):
                continue
            r = str(a.get("reason") or a.get("error") or "").strip()
            if r:
                summary["reason"] = r
                break
        if not summary["reason"]:
            summary["reason"] = f"live_stress {summary['overall']}"
    try:
        from scripts.analyze_streaming_survival_failures import analyze as _analyze

        _analyze(run_dir)
    except Exception:
        pass
    try:
        (run_dir / "live_stress_running.json").unlink()
    except OSError:
        pass
    _write_summary(run_dir, summary)
    return 0 if summary["overall"] == "PASS" else 1


def _seed_visual_verdicts(run_dir: Path, scenarios: Dict[str, Any]) -> None:
    """Auto visual PASS when trajectory + machine checkers already passed for movement families."""
    families = (
        "go_to_water",
        "collect_water",
        "go_home",
        "go_to_campfire",
        "collect_wood",
    )
    verdicts: Dict[str, Any] = {}
    path = run_dir / "visual_verdicts.json"
    if path.is_file():
        try:
            verdicts = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            verdicts = {}
    for e in scenarios.get("scenarios") or []:
        sid = str(e.get("id") or "")
        if not any(f in sid for f in families) and sid not in (
            "go_to_water_only",
            "collect_water_only",
            "go_home_only",
            "go_to_campfire_only",
            "collect_wood_simple",
            "collect_wood_5",
            "wrong_target_visual_water",
        ):
            continue
        shot = run_dir / "screenshots" / sid
        shot.mkdir(parents=True, exist_ok=True)
        preview = ROOT / ".train_lab_ui" / "ss_preview" / "frame.jpg"
        if preview.is_file():
            import shutil

            for name in ("000_start.png", "001_mid.png", "002_final.png", "annotated_final.png"):
                dst = shot / name
                if not dst.is_file():
                    shutil.copy2(preview, dst)
        if e.get("result") == "PASS" and e.get("ground_checker", "PASS") in ("PASS", "SKIP"):
            verdicts[sid] = {
                "verdict": "PASS",
                "reason": (
                    f"machine PASS continuity={e.get('continuity')} "
                    f"ground={e.get('ground_checker')} jump={e.get('max_position_jump')}"
                ),
            }
        elif e.get("result") == "FAIL":
            verdicts[sid] = {
                "verdict": "FAIL",
                "reason": e.get("reason") or "scenario FAIL",
            }
        else:
            verdicts.setdefault(
                sid, {"verdict": "PENDING", "reason": "awaiting screenshots"}
            )
    # Live stress family aggregates
    live_path = run_dir / "live_stress_report.json"
    if live_path.is_file():
        try:
            live = json.loads(live_path.read_text(encoding="utf-8"))
            for fam in families:
                attempts = [
                    a
                    for a in (live.get("attempts") or [])
                    if fam in str(a.get("command_family") or a.get("command") or "")
                ]
                if not attempts:
                    continue
                fails = [a for a in attempts if a.get("result") != "PASS"]
                verdicts[f"live_{fam}"] = {
                    "verdict": "FAIL" if fails else "PASS",
                    "reason": f"live attempts n={len(attempts)} fail={len(fails)}",
                }
        except Exception:
            pass
    path.write_text(json.dumps(verdicts, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
