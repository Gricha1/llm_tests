"""Тесты Streaming Survival: join/exit, эвристики #do, парсер."""

from __future__ import annotations

import os
import sys
import tempfile
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

from stream_bot.command_parser import (
    AVAILABLE_ACTIONS,
    EXIT_OK,
    JOIN_ALREADY,
    JOIN_OK,
    ParsedKind,
    available_actions_text,
    parse_message,
)
from stream_bot.streaming_survival_store import StreamingSurvivalStore
from stream_bot.validator import (
    FALLBACK_REPLY,
    heuristic_action,
    validate_streaming_survival_action,
)


class ParseTests(unittest.TestCase):
    def test_join(self):
        self.assertEqual(parse_message("#join").kind, ParsedKind.JOIN)

    def test_exit_aliases(self):
        for cmd in ("#exit", "#leave", "#quit", "#delete"):
            self.assertEqual(parse_message(cmd).kind, ParsedKind.EXIT, cmd)

    def test_do_needs_join(self):
        self.assertEqual(
            parse_message("#do вода", has_joined=False).kind, ParsedKind.NEED_JOIN
        )
        p = parse_message("#do вода", has_joined=True)
        self.assertEqual(p.kind, ParsedKind.DO)
        self.assertEqual(p.text, "вода")

    def test_stats(self):
        self.assertEqual(parse_message("#stats").kind, ParsedKind.STATS)

    def test_available_actions_list(self):
        text = available_actions_text()
        self.assertIn("collect_water", text)
        self.assertIn("idle", text)
        self.assertGreaterEqual(len(AVAILABLE_ACTIONS), 6)


class StoreSessionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.NamedTemporaryFile(suffix=".db", delete=False)
        self.tmp.close()
        self.ss = StreamingSurvivalStore(self.tmp.name)

    def tearDown(self):
        try:
            os.unlink(self.tmp.name)
        except OSError:
            pass

    def test_fresh_join_then_already(self):
        self.assertTrue(self.ss.join("debug_user"))
        self.assertTrue(self.ss.has_joined("debug_user"))
        self.assertFalse(self.ss.join("debug_user"))  # уже активен

    def test_deactivate_all_clears_session(self):
        self.ss.join("a")
        self.ss.join("b")
        self.assertEqual(len(self.ss.list_active()), 2)
        n = self.ss.deactivate_all()
        self.assertEqual(n, 2)
        self.assertEqual(self.ss.list_active(), [])
        self.assertFalse(self.ss.has_joined("a"))
        # повторный join после сброса — снова «добавлен»
        self.assertTrue(self.ss.join("a"))

    def test_exit_leave(self):
        self.ss.join("u")
        self.assertTrue(self.ss.leave("u"))
        self.assertFalse(self.ss.has_joined("u"))
        self.assertFalse(self.ss.leave("u"))

    def test_stats_and_survival_time(self):
        self.ss.join("stats_user")
        self.ss.record_stat("stats_user", "water", 3)
        self.ss.record_stat("stats_user", "sheep", 2)
        self.ss.record_stat("stats_user", "campfire", 1)
        reply = self.ss.format_stats_reply("stats_user")
        self.assertIn("вода 3", reply)
        self.assertIn("овцы 2", reply)
        self.assertIn("костры 1", reply)
        self.assertIn("в игре", reply.lower())
        self.assertTrue(self.ss.leave("stats_user"))
        row = self.ss.get_user("stats_user")
        self.assertGreater(float(row.get("total_survival_seconds") or 0), 0)


class HeuristicDoTests(unittest.TestCase):
    def _act(self, text: str) -> str:
        raw = heuristic_action(text, "debug_user")
        self.assertIsNotNone(raw, f"no heuristic for: {text!r}")
        vr = validate_streaming_survival_action(raw, username="debug_user")
        self.assertTrue(vr.ok, vr.reason)
        return vr.payload["action"]

    def test_water_variants(self):
        for t in ("добывай воду", "принеси воды", "water please", "набери воды"):
            self.assertEqual(self._act(t), "collect_water", t)

    def test_wood_variants(self):
        for t in ("руби дерево", "добывай дерево", "дрова", "wood"):
            self.assertEqual(self._act(t), "collect_wood", t)

    def test_food_and_sheep(self):
        self.assertEqual(self._act("собирай еду"), "collect_food")
        self.assertEqual(self._act("добывай еду"), "collect_food")
        self.assertEqual(self._act("убивай овечек"), "kill_sheep")

    def test_campfire(self):
        self.assertEqual(self._act("поставь костёр"), "build_campfire")
        self.assertEqual(self._act("разведи огонь"), "build_campfire")
        self.assertEqual(self._act("добывай тепло"), "build_campfire")
        self.assertEqual(self._act("зажги котсёр"), "build_campfire")

    def test_wander_typos_idle(self):
        cases = (
            "кружиться по кругу",
            "кружитсья по кругу",  # опечатка пользователя
            "гуляй",
            "броди у базы",
            "танцуй",
            "ходи вокруг",
            "walk around",
        )
        for t in cases:
            raw = heuristic_action(t, "debug_user")
            self.assertIsNotNone(raw, t)
            self.assertEqual(raw["action"], "idle", t)
            self.assertNotEqual(raw.get("chat_reply"), FALLBACK_REPLY, t)
            self.assertIn("гуля", raw["chat_reply"].lower() + raw["action_name"].lower(), t)

    def test_go_home(self):
        self.assertEqual(self._act("иди к дому"), "go_home")
        self.assertEqual(self._act("на базу"), "go_home")

    def test_move_demos(self):
        self.assertEqual(self._act("ходи кругом"), "walk_circle")
        self.assertEqual(self._act("иди вперёд"), "walk_forward")
        self.assertEqual(self._act("иди назад"), "walk_back")
        self.assertEqual(self._act("крутись на месте"), "spin_in_place")


    def test_sequence_water_then_wood(self):
        raw = heuristic_action("добудь 10 воды затем 10 дерева", "debug_user")
        self.assertIsNotNone(raw)
        vr = validate_streaming_survival_action(raw, username="debug_user")
        self.assertTrue(vr.ok, vr.reason)
        self.assertEqual(vr.payload["action"], "collect_water")
        self.assertEqual(vr.payload["amount"], 10)
        self.assertIn("collect_water:10", vr.payload["action_queue"])
        self.assertIn("collect_wood:10", vr.payload["action_queue"])

    def test_amount_single(self):
        raw = heuristic_action("добудь 5 воды", "debug_user")
        self.assertEqual(raw["action"], "collect_water")
        self.assertEqual(raw["amount"], 5)

    def test_bare_collect_omits_one_step_queue(self):
        # Unity treats a queue as a finished plan; bare #do must keep farming.
        for t, act in (
            ("добывай воду", "collect_water"),
            ("руби дерево", "collect_wood"),
            ("добывай дерево", "collect_wood"),
        ):
            raw = heuristic_action(t, "debug_user")
            vr = validate_streaming_survival_action(raw, username="debug_user")
            self.assertTrue(vr.ok, vr.reason)
            self.assertEqual(vr.payload["action"], act, t)
            self.assertEqual(vr.payload["amount"], 1, t)
            self.assertEqual(vr.payload["action_queue"], "", t)

    def test_counted_collect_keeps_queue(self):
        raw = heuristic_action("добудь 5 воды", "debug_user")
        vr = validate_streaming_survival_action(raw, username="debug_user")
        self.assertTrue(vr.ok, vr.reason)
        self.assertEqual(vr.payload["action_queue"], "collect_water:5")

    def test_unknown_returns_none(self):
        self.assertIsNone(heuristic_action("квантовый портал xyz", "u"))


class MessagesSmoke(unittest.TestCase):
    def test_join_exit_copy(self):
        self.assertIn("добавлен", JOIN_OK.lower())
        self.assertNotIn("уже", JOIN_OK.lower())
        self.assertIn("уже", JOIN_ALREADY.lower())
        self.assertIn("вышел", EXIT_OK.lower())


if __name__ == "__main__":
    print(available_actions_text())
    unittest.main(verbosity=2)
