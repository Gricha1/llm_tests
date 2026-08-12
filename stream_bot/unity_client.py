"""UDP JSON → Unity."""

from __future__ import annotations

import json
import logging
import socket
import time
from typing import Any, Dict, List, Optional

log = logging.getLogger("stream_bot.unity")


class UnityClient:
    def __init__(self, host: str = "127.0.0.1", port: int = 5055, transport: str = "udp") -> None:
        self.host = host
        self.port = port
        self.transport = transport.lower()
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    def close(self) -> None:
        try:
            self._sock.close()
        except OSError:
            pass

    def _send(self, payload: Dict[str, Any]) -> None:
        if self.transport != "udp":
            log.warning("Only udp transport supported, got %s", self.transport)
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        try:
            self._sock.sendto(data, (self.host, self.port))
            log.debug("UDP → %s:%s %s", self.host, self.port, payload.get("type"))
        except OSError as exc:
            log.warning("UDP send failed: %s", exc)

    def send_command(
        self,
        command: str,
        value: float,
        user: str,
        source: str,
        message: str = "",
    ) -> None:
        self._send(
            {
                "type": "stream_command",
                "command": command,
                "value": value,
                "user": user,
                "source": source,
                "message": message,
                "timestamp": int(time.time()),
            }
        )

    def send_poll_state(
        self,
        title: str,
        options: List[Dict[str, Any]],
        seconds_left: int,
    ) -> None:
        self._send(
            {
                "type": "poll_state",
                "title": title,
                "options": options,
                "seconds_left": max(0, int(seconds_left)),
            }
        )

    def send_event(self, message: str, severity: str = "info") -> None:
        self._send(
            {
                "type": "stream_event",
                "message": message,
                "severity": severity,
                "timestamp": int(time.time()),
            }
        )

    def send_character_behavior(self, program: Dict[str, Any]) -> None:
        self.send_payload(program)

    def send_behavior_program(self, program: Dict[str, Any]) -> None:
        self.send_payload(program)

    def send_payload(self, payload: Dict[str, Any]) -> None:
        data = dict(payload)
        data.setdefault("timestamp", int(time.time()))
        self._send(data)
        log.info("UDP → Unity type=%s user=%s", data.get("type"), data.get("username"))
