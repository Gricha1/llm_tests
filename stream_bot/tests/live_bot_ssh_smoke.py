#!/usr/bin/env python3
"""Прямой тест бота на lab через SSH curl (без UI)."""
from __future__ import annotations

import json
import subprocess
import sys

REMOTE = "lab_comp"
BOT = "http://127.0.0.1:8765"


def ssh_curl(method: str, path: str, body: dict | None = None, timeout: int = 90) -> dict:
    if body is None:
        cmd = [
            "ssh.exe",
            REMOTE,
            f"curl -s -m {timeout} -X {method} {BOT}{path}",
        ]
    else:
        payload = json.dumps(body, ensure_ascii=False).replace("'", "'\\''")
        cmd = [
            "ssh.exe",
            REMOTE,
            f"curl -s -m {timeout} -X {method} -H 'Content-Type: application/json' "
            f"-d '{payload}' {BOT}{path}",
        ]
    r = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout + 30,
    )
    if r.returncode != 0:
        raise RuntimeError(r.stderr or r.stdout or f"ssh exit {r.returncode}")
    if not r.stdout:
        raise RuntimeError(f"empty stdout stderr={r.stderr!r}")
    return json.loads(r.stdout)


def main() -> int:
    h = ssh_curl("GET", "/health")
    print("health", h)
    st = ssh_curl("GET", "/status")
    acts = [a.get("action") for a in (st.get("available_actions") or [])]
    print("available_actions", acts)
    need = {
        "collect_water",
        "collect_wood",
        "collect_food",
        "kill_sheep",
        "build_campfire",
        "go_home",
        "idle",
    }
    if need - set(acts):
        print("FAIL missing", need - set(acts))
        return 1

    # exit + join
    for msg in ("#exit", "#join"):
        out = ssh_curl("POST", "/local_chat", {"username": "debug_user", "message": msg})
        print(msg, "->", out.get("chat_reply"))

    # разные юзеры — чтобы не упираться в do_cooldown
    cases = [
        ("u_water", "#do добывай воду", "collect_water"),
        ("u_wood", "#do руби дерево", "collect_wood"),
        ("u_food", "#do собирай еду", "collect_food"),
        ("u_sheep", "#do убивай овечек", "kill_sheep"),
        ("u_fire", "#do поставь костёр", "build_campfire"),
        ("u_home", "#do иди к дому", "go_home"),
        ("u_idle", "#do жди", "idle"),
        ("u_seq", "#do добудь 10 воды затем 10 дерева", "collect_water"),
    ]
    fails = 0
    for user, msg, expect in cases:
        ssh_curl("POST", "/local_chat", {"username": user, "message": "#join"})
        out = ssh_curl("POST", "/local_chat", {"username": user, "message": msg})
        cmds = out.get("unity_commands") or []
        act = None
        queue = ""
        for c in cmds:
            if isinstance(c, dict) and c.get("type") == "streaming_survival_action":
                act = c.get("action")
                queue = c.get("action_queue") or ""
                break
        ok = act == expect
        if "затем" in msg:
            ok = ok and "collect_wood:10" in queue and "collect_water:10" in queue
        print(("OK" if ok else "FAIL"), user, msg, "->", act, "q=", queue[:50], "reply=", (out.get("chat_reply") or "")[:70])
        if not ok:
            fails += 1
            print("  raw=", {k: out.get(k) for k in ("ok", "chat_reply", "system_summary")})
    print("fails", fails)
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
