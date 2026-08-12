# -*- coding: utf-8 -*-
"""Live HTTP scenarios against stream_bot on lab."""
from __future__ import annotations

import json
import sys
import urllib.request

B = "http://127.0.0.1:8765"


def get(path: str):
    with urllib.request.urlopen(B + path, timeout=15) as r:
        return json.loads(r.read().decode())


def post(path: str, body: dict):
    req = urllib.request.Request(
        B + path,
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=45) as r:
        return json.loads(r.read().decode())


def chat(msg: str, user: str = "debug_user"):
    return post("/local_chat", {"username": user, "message": msg})


def main() -> int:
    st = get("/status")
    print("active_before", st.get("active_users"))
    print("llm_enabled", st.get("llm_enabled"), "model", st.get("ollama_model"))
    acts = st.get("available_actions") or []
    print("available_actions:")
    for a in acts:
        print(" ", a)

    # ensure clean
    chat("#exit")

    o = chat("#join")
    print("JOIN1", o.get("chat_reply"))
    assert "добавлен" in (o.get("chat_reply") or "").lower(), o

    o = chat("#join")
    print("JOIN2", o.get("chat_reply"))
    assert "уже" in (o.get("chat_reply") or "").lower(), o

    cases = [
        ("#do кружитсья по кругу", "idle", "гуля"),
        ("#do добывай воду", "collect_water", "вод"),
        ("#do руби дерево", "collect_wood", "дерев"),
        ("#do еды", "collect_food", "ед"),
        ("#do убивай овечек", "kill_sheep", "овеч"),
        ("#do костер", "build_campfire", "кост"),
        ("#do гуляй", "idle", "гуля"),
    ]
    import time

    for i, (msg, expect_act, needle) in enumerate(cases):
        if i:
            time.sleep(11)  # DO_COOLDOWN_SECONDS=10 на сервере
        o = chat(msg)
        reply = (o.get("chat_reply") or "").lower()
        summary = (o.get("system_summary") or "").lower()
        print("CASE", msg, "→", o.get("chat_reply"), "|", o.get("system_summary"))
        assert expect_act in summary or expect_act in str(o.get("unity_commands")), (
            msg,
            o,
        )
        assert "не понял" not in reply, (msg, reply)
        assert needle in reply or needle in summary, (msg, reply, summary)

    o = chat("#exit")
    print("EXIT", o.get("chat_reply"))
    assert "вышел" in (o.get("chat_reply") or "").lower(), o

    o = chat("#join")
    print("JOIN3", o.get("chat_reply"))
    assert "добавлен" in (o.get("chat_reply") or "").lower(), o

    st = get("/status")
    print("active_after", st.get("active_users"))
    print("OK_ALL_SCENARIOS")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print("FAIL", exc)
        raise
