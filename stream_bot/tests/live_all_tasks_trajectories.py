#!/usr/bin/env python3
"""Траектории по КАЖДОЙ доступной задаче — координаты из Unity [SSPos] логов."""

from __future__ import annotations

import json
import re
import subprocess
import time
from typing import Dict, List, Optional, Tuple

REMOTE = "lab_comp"
BOT = "http://127.0.0.1:8765"
LOG = "~/lab_work_space/forest_survival/results/streaming_survival.log"

POS_RE = re.compile(
    r"\[SSPos\].*?user=(?P<user>\S+).*?pos=\((?P<x>-?[\d.]+),(?P<y>-?[\d.]+),(?P<z>-?[\d.]+)\)"
    r"(?:.*?phase=(?P<phase>\S+))?(?:.*?action=(?P<action>\S+))?(?:.*?target=\((?P<tx>-?[\d.]+),(?P<ty>-?[\d.]+),(?P<tz>-?[\d.]+)\))?"
)

# все whitelist-задачи + команда #do
TASKS = [
    ("t_water", "collect_water", "#do добывай воду", 10.0),
    ("t_wood", "collect_wood", "#do руби дерево", 10.0),
    ("t_food", "collect_food", "#do собирай еду", 10.0),
    ("t_sheep", "kill_sheep", "#do убивай овечек", 10.0),
    ("t_fire", "build_campfire", "#do поставь костёр", 6.0),
    ("t_home", "go_home", "#do иди к дому", 8.0),
    ("t_idle", "idle", "#do жди", 4.0),
]


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


def curl(method: str, path: str, body: Optional[dict] = None, timeout: int = 90) -> dict:
    if body is None:
        out = ssh(f"curl -s -m {timeout} -X {method} {BOT}{path}", timeout=timeout + 20)
    else:
        payload = json.dumps(body, ensure_ascii=False).replace("'", "'\\''")
        out = ssh(
            f"curl -s -m {timeout} -X {method} -H 'Content-Type: application/json' "
            f"-d '{payload}' {BOT}{path}",
            timeout=timeout + 20,
        )
    return json.loads(out) if out.strip() else {}


def mark() -> int:
    try:
        return int(ssh(f"wc -l < {LOG}").strip().split()[0])
    except Exception:
        return 0


def log_since(n: int) -> str:
    return ssh(
        f"tail -n +{max(1, n + 1)} {LOG} 2>/dev/null | grep '\\[SSPos\\]' || true"
    )


def parse(user: str, text: str) -> List[dict]:
    rows = []
    for m in POS_RE.finditer(text):
        if m.group("user").lower() != user.lower():
            continue
        rows.append(
            {
                "x": float(m.group("x")),
                "y": float(m.group("y")),
                "z": float(m.group("z")),
                "phase": (m.group("phase") or "").rstrip(","),
                "action": (m.group("action") or "").rstrip(","),
                "tx": float(m.group("tx") or 0),
                "tz": float(m.group("tz") or 0),
            }
        )
    return rows


def xz(a: dict, b: dict) -> float:
    return ((a["x"] - b["x"]) ** 2 + (a["z"] - b["z"]) ** 2) ** 0.5


def path_len(rows: List[dict]) -> float:
    s = 0.0
    for i in range(1, len(rows)):
        s += xz(rows[i - 1], rows[i])
    return s


def summarize_traj(rows: List[dict]) -> str:
    if not rows:
        return "(no points)"
    pts = [f"({r['x']:.1f},{r['z']:.1f})" for r in rows[:: max(1, len(rows) // 6)][:7]]
    if pts[-1] != f"({rows[-1]['x']:.1f},{rows[-1]['z']:.1f})":
        pts.append(f"({rows[-1]['x']:.1f},{rows[-1]['z']:.1f})")
    return " → ".join(pts)


def main() -> int:
    print("=== FULL trajectory test for ALL tasks ===")
    h = curl("GET", "/health")
    print("health", h)
    if not h.get("ok"):
        print("FAIL bot down")
        return 1

    fails = 0
    results: Dict[str, dict] = {}

    # exit leftovers
    for u, _, _, _ in TASKS:
        try:
            curl("POST", "/local_chat", {"username": u, "message": "#exit"})
        except Exception:
            pass
    time.sleep(1)

    for user, expect, msg, wait_s in TASKS:
        print(f"\n--- {expect} / {user} ---")
        line0 = mark()
        j = curl("POST", "/local_chat", {"username": user, "message": "#join"})
        print("join:", (j.get("chat_reply") or "")[:50])
        time.sleep(1.2)
        # spawn snapshot
        spawn_log = log_since(line0)
        spawn_rows = parse(user, spawn_log)
        spawn = spawn_rows[0] if spawn_rows else None

        line1 = mark()
        out = curl("POST", "/local_chat", {"username": user, "message": msg})
        act = None
        for c in out.get("unity_commands") or []:
            if isinstance(c, dict) and c.get("type") == "streaming_survival_action":
                act = c.get("action")
        print(f"cmd -> action={act} expect={expect} reply={(out.get('chat_reply') or '')[:50]}")
        if act != expect:
            print("FAIL action mismatch")
            fails += 1

        time.sleep(wait_s)
        traj_log = log_since(line1)
        rows = parse(user, traj_log)
        # keep only after set_action for this expect if possible
        after = [r for r in rows if r["action"] == expect or not r["action"]]
        if len(after) >= 2:
            rows = after

        moved = xz(rows[0], rows[-1]) if len(rows) >= 2 else 0.0
        plen = path_len(rows)
        phases = sorted({r["phase"] for r in rows if r["phase"]})
        actions = sorted({r["action"] for r in rows if r["action"]})

        print(f"points={len(rows)} moved_xz={moved:.2f} path_len={plen:.2f}")
        print(f"phases={phases} actions={actions}")
        print(f"traj: {summarize_traj(rows)}")
        if spawn:
            print(f"spawn=({spawn['x']:.2f},{spawn['y']:.2f},{spawn['z']:.2f})")

        ok = True
        if len(rows) < 2:
            print("FAIL too few position samples")
            ok = False
        elif expect == "idle":
            # idle должен почти стоять
            if moved > 1.5:
                print("FAIL idle ушёл слишком далеко")
                ok = False
            else:
                print("OK idle стоит у спавна")
        elif expect == "build_campfire":
            # может стоять у базы или идти к дому
            if "build_campfire" not in actions and expect not in actions:
                print("FAIL campfire action not in logs")
                ok = False
            else:
                print("OK campfire action active")
        else:
            # должны двигаться к цели
            if moved < 0.5 and plen < 0.5:
                print("FAIL no movement for moving task")
                ok = False
            elif expect not in actions and not any(expect in a for a in actions):
                print("FAIL expected action not seen in SSPos")
                ok = False
            else:
                print(f"OK {expect} moved toward task")

        # смена действия: с текущей → go_home (кроме уже go_home/idle)
        if expect not in ("go_home", "idle"):
            line2 = mark()
            curl("POST", "/local_chat", {"username": user, "message": "#do иди к дому"})
            time.sleep(4.0)
            sw = parse(user, log_since(line2))
            sw_acts = {r["action"] for r in sw if r["action"]}
            sw_ph = {r["phase"] for r in sw if r["phase"]}
            print(f"switch->go_home acts={sw_acts} phases={sw_ph}")
            if "go_home" not in sw_acts and "GoHouse" not in sw_ph:
                print("FAIL action switch to go_home")
                ok = False
            else:
                print("OK action switch")

        if not ok:
            fails += 1
        results[expect] = {
            "ok": ok,
            "moved": moved,
            "path_len": plen,
            "points": len(rows),
            "traj": summarize_traj(rows),
        }

        curl("POST", "/local_chat", {"username": user, "message": "#exit"})
        time.sleep(0.5)

    print("\n=== SUMMARY ===")
    for k, v in results.items():
        print(f"{'OK' if v['ok'] else 'FAIL'} {k}: moved={v['moved']:.2f} path={v['path_len']:.2f} pts={v['points']} | {v['traj']}")
    print("fails", fails)
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
