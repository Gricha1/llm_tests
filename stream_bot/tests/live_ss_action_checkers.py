#!/usr/bin/env python3
"""Live checkers: go_home, water route, campfire@house, wood Do, move demos."""

from __future__ import annotations

import json
import re
import subprocess
import time
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Tuple

REMOTE = "lab_comp"
BOT = "http://127.0.0.1:8765"
LOG = "~/lab_work_space/forest_survival/results/streaming_survival.log"
OUT = Path(__file__).resolve().parent / "results"
OUT.mkdir(parents=True, exist_ok=True)

POS_RE = re.compile(
    r"\[SSPos\].*?user=(?P<user>\S+).*?pos=\((?P<x>-?[\d.]+),(?P<y>-?[\d.]+),(?P<z>-?[\d.]+)\)"
    r"(?:.*?phase=(?P<phase>\S+))?(?:.*?action=(?P<action>\S+))?"
    r"(?:.*?target=\((?P<tx>-?[\d.]+),(?P<ty>-?[\d.]+),(?P<tz>-?[\d.]+)\))?"
)
DONE_RE = re.compile(
    r"\[SSPos\] action_done user=(?P<user>\S+) action=(?P<action>\S+) ok=(?P<ok>\d)"
)
ANIM_RE = re.compile(r"\[SSPos\] anim_do user=(?P<user>\S+)")
CHOP_RE = re.compile(r"\[SSPos\] chop user=(?P<user>\S+)")
CAMP_RE = re.compile(
    r"\[SSPos\] campfire_built user=(?P<user>\S+) pos=\((?P<x>-?[\d.]+),(?P<z>-?[\d.]+)\)"
)
WATER_WP_RE = re.compile(r"\[SSPos\] water_wp user=(?P<user>\S+) i=(?P<i>\d+)")
HOME_T_RE = re.compile(
    r"\[SSPos\] go_home_target user=(?P<user>\S+) house=\((?P<hx>-?[\d.]+),(?P<hz>-?[\d.]+)\)"
)
HOUSE_XZ = (-3.27, 18.85)  # HomeSpot/Fire local ≈ world in presentation
SPAWN_Z_BAND = (11.0, 16.0)


def ssh(cmd: str, timeout: int = 90) -> str:
    r = subprocess.run(
        ["ssh.exe", REMOTE, cmd],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
    )
    return r.stdout or ""


def curl(method: str, path: str, body: Optional[dict] = None, timeout: int = 60) -> dict:
    if body is None:
        out = ssh(f"curl -s -m {timeout} -X {method} {BOT}{path}", timeout=timeout + 20)
    else:
        payload = json.dumps(body, ensure_ascii=False).replace("'", "'\\''")
        out = ssh(
            f"curl -s -m {timeout} -X {method} -H 'Content-Type: application/json' "
            f"-d '{payload}' {BOT}{path}",
            timeout=timeout + 20,
        )
    try:
        return json.loads(out) if out.strip() else {}
    except json.JSONDecodeError:
        return {"_raw": out[:400]}


def mark() -> int:
    try:
        return int(ssh(f"wc -l < {LOG}").strip().split()[0])
    except Exception:
        return 0


def pull(since: int) -> str:
    return ssh(
        f"tail -n +{max(1, since + 1)} {LOG} 2>/dev/null | grep '\\[SSPos\\]\\|\\[SSRes\\]' || true",
        timeout=60,
    )


def parse_pos(text: str, user: str) -> List[dict]:
    rows = []
    for m in POS_RE.finditer(text):
        if m.group("user") != user:
            continue
        rows.append(
            {
                "x": float(m.group("x")),
                "z": float(m.group("z")),
                "phase": (m.group("phase") or "").rstrip(","),
                "action": (m.group("action") or "").rstrip(","),
                "tx": float(m.group("tx") or 0),
                "tz": float(m.group("tz") or 0),
            }
        )
    return rows


def path_len(rows: List[dict]) -> float:
    s = 0.0
    for i in range(1, len(rows)):
        s += ((rows[i]["x"] - rows[i - 1]["x"]) ** 2 + (rows[i]["z"] - rows[i - 1]["z"]) ** 2) ** 0.5
    return s


def assign(user: str, msg: str, expect: str) -> bool:
    curl("POST", "/local_chat", {"username": user, "message": "#exit"})
    time.sleep(0.3)
    curl("POST", "/local_chat", {"username": user, "message": "#join"})
    time.sleep(0.4)
    out = curl("POST", "/local_chat", {"username": user, "message": msg})
    act = None
    for c in out.get("unity_commands") or []:
        if isinstance(c, dict) and c.get("type") == "streaming_survival_action":
            act = c.get("action")
    print(f"  assign {user} {expect} -> {act}")
    return act == expect


def wait_log(since: int, seconds: float) -> str:
    time.sleep(seconds)
    return pull(since)


def check_go_home(text: str, user: str) -> Tuple[bool, str]:
    rows = parse_pos(text, user)
    if not rows:
        return False, "no ticks"
    # target near house
    ht = list(HOME_T_RE.finditer(text))
    if ht:
        hx, hz = float(ht[-1].group("hx")), float(ht[-1].group("hz"))
        if abs(hx - HOUSE_XZ[0]) > 6 or abs(hz - HOUSE_XZ[1]) > 6:
            return False, f"go_home_target far from house ({hx},{hz})"
    end = rows[-1]
    dist = ((end["x"] - HOUSE_XZ[0]) ** 2 + (end["z"] - HOUSE_XZ[1]) ** 2) ** 0.5
    moved = path_len(rows)
    done = any(m.group("user") == user and m.group("action") == "go_home" for m in DONE_RE.finditer(text))
    ok = (dist < 5.5 or done) and moved > 1.0
    return ok, f"end_dist_house={dist:.1f} moved={moved:.1f} done={done}"


def check_water(text: str, user: str) -> Tuple[bool, str]:
    rows = parse_pos(text, user)
    credited = []
    for m in re.finditer(
        r'\[SSPos\] water_credited user=(\S+) pos=\((-?[\d.]+),(-?[\d.]+)\)',
        text,
    ):
        if m.group(1) == user:
            credited.append((float(m.group(2)), float(m.group(3))))
    ok_credit = False
    detail_c = 'none'
    for fx, fz in credited:
        near_spawn = abs(fx - 10.2) < 5 and abs(fz - 13.3) < 5
        near_house = abs(fx + 3.3) < 5 and abs(fz - 18.9) < 5
        far_enough = fz >= 20.0
        ok_credit = far_enough and not near_spawn and not near_house
        detail_c = f'cred=({fx:.1f},{fz:.1f}) far={far_enough}'
        if ok_credit:
            break
    gather = 'water_gather' in text
    late_z = max((r['z'] for r in rows[-8:]), default=0.0)
    ok = ok_credit or (gather and late_z >= 20)
    return ok, f'{detail_c} gather={gather} late_z={late_z:.1f} n={len(rows)}'


def check_campfire(text: str, user: str) -> Tuple[bool, str]:
    camps = [m for m in CAMP_RE.finditer(text) if m.group("user") == user]
    rows = parse_pos(text, user)
    moved_to_house = False
    if rows:
        end = rows[-1]
        d = ((end["x"] - HOUSE_XZ[0]) ** 2 + (end["z"] - HOUSE_XZ[1]) ** 2) ** 0.5
        moved_to_house = d < 6.0 or path_len(rows) > 2.0 and end["z"] > 16
    if camps:
        x, z = float(camps[-1].group("x")), float(camps[-1].group("z"))
        d = ((x - HOUSE_XZ[0]) ** 2 + (z - HOUSE_XZ[1]) ** 2) ** 0.5
        # must NOT be at flower spawn (~10,13)
        at_spawn = abs(x - 10.2) < 3 and abs(z - 13.3) < 3
        ok = d < 8.0 and not at_spawn
        return ok, f"campfire=({x:.1f},{z:.1f}) dist_house={d:.1f} at_spawn={at_spawn}"
    # may lack wood — still must walk toward house
    return moved_to_house, f"no_build_yet moved_house={moved_to_house}"


def check_wood(text: str, user: str) -> Tuple[bool, str]:
    anim = any(m.group("user") == user for m in ANIM_RE.finditer(text))
    chop = any(m.group("user") == user for m in CHOP_RE.finditer(text))
    rows = parse_pos(text, user)
    work = any(r["phase"] == "Work" for r in rows)
    ok = anim and (chop or work)
    return ok, f"anim_do={anim} chop={chop} work={work}"


def check_move(text: str, user: str, kind: str) -> Tuple[bool, str]:
    rows = parse_pos(text, user)
    done = any(m.group("user") == user and m.group("action") == kind for m in DONE_RE.finditer(text))
    moved = path_len(rows)
    if kind == "spin_in_place":
        ok = done or any(r["action"] == kind for r in rows)
        return ok, f"done={done} ticks={len(rows)}"
    ok = done and moved > 1.5
    return ok, f"done={done} moved={moved:.1f}"


def main() -> int:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    report = OUT / f"action_checkers_{stamp}.md"
    traj = OUT / f"action_checkers_{stamp}.jsonl"

    if not curl("GET", "/health").get("ok"):
        print("FAIL bot down")
        return 1

    # ensure some wood for campfire via wood agent briefly
    results: List[Tuple[str, bool, str]] = []
    line0 = mark()

    scenarios = [
        ("ck_home", "#do иди к дому", "go_home", 16.0, check_go_home),
        ("ck_water_a", "#do добудь воду", "collect_water", 18.0, check_water),
        ("ck_fwd", "#do иди вперёд", "walk_forward", 10.0, lambda t, u: check_move(t, u, "walk_forward")),
        ("ck_circle", "#do ходи кругом", "walk_circle", 10.0, lambda t, u: check_move(t, u, "walk_circle")),
        ("ck_back", "#do иди назад", "walk_back", 10.0, lambda t, u: check_move(t, u, "walk_back")),
        ("ck_spin", "#do крутись на месте", "spin_in_place", 9.0, lambda t, u: check_move(t, u, "spin_in_place")),
        ("ck_wood", "#do руби дерево", "collect_wood", 16.0, check_wood),
    ]

    all_log = ""
    for user, msg, expect, wait_s, checker in scenarios:
        print(f"=== {expect} ({user}) ===")
        if not assign(user, msg, expect):
            results.append((expect, False, "assign fail"))
            continue
        chunk = wait_log(line0, wait_s)
        all_log += chunk + "\n"
        ok, detail = checker(chunk, user)
        results.append((expect, ok, detail))
        print(f"  -> {'PASS' if ok else 'FAIL'} {detail}")
        curl("POST", "/local_chat", {"username": user, "message": "#exit"})
        time.sleep(0.4)

    # water from house: go_home then water
    print("=== water from house ===")
    u = "ck_water_b"
    assign(u, "#do иди к дому", "go_home")
    wait_log(line0, 14.0)
    curl("POST", "/local_chat", {"username": u, "message": "#do добудь воду"})
    chunk = wait_log(line0, 20.0)
    all_log += chunk + "\n"
    ok, detail = check_water(chunk, u)
    results.append(("collect_water_from_house", ok, detail))
    print(f"  -> {'PASS' if ok else 'FAIL'} {detail}")
    curl("POST", "/local_chat", {"username": u, "message": "#exit"})

    # campfire: chop a bit then build
    print("=== campfire near house ===")
    u = "ck_fire"
    assign(u, "#do добудь 5 дерева", "collect_wood")
    wait_log(line0, 22.0)
    curl("POST", "/local_chat", {"username": u, "message": "#do поставь костёр"})
    chunk = wait_log(line0, 18.0)
    all_log += chunk + "\n"
    ok, detail = check_campfire(chunk, u)
    results.append(("build_campfire", ok, detail))
    print(f"  -> {'PASS' if ok else 'FAIL'} {detail}")
    curl("POST", "/local_chat", {"username": u, "message": "#exit"})

    with traj.open("w", encoding="utf-8") as f:
        for r in POS_RE.finditer(all_log):
            f.write(json.dumps(r.groupdict(), ensure_ascii=False) + "\n")
        for name, ok, detail in results:
            f.write(json.dumps({"check": name, "ok": ok, "detail": detail}, ensure_ascii=False) + "\n")

    lines = [f"# Action checkers {stamp}", ""]
    fails = 0
    for name, ok, detail in results:
        lines.append(f"- {'PASS' if ok else 'FAIL'} **{name}**: {detail}")
        if not ok:
            fails += 1
    lines += ["", f"Overall: {'PASS' if fails == 0 else 'FAIL'} ({fails} failed)"]
    report.write_text("\n".join(lines), encoding="utf-8")
    print(report.read_text(encoding="utf-8"))
    print("traj", traj)
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
