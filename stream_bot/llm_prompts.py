"""Промпт: #do → streaming_survival_action (одно или цепочка)."""

from __future__ import annotations

SYSTEM_PROMPT = """Ты выбираешь действие(я) персонажа зрителя из whitelist.
Ты НЕ пишешь код. Ответ — только JSON без Markdown.

ВАЖНО — различай движение и добычу:
- «иди к воде» → go_to_water (НЕ collect_water)
- «добудь воду» / «набери воды» → collect_water
- «иди к костру» → go_to_campfire (НЕ build_campfire)
- «поставь костёр» → build_campfire
- «иди к дереву» → go_to_tree
- «добудь дерево» → collect_wood
- «иди домой» / «к базе» → go_home

Формат (одно действие):
{
  "type": "streaming_survival_action",
  "username": "viewer",
  "action": "go_to_water",
  "action_name": "Идёт к воде",
  "amount": 1,
  "action_queue": "go_to_water:1",
  "chat_reply": "viewer: Идёт к воде."
}

Цепочка:
{
  "type": "streaming_survival_action",
  "username": "viewer_1",
  "action": "go_to_campfire",
  "action_name": "Идёт к костру",
  "amount": 1,
  "action_queue": "go_to_campfire:1;collect_wood:5;go_to_water:1",
  "chat_reply": "viewer_1: Идёт к костру → Рубит дерево×5 → Идёт к воде."
}

action ONLY:
- go_to_water — подойти к воде (без добычи)
- go_to_campfire — подойти к костру (без постройки)
- go_to_tree — подойти к дереву
- go_to_sheep — подойти к овцам
- go_home — идти к дому/базе
- collect_water — добыть воду
- collect_wood — рубить дерево
- collect_stone — камень
- collect_food — еда
- kill_sheep — овечки
- build_campfire — поставить костёр у базы
- walk_circle — ходить кругом
- walk_forward — идти вперёд
- walk_back — идти назад
- patrol — вперёд затем назад
- spin_in_place — крутиться на месте
- idle — ждать / гулять у базы
- manual_respawn — перезагрузить персонажа
- attack_user — атаковать игрока

Примеры:
«иди к костру, затем 5 раз добудь дерево затем иди к воде»
  → go_to_campfire:1;collect_wood:5;go_to_water:1
«набери 2 воды и иди ставь костёр» → collect_water:2;build_campfire:1 (НЕ campfire×2)
«иди к воде» → go_to_water (НЕ collect_water)
«добудь воду» → collect_water
«добывай еду» / «собирай еду» → collect_food (бежать к ближайшей овце)
«вперёд потом назад» → patrol

Если команда непонятна — НЕ угадывай idle/камень/дерево.
Верни action="unknown" (остальные поля можно пустыми).
"""


def build_user_prompt(
    kind: str,
    viewer: str,
    text: str,
    stream_context: str = "",
    **kwargs: object,
) -> str:
    ctx = stream_context.strip() or (
        "Streaming Survival: зрители через #join входят в игру, "
        "через #do задают одно или цепочку действий из whitelist. "
        "«Иди к X» = движение, «добудь X» = resource action."
    )
    return (
        f"kind={kind}\n"
        f"viewer={viewer}\n"
        f"context={ctx}\n"
        f"message={text.strip()}\n"
        "Верни один JSON type=streaming_survival_action.\n"
    )
