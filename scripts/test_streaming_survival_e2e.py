#!/usr/bin/env python3
"""E2E Streaming Survival via local bot /local_chat (no Twitch)."""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Dict

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from stream_bot.validator import plan_from_payload


def _post(url: str, body: dict, timeout: float = 30.0) -> Dict[str, Any]:
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


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--bot-url", default="http://127.0.0.1:8765")
    ap.add_argument("--run-dir", default="")
    ap.add_argument("--username", default="")
    args = ap.parse_args()
    base = args.bot_url.rstrip("/")

    report: Dict[str, Any] = {
        "overall": "FAIL",
        "parser_pass": False,
        "world_registry_pass": None,
        "trajectory_pass": None,
        "resource_pass": None,
    }

    try:
        st = _get(base + "/status")
    except Exception as e:
        print(f"FAIL bot unreachable: {e}")
        report["reason"] = str(e)
        _write(args.run_dir, report)
        return 1

    # ensure local debug mode if API supports it
    try:
        _post(base + "/mode", {"listen_stream": False})
    except Exception:
        pass

    user = args.username or f"e2e_user_{int(time.time()) % 100000}"
    # Leave if leftover, then fresh join
    try:
        _post(base + "/local_chat", {"username": user, "message": "#exit"})
    except Exception:
        pass
    time.sleep(0.3)
    join = _post(base + "/local_chat", {"username": user, "message": "#join"})
    print("join:", json.dumps(join, ensure_ascii=False)[:400])

    # cooldown: wait if needed
    time.sleep(0.5)
    msg = "#do набери 2 штуки воды и иди ставь костер"
    do = _post(base + "/local_chat", {"username": user, "message": msg})
    print("do:", json.dumps(do, ensure_ascii=False)[:800])

    # Prefer unity_commands / parsed_json from bot response
    payload = do
    if isinstance(do.get("parsed_json"), dict):
        payload = do["parsed_json"]
    elif isinstance(do.get("unity_commands"), list):
        for cmd in do["unity_commands"]:
            if isinstance(cmd, dict) and cmd.get("type") == "streaming_survival_action":
                payload = cmd
                break
    if isinstance(do.get("result"), dict):
        payload = do["result"]
    if isinstance(do.get("payload"), dict):
        payload = do["payload"]

    rtype = str(payload.get("type") or do.get("type") or "")
    plan = []
    if rtype == "streaming_survival_action" or payload.get("action_queue") or payload.get("action"):
        plan = plan_from_payload(payload)
    else:
        for key in ("action_payload", "data", "unity_payload"):
            if isinstance(do.get(key), dict):
                plan = plan_from_payload(do[key])
                if plan:
                    payload = do[key]
                    break

    expect = [
        {"action": "collect_water", "count": 2},
        {"action": "build_campfire", "count": 1},
    ]
    parser_ok = plan == expect
    # Also accept queue encoding
    q = str(payload.get("action_queue") or "")
    if not parser_ok:
        parser_ok = (
            "collect_water:2" in q
            and "build_campfire:1" in q
            and "build_campfire:2" not in q
        )
    report["parser_pass"] = parser_ok
    report["plan"] = plan
    report["type"] = payload.get("type") or rtype
    report["action_queue"] = q

    if not parser_ok:
        report["overall"] = "FAIL"
        report["reason"] = f"plan {plan} queue={q!r} != {expect}"
    else:
        report["overall"] = "PASS"
        report["reason"] = ""

    # join idempotency
    join2 = _post(base + "/local_chat", {"username": user, "message": "#join"})
    report["join_repeat"] = join2

    # cooldown: second do quickly
    time.sleep(0.2)
    do2 = _post(base + "/local_chat", {"username": user, "message": "#do добывай дерево"})
    report["cooldown_second_do"] = {
        "ok": True,
        "note": "bot may reject/delay within 10s",
        "response_keys": list(do2.keys()) if isinstance(do2, dict) else [],
    }

    print(json.dumps(report, ensure_ascii=False, indent=2))
    _write(args.run_dir, report)
    return 0 if report["overall"] == "PASS" else 1


def _write(run_dir: str, report: Dict[str, Any]) -> None:
    if not run_dir:
        return
    p = Path(run_dir)
    p.mkdir(parents=True, exist_ok=True)
    (p / "e2e_results.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
