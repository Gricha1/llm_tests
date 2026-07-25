"""Голосования чата → победитель через validator → Unity."""

from __future__ import annotations

import logging
import random
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Dict, List, Optional

log = logging.getLogger("stream_bot.vote")

ChatSender = Callable[[str], None]


@dataclass
class PollOption:
    id: int
    label: str
    command: str
    value: float
    votes: int = 0


@dataclass
class Poll:
    title: str
    options: List[PollOption]
    duration_seconds: int
    started_at: float = field(default_factory=time.time)
    voters: Dict[str, int] = field(default_factory=dict)
    finished: bool = False
    suggested_by: str = ""


class VoteManager:
    def __init__(
        self,
        unity,
        event_log,
        validator,
        user_store,
        send_chat: ChatSender,
        empty_poll_policy: str = "do_nothing",
    ) -> None:
        self.unity = unity
        self.event_log = event_log
        self.validator = validator
        self.user_store = user_store
        self.send_chat = send_chat
        self.empty_poll_policy = empty_poll_policy
        self._poll: Optional[Poll] = None

    def is_poll_active(self) -> bool:
        return self._poll is not None and not self._poll.finished

    def get_poll_state(self) -> Optional[Dict[str, Any]]:
        poll = self._poll
        if poll is None or poll.finished:
            return None
        left = max(0, int(poll.duration_seconds - (time.time() - poll.started_at)))
        return {
            "type": "poll_state",
            "title": poll.title,
            "options": [
                {"id": o.id, "label": o.label, "votes": o.votes} for o in poll.options
            ],
            "seconds_left": left,
        }

    def start_poll(
        self,
        title: str,
        options: List[Dict[str, Any]],
        duration_seconds: int,
        suggested_by: str = "",
    ) -> bool:
        if self.is_poll_active():
            return False

        cleaned: List[PollOption] = []
        for raw in options[:4]:
            cmd = str(raw.get("command", "")).strip().lower()
            val = float(raw.get("value", 0) or 0)
            vr = self.validator.validate_poll_option(cmd, val)
            if not vr.ok:
                continue
            oid = int(raw.get("id", len(cleaned) + 1))
            label = str(raw.get("label") or f"{vr.command}={vr.value}")[:80]
            cleaned.append(PollOption(id=oid, label=label, command=vr.command, value=vr.value))

        if len(cleaned) < 2:
            return False

        self._poll = Poll(
            title=(title or "Голосование")[:80],
            options=cleaned,
            duration_seconds=max(10, int(duration_seconds)),
            suggested_by=suggested_by.lower(),
        )
        ids = " ".join(f"/{o.id}" for o in cleaned)
        self.send_chat(f"Голосование: {self._poll.title} → {ids}")
        self.event_log.log(
            "poll_start",
            username=suggested_by,
            message=self._poll.title,
            raw=self.get_poll_state(),
        )
        self._push_state()
        return True

    def handle_vote(self, user: str, option_id: int) -> str:
        poll = self._poll
        if poll is None or poll.finished:
            return "Сейчас нет активного голосования."
        u = user.lower()
        if u in poll.voters:
            return f"Уже учтён твой голос /{poll.voters[u]}"
        opt = next((o for o in poll.options if o.id == option_id), None)
        if opt is None:
            return "Нет такого варианта. Используй /1 /2 /3 /4"
        poll.voters[u] = option_id
        opt.votes += 1
        self.user_store.increment_votes(u)
        self.event_log.log("vote", username=u, value=float(option_id), message=opt.label)
        self._push_state()
        return f"Принято: твой голос /{option_id}"

    def finish_poll_if_needed(self) -> bool:
        poll = self._poll
        if poll is None or poll.finished:
            return False
        if time.time() - poll.started_at < poll.duration_seconds:
            return False
        self._finish(poll)
        return True

    def force_finish(self) -> None:
        poll = self._poll
        if poll and not poll.finished:
            self._finish(poll)

    def _finish(self, poll: Poll) -> None:
        poll.finished = True
        winner: Optional[PollOption] = None
        if any(o.votes > 0 for o in poll.options):
            winner = max(poll.options, key=lambda o: (o.votes, -o.id))
        else:
            if self.empty_poll_policy == "random_safe":
                safe = [o for o in poll.options if o.command in ("do_nothing", "food_rain", "heal_agent")]
                pool = safe or poll.options
                winner = random.choice(pool)
            else:
                winner = next((o for o in poll.options if o.command == "do_nothing"), poll.options[-1])

        assert winner is not None
        msg = f"Чат выбрал: {winner.label}"
        self.send_chat(msg)
        self.unity.send_event(msg, severity="info")
        self.event_log.log(
            "poll_winner",
            username=poll.suggested_by,
            command=winner.command,
            value=winner.value,
            message=msg,
            raw={"title": poll.title, "winner": winner.label, "votes": winner.votes},
        )

        if winner.command != "do_nothing":
            # Победитель — system/vote, cooldown помечаем от имени system
            vr = self.validator.validate(
                winner.command,
                winner.value,
                username="vote",
                skip_cooldown=False,
            )
            if vr.ok:
                self.validator.mark_used(vr.command, "vote")
                self.unity.send_command(
                    vr.command,
                    vr.value,
                    user=poll.suggested_by or "chat",
                    source="vote",
                    message=msg,
                )
                if poll.suggested_by:
                    # +10 тем, кто голосовал за победителя
                    for user, oid in poll.voters.items():
                        if oid == winner.id:
                            self.user_store.increment_successful_events(user)
            else:
                self.send_chat(f"Победа не исполнена: {vr.reason}")
                self.event_log.log("poll_blocked", command=winner.command, message=vr.reason)

        self._poll = None

    def _push_state(self) -> None:
        state = self.get_poll_state()
        if state:
            self.unity.send_poll_state(
                state["title"],
                state["options"],
                state["seconds_left"],
            )

    def tick_push_state(self) -> None:
        if self.is_poll_active():
            self._push_state()
