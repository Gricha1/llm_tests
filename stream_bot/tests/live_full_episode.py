#!/usr/bin/env python3
"""
Полный эпизод Streaming Survival:
  несколько агентов → 10 воды / 10 дерева / 10 еды / 10 тепла.
Пишет файл траекторий + анализ багов.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import time
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Tuple

REMOTE = "lab_comp"
BOT = "http://127.0.0.1:8765"
LOG = "~/lab_work_space/forest_survival/results/streaming_survival.log"
OUT_DIR = Path(__file__).resolve().parent / "results"
OUT_DIR.mkdir(parents=True, exist_ok=True)

POS_RE = re.compile(
    r"\[SSPos\].*?user=(?P<user>\S+).*?pos=\((?P<x>-?[\d.]+),(?P<y>-?[\d.]+),(?P<z>-?[\d.]+)\)"
    r"(?:.*?phase=(?P<phase>\S+))?(?:.*?action=(?P<action>\S+))?"
    r"(?:.*?target=\((?P<tx>-?[\d.]+),(?P<ty>-?[\d.]+),(?P<tz>-?[\d.]+)\))?"
)
RES_RE = re.compile(
    r"\[SSRes\].*?water=(?P<w>\d+).*?wood=(?P<wood>\d+).*?food=(?P<food>\d+).*?heat=(?P<heat>\d+).*?goal=(?P<goal>\d+)"
)
TARGET_TREE_RE = re.compile(
    r"\[SSPos\] target_tree user=(?P<user>\S+) tree=\((?P<x>-?[\d.]+),(?P<y>-?[\d.]+),(?P<z>-?[\d.]+)\)"
)
CHOP_RE = re.compile(
    r"\[SSPos\] chop user=(?P<user>\S+) tree=\((?P<x>-?[\d.]+),(?P<z>-?[\d.]+)\)"
)
WIN_RE = re.compile(r"\[SSRes\] ROUND_WIN")
FLOWER_Z_MIN = 8.0  # ниже — за забором / вне поляны

AGENTS = [
    ("ep_water", "#do добудь 30 воды", "collect_water"),
    ("ep_wood", "#do добудь 30 дерева", "collect_wood"),
    ("ep_food", "#do добудь 30 еды", "collect_food"),
    ("ep_heat", "#do поставь костёр", "build_campfire"),
]


def ssh(cmd: str, timeout: int = 120) -> str:
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
        out = ssh(f"curl -s -m {timeout} -X {method} {BOT}{path}", timeout=timeout + 25)
    else:
        payload = json.dumps(body, ensure_ascii=False).replace("'", "'\\''")
        out = ssh(
            f"curl -s -m {timeout} -X {method} -H 'Content-Type: application/json' "
            f"-d '{payload}' {BOT}{path}",
            timeout=timeout + 25,
        )
    try:
        return json.loads(out) if out.strip() else {}
    except json.JSONDecodeError:
        return {"_raw": out[:500]}


def mark() -> int:
    try:
        return int(ssh(f"wc -l < {LOG}").strip().split()[0])
    except Exception:
        return 0


def pull_log(since: int) -> str:
    return ssh(
        f"tail -n +{max(1, since + 1)} {LOG} 2>/dev/null "
        f"| grep -E '\\[SSPos\\]|\\[SSRes\\]|followerSpawn|ROUND_' || true",
        timeout=60,
    )


def parse_pos(text: str) -> List[dict]:
    rows = []
    for m in POS_RE.finditer(text):
        rows.append(
            {
                "user": m.group("user"),
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


def last_res(text: str) -> Optional[dict]:
    last = None
    for m in RES_RE.finditer(text):
        last = {
            "water": int(m.group("w")),
            "wood": int(m.group("wood")),
            "food": int(m.group("food")),
            "heat": int(m.group("heat")),
            "goal": int(m.group("goal")),
        }
    return last


def analyze(text: str, agents: List[str]) -> Tuple[List[str], dict]:
    bugs: List[str] = []
    rows = parse_pos(text)
    by_user: Dict[str, List[dict]] = {u: [] for u in agents}
    for r in rows:
        if r["user"] in by_user:
            by_user[r["user"]].append(r)

    # база/target за забором
    for r in rows:
        if r["phase"] == "GoBase" and r.get("tz", 0) < FLOWER_Z_MIN and r.get("tz", 0) != 0:
            bugs.append(
                f"BUG GoBase target за забором: user={r['user']} target_z={r['tz']:.2f}"
            )
            break

    # деревья за забором
    for m in TARGET_TREE_RE.finditer(text):
        z = float(m.group("z"))
        if z < FLOWER_Z_MIN:
            bugs.append(
                f"BUG target_tree за забором: user={m.group('user')} z={z:.2f} < {FLOWER_Z_MIN}"
            )
            break

    # агент wood упёрся вниз (z маленький) надолго
    wood_rows = by_user.get("ep_wood", [])
    if len(wood_rows) >= 8:
        low = [r for r in wood_rows[-8:] if r["z"] < FLOWER_Z_MIN]
        if len(low) >= 6:
            bugs.append(
                f"BUG ep_wood застрял у низа карты: last_z={[round(r['z'],2) for r in wood_rows[-5:]]}"
            )

    # нет движения при GoTarget: окно из 8 тиков подряд, далеко от цели
    for u, series in by_user.items():
        moving = [r for r in series if r["phase"] in ("GoTarget", "GoWaterWp", "GoHomeFirst", "GoHouse", "GoBase")]
        for i in range(0, max(0, len(moving) - 7)):
            window = moving[i : i + 8]
            path = 0.0
            for j in range(1, len(window)):
                path += (
                    (window[j]["x"] - window[j - 1]["x"]) ** 2
                    + (window[j]["z"] - window[j - 1]["z"]) ** 2
                ) ** 0.5
            last = window[-1]
            remain = ((last["x"] - last.get("tx", last["x"])) ** 2 + (last["z"] - last.get("tz", last["z"])) ** 2) ** 0.5
            if path < 0.35 and remain > 3.0:
                bugs.append(
                    f"BUG {u}: застрял в ходьбе (сдвиг {path:.2f}м за 8 тиков, до цели {remain:.1f}м)"
                )
                break
        # цель воды/дерева за верхним забором (пруд ~z=33)
        late = series[-12:] if len(series) >= 12 else series
        for r in late:
            if r["phase"] == "GoTarget" and r.get("tz", 0) > 28.5:
                bugs.append(
                    f"BUG {u}: GoTarget на пруд/забор target_z={r['tz']:.2f} pos_z={r['z']:.2f}"
                )
                break

    traj_summary = {}
    for u, series in by_user.items():
        if not series:
            traj_summary[u] = {"points": 0, "path": ""}
            continue
        step = max(1, len(series) // 8)
        pts = [f"({r['x']:.1f},{r['z']:.1f})" for r in series[::step][:9]]
        path = 0.0
        for i in range(1, len(series)):
            path += ((series[i]["x"] - series[i - 1]["x"]) ** 2 + (series[i]["z"] - series[i - 1]["z"]) ** 2) ** 0.5
        traj_summary[u] = {
            "points": len(series),
            "path_len": round(path, 2),
            "start": (series[0]["x"], series[0]["y"], series[0]["z"]),
            "end": (series[-1]["x"], series[-1]["y"], series[-1]["z"]),
            "phases": sorted({r["phase"] for r in series if r["phase"]}),
            "actions": sorted({r["action"] for r in series if r["action"]}),
            "sample": " → ".join(pts),
        }
    return bugs, traj_summary


def main() -> int:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    traj_path = OUT_DIR / f"episode_trajectories_{stamp}.jsonl"
    report_path = OUT_DIR / f"episode_analysis_{stamp}.md"

    print("=== FULL EPISODE multi-agent resource race ===")
    if not curl("GET", "/health").get("ok"):
        print("FAIL bot down")
        return 1

    # очистка
    for u, _, _ in AGENTS:
        curl("POST", "/local_chat", {"username": u, "message": "#exit"})
    for u in ("debug_user", "t_wood", "t_water"):
        curl("POST", "/local_chat", {"username": u, "message": "#exit"})
    time.sleep(1.5)

    line0 = mark()
    print("log mark", line0)

    # join + assign
    for u, msg, expect in AGENTS:
        j = curl("POST", "/local_chat", {"username": u, "message": "#join"})
        print("join", u, (j.get("chat_reply") or "")[:40])
        time.sleep(0.5)
        out = curl("POST", "/local_chat", {"username": u, "message": msg})
        act = None
        for c in out.get("unity_commands") or []:
            if isinstance(c, dict) and c.get("type") == "streaming_survival_action":
                act = c.get("action")
        print(f"  assign {expect} -> {act}")
        if act != expect:
            print("FAIL assign")
            return 1
        time.sleep(0.4)

    # poll until win or timeout
    deadline = time.time() + 220
    won = False
    last_r = None
    poll_n = 0
    while time.time() < deadline:
        time.sleep(5.0)
        poll_n += 1
        chunk = pull_log(line0)
        last_r = last_res(chunk) or last_r
        won = bool(WIN_RE.search(chunk))
        print(f"[{poll_n}] res={last_r} win={won}")
        # dump incremental traj
        with traj_path.open("w", encoding="utf-8") as f:
            for r in parse_pos(chunk):
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
            if last_r:
                f.write(json.dumps({"type": "resources", **last_r}, ensure_ascii=False) + "\n")
        if won:
            break
        if last_r and all(last_r.get(k, 0) >= last_r.get("goal", 10) for k in ("water", "wood", "food", "heat")):
            won = True
            break

    full = pull_log(line0)
    # пик ресурсов до ROUND_WIN (после винa счётчики сбрасываются)
    peak = {"water": 0, "wood": 0, "food": 0, "heat": 0, "goal": 10}
    for m in RES_RE.finditer(full):
        cur = {
            "water": int(m.group("w")),
            "wood": int(m.group("wood")),
            "food": int(m.group("food")),
            "heat": int(m.group("heat")),
            "goal": int(m.group("goal")),
        }
        for k in ("water", "wood", "food", "heat"):
            peak[k] = max(peak[k], cur[k])
        peak["goal"] = cur["goal"]
    with traj_path.open("w", encoding="utf-8") as f:
        for r in parse_pos(full):
            f.write(json.dumps(r, ensure_ascii=False) + "\n")
        for m in TARGET_TREE_RE.finditer(full):
            f.write(
                json.dumps(
                    {
                        "type": "target_tree",
                        "user": m.group("user"),
                        "x": float(m.group("x")),
                        "y": float(m.group("y")),
                        "z": float(m.group("z")),
                    },
                    ensure_ascii=False,
                )
                + "\n"
            )
        for m in RES_RE.finditer(full):
            f.write(
                json.dumps(
                    {
                        "type": "resources",
                        "water": int(m.group("w")),
                        "wood": int(m.group("wood")),
                        "food": int(m.group("food")),
                        "heat": int(m.group("heat")),
                        "goal": int(m.group("goal")),
                    },
                    ensure_ascii=False,
                )
                + "\n"
            )
        f.write(json.dumps({"type": "round_win", "won": won}, ensure_ascii=False) + "\n")

    bugs, traj = analyze(full, [a[0] for a in AGENTS])
    last_r = peak if any(peak[k] > 0 for k in ("water", "wood", "food", "heat")) else (last_res(full) or last_r or {})
    goal = int(last_r.get("goal", 10))
    solved = (
        won
        or (
            last_r.get("water", 0) >= goal
            and last_r.get("wood", 0) >= goal
            and last_r.get("food", 0) >= goal
            and last_r.get("heat", 0) >= goal
        )
    )

    lines = [
        f"# Episode analysis {stamp}",
        "",
        f"- won/solved: **{solved}** (ROUND_WIN={won})",
        f"- resources: `{last_r}`",
        f"- trajectories file: `{traj_path}`",
        f"- bugs found: **{len(bugs)}**",
        "",
        "## Bugs",
    ]
    if bugs:
        lines.extend(f"- {b}" for b in bugs)
    else:
        lines.append("- none")
    lines += ["", "## Per-agent trajectories"]
    for u, info in traj.items():
        lines.append(f"### {u}")
        lines.append(f"- points={info.get('points')} path_len={info.get('path_len')}")
        lines.append(f"- start={info.get('start')} end={info.get('end')}")
        lines.append(f"- phases={info.get('phases')} actions={info.get('actions')}")
        lines.append(f"- sample: {info.get('sample')}")
        lines.append("")

    lines += [
        "## Verdict",
        f"- Episode goal 10/10/10/10: {'PASS' if solved else 'FAIL'}",
        f"- Trajectory bugs: {'PASS' if not bugs else 'FAIL'}",
        f"- Overall: {'PASS' if solved and not bugs else 'FAIL'}",
    ]
    report_path.write_text("\n".join(lines), encoding="utf-8")
    print(report_path.read_text(encoding="utf-8"))

    # cleanup agents
    for u, _, _ in AGENTS:
        curl("POST", "/local_chat", {"username": u, "message": "#exit"})

    print("traj_file", traj_path)
    print("report_file", report_path)
    return 0 if (solved and not bugs) else 1


if __name__ == "__main__":
    raise SystemExit(main())
