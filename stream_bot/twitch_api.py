"""Twitch Helix: followers + chatters. Без креденшалов — безопасный no-op."""

from __future__ import annotations

import logging
import time
from dataclasses import dataclass
from typing import Any, Dict, List, Optional

import urllib.error
import urllib.parse
import urllib.request
import json

log = logging.getLogger("stream_bot.twitch_api")


@dataclass
class Chatter:
    user_id: str
    user_login: str
    user_name: str


class TwitchApiClient:
    def __init__(
        self,
        client_id: str,
        access_token: str,
        broadcaster_id: str,
        moderator_id: str = "",
    ) -> None:
        self.client_id = (client_id or "").strip()
        self.access_token = (access_token or "").strip()
        self.broadcaster_id = (broadcaster_id or "").strip()
        self.moderator_id = (moderator_id or broadcaster_id or "").strip()
        self._login_cache: Dict[str, tuple[Optional[str], float]] = {}
        self._login_ttl = 600.0

    @property
    def configured(self) -> bool:
        return bool(self.client_id and self.access_token and self.broadcaster_id)

    def _headers(self) -> Dict[str, str]:
        return {
            "Client-ID": self.client_id,
            "Authorization": f"Bearer {self.access_token}",
            "Accept": "application/json",
        }

    def _get(self, path: str, params: Dict[str, Any]) -> Optional[Dict[str, Any]]:
        if not self.configured:
            return None
        qs = urllib.parse.urlencode(params, doseq=True)
        url = f"https://api.twitch.tv/helix/{path.lstrip('/')}?{qs}"
        req = urllib.request.Request(url, headers=self._headers(), method="GET")
        try:
            with urllib.request.urlopen(req, timeout=8) as resp:
                raw = resp.read().decode("utf-8")
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as exc:
            body = ""
            try:
                body = exc.read().decode("utf-8", errors="replace")[:300]
            except Exception:
                pass
            log.warning("Twitch Helix HTTP %s %s: %s", exc.code, path, body)
            return None
        except Exception as exc:
            log.warning("Twitch Helix failed %s: %s", path, exc)
            return None

    def get_user_id_by_login(self, login: str) -> Optional[str]:
        key = (login or "").strip().lower()
        if not key:
            return None
        now = time.time()
        cached = self._login_cache.get(key)
        if cached and now - cached[1] < self._login_ttl:
            return cached[0]
        data = self._get("users", {"login": key})
        uid: Optional[str] = None
        if data and isinstance(data.get("data"), list) and data["data"]:
            uid = str(data["data"][0].get("id") or "") or None
        self._login_cache[key] = (uid, now)
        return uid

    def is_follower(self, user_id: str) -> bool:
        """Get Channel Followers: data non-empty ⇒ follower."""
        uid = (user_id or "").strip()
        if not uid or not self.configured:
            return False
        data = self._get(
            "channels/followers",
            {
                "broadcaster_id": self.broadcaster_id,
                "user_id": uid,
                "first": 1,
            },
        )
        if data is None:
            raise RuntimeError("Twitch followers API unavailable")
        rows = data.get("data") if isinstance(data, dict) else None
        if not isinstance(rows, list):
            return False
        for row in rows:
            if str(row.get("user_id") or "") == uid:
                return True
        return len(rows) > 0

    def get_chatters(self) -> List[Chatter]:
        if not self.configured:
            return []
        mod = self.moderator_id or self.broadcaster_id
        out: List[Chatter] = []
        cursor: Optional[str] = None
        for _ in range(20):
            params: Dict[str, Any] = {
                "broadcaster_id": self.broadcaster_id,
                "moderator_id": mod,
                "first": 1000,
            }
            if cursor:
                params["after"] = cursor
            data = self._get("chat/chatters", params)
            if data is None:
                break
            rows = data.get("data") if isinstance(data, dict) else None
            if isinstance(rows, list):
                for row in rows:
                    out.append(
                        Chatter(
                            user_id=str(row.get("user_id") or ""),
                            user_login=str(row.get("user_login") or ""),
                            user_name=str(row.get("user_name") or ""),
                        )
                    )
            pag = data.get("pagination") if isinstance(data, dict) else None
            cursor = str((pag or {}).get("cursor") or "") or None
            if not cursor:
                break
        return out
