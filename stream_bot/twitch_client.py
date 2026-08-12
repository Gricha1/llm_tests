"""Twitch IRC client (read + optional PRIVMSG replies)."""

from __future__ import annotations

import logging
import socket
import ssl
import threading
import time
from typing import Callable, Optional

log = logging.getLogger("stream_bot.twitch")

MessageHandler = Callable[[str, str], None]


class TwitchClient:
    def __init__(
        self,
        channel: str,
        nick: str = "",
        oauth: str = "",
        on_message: Optional[MessageHandler] = None,
    ) -> None:
        self.channel = channel.lstrip("#").lower()
        self.nick = (nick or f"justinfan{int(time.time()) % 1000000}").lower()
        self.oauth = oauth.strip()
        if self.oauth and not self.oauth.startswith("oauth:"):
            self.oauth = f"oauth:{self.oauth}"
        self.on_message = on_message
        self._sock: Optional[socket.socket] = None
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._write_lock = threading.Lock()
        self._connected = False

    @property
    def can_send(self) -> bool:
        return bool(self.oauth) and not self.nick.startswith("justinfan")

    @property
    def connected(self) -> bool:
        return bool(self._connected)

    def start(self) -> None:
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="twitch-irc", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        try:
            if self._sock:
                self._sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        try:
            if self._sock:
                self._sock.close()
        except OSError:
            pass
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=3.0)

    def send_chat_message(self, text: str) -> None:
        text = (text or "").strip()
        if not text:
            return
        if len(text) > 450:
            text = text[:447] + "..."
        if not self.can_send:
            log.info("[chat dry-run] %s", text)
            return
        self._send_raw(f"PRIVMSG #{self.channel} :{text}")

    def _send_raw(self, line: str) -> None:
        if not self._sock:
            return
        payload = (line.rstrip("\r\n") + "\r\n").encode("utf-8")
        with self._write_lock:
            try:
                self._sock.sendall(payload)
            except OSError as exc:
                log.warning("IRC send failed: %s", exc)

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                self._connect_and_read()
            except Exception as exc:
                log.exception("IRC loop error: %s", exc)
            if self._stop.is_set():
                break
            time.sleep(3.0)

    def _connect_and_read(self) -> None:
        log.info("Connecting to Twitch IRC #%s as %s", self.channel, self.nick)
        raw = socket.create_connection(("irc.chat.twitch.tv", 6697), timeout=20)
        context = ssl.create_default_context()
        sock = context.wrap_socket(raw, server_hostname="irc.chat.twitch.tv")
        sock.settimeout(1.0)
        self._sock = sock

        if self.oauth:
            self._send_raw(f"PASS {self.oauth}")
        else:
            self._send_raw("PASS SCHMOOPIIE")
        self._send_raw(f"NICK {self.nick}")
        self._send_raw("CAP REQ :twitch.tv/tags twitch.tv/commands")
        self._send_raw(f"JOIN #{self.channel}")
        self._connected = True
        log.info("Joined #%s", self.channel)

        buf = ""
        while not self._stop.is_set():
            try:
                chunk = sock.recv(4096)
            except socket.timeout:
                continue
            except OSError:
                break
            if not chunk:
                break
            buf += chunk.decode("utf-8", errors="ignore")
            while "\n" in buf:
                line, buf = buf.split("\n", 1)
                self._handle_line(line.rstrip("\r"))

        self._connected = False
        try:
            sock.close()
        except OSError:
            pass
        self._sock = None
        log.warning("IRC disconnected")

    def _handle_line(self, line: str) -> None:
        if not line:
            return
        if line.startswith("PING"):
            # PING :tmi.twitch.tv
            token = line.split(" ", 1)[1] if " " in line else ":tmi.twitch.tv"
            self._send_raw(f"PONG {token}")
            return

        # @tags :user!user@user.tmi.twitch.tv PRIVMSG #channel :message
        if "PRIVMSG" not in line:
            return

        user = ""
        msg = ""
        try:
            rest = line
            if rest.startswith("@"):
                rest = rest.split(" ", 1)[1]
            prefix, _, after = rest.partition(" ")
            if prefix.startswith(":"):
                user = prefix[1:].split("!", 1)[0].lower()
            if " :" in after:
                msg = after.split(" :", 1)[1]
            else:
                return
        except Exception:
            return

        if not user or msg is None:
            return
        if self.on_message:
            try:
                self.on_message(user, msg)
            except Exception:
                log.exception("on_message failed for %s", user)
