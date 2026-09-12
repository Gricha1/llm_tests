# Forest Survival — архитектура lab / stream (handoff)

**Читай это перед любым запуском.** Неверный билд / RUN_ID / флаги Unity = «не та среда».

## Машины и репозиторий

| Где | Что |
|---|---|
| **Dev (Windows)** | `c:\Grisha\unity_projects\forest_survival` — Unity Editor 6000.0.26f1, правки C#, компиляция DLL |
| **Prod lab** | SSH host `lab_comp` → `~/lab_work_space/forest_survival` |
| Git | один репозиторий; на lab обычно синк скриптов + hotpatch DLL, полный rebuild редкий |

## Два параллельных мира (не путать)

```
┌─────────────────────────────┐     ┌──────────────────────────────────────┐
│ TRAIN (headless)            │     │ STREAM / OBS (presentation)          │
│ mlagents-learn              │     │ stream_onnx_infer.py + Unity window  │
│ много Env, без графики      │     │ DISPLAY=:1, OBS захватывает окно     │
│ порты ~5005+                │     │ порт ML-Agents ~7000                 │
│ НЕ трогать при фиксе стрима │     │ флаги: -forestStreamOnly             │
└─────────────────────────────┘     │         -forestExternalBrain         │
                                    └──────────────────────────────────────┘
```

- **Train** и **Stream** живут одновременно. Убивать стрим: только `stream_onnx_infer` / `forestStreamOnly`, **не** train.
- Стрим **не** использует `mlagents-learn`: Python гоняет onnx и шлёт actions в Unity.

## Текущий прод-стрим (актуально)

| Параметр | Значение |
|---|---|
| **BUILD** | `stream_forest_survival_2_12_07_2026` |
| Бинарь | `build_versions/stream_forest_survival_2_12_07_2026.x86_64` |
| Data | `build_versions/stream_forest_survival_2_12_07_2026_Data/` |
| **RUN_ID** | `jlg_finetune_2` |
| Веса | `results/jlg_finetune_2/{Jack,Lily,George}LowLevelAgent/*.onnx` |
| Лог стрима | `results/stream_onnx_jlg_finetune_2.log` |
| Unity Player.log | `~/.config/unity3d/DefaultCompany/forest_survival/Player.log` |
| Twitch bot | `python -m stream_bot.main` → HTTP `:8765`, UDP в Unity |
| Roster | SQLite `stream_bot.sqlite3` + `POST /roster/resync` |

### Запуск / рестарт стрима (правильно)

```bash
# на lab_comp:
cd ~/lab_work_space/forest_survival
RUN_ID=jlg_finetune_2 bash train_scripts/lab_comp/restart_stream_for_run.bash
```

Внутри: sticky onnx → kill только stream → `run_stream_onnx.bash` → после подъёма `POST :8765/roster/resync`.

**Не запускать** без `RUN_ID=jlg_finetune_2` (дефолт в скрипте — старый `run_80`).  
**Не подставлять** другой `BUILD` (дефолт правильный: `stream_forest_survival_2_12_07_2026`).

## Editor Play = стрим (sync)

В Unity Editor **Play** по умолчанию поднимает тот же режим, что lab stream:
`EnvRunMode.StreamOnly` (как `-forestStreamOnly`): одна presentation Env, followers, апокалипсис, HUDы.

В Console ищи: `[TrainingEnvSpace] EnvRunMode=StreamOnly ... editorPlay=streamMirror`.

| Нужно | Как |
|---|---|
| Как на стриме (дефолт) | просто Play |
| Старый multi-Env / train layout | `FOREST_EDITOR_TRAIN=1` или `-forestEditorTrain` |

Мозг героев в Editor без `stream_onnx` — Heuristic/локальный Inference (не Python External Brain); геймплей/мир совпадают со стримом.

## Что крутится в stream-режиме

Unity с `-forestStreamOnly -forestExternalBrain`:

1. **Jack / Lily / George** — ML-герои на presentation Env; мозг = onnx из Python.
2. **Streaming Survival followers** — зрители `#join` / `#do` (скины human / wolf / soldier).
3. **Зомби / овцы / деревья / фазы выживания** — на presentation (не full SS round).
4. **HUD**: HP бары, топ-5 по времени, Players in game, help `#skins`.

Режим `-forestStreamingSurvival` (полный SS без героев) — **не** текущий прод-стрим.

## Как мы билдим / деплоим геймплей

### Обычный путь (hotpatch) — 99% фиксов

1. Править `Assets/**/*.cs` локально.
2. Unity batchmode:  
   `-executeMethod CompileStreamingSurvivalDll.CompileAndCopyFromCommandLine`
3. Получить `Assembly-CSharp.dll` (маркер-строка в коде для проверки).
4. Скопировать в  
   `build_versions/stream_forest_survival_2_12_07_2026_Data/Managed/Assembly-CSharp.dll`
5. `scp` на lab в тот же путь.
6. **Проверить size/md5** (урезанный scp → Unity SIGABRT / чёрный экран).
7. `RUN_ID=jlg_finetune_2 bash train_scripts/lab_comp/restart_stream_for_run.bash`
8. `POST http://127.0.0.1:8765/roster/resync` (часто уже в restart-скрипте).

Полный Unity rebuild билда — редко; ассеты `sharedassets0.*` трогать только если Unity пишет `sharedassets0.assets is corrupted`.

### Deploy-скрипты

Лежат в `artifacts/deploy_*.ps1` (Windows → compile → scp → remote restart).

## Bot / Unity связь

```
Twitch chat → stream_bot (:8765)
                ├─ SQLite roster / stats
                ├─ UDP JSON → StreamCommandReceiver (Unity)
                └─ files: results/stream_roster.json, stream_time_leaderboard.json
```

Если на стриме «все персонажи пропали»: бот имеет roster, Unity пустая → `curl -X POST :8765/roster/resync`.  
Авто-bootstrap roster после рестарта — в `StreamingSurvivalController` (`ROSTER_BOOTSTRAP_ALWAYS_V1`).

## Частые ошибки другого агента

| Ошибка | Почему плохо |
|---|---|
| Запуск train / Editor Play вместо stream билда | Не то окно, OBS чёрный / другой мир |
| `RUN_ID=run_80` или другой finetune | Не те onnx / пустой стрим |
| `BUILD=stream_forest_survival_1_*` или jack_*_Data | Старый/train билд |
| `pkill -f unity` / kill train | Убьёт обучение |
| Hotpatch DLL без проверки размера | Truncated DLL → crash / чёрный экран |
| Restart без `roster/resync` | Фолловеры 0 при живом боте |
| Правки только локально без scp+restart | На стриме ничего не меняется |

## Быстрая диагностика стрима

```bash
ssh lab_comp
cd ~/lab_work_space/forest_survival
pgrep -af 'stream_forest|stream_onnx|stream_bot'
tail -50 results/stream_onnx_jlg_finetune_2.log
grep -E 'corrupted|fatal signal|SIGABRT' ~/.config/unity3d/DefaultCompany/forest_survival/Player.log | tail
curl -s http://127.0.0.1:8765/roster | python3 -m json.tool | head
# live world:
python3 -c "import json;d=json.load(open('results/jlg_finetune_2/stream_world_live.json',encoding='utf-8-sig'));print(d['jack'], len(d.get('followers',[])))"
```

## Карта ключевых путей

```
Assets/JackScript.cs, LilyScript.cs, GeorgeScript.cs
Assets/ZombieChase.cs, ZombieSpawner.cs, ZombieAttack.cs
Assets/Twitch/StreamingSurvival/*     # followers, HUD, controller
Assets/Twitch/StreamCommandReceiver.cs
Assets/TrainingEnvSpace.cs            # флаги режимов
Assets/Editor/CompileStreamingSurvivalDll.cs
train_scripts/lab_comp/run_stream_onnx.bash
train_scripts/lab_comp/restart_stream_for_run.bash
train_scripts/lab_comp/stream_onnx_infer.py
stream_bot/main.py
build_versions/stream_forest_survival_2_12_07_2026*
results/jlg_finetune_2/
```
