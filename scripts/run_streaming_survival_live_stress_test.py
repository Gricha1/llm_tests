#!/usr/bin/env python3
"""
Live Streaming Survival stress test: real bot #join/#do + Unity trajectories.

Not ML-Agents / Training AI. Requires Streaming Survival runtime + stream_bot Local Debug.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import socket
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from stream_bot.ss_trajectory_checker import check_trajectory
from stream_bot.validator import plan_from_payload

ARTIFACTS = ROOT / "artifacts" / "streaming_survival" / "test_runs"
PREVIEW = ROOT / ".train_lab_ui" / "ss_preview" / "frame.jpg"

# 5 users × 8 commands = 40 attempts
# Prefer multi-step #do chains (parser + step-order + long trajectories).
DEFAULT_USERS = [f"stress_user_{i:02d}" for i in range(5)]
DEFAULT_COMMANDS = [
    "#join",
    "#do подойди к воде, затем подойди к костру, потом добудь 10 деревьев, потом добудь 5 воды",
    "#do иди к воде собери 5 штук а потом к дому а потом собери 5 дерева",
    "#do набери 2 воды и иди ставь костер",
    "#do подойди к дому, затем добудь 3 воды, потом иди к костру и добудь 5 деревьев",
    "#do иди к воде затем домой затем к костру потом ходи по кругу",
    "#do добудь 3 дерева затем добудь 2 воды потом иди домой",
    "#do подойди к костру, затем ходи по кругу, потом иди к воде и добудь 4 воды",
]

# Extra pool if attempts > 40
EXTRA_COMMANDS = [
    "#do иди домой затем подойди к воде и добудь 5 воды потом добудь 3 дерева",
    "#do подойди к воде затем подойди к костру потом добудь 8 деревьев и добудь 3 воды",
    "#do иди к костру, затем 5 раз добудь дерево затем иди к воде",
    "#do добудь 4 воды затем добудь 4 дерева потом иди домой и подойди к костру",
]


def _utc_ts() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S_%f")[:21]


def _post(url: str, body: dict, timeout: float = 60.0) -> Dict[str, Any]:
    data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def _get(url: str, timeout: float = 5.0) -> Dict[str, Any]:
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def _udp_send(payload: dict, host: str, port: int) -> bool:
    try:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.settimeout(2.0)
        sock.sendto(data, (host, port))
        sock.close()
        return True
    except OSError as e:
        print(f"[live_stress] UDP failed: {e}")
        return False


def _command_family(cmd: str) -> str:
    c = cmd.lower()
    if c.strip() == "#join":
        return "idle"
    if "ничего" in c or "не делай" in c:
        return "idle"
    # Multi-step connectors first (must beat single-keyword families).
    has_fire = "кост" in c  # костёр / костер / костру / …
    if (
        "затем" in c
        or "потом" in c
        or "после этого" in c
        or ("набери" in c and has_fire)
        or ("ставь" in c and has_fire)
        or ("build_campfire" in c)
        or (c.count(",") >= 2 and ("вод" in c or "дерев" in c or has_fire or "дом" in c))
    ):
        return "sequential"
    if "круг" in c:
        return "circle"
    if has_fire:
        return "go_to_campfire"
    if "5 раз" in c and "дерев" in c:
        return "collect_wood"
    if "дерев" in c:
        return "collect_wood"
    if "добудь вод" in c or "набери" in c:
        return "collect_water"
    if "иди к вод" in c or "подойди к вод" in c:
        return "go_to_water"
    if "домой" in c or "баз" in c or "к дому" in c:
        return "go_home"
    return "other"


def _plan_resource_needs(plan: List[Dict[str, Any]]) -> Dict[str, int]:
    need = {"water": 0, "wood": 0, "food": 0}
    for step in plan or []:
        if not isinstance(step, dict):
            continue
        act = str(step.get("action") or "").lower()
        try:
            cnt = max(1, int(step.get("count") or 1))
        except (TypeError, ValueError):
            cnt = 1
        if act == "collect_water":
            need["water"] += cnt
        elif act == "collect_wood":
            need["wood"] += cnt
        elif act == "collect_food":
            need["food"] += cnt
    return need


def _format_plan_actions(plan: List[Dict[str, Any]]) -> str:
    parts: List[str] = []
    for step in plan or []:
        if not isinstance(step, dict):
            continue
        act = str(step.get("action") or "").strip()
        if not act:
            continue
        try:
            cnt = int(step.get("count") or 1)
        except (TypeError, ValueError):
            cnt = 1
        parts.append(f"{act}x{cnt}" if cnt != 1 else act)
    return " -> ".join(parts)


def _wait_seconds(cmd: str, plan: Optional[List[Dict[str, Any]]] = None) -> float:
    fam = _command_family(cmd)
    base = {
        "idle": 8.0,
        "go_to_water": 50.0,
        "collect_water": 70.0,
        "go_home": 45.0,
        "collect_wood": 80.0,
        "go_to_campfire": 40.0,
        "circle": 22.0,
        "sequential": 120.0,
        "other": 35.0,
    }.get(fam, 35.0)
    if fam != "sequential":
        return base
    # Scale max wait by parsed plan cost (early-idle exit still short-circuits).
    if plan:
        need = _plan_resource_needs(plan)
        steps = max(1, len(plan))
        return min(480.0, 70.0 + steps * 35.0 + need["water"] * 22.0 + need["wood"] * 18.0)
    c = cmd.lower()
    nums = [int(x) for x in re.findall(r"\d+", c)]
    units = sum(min(n, 15) for n in nums) if nums else 6
    return min(480.0, 100.0 + units * 20.0)


def _scenario_for_family(
    fam: str,
    cmd: str,
    plan: Optional[List[Dict[str, Any]]] = None,
) -> Dict[str, Any]:
    wait = _wait_seconds(cmd, plan)
    sc: Dict[str, Any] = {"id": f"live_{fam}", "chat_command": cmd, "max_seconds": wait}
    if fam == "idle":
        sc["expected_motion_pattern"] = "idle"
        sc["expected_resource_delta"] = {"water": 0, "wood": 0, "food": 0}
    elif fam == "go_to_water":
        sc["expected_target_type"] = "water_source"
        sc["expected_resource_delta"] = {"water": 0}
    elif fam == "collect_water":
        sc["expected_target_type"] = "water_source"
        sc["expected_resource_delta"] = {"water": 1}
    elif fam == "go_home":
        sc["expected_target_type"] = "home_interaction_point"
    elif fam == "collect_wood":
        sc["expected_target_type"] = "tree"
        if "5" in cmd:
            sc["expected_resource_delta"] = {"wood": 5}
        else:
            sc["expected_resource_delta"] = {"wood": 1}
    elif fam == "go_to_campfire":
        sc["expected_target_type"] = "campfire_slot"
    elif fam == "circle":
        sc["expected_motion_pattern"] = "circle"
    elif fam == "sequential":
        need = _plan_resource_needs(plan or [])
        delta = {k: v for k, v in need.items() if v > 0}
        if delta:
            sc["expected_resource_delta"] = delta
        # Prefer last collect/go target for target checker (end of chain).
        for step in reversed(plan or []):
            act = str((step or {}).get("action") or "").lower()
            if act in ("collect_water", "go_to_water"):
                sc["expected_target_type"] = "water_source"
                break
            if act == "collect_wood":
                sc["expected_target_type"] = "tree"
                break
            if act in ("go_home",):
                sc["expected_target_type"] = "home_interaction_point"
                break
            if act in ("go_to_campfire", "build_campfire"):
                sc["expected_target_type"] = "campfire_slot"
                break
    return sc


def _extract_plan(resp: Dict[str, Any]) -> List[Dict[str, Any]]:
    payload = resp
    if isinstance(resp.get("parsed_json"), dict):
        payload = resp["parsed_json"]
    for cmd in resp.get("unity_commands") or []:
        if isinstance(cmd, dict) and cmd.get("type") == "streaming_survival_action":
            payload = cmd
            break
    return plan_from_payload(payload) if isinstance(payload, dict) else []


def _copy_shot(dst: Path) -> Optional[str]:
    """Grab a lab screen frame into dst.

    Prefer live DISPLAY=:1 capture (Unity SS window / root). Fall back to
    ffmpeg x11grab, then cached .train_lab_ui/ss_preview/frame.jpg.
    """
    dst.parent.mkdir(parents=True, exist_ok=True)
    # Real Unity frames are tens of KB; tiny jpegs are empty desktop / fail.
    min_bytes = 2500
    cmds = [
        # Prefer the Unity/SS window if present, else whole root.
        r"""DISPLAY=:1 bash -lc '
          wid=$(xwininfo -root -tree 2>/dev/null | grep -iE "forest_survival|stream_forest|Unity" | head -1 | grep -oE "0x[0-9a-f]+" | head -1 || true)
          if [ -n "$wid" ]; then
            import -window "$wid" -resize 960x -quality 70 jpeg:-
          else
            import -window root -resize 960x -quality 70 jpeg:-
          fi
        ' 2>/dev/null""",
        "DISPLAY=:1 import -window root -resize 960x -quality 70 jpeg:- 2>/dev/null",
        (
            "ffmpeg -loglevel error -y -f x11grab -video_size 1920x1080 -i :1.0 "
            "-frames:v 1 -vf scale=960:-1 -q:v 5 -f image2pipe -vcodec mjpeg - 2>/dev/null"
        ),
    ]
    try:
        import subprocess

        for cmd in cmds:
            try:
                proc = subprocess.run(
                    ["bash", "-lc", cmd],
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    timeout=12,
                )
            except Exception:
                continue
            data = proc.stdout or b""
            if proc.returncode == 0 and len(data) >= min_bytes and data[:2] == b"\xff\xd8":
                dst.write_bytes(data)
                return str(dst)
    except Exception as e:
        print(f"[live_stress] WARN shot capture failed: {e}", flush=True)
    if PREVIEW.is_file() and PREVIEW.stat().st_size >= min_bytes:
        shutil.copy2(PREVIEW, dst)
        return str(dst)
    print(f"[live_stress] WARN no screenshot for {dst.name} (DISPLAY=:1 empty?)", flush=True)
    return None


def _resource_delta(samples: List[Dict[str, Any]]) -> Dict[str, int]:
    if not samples:
        return {}
    first = samples[0]
    last = samples[-1]
    before = first.get("resource_before") or first.get("resource_after") or {}
    after = last.get("resource_after") or last.get("resource_before") or {}
    out = {}
    for k in ("water", "wood", "food", "heat", "stone"):
        try:
            out[k] = int(after.get(k, 0)) - int(before.get(k, 0))
        except (TypeError, ValueError):
            out[k] = 0
    return out


def build_attempts(n: int) -> List[Tuple[str, str]]:
    """Build (username, command) pairs.

    For small n (quick smoke): do NOT return only bare #join — that records a
    stationary trajectory when the user is already in-world from a prior run.
    Prefer real #do movement commands; #join is still sent as setup when needed.
    """
    n = max(1, int(n))
    do_cmds = [c for c in DEFAULT_COMMANDS if c.strip().lower() != "#join"]
    if n <= 8:
        # Quick / small runs: one user, sequence of real #do (join handled in run loop).
        pairs: List[Tuple[str, str]] = []
        user = DEFAULT_USERS[0]
        for cmd in do_cmds:
            pairs.append((user, cmd))
            if len(pairs) >= n:
                return pairs
        return pairs[:n]

    cmds = list(DEFAULT_COMMANDS)
    while len(cmds) * len(DEFAULT_USERS) < n:
        cmds.extend(EXTRA_COMMANDS)
    pairs = []
    for user in DEFAULT_USERS:
        for cmd in cmds:
            pairs.append((user, cmd))
            if len(pairs) >= n:
                return pairs
    return pairs[:n]


def run_stress(
    run_dir: Path,
    *,
    bot_url: str,
    unity_host: str,
    unity_port: int,
    attempts: int,
    run_id: str,
) -> Dict[str, Any]:
    run_dir.mkdir(parents=True, exist_ok=True)
    (run_dir / "trajectories").mkdir(parents=True, exist_ok=True)
    (run_dir / "screenshots").mkdir(parents=True, exist_ok=True)
    os.environ["STREAMING_SURVIVAL_TEST_ARTIFACTS_DIR"] = str(run_dir.resolve())

    try:
        _get(bot_url.rstrip("/") + "/status")
    except Exception as e:
        return {"overall": "FAIL", "reason": f"bot unreachable: {e}", "attempts_total": 0}

    try:
        _post(bot_url.rstrip("/") + "/mode", {"listen_stream": False})
    except Exception:
        pass

    # Fresh empty world: nobody from prior runs may remain in bot or Unity.
    try:
        cleared = _post(bot_url.rstrip("/") + "/session/reset", {})
        print(
            f"[live_stress] session/reset bot cleared={cleared.get('cleared')} "
            f"active={cleared.get('active_users')}",
            flush=True,
        )
    except Exception as e:
        print(f"[live_stress] WARN session/reset failed: {e}", flush=True)
    _udp_send(
        {
            "type": "streaming_survival_test",
            "cmd": "clear_all_players",
            "run_id": run_id,
            "output_dir": str(run_dir.resolve()),
        },
        unity_host,
        unity_port,
    )
    time.sleep(0.6)

    # Ensure Unity writes into this run_dir + world_map.json (landmarks/fences)
    _udp_send(
        {
            "type": "streaming_survival_test",
            "cmd": "ping",
            "run_id": run_id,
            "output_dir": str(run_dir.resolve()),
        },
        unity_host,
        unity_port,
    )
    time.sleep(0.3)
    _udp_send(
        {
            "type": "streaming_survival_test",
            "cmd": "dump_world_map",
            "run_id": run_id,
            "output_dir": str(run_dir.resolve()),
        },
        unity_host,
        unity_port,
    )
    time.sleep(0.5)

    pairs = build_attempts(attempts)
    results: List[Dict[str, Any]] = []
    ground_failures = 0
    teleport_failures = 0
    stuck_failures = 0
    parser_failures = 0
    resource_guard_failures = 0
    visual_failures = 0
    max_jump = 0.0
    max_speed = 0.0
    min_ground = None
    t0 = time.time()

    for i, (user, cmd) in enumerate(pairs):
        attempt_id = f"live_{i:02d}_{user}"
        fam = _command_family(cmd)
        # Provisional wait; refined after parse for sequential chains.
        wait_s = _wait_seconds(cmd)
        shot_dir = run_dir / "screenshots" / attempt_id
        shot_dir.mkdir(parents=True, exist_ok=True)
        traj_path = run_dir / "trajectories" / f"{attempt_id}_trajectory.jsonl"
        if traj_path.is_file():
            try:
                traj_path.unlink()
            except OSError:
                pass

        entry: Dict[str, Any] = {
            "attempt": i,
            "attempt_id": attempt_id,
            "username": user,
            "command": cmd,
            "command_family": fam,
            "result": "FAIL",
        }
        print(f"[live_stress] {i+1}/{len(pairs)} {user} {cmd!r} wait~={wait_s:.0f}s", flush=True)

        # World starts empty; character appears only via #join (at spawn).
        # If already joined from an earlier attempt of this run, re-#join + spawn reset.
        is_join = cmd.strip().lower() == "#join"
        if not is_join:
            try:
                _post(
                    bot_url.rstrip("/") + "/local_chat",
                    {"username": user, "message": "#join"},
                )
            except Exception:
                pass
            time.sleep(0.4)
            _udp_send(
                {
                    "type": "streaming_survival_test",
                    "cmd": "reset_player_to_spawn",
                    "username": user,
                    "run_id": run_id,
                    "output_dir": str(run_dir.resolve()),
                },
                unity_host,
                unity_port,
            )
            time.sleep(0.35)

        if is_join:
            try:
                resp = _post(
                    bot_url.rstrip("/") + "/local_chat",
                    {"username": user, "message": cmd},
                )
            except Exception as e:
                entry["reason"] = f"local_chat failed: {e}"
                results.append(entry)
                continue
            time.sleep(0.5)

        _udp_send(
            {
                "type": "streaming_survival_test",
                "cmd": "begin_live_trajectory",
                "run_id": run_id,
                "output_dir": str(run_dir.resolve()),
                "attempt_id": attempt_id,
                "username": user,
            },
            unity_host,
            unity_port,
        )
        time.sleep(0.3)
        _copy_shot(shot_dir / "000_start.png")

        if not is_join:
            try:
                resp = _post(
                    bot_url.rstrip("/") + "/local_chat",
                    {"username": user, "message": cmd},
                )
            except Exception as e:
                entry["reason"] = f"local_chat failed: {e}"
                results.append(entry)
                continue

        plan = _extract_plan(resp)
        wait_s = _wait_seconds(cmd, plan)
        entry["parsed_plan"] = plan
        entry["chat_reply"] = resp.get("chat_reply")
        entry["validator_result"] = resp.get("validator_result")
        entry["parsed_json"] = resp.get("parsed_json")
        entry["events"] = resp.get("events")
        # parser_mode: heuristic→deterministic, llm, fallback
        mode = "deterministic"
        fb = False
        fb_reason = ""
        vr = resp.get("validator_result") or {}
        if isinstance(vr, dict) and vr.get("fallback"):
            mode, fb, fb_reason = "fallback", True, str(vr.get("reason") or "")
        else:
            for ev in resp.get("events") or []:
                if isinstance(ev, dict) and ev.get("source") in ("llm", "fallback", "heuristic"):
                    src = ev.get("source")
                    mode = "llm" if src == "llm" else ("fallback" if src == "fallback" else "deterministic")
                    fb = src == "fallback"
                    break
            # local_chat may not echo source; infer from payload presence
            if resp.get("parsed_json") is None and cmd.startswith("#do"):
                mode, fb, fb_reason = "fallback", True, "no_parsed_json"
        entry["parser_mode"] = mode
        entry["fallback_used"] = fb
        entry["fallback_reason"] = fb_reason
        entry["expected_actions"] = (
            _format_plan_actions(plan) if plan else _plan_actions_expected(fam, cmd)
        )
        if fam == "sequential" and len(plan) < 2 and cmd.startswith("#do"):
            # Complex stress cmds must parse into multi-step plans, not idle-fallback.
            entry["reason"] = f"expected multi-step plan, got {plan!r}"
            parser_failures += 1
            results.append(entry)
            time.sleep(10.5)
            continue
        print(f"[live_stress]   plan={entry['expected_actions']!r} wait={wait_s:.0f}s", flush=True)
        mid_at = time.time() + max(2.0, wait_s * 0.45)
        end_at = time.time() + wait_s
        mid_taken = False
        min_hold = time.time() + (3.0 if is_join else 12.0)
        need = _plan_resource_needs(plan)
        while time.time() < end_at:
            if not mid_taken and time.time() >= mid_at:
                _copy_shot(shot_dir / "001_mid.png")
                mid_taken = True
            # Early finish when character returns to idle after doing work
            if time.time() >= min_hold and traj_path.is_file() and traj_path.stat().st_size > 50:
                try:
                    lines = traj_path.read_text(encoding="utf-8-sig").strip().splitlines()
                    if lines:
                        last = json.loads(lines[-1])
                        act = str(last.get("action") or last.get("current_action") or "").lower()
                        phase = str(last.get("current_phase") or "").lower()
                        evt = str(last.get("event") or "").lower()
                        ra = last.get("resource_after") or {}
                        rb = (json.loads(lines[0]).get("resource_before") or {})
                        water_ok = int(ra.get("water", 0)) - int(rb.get("water", 0)) >= need["water"]
                        wood_ok = int(ra.get("wood", 0)) - int(rb.get("wood", 0)) >= need["wood"]
                        if fam == "sequential":
                            if water_ok and wood_ok and act == "idle" and (
                                "idle" in phase or evt in ("plan_completed", "work_finished", "")
                            ):
                                break
                        elif not is_join and act == "idle" and "idle" in phase:
                            break
                        if fam == "collect_water":
                            if int(ra.get("water", 0)) > int(rb.get("water", 0)) and act == "idle":
                                break
                        if fam == "collect_wood":
                            need_wood = 5 if "5" in cmd else 1
                            if int(ra.get("wood", 0)) - int(rb.get("wood", 0)) >= need_wood:
                                if act == "idle":
                                    break
                except Exception:
                    pass
            time.sleep(0.5)
        if not mid_taken:
            _copy_shot(shot_dir / "001_mid.png")
        _copy_shot(shot_dir / "002_final.png")
        _copy_shot(shot_dir / "annotated_final.png")

        _udp_send(
            {
                "type": "streaming_survival_test",
                "cmd": "end_live_trajectory",
                "run_id": run_id,
                "output_dir": str(run_dir.resolve()),
                "attempt_id": attempt_id,
                "username": user,
                "event": "live_end",
            },
            unity_host,
            unity_port,
        )
        # Wait for trajectory flush
        deadline = time.time() + 8.0
        while time.time() < deadline and (
            not traj_path.is_file() or traj_path.stat().st_size < 10
        ):
            time.sleep(0.2)

        if not traj_path.is_file() or traj_path.stat().st_size < 10:
            entry["reason"] = "missing trajectory"
            results.append(entry)
            continue

        samples = []
        for line in traj_path.read_text(encoding="utf-8-sig").splitlines():
            if not line.strip():
                continue
            try:
                samples.append(json.loads(line))
            except json.JSONDecodeError:
                continue

        sc = _scenario_for_family(fam, cmd, plan)
        # Join must stay idle — no collect actions
        if cmd.strip() == "#join":
            sc = {
                "id": "join_default_idle",
                "expected_motion_pattern": "idle",
                "expected_resource_delta": {"water": 0, "wood": 0, "food": 0},
                "max_seconds": wait_s,
            }
            bad = any(
                str(s.get("action") or s.get("current_action") or "").lower()
                in ("collect_wood", "collect_water")
                for s in samples
            )
            if bad:
                entry["reason"] = "join started collect without #do"
                entry["ground_checker"] = "SKIP"
                parser_failures += 1
                results.append(entry)
                continue

        cr = check_trajectory(sc, traj_path)
        entry["command_checker"] = "PASS" if cr.ok else "FAIL"
        entry["trajectory_checker"] = "PASS" if cr.trajectory_pass else "FAIL"
        entry["ground_checker"] = "PASS" if cr.ground_pass else "FAIL"
        entry["continuity"] = "PASS" if cr.continuity_pass else "FAIL"
        entry["max_position_jump"] = round(float(cr.max_position_jump), 3)
        entry["max_speed"] = round(float(cr.max_speed), 3)
        entry["min_ground_clearance"] = round(float(cr.min_ground_clearance), 3)
        entry["max_ground_clearance"] = round(float(cr.max_ground_clearance), 3)
        entry["illegal_teleports"] = list(cr.illegal_teleports)
        entry["resource_delta"] = _resource_delta(samples)
        entry["trajectory"] = str(traj_path.relative_to(run_dir)).replace("\\", "/")
        entry["screenshot"] = str((shot_dir / "annotated_final.png").relative_to(run_dir)).replace(
            "\\", "/"
        )
        if samples:
            last = samples[-1]
            entry["final_state"] = {
                "action": last.get("action") or last.get("current_action"),
                "phase": last.get("current_phase") or last.get("phase"),
                "pos": last.get("pos"),
                "target_type": last.get("target_type"),
            }

        max_jump = max(max_jump, float(cr.max_position_jump or 0))
        max_speed = max(max_speed, float(cr.max_speed or 0))
        if cr.min_ground_clearance is not None:
            gv = float(cr.min_ground_clearance)
            min_ground = gv if min_ground is None else min(min_ground, gv)

        if not cr.ground_pass:
            ground_failures += 1
        if cr.illegal_teleports or not cr.continuity_pass:
            teleport_failures += 1
        if "stuck" in (cr.reason or "").lower():
            stuck_failures += 1
        if "guard" in (cr.reason or "").lower() or not cr.resource_pass:
            if "guard" in (cr.reason or "").lower():
                resource_guard_failures += 1

        # Movement / multi-step families need screenshots
        if fam in (
            "go_to_water",
            "collect_water",
            "go_home",
            "go_to_campfire",
            "collect_wood",
            "sequential",
        ):
            if not (shot_dir / "annotated_final.png").is_file():
                visual_failures += 1
                entry["visual_checker"] = "FAIL"
            else:
                entry["visual_checker"] = "PASS"
        else:
            entry["visual_checker"] = "SKIP"

        if cr.ok and cr.ground_pass and cr.continuity_pass and not cr.illegal_teleports:
            entry["result"] = "PASS"
            entry["reason"] = ""
        else:
            entry["result"] = "FAIL"
            entry["reason"] = cr.reason or "checker FAIL"
            if not plan and cmd.startswith("#do"):
                parser_failures += 1

        # Cooldown between #do for same user (bot enforces 10s)
        time.sleep(10.5 if cmd.startswith("#do") else 0.8)
        results.append(entry)

    passed = sum(1 for r in results if r.get("result") == "PASS")
    failed = [r for r in results if r.get("result") != "PASS"]
    report = {
        "overall": "PASS" if passed == len(results) and len(results) >= attempts else "FAIL",
        "attempts_total": len(results),
        "attempts_passed": passed,
        "attempts_failed": len(failed),
        "duration_sec": round(time.time() - t0, 1),
        "ground_failures": ground_failures,
        "teleport_failures": teleport_failures,
        "stuck_failures": stuck_failures,
        "parser_failures": parser_failures,
        "resource_guard_failures": resource_guard_failures,
        "visual_failures": visual_failures,
        "max_position_jump": round(max_jump, 3),
        "max_speed": round(max_speed, 3),
        "min_ground_clearance": round(min_ground, 3) if min_ground is not None else None,
        "ground_status": "FAIL" if ground_failures else "PASS",
        "failed_attempts": failed,
        "attempts": results,
        "run_dir": str(run_dir),
    }
    (run_dir / "live_stress_report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    _write_md(run_dir, report)
    try:
        from scripts.generate_streaming_survival_test_stats import build_stats

        stats = build_stats(run_dir)
        report["stats_dashboard_status"] = stats.get("stats_dashboard_status")
        report["stats_summary"] = {
            k: stats.get(k)
            for k in (
                "attempts_total",
                "parser_failures",
                "trajectory_failures",
                "ground_failures",
                "target_failures",
                "resource_guard_failures",
                "visual_failures",
                "max_position_jump",
                "mean_position_jump",
                "max_speed",
                "min_ground_clearance",
                "stats_dashboard_status",
            )
        }
        (run_dir / "live_stress_report.json").write_text(
            json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        print(f"[live_stress] stats_dashboard={run_dir / 'stats' / 'stats_dashboard.html'}")
    except Exception as e:
        print(f"[live_stress] stats generation failed: {e}")
        report["stats_dashboard_status"] = "FAIL"
    print(json.dumps({k: report[k] for k in (
        "overall", "attempts_total", "attempts_passed", "attempts_failed",
        "ground_failures", "teleport_failures", "min_ground_clearance",
        "max_position_jump", "max_speed",
    )}, indent=2))
    # Leave the world empty after the run (no leftover stress users).
    try:
        _post(bot_url.rstrip("/") + "/session/reset", {})
    except Exception:
        pass
    _udp_send(
        {
            "type": "streaming_survival_test",
            "cmd": "clear_all_players",
            "run_id": run_id,
            "output_dir": str(run_dir.resolve()),
        },
        unity_host,
        unity_port,
    )
    return report


def _plan_actions_expected(fam: str, cmd: str) -> str:
    c = cmd.lower()
    if fam == "idle" or c.strip() == "#join" or "ничего" in c:
        return "idle"
    if fam == "go_to_water":
        return "go_to_water"
    if fam == "collect_water":
        return "collect_water"
    if fam == "go_home":
        return "go_home"
    if fam == "collect_wood":
        return "collect_woodx5" if "5" in cmd else "collect_wood"
    if fam == "go_to_campfire":
        return "go_to_campfire"
    if fam == "circle":
        return "walk_circle"
    if fam == "sequential":
        if "10 дерев" in c and "5 вод" in c:
            return "go_to_water -> go_to_campfire -> collect_woodx10 -> collect_waterx5"
        if "костр" in c and "дерев" in c and "вод" in c:
            return "go_to_campfire -> collect_wood -> go_to_water"
        if "вод" in c and "костр" in c:
            return "collect_water -> go_to_campfire/build_campfire"
        return "sequential_multi_step"
    return fam


def _write_md(run_dir: Path, report: Dict[str, Any]) -> None:
    lines = [
        "# Live Stress Report",
        "",
        f"- overall: **{report.get('overall')}**",
        f"- attempts_total: {report.get('attempts_total')}",
        f"- attempts_passed: {report.get('attempts_passed')}",
        f"- attempts_failed: {report.get('attempts_failed')}",
        f"- ground_failures: {report.get('ground_failures')}",
        f"- teleport_failures: {report.get('teleport_failures')}",
        f"- min_ground_clearance: {report.get('min_ground_clearance')}",
        f"- max_position_jump: {report.get('max_position_jump')}",
        f"- max_speed: {report.get('max_speed')}",
        "",
        "| # | user | command | plan | result | jump | min_g | shot |",
        "|---|------|---------|------|--------|------|-------|------|",
    ]
    for a in report.get("attempts") or []:
        plan = a.get("parsed_plan") or []
        plan_s = ",".join(
            f"{p.get('action')}:{p.get('count', 1)}" for p in plan if isinstance(p, dict)
        )[:40]
        lines.append(
            f"| {a.get('attempt')} | {a.get('username')} | `{a.get('command')}` | "
            f"{plan_s} | {a.get('result')} | {a.get('max_position_jump')} | "
            f"{a.get('min_ground_clearance')} | {a.get('screenshot', '')} |"
        )
    (run_dir / "live_stress_report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--bot-url", default=os.environ.get("STREAM_BOT_URL", "http://127.0.0.1:8765"))
    ap.add_argument("--unity-host", default=os.environ.get("FOREST_UNITY_HOST", "127.0.0.1"))
    ap.add_argument(
        "--unity-port",
        type=int,
        default=int(os.environ.get("FOREST_STREAM_BOT_PORT", "5055")),
    )
    ap.add_argument("--attempts", type=int, default=40)
    ap.add_argument("--run-dir", default="")
    ap.add_argument("--run-id", default="")
    args = ap.parse_args()
    run_id = args.run_id or _utc_ts()
    run_dir = Path(args.run_dir) if args.run_dir else ARTIFACTS / run_id
    report = run_stress(
        run_dir,
        bot_url=args.bot_url,
        unity_host=args.unity_host,
        unity_port=args.unity_port,
        attempts=args.attempts,
        run_id=run_id,
    )
    return 0 if report.get("overall") == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
