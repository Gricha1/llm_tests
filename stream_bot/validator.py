"""Whitelist: streaming_survival_action — одно или цепочка действий."""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Tuple

# Canonical Unity actions. Aliases normalize on validate.
ALLOWED_ACTIONS = frozenset(
    {
        "collect_water",
        "collect_wood",
        "collect_stone",
        "collect_food",
        "kill_sheep",
        "build_campfire",
        "go_home",
        "go_to_base",
        "go_to_water",
        "go_to_tree",
        "go_to_campfire",
        "go_to_sheep",
        "manual_respawn",
        "respawn_character",
        "walk_circle",
        "circle",
        "walk_forward",
        "walk_back",
        "patrol",
        "spin_in_place",
        "idle",
        "attack_user",
    }
)

ACTION_ALIASES = {
    "circle": "walk_circle",
    "walk_circle": "walk_circle",
    "go_to_base": "go_home",
    "respawn_character": "manual_respawn",
    "respawn": "manual_respawn",
}

# Bare #do (no count, one step): Unity farms until the next chat command.
# A 1-step queue like "collect_wood:1" would stop after one chop.
KEEP_FARM_ACTIONS = frozenset(
    {
        "collect_water",
        "collect_wood",
        "collect_food",
        "kill_sheep",
        "build_campfire",
    }
)

ACTION_NAMES_RU = {
    "collect_water": "Добывает воду",
    "collect_wood": "Рубит дерево",
    "collect_stone": "Добывает камень",
    "collect_food": "Собирает еду",
    "kill_sheep": "Убивает овечек",
    "build_campfire": "Ставит костёр",
    "go_home": "Идёт к дому",
    "go_to_base": "Идёт к базе",
    "go_to_water": "Идёт к воде",
    "go_to_tree": "Идёт к дереву",
    "go_to_campfire": "Идёт к костру",
    "go_to_sheep": "Идёт к овцам",
    "manual_respawn": "Перезагрузка",
    "respawn_character": "Перезагрузка",
    "walk_circle": "Ходит кругом",
    "circle": "Ходит кругом",
    "walk_forward": "Идёт вперёд",
    "walk_back": "Идёт назад",
    "patrol": "Патрулирует",
    "spin_in_place": "Крутится",
    "idle": "Ждёт у базы",
    "attack_user": "Атакует игрока",
}

FALLBACK_REPLY = "Не понял точно, персонаж будет ждать у базы."

_CANONICAL = frozenset(
    {
        "collect_water",
        "collect_wood",
        "collect_stone",
        "collect_food",
        "kill_sheep",
        "build_campfire",
        "go_home",
        "go_to_water",
        "go_to_tree",
        "go_to_campfire",
        "go_to_sheep",
        "manual_respawn",
        "walk_circle",
        "walk_forward",
        "walk_back",
        "patrol",
        "spin_in_place",
        "idle",
        "attack_user",
    }
)

# Sequence connectors. Comma / semicolon / then / затем / потом / …
_SPLIT_SEQ = re.compile(
    r"\s*(?:"
    r"затем|потом|после\s+этого|и\s+потом|и\s+затем|а\s+потом|"
    r"then|and\s+then|"
    r";|,|\n"
    r")\s*"
    r"|\s+и\s+(?=(?:иди|подойди|беги|ставь|поставь|собери|собира|"
    r"добудь|добыв|набер|руби|убей|ходи|пойди|развед|верн))",
    re.IGNORECASE,
)

# Count: "5 раз", "2 штуки", "×5", bare digits near resource phrase.
_AMOUNT_RE = re.compile(
    r"(\d+)\s*(?:раз(?:а|ов)?|шт(?:ук[аи]?)?\.?|x|×)?",
    re.IGNORECASE,
)

_HARVEST = re.compile(
    r"добыв|набер|собери|собира|руби|пилит|убей|убива|зареж|принеси|принес",
    re.IGNORECASE,
)
_APPROACH = re.compile(
    r"(?:иди|подойди|подойти|беги|бегом|пойди|сходи|идите|шагай|двинься|"
    r"go\s+to|move\s+to|walk\s+to|run\s+to)\s*(?:к|ко|до)?|"
    r"к\s+",
    re.IGNORECASE,
)
_BUILD_CAMP = re.compile(
    r"поставь|ставь|развед|построить|сделай|зажг|зажеч|зажиг|build",
    re.IGNORECASE,
)


@dataclass
class ActionValidationResult:
    ok: bool
    reason: str
    payload: Optional[Dict[str, Any]] = None


def normalize_action(action: str) -> str:
    a = (action or "").strip().lower()
    return ACTION_ALIASES.get(a, a)


def plan_from_payload(payload: Dict[str, Any]) -> List[Dict[str, Any]]:
    """Extract [{action, count}, ...] from validated streaming_survival_action/plan."""
    if not payload:
        return []
    # Prefer explicit steps[] (streaming_survival_plan)
    steps_raw = payload.get("steps")
    if isinstance(steps_raw, list) and steps_raw:
        out: List[Dict[str, Any]] = []
        for step in steps_raw:
            if not isinstance(step, dict):
                continue
            a = normalize_action(str(step.get("action") or ""))
            try:
                n = max(1, min(int(step.get("count") or step.get("amount") or 1), 50))
            except (TypeError, ValueError):
                n = 1
            if a:
                out.append({"action": a, "count": n})
        if out:
            return out
    queue = str(payload.get("action_queue") or "").strip()
    steps: List[Dict[str, Any]] = []
    if queue:
        for part in queue.split(";"):
            part = part.strip()
            if not part:
                continue
            bits = part.split(":")
            a = normalize_action(bits[0].strip().lower())
            n = 1
            if len(bits) > 1:
                try:
                    n = max(1, min(int(bits[1]), 50))
                except ValueError:
                    n = 1
            steps.append({"action": a, "count": n})
        return steps
    a = normalize_action(str(payload.get("action") or "idle"))
    try:
        n = max(1, min(int(payload.get("amount") or 1), 50))
    except (TypeError, ValueError):
        n = 1
    return [{"action": a, "count": n}]


def normalize_expected_plan(plan: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    out: List[Dict[str, Any]] = []
    for step in plan or []:
        a = normalize_action(str(step.get("action") or ""))
        try:
            n = int(step.get("count") or step.get("amount") or 1)
        except (TypeError, ValueError):
            n = 1
        out.append({"action": a, "count": max(1, min(n, 50))})
    return out


def fallback_action(username: str) -> Dict[str, Any]:
    return {
        "type": "streaming_survival_action",
        "username": username or "viewer",
        "action": "idle",
        "action_name": ACTION_NAMES_RU["idle"],
        "amount": 1,
        "action_queue": "",
        "chat_reply": FALLBACK_REPLY,
    }


def _has_campfire(t: str) -> bool:
    # костёр / костер / котсёр (typo) / campfire / огонь / тепло
    return bool(
        re.search(
            r"костр[ауеыо]?|кост[её]р|котс[её]р|каст[её]р|campfire|огонь|тепл|heat",
            t,
            re.IGNORECASE,
        )
    )


def _has_water(t: str) -> bool:
    return bool(re.search(r"вод[аеуыо]?|water|пруд|озеро", t, re.IGNORECASE))


def _has_wood(t: str) -> bool:
    return bool(re.search(r"дерев|лес|дров|wood|бревн", t, re.IGNORECASE))


def _has_sheep(t: str) -> bool:
    return bool(re.search(r"овеч|овц|sheep|мяс", t, re.IGNORECASE))


def _has_home(t: str) -> bool:
    return bool(
        re.search(
            r"дом[ауы]?|баз[аеуы]?|home|spawn|верн.*баз|к\s*дому|домой",
            t,
            re.IGNORECASE,
        )
    )


def _detect_action(t: str) -> Optional[str]:
    """Map one clause to a canonical action. Movement vs resource is distinguished."""
    if not t or not t.strip():
        return None
    t = t.strip().lower()

    if re.search(
        r"перезагруз|респавн|respawn|reload\s*(?:character|персонаж)|"
        r"сброс\s*персонаж|перезапусти\s*персонаж",
        t,
    ):
        return "manual_respawn"

    if re.search(r"атакуй|атаковать|attack\s*user|бей\s+\w+", t):
        return "attack_user"

    if re.search(r"patrol|патрул|впер[её]д.*назад|назад.*впер[её]д|forward.*back", t):
        return "patrol"

    # Whole-clause circle / idle before resource heuristics
    if re.search(r"кружи|кружит", t) and not re.search(
        r"ходи|ходить|walk_circle|\bcircle\b", t
    ):
        return "idle"
    if re.search(
        r"walk_circle|\bcircle\b|ходи\s*(?:по\s*)?круг|ходить\s*(?:по\s*)?круг|кругом|по\s*кругу",
        t,
    ):
        return "walk_circle"
    if re.search(r"walk_forward|вперёд|вперед|иди\s*прямо", t) and not (
        _has_water(t) or _has_wood(t) or _has_campfire(t) or _has_home(t) or _has_sheep(t)
    ):
        return "walk_forward"
    if re.search(r"walk_back|назад|иди\s*назад", t) and not (
        _has_water(t) or _has_wood(t) or _has_campfire(t) or _has_home(t)
    ):
        return "walk_back"
    if re.search(r"spin_in_place|крутись|крутиться\s*на\s*месте", t):
        return "spin_in_place"
    if re.search(r"ничего|стой|жди|idle|бездейств", t):
        return "idle"
    if re.search(
        r"кружи|кружит|гуля|броди|бродит|прогул|wand|walk\s*around|ход[ия]\s*вокруг|танц|верти",
        t,
    ) and not (
        _has_water(t) or _has_wood(t) or _has_campfire(t) or _has_home(t) or _has_sheep(t)
    ):
        return "idle"

    harvest = bool(_HARVEST.search(t))
    approach = bool(_APPROACH.search(t)) or bool(
        re.search(r"^(?:к|ко|до)\s+\S+", t)
    )
    build = bool(_BUILD_CAMP.search(t))

    # Campfire: build vs go_to
    if _has_campfire(t):
        if build or re.search(r"ставь|поставь|развед", t):
            return "build_campfire"
        if approach or re.search(r"к\s*костр", t):
            return "go_to_campfire"
        # bare "костёр" without approach → build (legacy)
        return "build_campfire"

    # Home / base
    if _has_home(t) and not harvest:
        # «броди/гуляй у базы» = idle, not go_home
        if re.search(r"гуля|броди|бродит|прогул|кружи|wand|walk\s*around", t) and not re.search(
            r"иди|подойди|верн|go\s*home|к\s*дому|домой", t
        ):
            return "idle"
        return "go_home"

    # Sheep
    if _has_sheep(t):
        if harvest or re.search(r"убива|убей|зареж|kill", t):
            return "kill_sheep"
        if approach:
            return "go_to_sheep"
        return "kill_sheep"

    # Stone
    if re.search(r"камен|камн|stone|скал|валун", t):
        return "collect_stone"

    # Food (not water)
    if re.search(r"ед[ауы]|food|пищ|ягод|фрукт", t) and not _has_water(t):
        return "collect_food"

    # Water: go_to vs collect
    if _has_water(t):
        if harvest or re.search(r"добыв.*вод|набер.*вод|собери.*вод|принес.*вод", t):
            return "collect_water"
        if approach or re.search(r"к\s*вод", t):
            return "go_to_water"
        # bare "вода" without approach → collect (legacy #do добывай воду handled by harvest)
        return "collect_water"

    # Wood / tree
    if _has_wood(t):
        if harvest or re.search(r"руби|пилит|добыв|собери", t):
            return "collect_wood"
        if approach or re.search(r"к\s*дерев", t):
            return "go_to_tree"
        return "collect_wood"

    # Explicit English action ids
    for a in (
        "go_to_campfire",
        "go_to_water",
        "go_to_tree",
        "go_to_sheep",
        "go_to_base",
        "go_home",
        "collect_water",
        "collect_wood",
        "build_campfire",
    ):
        if a in t:
            return normalize_action(a)

    return None


def _omit_single_farm_queue(queue: str) -> str:
    parts = [p for p in (queue or "").split(";") if p.strip()]
    if len(parts) != 1:
        return queue or ""
    bits = parts[0].split(":")
    a = normalize_action(bits[0].strip().lower())
    n = 1
    if len(bits) > 1:
        try:
            n = int(bits[1])
        except ValueError:
            n = 1
    if a in KEEP_FARM_ACTIONS and n <= 1:
        return ""
    return queue or ""


def _amount_in(t: str, default: int = 1) -> int:
    m = _AMOUNT_RE.search(t)
    if not m:
        return default
    try:
        n = int(m.group(1))
        return max(1, min(n, 50))
    except ValueError:
        return default


def _pack_steps(
    username: str, steps: List[Tuple[str, int]], *, wander: bool = False
) -> Dict[str, Any]:
    u = username or "viewer"
    norm_steps = [(normalize_action(a), n) for a, n in steps]
    action, amount = norm_steps[0]
    name = ACTION_NAMES_RU.get(action, action)
    if wander and action == "idle":
        name = "Гуляет у базы"
    if amount > 1:
        name = f"{name} (0/{amount})"
    full_queue = ";".join(f"{a}:{n}" for a, n in norm_steps)
    labels = []
    for a, n in norm_steps:
        lab = ACTION_NAMES_RU.get(a, a)
        labels.append(f"{lab}×{n}" if n > 1 else lab)
    reply = f"{u}: " + " → ".join(labels) + "."
    plan_steps = [{"action": a, "count": n} for a, n in norm_steps]
    return {
        "type": "streaming_survival_action",
        "username": u,
        "action": action,
        "action_name": name,
        "amount": amount,
        "action_queue": full_queue,
        "steps": plan_steps,
        "plan_name": " → ".join(labels),
        "loop": False,
        "chat_reply": reply,
    }


def heuristic_action(text: str, username: str) -> Optional[Dict[str, Any]]:
    t = (text or "").strip().lower()
    if not t:
        return None
    u = username or "viewer"

    # Whole-phrase patrol before splitting
    if re.search(
        r"впер[её]д.*назад|назад.*впер[её]д|шел\s*вперед.*назад|шёл\s*вперед.*назад|"
        r"шел\s*вперёд.*назад|шёл\s*вперёд.*назад",
        t,
    ):
        return _pack_steps(u, [("patrol", 1)])

    parts = [p.strip(" .") for p in _SPLIT_SEQ.split(t) if p and p.strip(" .")]
    if len(parts) >= 2:
        steps: List[Tuple[str, int]] = []
        for part in parts:
            act = _detect_action(part)
            if act is None:
                steps = []
                break
            steps.append((act, _amount_in(part, 1)))
        if len(steps) >= 2:
            return _pack_steps(u, steps)

    act = _detect_action(t)
    if act is None:
        return None
    amount = _amount_in(t, 1)
    wander = bool(
        re.search(
            r"круг|круж|гуля|броди|прогул|wand|walk|танц|верти|по\s*круг",
            t,
        )
    ) and act == "idle"
    return _pack_steps(u, [(act, amount)], wander=wander)


def validate_streaming_survival_action(
    data: Any, *, username: str = ""
) -> ActionValidationResult:
    if not isinstance(data, dict):
        return ActionValidationResult(False, "ожидался JSON-объект")
    rtype = str(data.get("type") or "").strip().lower()
    # Accept plan schema and normalize to action wire format.
    if rtype not in ("streaming_survival_action", "streaming_survival_plan"):
        return ActionValidationResult(
            False,
            f"type должен быть streaming_survival_action|streaming_survival_plan, получено: {rtype}",
        )

    # Expand steps[] into action_queue when present.
    if isinstance(data.get("steps"), list) and data["steps"]:
        cleaned_from_steps: List[str] = []
        for step in data["steps"]:
            if not isinstance(step, dict):
                continue
            a = normalize_action(str(step.get("action") or "").strip().lower())
            if a not in _CANONICAL:
                return ActionValidationResult(False, f"steps action не в whitelist: {a}")
            try:
                n = max(1, min(int(step.get("count") or step.get("amount") or 1), 50))
            except (TypeError, ValueError):
                n = 1
            cleaned_from_steps.append(f"{a}:{n}")
        if cleaned_from_steps:
            data = dict(data)
            data["action_queue"] = ";".join(cleaned_from_steps)
            first = cleaned_from_steps[0].split(":")
            data["action"] = first[0]
            data["amount"] = int(first[1]) if len(first) > 1 else 1

    action = normalize_action(str(data.get("action") or "").strip().lower())
    if action not in _CANONICAL:
        return ActionValidationResult(False, f"action не в whitelist: {action}")
    user = (username or str(data.get("username") or "viewer")).strip()[:64]
    name = str(data.get("action_name") or ACTION_NAMES_RU.get(action, action)).strip()[:80]
    if not name:
        name = ACTION_NAMES_RU.get(action, action)
    try:
        amount = int(data.get("amount") or 1)
    except (TypeError, ValueError):
        amount = 1
    amount = max(1, min(amount, 50))
    queue = str(data.get("action_queue") or "").strip()
    if queue:
        cleaned: List[str] = []
        for part in queue.split(";"):
            part = part.strip()
            if not part:
                continue
            bits = part.split(":")
            a = normalize_action(bits[0].strip().lower())
            if a not in _CANONICAL:
                return ActionValidationResult(False, f"queue action не в whitelist: {a}")
            n = 1
            if len(bits) > 1:
                try:
                    n = max(1, min(int(bits[1]), 50))
                except ValueError:
                    n = 1
            cleaned.append(f"{a}:{n}")
        queue = ";".join(cleaned)
        if cleaned:
            first = cleaned[0].split(":")
            action = first[0]
            amount = int(first[1]) if len(first) > 1 else 1
            name = str(data.get("action_name") or ACTION_NAMES_RU.get(action, action))[:80]
    queue = _omit_single_farm_queue(queue)
    reply = str(
        data.get("chat_reply") or f"{user} теперь: {name.lower()}"
    ).strip()[:250]
    plan_steps = plan_from_payload(
        {"action": action, "amount": amount, "action_queue": queue, "steps": data.get("steps")}
    )
    payload = {
        "type": "streaming_survival_action",
        "username": user,
        "action": action,
        "action_name": name,
        "amount": amount,
        "action_queue": queue,
        "steps": plan_steps,
        "plan_name": str(data.get("plan_name") or "")[:120],
        "loop": bool(data.get("loop", False)),
        "chat_reply": reply,
    }
    return ActionValidationResult(True, "ok", payload=payload)


# ── legacy stubs ────────────────────────────────────────────────────

@dataclass
class BehaviorValidationResult:
    ok: bool
    reason: str
    program: Optional[Dict[str, Any]] = None


@dataclass
class ValidationResult:
    ok: bool
    command: str
    value: float
    reason: str
    requires_vote: bool
    cooldown_left: float = 0.0


class CommandValidator:
    def __init__(self, *args: Any, **kwargs: Any) -> None:
        pass

    def validate(self, command: str, value: float, username: str, **kwargs: Any) -> ValidationResult:
        return ValidationResult(
            False, command or "", 0.0, "команды мира отключены", False
        )

    def mark_used(self, command: str, username: str) -> None:
        return

    def validate_poll_option(self, command: str, value: float) -> ValidationResult:
        return self.validate(command, value, "poll")


def validate_character_behavior(data: Any, **kwargs: Any) -> BehaviorValidationResult:
    return BehaviorValidationResult(False, "character_behavior заменён на streaming_survival_action")


def validate_behavior_program(data: Any, **kwargs: Any) -> BehaviorValidationResult:
    return validate_character_behavior(data, **kwargs)


def heuristic_character_behavior(text: str, username: str) -> Optional[Dict[str, Any]]:
    return heuristic_action(text, username)


def fallback_character_behavior(username: str) -> Dict[str, Any]:
    return fallback_action(username)
