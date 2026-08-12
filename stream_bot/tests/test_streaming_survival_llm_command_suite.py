"""Deterministic (+ optional LLM) suite for Streaming Survival #do plans."""

from __future__ import annotations

import json
import os
import sys
import unittest
from pathlib import Path
from typing import Any, Dict, List, Optional

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from stream_bot.command_parser import ParsedKind, parse_message
from stream_bot.validator import (
    heuristic_action,
    normalize_expected_plan,
    plan_from_payload,
    validate_streaming_survival_action,
)

SUITE_PATH = ROOT / "streaming_survival_tests" / "llm_command_suite.json"
CATALOG_PATH = ROOT / "streaming_survival_tests" / "command_catalog.json"


def _do_text(chat_command: str) -> str:
    p = parse_message(chat_command, has_joined=True)
    if p.kind == ParsedKind.DO:
        return p.text or ""
    return ""


def _resolve_plan(text: str, username: str = "viewer_1") -> Optional[Dict[str, Any]]:
    raw = heuristic_action(text, username)
    if raw is None and os.environ.get("SS_LLM_FALLBACK", "").strip() in ("1", "true", "yes"):
        try:
            from stream_bot.llm_client import LlmClient
            from stream_bot.llm_prompts import SYSTEM_PROMPT, build_user_prompt

            client = LlmClient()
            prompt = build_user_prompt("do", username, text)
            raw = client.generate_json(SYSTEM_PROMPT, prompt)
        except Exception:
            raw = None
    if raw is None:
        return None
    vr = validate_streaming_survival_action(raw, username=username)
    if not vr.ok:
        return None
    return vr.payload


def load_suite() -> List[Dict[str, Any]]:
    with open(SUITE_PATH, encoding="utf-8") as f:
        return json.load(f)


class StreamingSurvivalLlmCommandSuite(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.suite = load_suite()
        cls.catalog = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))

    def test_catalog_exists(self):
        self.assertTrue(CATALOG_PATH.is_file())
        self.assertIn("actions", self.catalog)
        for key in ("go_to_water", "collect_water", "go_to_campfire", "collect_wood"):
            self.assertIn(key, self.catalog["actions"])

    def test_suite_cases(self):
        for case in self.suite:
            with self.subTest(case["id"]):
                text = _do_text(case["command"])
                self.assertTrue(text, f"not a #do: {case['command']}")
                payload = _resolve_plan(text)
                self.assertIsNotNone(payload, f"no plan for {case['command']!r}")
                got = plan_from_payload(payload)
                want = normalize_expected_plan(case["expected_plan"])
                self.assertEqual(got, want, f"{case['id']}: {got} != {want}")
                for bad in case.get("forbid") or []:
                    self.assertFalse(
                        any(
                            s["action"] == bad["action"] and s["count"] == bad["count"]
                            for s in got
                        ),
                        f"{case['id']} forbid {bad} in {got}",
                    )
                reply = str((payload or {}).get("chat_reply") or "")
                for frag in case.get("expected_chat_contains") or []:
                    self.assertIn(
                        frag.lower(),
                        reply.lower(),
                        f"{case['id']} chat_reply missing {frag!r}: {reply!r}",
                    )

    def test_regression_kostru_not_water_x5(self):
        """Bug: «костру» missed → whole phrase became collect_water×5."""
        payload = _resolve_plan(
            "иди к костру, затем 5 раз добудь дерево затем иди к воде", "viewer_1"
        )
        self.assertIsNotNone(payload)
        got = plan_from_payload(payload)
        self.assertEqual(
            got,
            [
                {"action": "go_to_campfire", "count": 1},
                {"action": "collect_wood", "count": 5},
                {"action": "go_to_water", "count": 1},
            ],
        )
        reply = str(payload.get("chat_reply") or "")
        self.assertNotIn("Добывает воду×5", reply)


if __name__ == "__main__":
    unittest.main()
