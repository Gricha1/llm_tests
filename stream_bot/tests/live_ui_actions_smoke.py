"""UI smoke: available_actions + debug buttons HTML + async #do через чат."""

from __future__ import annotations

import json
import sys
import time
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8877"


def get(path: str, timeout: float = 15.0) -> dict | str:
    with urllib.request.urlopen(BASE + path, timeout=timeout) as r:
        raw = r.read().decode("utf-8")
        if path.startswith("/api/"):
            return json.loads(raw)
        return raw


def post(path: str, body: dict, timeout: float = 30.0) -> dict:
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def main() -> int:
    print("BASE", BASE)
    st = get("/api/llm_bot/status")
    assert isinstance(st, dict)
    acts = st.get("available_actions") or []
    print("available_actions", len(acts), [a.get("action") for a in acts])
    need = {
        "collect_water",
        "collect_wood",
        "collect_food",
        "kill_sheep",
        "build_campfire",
        "go_home",
        "idle",
    }
    if need - {a.get("action") for a in acts}:
        print("FAIL missing", need - {a.get("action") for a in acts})
        return 1

    html = get("/")
    assert isinstance(html, str)
    if "ssDebugActions" not in html:
        print("FAIL no ssDebugActions in HTML")
        return 1
    if "renderSsDebugActions" not in html:
        print("FAIL no renderSsDebugActions JS")
        return 1
    print("OK debug actions UI markup")

    # async local_chat: шлём, ждём ответ в chat
    user = "ui_smoke"
    post("/api/llm_bot/local_chat", {"username": user, "message": "#join"})
    time.sleep(2.5)
    post("/api/llm_bot/local_chat", {"username": user, "message": "#do иди к дому"})
    reply = None
    for _ in range(20):
        time.sleep(1.0)
        st = get("/api/llm_bot/status")
        assert isinstance(st, dict)
        for row in reversed(st.get("chat") or []):
            if row.get("role") == "bot" and row.get("text"):
                t = str(row.get("text") or "")
                if "SSH занят" in t or "ошибка" in t.lower():
                    continue
                reply = t
                break
        if reply and (
            "дом" in reply.lower()
            or "баз" in reply.lower()
            or "идёт" in reply.lower()
            or "идет" in reply.lower()
            or "добавлен" in reply.lower()
        ):
            break
    print("bot reply:", (reply or "")[:120])
    if not reply:
        print("FAIL no bot reply in UI chat (async)")
        return 1
    print("OK UI local_chat async path")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
