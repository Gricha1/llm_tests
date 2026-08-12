"""Интеграция process_message: join/exit/#do сценарии как в чате."""

from __future__ import annotations

import os
import sys
import tempfile
import unittest
from unittest.mock import MagicMock

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)


class FakeUnity:
    def __init__(self):
        self.sent = []

    def send_payload(self, payload):
        self.sent.append(payload)

    def send_event(self, *args, **kwargs):
        self.sent.append({"type": "event", "args": args, "kwargs": kwargs})

    def close(self):
        pass


class ProcessMessageScenarios(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.NamedTemporaryFile(suffix=".db", delete=False)
        self.tmp.close()
        os.environ["DB_PATH"] = self.tmp.name
        os.environ["USE_LOCAL_LLM"] = "0"
        os.environ["LOCAL_DEBUG_BYPASS_FOLLOWER_CHECK"] = "1"
        os.environ["DO_COOLDOWN_SECONDS"] = "0"
        os.environ["LISTEN_STREAM_ON_START"] = "0"
        # перезагрузить config-модуль с новыми env
        import importlib
        import stream_bot.config as cfg_mod
        import stream_bot.main as main_mod

        importlib.reload(cfg_mod)
        importlib.reload(main_mod)
        self.main_mod = main_mod
        self.bot = main_mod.StreamBot()
        self.bot.unity = FakeUnity()
        self.bot.twitch = MagicMock()
        self.bot.llm = None
        self.bot.do_cooldown_seconds = 0
        from stream_bot.command_parser import JOIN_ALREADY, JOIN_OK

        self.JOIN_OK = JOIN_OK
        self.JOIN_ALREADY = JOIN_ALREADY

    def tearDown(self):
        try:
            self.bot.stop()
        except Exception:
            pass
        try:
            os.unlink(self.tmp.name)
        except OSError:
            pass

    def _chat(self, msg: str, user: str = "debug_user"):
        return self.bot.process_message(user, msg, source="local_debug")

    def test_start_clears_leftover_actives(self):
        self.bot.ss.join("debug_user")
        self.assertEqual(len(self.bot.ss.list_active()), 1)
        self.bot.start_background()
        self.assertEqual(self.bot.ss.list_active(), [])
        syncs = [
            p
            for p in self.bot.unity.sent
            if p.get("type") == "streaming_survival_users_sync"
        ]
        self.assertTrue(syncs)
        self.assertEqual(syncs[-1].get("users"), [])

    def test_join_says_added_not_already(self):
        out = self._chat("#join")
        self.assertEqual(out.get("chat_reply"), self.JOIN_OK)
        out2 = self._chat("#join")
        self.assertEqual(out2.get("chat_reply"), self.JOIN_ALREADY)

    def test_exit_then_join_again(self):
        self._chat("#join")
        out = self._chat("#exit")
        self.assertIn("вышел", (out.get("chat_reply") or "").lower())
        self.assertFalse(self.bot.ss.has_joined("debug_user"))
        out2 = self._chat("#join")
        self.assertEqual(out2.get("chat_reply"), self.JOIN_OK)

    def test_do_wander_typo(self):
        self._chat("#join")
        out = self._chat("#do кружитсья по кругу")
        self.assertIn("гуля", (out.get("chat_reply") or "").lower())
        self.assertNotIn("не понял", (out.get("chat_reply") or "").lower())
        acts = [
            p
            for p in self.bot.unity.sent
            if p.get("type") == "streaming_survival_action"
        ]
        self.assertTrue(acts)
        self.assertEqual(acts[-1]["action"], "idle")

    def test_do_diverse_user_texts(self):
        self._chat("#join")
        cases = [
            ("#do добывай воду", "collect_water"),
            ("#do руби лес пж", "collect_wood"),
            ("#do еды пожалуйста", "collect_food"),
            ("#do убивай овечек!!!", "kill_sheep"),
            ("#do костер у базы", "build_campfire"),
            ("#do стой на месте", "idle"),
            ("#do погуляй", "idle"),
        ]
        for msg, expect in cases:
            out = self._chat(msg)
            self.assertTrue(out.get("sent_to_unity"), msg)
            acts = [
                p
                for p in self.bot.unity.sent
                if p.get("type") == "streaming_survival_action"
            ]
            self.assertEqual(acts[-1]["action"], expect, msg)

    def test_delete_alias_still_exits(self):
        self._chat("#join")
        out = self._chat("#delete")
        self.assertFalse(self.bot.ss.has_joined("debug_user"))
        self.assertIn("вышел", (out.get("chat_reply") or "").lower())


if __name__ == "__main__":
    unittest.main(verbosity=2)
