#!/usr/bin/env python3
"""Координатный тест SS-агентов: спавн у цветов, смена действий, траектории по логам Unity."""

from __future__ import annotations

import json
import re
import subprocess
import time
from dataclasses import dataclass
from typing import List, Optional, Tuple

REMOTE = "lab_comp"
BOT = "http://127.0.0.1:8765"
LOG = "~/lab_work_space/forest_survival/results/streaming_survival.log"
ROOT = "~/lab_work_space/forest_survival"

POS_RE = re.compile(
    r"\[SSPos\].*?user=(?P<user>\S+).*?pos=\((?P<x>-?[\d.]+),(?P<y>-?[\d.]+),(?P<z>-?[\d.]+)\)"
    r"(?:.*?phase=(?P<phase>\S+))?(?:.*?action=(?P<action>\S+))?(?:.*?target=\((?P<tx>-?[\d.]+),(?P<ty>-?[\d.]+),(?P<tz>-?[\d.]+)\))?"
)
SPAWN_RE = re.compile(
    r"\[StreamingSurvival\] spawned SSPlayer_(?P<user>\S+) at \((?P<x>-?[\d.]+), (?P<y>-?[\d.]+), (?P<z>-?[\d.]+)\).*?"
    r"flowerCenter=\((?P<fx>-?[\d.]+), (?P<fy>-?[\d.]+), (?P<fz>-?[\d.]+)\)"
)
TELEPORT_RE = re.compile(
    r"\[SSPos\] teleport user=(?P<user>\S+) reason=(?P<reason>\S+) "
    r"pos=\((?P<x>-?[\d.]+),(?P<y>-?[\d.]+),(?P<z>-?[\d.]+)\)"
)
FLOWER_RE = re.compile(
    r"\[StreamingSurvival\] followerSpawn\(flowerCenter\)=\((?P<x>-?[\d.]+), (?P<y>-?[\d.]+), (?P<z>-?[\d.]+)\)"
)


@dataclass
class Pos:
    user: str
    x: float
    y: float
    z: float
    phase: str = ""
    action: str = ""
    tx: float = 0.0
    ty: float = 0.0
    tz: float = 0.0


def ssh(cmd: str, timeout: int = 60) -> str:
    r = subprocess.run(
        ["ssh.exe", REMOTE, cmd],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
    )
    if r.returncode != 0 and not r.stdout:
        raise RuntimeError(r.stderr or f"ssh fail {r.returncode}")
    return r.stdout or ""


def ssh_curl(method: str, path: str, body: Optional[dict] = None, timeout: int = 90) -> dict:
    if body is None:
        out = ssh(f"curl -s -m {timeout} -X {method} {BOT}{path}", timeout=timeout + 20)
    else:
        payload = json.dumps(body, ensure_ascii=False).replace("'", "'\\''")
        out = ssh(
            f"curl -s -m {timeout} -X {method} -H 'Content-Type: application/json' "
            f"-d '{payload}' {BOT}{path}",
            timeout=timeout + 20,
        )
    return json.loads(out)


def mark_log() -> int:
    out = ssh(f"wc -l < {LOG} 2>/dev/null || echo 0")
    try:
        return int(out.strip().split()[0])
    except Exception:
        return 0


def log_since(line0: int) -> str:
    # sed 1-indexed; skip first line0 lines
    return ssh(f"tail -n +{max(1, line0 + 1)} {LOG} 2>/dev/null | grep -E '\\[SSPos\\]|followerSpawn|spawned SSPlayer' || true")


def parse_positions(text: str) -> List[Pos]:
    out: List[Pos] = []
    for m in POS_RE.finditer(text):
        out.append(
            Pos(
                user=m.group("user"),
                x=float(m.group("x")),
                y=float(m.group("y")),
                z=float(m.group("z")),
                phase=(m.group("phase") or ""),
                action=(m.group("action") or "").rstrip(","),
                tx=float(m.group("tx") or 0),
                ty=float(m.group("ty") or 0),
                tz=float(m.group("tz") or 0),
            )
        )
    return out


def horiz(a: Tuple[float, float], b: Tuple[float, float]) -> float:
    return ((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2) ** 0.5


def last_pos(rows: List[Pos], user: str) -> Optional[Pos]:
    for p in reversed(rows):
        if p.user.lower() == user.lower():
            return p
    return None


def first_pos(rows: List[Pos], user: str) -> Optional[Pos]:
    for p in rows:
        if p.user.lower() == user.lower():
            return p
    return None


def main() -> int:
    fails = 0
    print("=== SS coordinate trajectory test ===")
    h = ssh_curl("GET", "/health")
    print("health", h)
    if not h.get("ok"):
        print("FAIL bot health")
        return 1

    # очистка прошлых
    for u in ("coord_a", "coord_b", "coord_c", "debug_user", "ui_smoke"):
        try:
            ssh_curl("POST", "/local_chat", {"username": u, "message": "#exit"})
        except Exception:
            pass
    time.sleep(1.5)

    line0 = mark_log()
    print("log mark line", line0)

    users = ["coord_a", "coord_b", "coord_c"]
    for u in users:
        out = ssh_curl("POST", "/local_chat", {"username": u, "message": "#join"})
        print("join", u, "->", (out.get("chat_reply") or "")[:60])
        time.sleep(0.4)

    print("wait spawn logs…")
    time.sleep(4.0)
    chunk = log_since(line0)
    print("--- spawn/teleport excerpt ---")
    for line in chunk.splitlines()[-40:]:
        print(line)

    flower = None
    m = FLOWER_RE.search(chunk)
    if m:
        flower = (float(m.group("x")), float(m.group("y")), float(m.group("z")))
        print("flowerCenter", flower)
    else:
        # из spawned line
        for sm in SPAWN_RE.finditer(chunk):
            flower = (float(sm.group("fx")), float(sm.group("fy")), float(sm.group("fz")))
            print("flowerCenter(from spawn)", flower)
            break

    if flower is None:
        print("FAIL: нет flowerCenter в логе")
        fails += 1
        flower = (0.0, -5.5, 13.0)

    # спавн позиции
    spawns = list(SPAWN_RE.finditer(chunk))
    teleports = list(TELEPORT_RE.finditer(chunk))
    print(f"spawn lines={len(spawns)} teleport lines={len(teleports)}")

    spawn_pts = []
    for sm in SPAWN_RE.finditer(chunk):
        u = sm.group("user")
        if u not in users:
            continue
        pt = (float(sm.group("x")), float(sm.group("y")), float(sm.group("z")))
        spawn_pts.append((u, pt))
        d = horiz((pt[0], pt[2]), (flower[0], flower[2]))
        print(f"  spawn {u} {pt} dist_to_flower_xz={d:.2f}")
        if d > 6.0:
            print(f"FAIL {u} слишком далеко от цветов (>{6}м)")
            fails += 1
        # не под забором: старый баг z≈-4
        if pt[2] < 2.0:
            print(f"FAIL {u} z={pt[2]:.2f} похоже под забором")
            fails += 1

    if len(spawn_pts) < 3:
        # fallback teleports join_spawn
        for tm in TELEPORT_RE.finditer(chunk):
            if tm.group("reason") != "join_spawn":
                continue
            u = tm.group("user")
            if u not in users:
                continue
            pt = (float(tm.group("x")), float(tm.group("y")), float(tm.group("z")))
            if any(s[0] == u for s in spawn_pts):
                continue
            spawn_pts.append((u, pt))
            d = horiz((pt[0], pt[2]), (flower[0], flower[2]))
            print(f"  teleport-spawn {u} {pt} dist={d:.2f}")
            if d > 6.0 or pt[2] < 2.0:
                print(f"FAIL {u} bad spawn")
                fails += 1

    if len(spawn_pts) < 2:
        print("FAIL мало спавнов")
        fails += 1
    else:
        # кластер + одна высота
        ys = [p[1][1] for p in spawn_pts]
        xs = [p[1][0] for p in spawn_pts]
        zs = [p[1][2] for p in spawn_pts]
        y_span = max(ys) - min(ys)
        xz_span = max(
            horiz((xs[i], zs[i]), (xs[j], zs[j]))
            for i in range(len(xs))
            for j in range(i + 1, len(xs))
        ) if len(xs) > 1 else 0.0
        print(f"cluster y_span={y_span:.3f} max_xz_span={xz_span:.2f}")
        if y_span > 1.5:
            print("FAIL агенты на разной высоте")
            fails += 1
        if xz_span > 5.0:
            print("FAIL агенты не в одном месте (разброс >5м)")
            fails += 1
        else:
            print("OK spawn cluster near flowers")

    # --- траектории ---
    line1 = mark_log()
    # A → дерево, B → вода, C → дом
    actions = [
        ("coord_a", "#do руби дерево", "collect_wood"),
        ("coord_b", "#do добывай воду", "collect_water"),
        ("coord_c", "#do иди к дому", "go_home"),
    ]
    for u, msg, expect in actions:
        out = ssh_curl("POST", "/local_chat", {"username": u, "message": msg})
        act = None
        for c in out.get("unity_commands") or []:
            if isinstance(c, dict) and c.get("type") == "streaming_survival_action":
                act = c.get("action")
        print(f"do {u} {msg} -> {act} expect={expect}")
        if act != expect:
            print("FAIL action mismatch")
            fails += 1
        time.sleep(0.3)

    print("wait trajectory 8s…")
    time.sleep(8.0)
    traj = log_since(line1)
    rows = parse_positions(traj)
    print(f"SSPos ticks={len(rows)}")

    for u, _, expect in actions:
        series = [p for p in rows if p.user.lower() == u.lower()]
        if len(series) < 2:
            print(f"FAIL {u}: мало тиков ({len(series)})")
            fails += 1
            continue
        p0, p1 = series[0], series[-1]
        moved = horiz((p0.x, p0.z), (p1.x, p1.z))
        print(
            f"  {u} {p0.action}/{p0.phase} ({p0.x:.1f},{p0.z:.1f}) -> "
            f"({p1.x:.1f},{p1.z:.1f}) moved_xz={moved:.2f} last_action={p1.action}"
        )
        if expect == "idle":
            continue
        # должны начать движение (кроме уже у цели)
        if moved < 0.3 and p1.phase in ("GoTarget", "GoWaterWp", "GoHomeFirst", "GoHouse", "GoBase"):
            print(f"FAIL {u}: заявлена фаза ходьбы но почти не сдвинулся")
            fails += 1
        if p1.action and expect not in p1.action and p1.action != expect:
            # action field might be collect_wood
            if p1.action != expect:
                print(f"WARN {u}: last action log={p1.action} expected={expect}")

    # --- смена действия ---
    line2 = mark_log()
    out = ssh_curl("POST", "/local_chat", {"username": "coord_a", "message": "#do иди к дому"})
    print("switch coord_a -> go_home", (out.get("chat_reply") or "")[:50])
    time.sleep(5.0)
    sw = log_since(line2)
    sw_rows = [p for p in parse_positions(sw) if p.user.lower() == "coord_a"]
    if not sw_rows:
        print("FAIL нет логов после смены действия")
        fails += 1
    else:
        acts = {p.action for p in sw_rows if p.action}
        phases = {p.phase for p in sw_rows if p.phase}
        print(f"  after switch actions={acts} phases={phases}")
        if "go_home" not in acts and "GoHouse" not in phases:
            print("FAIL смена на go_home не видна в логах")
            fails += 1
        else:
            print("OK action switch works")

    print("=== RESULT fails=", fails, "===")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
