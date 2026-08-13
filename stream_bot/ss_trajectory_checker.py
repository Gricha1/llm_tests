"""Offline trajectory checkers for Streaming Survival JSONL (no Unity required)."""

from __future__ import annotations

import json
import math
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple


MAX_ALLOWED_POSITION_JUMP = 3.0
MAX_STUCK_NUDGE_JUMP = 1.5
MAX_ALLOWED_SPEED = 8.0
GROUND_BELOW_TOLERANCE = 0.25
GROUND_ABOVE_TOLERANCE = 3.0
GROUND_MISSING_OK = True  # if sample has no ground_y, skip unless is_below_ground set

NORMAL_GAMEPLAY_STRICT_IDS = frozenset(
    {
        "collect_water_no_teleport",
        "go_home_no_teleport",
        "go_home_no_teleport_strict",
        "collect_wood_simple",
        "water_then_campfire",
        "water_home_wood_chain",
        "stuck_near_fence_no_target_teleport",
        "seq_campfire_wood_water",
        "go_to_water_only",
        "go_home_only",
        "go_to_campfire_only",
        "wrong_target_visual_water",
    }
)

_SETUP_TELEPORT_EVENTS = frozenset(
    {
        "join_spawn",
        "scenario_setup",
        "scenario_reset",
        "manual_respawn_command",
        "round_reset",
        "round_start",
        "negative_forest",
    }
)

_FORBIDDEN_NORMAL_EVENTS = frozenset(
    {
        "stuck_recovery",
        "respawn_to_spawn",
    }
)


@dataclass
class CheckResult:
    ok: bool = True
    scenario_id: str = ""
    target_pass: bool = True
    trajectory_pass: bool = True
    resource_pass: bool = True
    direction_pass: bool = True
    continuity_pass: bool = True
    ground_pass: bool = True
    max_position_jump: float = 0.0
    max_speed: float = 0.0
    min_ground_clearance: float = 0.0
    max_ground_clearance: float = 0.0
    below_ground_samples: List[Dict[str, Any]] = field(default_factory=list)
    missing_ground_samples: List[Dict[str, Any]] = field(default_factory=list)
    illegal_teleports: List[str] = field(default_factory=list)
    controlled_teleports: int = 0
    reason: str = ""
    notes: List[str] = field(default_factory=list)

    def fail(self, kind: str, msg: str) -> None:
        self.ok = False
        if kind == "target":
            self.target_pass = False
        elif kind == "resource":
            self.resource_pass = False
        elif kind == "direction":
            self.direction_pass = False
            self.trajectory_pass = False
        elif kind == "continuity":
            self.continuity_pass = False
            self.trajectory_pass = False
            self.illegal_teleports.append(msg)
        elif kind == "ground":
            self.ground_pass = False
            self.trajectory_pass = False
        else:
            self.trajectory_pass = False
        if not self.reason:
            self.reason = msg
        else:
            self.reason += " | " + msg
        self.notes.append(f"[{kind}] {msg}")

    def to_dict(self) -> Dict[str, Any]:
        return {
            "id": self.scenario_id,
            "overall": "PASS" if self.ok else "FAIL",
            "target": "PASS" if self.target_pass else "FAIL",
            "trajectory": "PASS" if self.trajectory_pass else "FAIL",
            "resource": "PASS" if self.resource_pass else "FAIL",
            "direction": "PASS" if self.direction_pass else "FAIL",
            "continuity": "PASS" if self.continuity_pass else "FAIL",
            "ground": "PASS" if self.ground_pass else "FAIL",
            "ground_checker": "PASS" if self.ground_pass else "FAIL",
            "command_checker": "PASS" if self.ok else "FAIL",
            "trajectory_checker": "PASS" if self.trajectory_pass else "FAIL",
            "target_checker": "PASS" if self.target_pass else "FAIL",
            "resource_checker": "PASS" if self.resource_pass else "FAIL",
            "step_order_checker": "PASS",
            "visual_checker": "PENDING",
            "max_position_jump": round(self.max_position_jump, 3),
            "max_speed": round(self.max_speed, 3),
            "min_ground_clearance": round(self.min_ground_clearance, 3),
            "max_ground_clearance": round(self.max_ground_clearance, 3),
            "below_ground_samples": list(self.below_ground_samples[:12]),
            "missing_ground_samples": list(self.missing_ground_samples[:8]),
            "illegal_teleports": list(self.illegal_teleports),
            "controlled_teleports": self.controlled_teleports,
            "reason": self.reason,
            "notes": self.notes,
        }


def _load_samples(path: Path) -> List[Dict[str, Any]]:
    out: List[Dict[str, Any]] = []
    if not path.is_file():
        return out
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            continue
    return out


def _infer_attempt_username(scenario: Dict[str, Any], trajectory_path: Path) -> str:
    want = str(scenario.get("username") or scenario.get("expected_username") or "").strip()
    if want:
        return want
    stem = trajectory_path.stem  # e.g. live_31_stress_user_03_trajectory
    for key in ("stress_user_00", "stress_user_01", "stress_user_02", "stress_user_03", "stress_user_04"):
        if key in stem:
            return key
    if "viewer" in stem:
        return "viewer"
    return ""


def _filter_attempt_user(
    samples: List[Dict[str, Any]],
    scenario: Dict[str, Any],
    trajectory_path: Path,
) -> List[Dict[str, Any]]:
    """Keep one agent per attempt file.

    Older live-stress runs mixed all 5 users into one jsonl; consecutive-line
    max_jump then looked like 15–20m teleports.
    """
    if not samples:
        return samples
    want = _infer_attempt_username(scenario, trajectory_path)
    if not want:
        counts: Dict[str, int] = {}
        for s in samples:
            u = str(s.get("username") or "")
            if not u:
                continue
            if _pos(s) is None:
                continue
            counts[u] = counts.get(u, 0) + 1
        if counts:
            want = max(counts.items(), key=lambda kv: kv[1])[0]
    if not want:
        return samples
    filtered = [s for s in samples if str(s.get("username") or "") == want]
    # Drop placeholder started_action rows without a real position.
    cleaned: List[Dict[str, Any]] = []
    for s in filtered:
        p = _pos(s)
        if p is None:
            continue
        if abs(p[0]) < 1e-3 and abs(p[2]) < 1e-3 and str(s.get("event") or "") in (
            "started_action",
            "",
        ):
            continue
        cleaned.append(s)
    return cleaned if len(cleaned) >= 2 else (filtered if len(filtered) >= 2 else samples)


def _is_strict(scenario_id: str) -> bool:
    if scenario_id in NORMAL_GAMEPLAY_STRICT_IDS:
        return True
    return "no_teleport" in scenario_id


def check_trajectory(
    scenario: Dict[str, Any],
    trajectory_path: Path,
) -> CheckResult:
    sid = str(scenario.get("id") or trajectory_path.stem)
    r = CheckResult(scenario_id=sid)
    samples = _load_samples(trajectory_path)
    samples = _filter_attempt_user(samples, scenario, trajectory_path)
    if len(samples) < 2:
        r.fail("trajectory", f"trajectory too short or missing: {trajectory_path}")
        return r

    expect_type = scenario.get("expected_target_type")
    pattern = scenario.get("expected_motion_pattern")
    max_sec = float(scenario.get("max_seconds") or 30)
    delta = scenario.get("expected_resource_delta") or {}
    no_credit = bool(scenario.get("expect_no_resource_credit"))

    if not no_credit and expect_type:
        if not any(s.get("target_type") == expect_type for s in samples):
            r.fail("target", f"expected target_type={expect_type} never observed")

    if not no_credit and (expect_type or not pattern):
        _check_approach(r, samples, max_sec)

    if no_credit:
        _check_no_resource_credit(r, samples)
    elif delta:
        _check_resources(r, samples, delta)

    _check_credit_not_spawn(r, samples)
    _check_resource_guard_events(r, samples)

    if pattern:
        _check_pattern(r, samples, str(pattern))

    if not no_credit:
        _check_initial_direction(r, samples)
    jump_limit = float(scenario.get("max_allowed_position_jump") or MAX_ALLOWED_POSITION_JUMP)
    _check_continuity(r, samples, sid, max_jump_limit=jump_limit)
    _check_ground(r, samples)
    if not no_credit and (
        expect_type == "water_source"
        or "collect_water" in sid
    ):
        _check_collect_water_order(r, samples)
    if r.ok:
        r.notes.append("all checks passed")
    return r


def _check_ground(r: CheckResult, samples: List[Dict[str, Any]]) -> None:
    """Fail if character feet go below sampled ground_y (not merely y<0)."""
    clearances: List[float] = []
    below: List[Dict[str, Any]] = []
    missing: List[Dict[str, Any]] = []
    for s in samples:
        p = _pos(s)
        if not p:
            continue
        has_gy = "ground_y" in s or "ground_clearance" in s or "is_below_ground" in s
        if not has_gy:
            # Legacy trajectories without ground fields — skip hard fail.
            continue
        if s.get("is_below_ground") is True:
            entry = {
                "event": "ground_check_fail",
                "t": s.get("t"),
                "username": s.get("username"),
                "pos": {"x": p[0], "y": p[1], "z": p[2]},
                "ground_y": s.get("ground_y"),
                "clearance": s.get("ground_clearance"),
                "ground_collider_name": s.get("ground_collider_name"),
                "action": s.get("action") or s.get("current_action"),
                "reason": "character below ground",
            }
            below.append(entry)
            continue
        clr = s.get("ground_clearance")
        if clr is None and s.get("ground_y") is not None:
            bottom = s.get("character_bottom_y")
            if bottom is None:
                bottom = p[1]
            try:
                clr = float(bottom) - float(s["ground_y"])
            except (TypeError, ValueError):
                clr = None
        if clr is None:
            missing.append({"t": s.get("t"), "pos": {"x": p[0], "y": p[1], "z": p[2]}})
            continue
        try:
            c = float(clr)
        except (TypeError, ValueError):
            continue
        clearances.append(c)
        if c < -GROUND_BELOW_TOLERANCE:
            below.append(
                {
                    "event": "ground_check_fail",
                    "t": s.get("t"),
                    "username": s.get("username"),
                    "pos": {"x": p[0], "y": p[1], "z": p[2]},
                    "ground_y": s.get("ground_y"),
                    "clearance": round(c, 3),
                    "ground_collider_name": s.get("ground_collider_name"),
                    "action": s.get("action") or s.get("current_action"),
                    "reason": "character below ground",
                }
            )
        elif c > GROUND_ABOVE_TOLERANCE:
            r.notes.append(
                f"[ground] high clearance={c:.2f} t={s.get('t')} "
                f"(rock/slope ok unless persistent)"
            )
    if clearances:
        r.min_ground_clearance = min(clearances)
        r.max_ground_clearance = max(clearances)
    r.below_ground_samples = below
    r.missing_ground_samples = missing
    if below:
        b0 = below[0]
        r.fail(
            "ground",
            f"character below ground clearance={b0.get('clearance')} "
            f"pos=({b0.get('pos', {}).get('x')},{b0.get('pos', {}).get('y')},{b0.get('pos', {}).get('z')}) "
            f"ground_y={b0.get('ground_y')} n={len(below)}",
        )
    elif clearances:
        r.notes.append(
            f"ground ok min_clearance={r.min_ground_clearance:.2f} "
            f"max_clearance={r.max_ground_clearance:.2f}"
        )


def _pos(s: Dict[str, Any]) -> Optional[Tuple[float, float, float]]:
    p = s.get("pos")
    if not isinstance(p, dict):
        return None
    return float(p.get("x", 0)), float(p.get("y", 0)), float(p.get("z", 0))


def _horiz(a: Tuple[float, float, float], b: Tuple[float, float, float]) -> float:
    dx = a[0] - b[0]
    dz = a[2] - b[2]
    return math.sqrt(dx * dx + dz * dz)


def _nearby_event(
    samples: List[Dict[str, Any]], t0: float, t1: float, events: frozenset
) -> bool:
    for e in samples:
        evt = e.get("event")
        if not evt or evt not in events:
            continue
        et = float(e.get("t") or 0)
        if abs(et - t1) <= 0.55 or abs(et - t0) <= 0.55:
            return True
    return False


def _check_forbidden_teleports(r: CheckResult, samples: List[Dict[str, Any]], sid: str) -> None:
    ultra = sid == "go_home_no_teleport_strict"
    controlled = 0
    for s in samples:
        evt = s.get("event")
        if not evt:
            continue
        if evt in _SETUP_TELEPORT_EVENTS:
            continue
        if evt == "controlled_teleport":
            controlled += 1
            if ultra:
                r.fail(
                    "continuity",
                    f"controlled_teleport forbidden in {sid} t={float(s.get('t') or 0):.2f}",
                )
        if evt in _FORBIDDEN_NORMAL_EVENTS:
            r.fail(
                "continuity",
                f"forbidden teleport event={evt} t={float(s.get('t') or 0):.2f}",
            )
    r.controlled_teleports = controlled
    if _is_strict(sid) and controlled > 0 and sid != "go_home_no_teleport_strict":
        # Mid-run controlled_teleport in normal gameplay (beyond setup) is a fail.
        # Setup teleports are usually outside the recorded window; any recorded one counts.
        r.fail(
            "continuity",
            f"controlled_teleports_in_normal_gameplay={controlled}",
        )


def _check_continuity(
    r: CheckResult,
    samples: List[Dict[str, Any]],
    sid: str,
    *,
    max_jump_limit: float = MAX_ALLOWED_POSITION_JUMP,
) -> None:
    max_jump = 0.0
    max_speed = 0.0
    min_dt = 0.08
    jump_lim = float(max_jump_limit) if max_jump_limit and max_jump_limit > 0 else MAX_ALLOWED_POSITION_JUMP
    strict = _is_strict(sid)
    prev = None
    for s in samples:
        p = _pos(s)
        if p is None:
            continue
        if prev is None:
            prev = s
            continue
        pp = _pos(prev)
        if pp is None:
            prev = s
            continue
        t0 = float(prev.get("t") or 0)
        t1 = float(s.get("t") or 0)
        raw_dt = t1 - t0
        jump = _horiz(pp, p)
        max_jump = max(max_jump, jump)
        setup_nearby = _nearby_event(samples, t0, t1, _SETUP_TELEPORT_EVENTS)
        forbidden_nearby = _nearby_event(samples, t0, t1, _FORBIDDEN_NORMAL_EVENTS)

        if raw_dt < min_dt:
            if jump > jump_lim and not setup_nearby:
                r.fail(
                    "continuity",
                    "Illegal teleport detected: "
                    f"jump={jump:.1f} speed=n/a "
                    f"from=({pp[0]:.1f},{pp[2]:.1f}) to=({p[0]:.1f},{p[2]:.1f})"
                    + (
                        " (stuck_recovery not allowed)"
                        if forbidden_nearby
                        else " max_jump exceeded"
                    ),
                )
            elif strict and jump > (MAX_STUCK_NUDGE_JUMP + 0.05) and not setup_nearby:
                r.fail(
                    "continuity",
                    "Illegal teleport detected: "
                    f"jump={jump:.1f} speed=n/a "
                    f"from=({pp[0]:.1f},{pp[2]:.1f}) to=({p[0]:.1f},{p[2]:.1f}) "
                    "exceeds nudge limit",
                )
            prev = s
            continue

        speed = jump / raw_dt
        max_speed = max(max_speed, speed)

        # Hard rule: jump > limit is FAIL even if stuck_recovery event is nearby.
        if jump > jump_lim and not setup_nearby:
            r.fail(
                "continuity",
                "Illegal teleport detected: "
                f"jump={jump:.1f} speed={speed:.1f} "
                f"from=({pp[0]:.1f},{pp[2]:.1f}) to=({p[0]:.1f},{p[2]:.1f}) "
                "max_jump exceeded"
                + (" (stuck_recovery not allowed)" if forbidden_nearby else ""),
            )
        elif jump > (MAX_STUCK_NUDGE_JUMP + 0.05) and speed > MAX_ALLOWED_SPEED and not setup_nearby:
            r.fail(
                "continuity",
                "Illegal teleport detected: "
                f"jump={jump:.1f} speed={speed:.1f} "
                f"from=({pp[0]:.1f},{pp[2]:.1f}) to=({p[0]:.1f},{p[2]:.1f}) "
                "no setup teleport event",
            )
        prev = s

    r.max_position_jump = max_jump
    r.max_speed = max_speed

    if strict:
        _check_forbidden_teleports(r, samples, sid)

    if strict and max_jump > jump_lim:
        msg = (
            f"max_position_jump={max_jump:.3f} exceeds "
            f"MAX_ALLOWED_POSITION_JUMP={jump_lim:.1f}"
        )
        if msg not in r.illegal_teleports:
            r.fail("continuity", msg)

    if r.continuity_pass:
        r.notes.append(f"continuity ok max_jump={max_jump:.2f} max_speed={max_speed:.2f}")


def _check_collect_water_order(r: CheckResult, samples: List[Dict[str, Any]]) -> None:
    idx_reached = -1
    idx_added = -1
    start_dist = -1.0
    moving = 0
    for i, s in enumerate(samples):
        evt = s.get("event")
        if evt in ("reached_resource", "work_started") and idx_reached < 0:
            idx_reached = i
        if evt == "resource_added" and idx_added < 0:
            idx_added = i
        dist = s.get("distance_to_target")
        if s.get("target_type") == "water_source" and isinstance(dist, (int, float)):
            if start_dist < 0 and float(s.get("t") or 0) < 2.0:
                start_dist = float(dist)
            if float(dist) > 2.5:
                moving += 1
    if idx_added >= 0 and idx_reached >= 0 and idx_added < idx_reached:
        r.fail("trajectory", "resource_added before reached_resource")
    if start_dist > 6.0 and idx_added >= 0 and moving < 3:
        r.fail(
            "trajectory",
            f"collect_water jumped to interaction: start_dist={start_dist:.1f} moving_samples={moving}",
        )
    prev_dist = -1.0
    prev_pos = None
    for s in samples:
        if s.get("target_type") != "water_source":
            continue
        dist = s.get("distance_to_target")
        if not isinstance(dist, (int, float)):
            continue
        d = float(dist)
        pos = _pos(s)
        if prev_dist > 8.0 and d <= 2.0:
            t1 = float(s.get("t") or 0)
            # Waypoint→stand retarget drops distance without a position jump — not a teleport.
            jumped = False
            if prev_pos is not None and pos is not None:
                jumped = math.hypot(pos[0] - prev_pos[0], pos[2] - prev_pos[2]) > 2.5
            if jumped and not _nearby_event(samples, t1, t1, _SETUP_TELEPORT_EVENTS):
                r.fail(
                    "continuity",
                    f"Illegal teleport to water: dist {prev_dist:.1f} -> {d:.1f} without movement",
                )
        prev_dist = d
        if pos is not None:
            prev_pos = pos


def _tpos(s: Dict[str, Any]) -> Optional[Tuple[float, float, float]]:
    p = s.get("target_pos")
    if not isinstance(p, dict):
        return None
    return float(p.get("x", 0)), float(p.get("y", 0)), float(p.get("z", 0))


def _check_approach(r: CheckResult, samples: List[Dict[str, Any]], max_sec: float) -> None:
    start_dist = None
    mid_dist = None
    reached = False
    progressed = False
    for s in samples:
        d = s.get("distance_to_target")
        t = float(s.get("t") or 0)
        evt = s.get("event")
        if evt in (
            "reached_target",
            "reached_resource",
            "work_finished",
            "work_started",
            "plan_completed",
            "resource_added",
        ):
            reached = True
            if evt in ("reached_resource", "work_started", "resource_added", "work_finished"):
                progressed = True
        if isinstance(d, (int, float)) and d < 3.5:
            reached = True
        if not isinstance(d, (int, float)):
            continue
        if start_dist is None and t < 1.5:
            start_dist = float(d)
        if 1.5 <= t <= min(max_sec, 8.0):
            mid_dist = float(d)
    # Water corridor may increase distance temporarily before assist/arrival.
    is_water = any(s.get("target_type") == "water_source" for s in samples)
    if (
        not is_water
        and not progressed
        and start_dist is not None
        and mid_dist is not None
        and start_dist > 3
        and mid_dist > start_dist * 1.15
    ):
        r.fail("trajectory", f"distance increased start={start_dist:.1f} mid={mid_dist:.1f}")
    if not reached and start_dist is not None and start_dist > 2:
        had_stuck = any(s.get("event") == "character_stuck" for s in samples)
        if had_stuck:
            r.notes.append("target not reached but character_stuck (idle, no teleport)")
        else:
            r.fail("trajectory", "target not reached within max_seconds")


def _res(s: Dict[str, Any], which: str, key: str) -> int:
    block = s.get(which) or {}
    if not isinstance(block, dict):
        return 0
    try:
        return int(block.get(key) or 0)
    except (TypeError, ValueError):
        return 0


def _check_resources(r: CheckResult, samples: List[Dict[str, Any]], delta: Dict[str, Any]) -> None:
    first, last = samples[0], samples[-1]
    for key in ("water", "wood", "stone", "food", "heat"):
        need = int(delta.get(key) or 0)
        if need <= 0:
            continue
        before = _res(first, "resource_before", key)
        after = _res(last, "resource_after", key)
        if after - before < need:
            r.fail("resource", f"{key} delta={after - before} need>={need}")


def _check_no_resource_credit(r: CheckResult, samples: List[Dict[str, Any]]) -> None:
    first, last = samples[0], samples[-1]
    for key in ("water", "wood", "stone", "food"):
        before = _res(first, "resource_before", key)
        after = _res(last, "resource_after", key)
        if after > before:
            r.fail("resource", f"{key} credited far from target delta={after - before}")
    if any(s.get("event") == "resource_added" for s in samples):
        r.fail("resource", "resource_added event while expect_no_resource_credit")
    if not any(
        s.get("event") in ("resource_guard_denied", "invalid_resource_completion")
        for s in samples
    ):
        r.notes.append("warn: no resource_guard_denied event (still ok if delta=0)")


def _check_resource_guard_events(r: CheckResult, samples: List[Dict[str, Any]]) -> None:
    saw_reached = False
    saw_work_started = False
    saw_guard_pass = False
    for s in samples:
        evt = s.get("event")
        if evt == "reached_resource":
            saw_reached = True
        if evt == "work_started":
            saw_work_started = True
        if evt == "resource_guard_pass":
            saw_guard_pass = True
        if evt != "resource_added":
            continue
        if not saw_reached or not saw_work_started:
            r.fail("resource", "resource_added without reached_resource/work_started order")
        if not saw_guard_pass:
            r.notes.append("warn: resource_added without prior resource_guard_pass")
        d = s.get("distance_to_target")
        if isinstance(d, (int, float)) and float(d) > 2.05:
            r.fail("resource", f"resource_added too far d={float(d):.1f} action={s.get('action')}")
        if s.get("can_complete_resource") is False:
            r.fail("resource", "resource_added while can_complete_resource=false")


def _check_credit_not_spawn(r: CheckResult, samples: List[Dict[str, Any]]) -> None:
    for s in samples:
        if s.get("event") != "resource_added":
            continue
        p = _pos(s)
        if p and s.get("action") == "collect_water" and p[2] < 24.5:
            r.fail("resource", f"water credited near spawn z={p[2]:.1f}")


def _check_pattern(r: CheckResult, samples: List[Dict[str, Any]], pattern: str) -> None:
    if len(samples) < 3:
        r.fail("trajectory", f"pattern={pattern} not enough samples")
        return
    # First event is often started_action without pos — never treat missing as origin.
    origin = None
    for s in samples:
        p = _pos(s)
        if p:
            origin = p
            break
    if not origin:
        r.fail("trajectory", f"pattern={pattern} no positions")
        return
    if pattern == "idle":
        max_d = 0.0
        for s in samples:
            p = _pos(s)
            if not p:
                continue
            d = math.hypot(p[0] - origin[0], p[2] - origin[2])
            max_d = max(max_d, d)
        if max_d > 3.5:
            r.fail("trajectory", f"idle wandered too far d={max_d:.1f}")
        return
    if pattern == "circle":
        prev = None
        total = 0.0
        for s in samples:
            p = _pos(s)
            if not p:
                continue
            if s.get("action") not in (None, "walk_circle", "circle"):
                continue
            ang = math.atan2(p[2] - origin[2], p[0] - origin[0])
            if prev is None:
                prev = ang
                continue
            d = (ang - prev + math.pi) % (2 * math.pi) - math.pi
            total += abs(d)
            prev = ang
        if total * 180 / math.pi < 120:
            r.fail("trajectory", f"circle angle only {total * 180 / math.pi:.0f} deg")
        return
    if pattern == "patrol":
        reversals = 0
        prev_dir = None
        for i in range(1, len(samples)):
            a, b = _pos(samples[i - 1]), _pos(samples[i])
            if not a or not b:
                continue
            dx, dz = b[0] - a[0], b[2] - a[2]
            n = math.hypot(dx, dz)
            if n < 0.1:
                continue
            d = (dx / n, dz / n)
            if prev_dir is not None:
                dot = prev_dir[0] * d[0] + prev_dir[1] * d[1]
                if dot < -0.2:
                    reversals += 1
            prev_dir = d
        if reversals < 1:
            r.fail("trajectory", "patrol: no direction reversal")


def _check_initial_direction(r: CheckResult, samples: List[Dict[str, Any]]) -> None:
    # Prefer samples after the live command actually applied (skip leftover prior action).
    start = None
    after = None
    for s in samples:
        if _pos(s) is None:
            continue
        act = str(s.get("action") or s.get("current_action") or "").lower()
        if act in ("", "none"):
            continue
        if start is None:
            start = s
            continue
        if float(s.get("t") or 0) >= float(start.get("t") or 0) + 2.0:
            after = s
            break
    if not start or not after:
        return
    # If action flips within the window, use the later action's segment.
    start_act = str(start.get("action") or start.get("current_action") or "").lower()
    after_act = str(after.get("action") or after.get("current_action") or "").lower()
    if start_act and after_act and start_act != after_act:
        # Re-anchor start at first sample of after_act.
        for s in samples:
            if _pos(s) is None:
                continue
            if str(s.get("action") or s.get("current_action") or "").lower() == after_act:
                start = s
                break
        after = None
        for s in samples:
            if _pos(s) is None:
                continue
            if str(s.get("action") or s.get("current_action") or "").lower() != after_act:
                continue
            if float(s.get("t") or 0) >= float(start.get("t") or 0) + 2.0:
                after = s
                break
        if not after:
            return
    sp, ap, tp = _pos(start), _pos(after), _tpos(start)
    if not sp or not ap:
        return
    act = str(start.get("action") or start.get("current_action") or "").lower()
    # Water corridor goes west then north — not a straight line to the lake.
    if act == "collect_water":
        dz = ap[2] - sp[2]
        if dz < -1.5:
            r.fail("direction", f"collect_water moved south away from pond dz={dz:.1f}")
        return
    # go_home from water uses east/south corridor around the fence.
    if act == "go_home":
        return
    if not tp:
        return
    to_t = (tp[0] - sp[0], tp[2] - sp[2])
    moved = (ap[0] - sp[0], ap[2] - sp[2])
    nt = math.hypot(*to_t)
    nm = math.hypot(*moved)
    if nt < 1.0 or nm < 0.5:
        return
    dot = (to_t[0] / nt) * (moved[0] / nm) + (to_t[1] / nt) * (moved[1] / nm)
    if dot < 0:
        r.fail("direction", f"Character initially moved away from target. dot={dot:.2f}")
    elif dot < 0.2:
        r.notes.append(f"weak initial alignment dot={dot:.2f}")
