"""Parser / plan tests for Streaming Survival scenarios.json."""

from __future__ import annotations

import json
import os
import sys
import unittest
from pathlib import Path

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

SCENARIOS_PATH = ROOT / "streaming_survival_tests" / "scenarios.json"


def load_scenarios():
    with open(SCENARIOS_PATH, encoding="utf-8") as f:
        return json.load(f)


def do_text(chat_command: str) -> str:
    """Strip #do prefix for heuristic."""
    p = parse_message(chat_command, has_joined=True)
    if p.kind == ParsedKind.DO:
        return p.text or ""
    if p.kind == ParsedKind.JOIN:
        return ""
    return chat_command.lstrip("#").split(None, 1)[-1] if chat_command else ""


def parse_plan(chat_command: str):
    text = do_text(chat_command)
    if not text:
        return []
    raw = heuristic_action(text, "test_user")
    assert raw is not None, f"no heuristic for {chat_command!r} text={text!r}"
    vr = validate_streaming_survival_action(raw, username="test_user")
    assert vr.ok, vr.reason
    return plan_from_payload(vr.payload)


class StreamingSurvivalParserTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scenarios = load_scenarios()
        cls.by_id = {s["id"]: s for s in cls.scenarios}

    def test_scenarios_file_exists(self):
        self.assertTrue(SCENARIOS_PATH.is_file())

    def test_all_do_scenarios_match_expected_plan(self):
        for sc in self.scenarios:
            if sc.get("expect_join") or not sc.get("expected_plan"):
                continue
            if sc.get("chat_command", "").strip().lower() == "#join":
                continue
            with self.subTest(sc["id"]):
                got = parse_plan(sc["chat_command"])
                want = normalize_expected_plan(sc["expected_plan"])
                self.assertEqual(got, want, f"{sc['id']}: {got} != {want}")

    def test_water_then_campfire_counts(self):
        got = parse_plan("#do набери 2 штуки воды и иди ставь костер")
        self.assertEqual(
            got,
            [
                {"action": "collect_water", "count": 2},
                {"action": "build_campfire", "count": 1},
            ],
        )
        # regression: must NOT become build_campfire count=2
        self.assertFalse(
            any(s["action"] == "build_campfire" and s["count"] == 2 for s in got)
        )

    def test_collect_stone(self):
        got = parse_plan("#do добывай камень")
        self.assertEqual(got, [{"action": "collect_stone", "count": 1}])

    def test_circle(self):
        got = parse_plan("#do ходи по кругу")
        self.assertEqual(got, [{"action": "walk_circle", "count": 1}])

    def test_patrol(self):
        got = parse_plan("#do хочу чтобы ты шел вперед а потом шел назад")
        self.assertEqual(got, [{"action": "patrol", "count": 1}])

    def test_idle(self):
        got = parse_plan("#do ничего не делай")
        self.assertEqual(got, [{"action": "idle", "count": 1}])

    def test_water_home_wood_chain(self):
        got = parse_plan(
            "#do иди к воде собери 5 штук а потом к дому а потом собери 5 дерева"
        )
        self.assertEqual(
            got,
            [
                {"action": "collect_water", "count": 5},
                {"action": "go_home", "count": 1},
                {"action": "collect_wood", "count": 5},
            ],
        )

    def test_join_parsed(self):
        self.assertEqual(parse_message("#join").kind, ParsedKind.JOIN)


if __name__ == "__main__":
    unittest.main()
