# Stream Bot — Twitch → (LLM) → validator → Unity

Локальная LLM (Ollama) только предлагает JSON. Реальные действия идут через whitelist, cooldown и UDP в Unity.

## Установка

```bash
cd stream_bot
python -m venv .venv
# Windows: .venv\Scripts\activate
# Linux: source .venv/bin/activate
pip install -r requirements.txt
cp .env.example ../.env   # или stream_bot/.env — load_dotenv ищет корень проекта и cwd
```

Заполни `.env` в корне репозитория `forest_survival/`:

- `TWITCH_CHANNEL` — канал без `#`
- `TWITCH_BOT_NICK` + `TWITCH_OAUTH` — если бот должен писать в чат (oauth:token)
- без OAuth бот только читает (justinfan), ответы пишутся в лог как dry-run

## Ollama

1. Установи [Ollama](https://ollama.com)
2. `ollama pull qwen3:4b`
3. Запусти Ollama
4. `USE_LOCAL_LLM=true` (или `false` — тогда работают только /vote, /add_zombie и т.д.)

## Запуск

Из корня репозитория:

```bash
python -m stream_bot.main
```

Unity stream-билд должен слушать UDP `UNITY_PORT` (по умолчанию 5055) — компонент `StreamBotUdpReceiver`.

## Команды чата

| Команда | Действие |
|---------|----------|
| `/help` | список |
| `/profile` `/points` | очки |
| `/top` | топ |
| `/status` | poll + llm |
| `/vote 1` или `/1` | голос |
| `/add_zombie` `/food` `/night` `/chaos` `/reset` | через validator |
| `/suggest …` `/ask …` `/why` `@bot …` | LLM → JSON |

## Безопасность

- LLM никогда не шлёт команды в Unity напрямую
- в Unity уходит только то, что прошло `validator.py`
- `TWITCH_OAUTH` не логируется
- произвольный JSON из чата не принимается

## Формат UDP → Unity

`stream_command` / `poll_state` / `stream_event` — см. `unity_client.py`.
