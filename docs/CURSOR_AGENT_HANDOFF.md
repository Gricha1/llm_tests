# Cursor Agent Handoff — Forest Survival (Aug 2026)

Handoff для следующего агента: архитектура, режимы, последние фиксы, lab-процессы.

---

## 1. Проект в двух словах

Unity-сцена **ForestScene** (`Assets/ForestScene.unity`) — одна сцена, несколько `Env` (presentation + train copies). Режим выбирается **CLI-флагами** и env (`TrainingEnvSpace.cs`), не отдельными сценами.

Две большие части (логически разделены, но код общий):

| Часть | Назначение |
|-------|------------|
| **Training AI** | ML-Agents PPO: Jack, Lily, George (Gera) |
| **Streaming Survival + Twitch** | `#join` / `#do` → scripted фолловеры, ресурсы, стрим |

**Правило:** не ломать ML-Agents / train scripts без явной задачи. SS-изменения — минимальный diff, проверять trajectories.

---

## 2. Три runtime-режима

| Режим | Unity flags | Процесс на lab |
|-------|-------------|----------------|
| **Train headless** | `-forestSingleEnvByPort -forestTrainAllHeadless -forestPresentationWorker0` | `mlagents-learn` + `launch_forest_env.bash` |
| **Stream inference** | `-forestStreamOnly` (+ onnx) | `run_stream_onnx.bash` |
| **Streaming Survival test** | `-forestStreamingSurvival -forestStreamOnly` | `start_streaming_survival.bash` |

Центральный switchboard: `Assets/TrainingEnvSpace.cs`

Ключевые свойства:

```csharp
IsStreamingSurvivalMode      // -forestStreamingSurvival — полный SS (раунды, HUD, герои скрыты)
IsStreamOnlyMode             // -forestStreamOnly
IsLivePresentationForObs     // stream / train presentation worker
UseUnifiedFollowers          // IsStreamingSurvivalMode || IsLivePresentationForObs
ShouldRunPresentationOnlyServices()  // UDP bot, Twitch — НЕ на headless train workers 1–20
```

### Unified followers (сделано недавно)

**Один код `#join` / `#do` для SS-теста и для stream/train presentation:**

- `-forestStreamingSurvival` → **полный SS**: `StreamingSurvivalController` скрывает Jack/Lily/George, раунды Water/Wood/Food/Heat, HUD.
- `-forestStreamOnly` (без `-forestStreamingSurvival`) → **followers-only**: те же Jack-like персонажи + `StreamingSurvivalPlayer`, **ML-герои остаются активными** (обучение/ONNX стрим).

Файлы:
- `StreamingSurvivalController.cs` — `FollowersOnlyMode`, `EnsureInstance()`
- `StreamCommandReceiver.cs` — `TryGetFollowerController()` → SS вместо `ViewerSimpleAgent` (овечки legacy)

---

## 3. Поток Twitch → Unity

```
Twitch #join / #do / #exit
  → stream_bot/main.py (heuristic → optional LLM → validator)
  → UDP JSON :5055 (FOREST_STREAM_BOT_PORT)
  → StreamCommandReceiver.cs
  → StreamingSurvivalController (unified) ИЛИ ViewerSimpleAgent (legacy, если UseUnifiedFollowers=false)
  → StreamingSurvivalPlayer (scripted movement, ResourceGuard)
```

**Bot:** `stream_bot/`
- `command_parser.py` — `#join`, `#do`, `#exit`
- `validator.py` — whitelist actions, `_omit_single_farm_queue` (bare `#do добывай воду` → пустой `action_queue`, infinite farm)
- `main.py` — `_twitch_say` по умолчанию **молчит**; **исключение:** непонятный `#do` → `UNKNOWN_DO_HINT` в чат (`to_twitch=True`)
- `llm_prompts.py` — при непонятной команде LLM должна вернуть `action="unknown"`

**Unity ingress:** `Assets/Twitch/StreamCommandReceiver.cs`

**Follower spawn (SS):**
- Центр: `FlowerSpawner.GetAreaCenterWorld()`
- Кольцо: `StreamingSurvivalController.NextFollowerSpawnPos(slot)`
- Тело: клон Jack visual, `StripTrainingComponents`, `StreamingSurvivalPlayer`

---

## 4. Whitelist actions (#do)

Canonical (bot → Unity JSON `action`):

| action | RU примеры |
|--------|------------|
| `collect_water` | добывай воду |
| `collect_wood` | руби дерево |
| `collect_food` | добывай еду, собирай еду |
| `kill_sheep` | убивай овечек |
| `build_campfire` | костёр, добывай тепло, зажги котсёр |
| `go_to_*` | иди к воде/дереву/овцам/дому |
| `idle` | жди, гуляй у базы |
| `manual_respawn` | перезагрузи персонажа |

**Infinite farm:** bare `#do` с amount≤1 и `KEEP_FARM_ACTIONS` → Unity **не** ставит `_startedWithQueue` (фикс «1 вода и стоп»).

---

## 5. Unity SS core files

| File | Role |
|------|------|
| `StreamingSurvivalController.cs` | Раунды, spawn, ApplyAction, ресурсы |
| `StreamingSurvivalPlayer.cs` | FSM: GoTarget, Work, water route, fence gap, stuck |
| `StreamingSurvivalWorldRegistry.cs` | water/tree/sheep/stone/home — **без ghost sheep** |
| `StreamingSurvivalResourceGuard.cs` | credit только у цели |
| `StreamingSurvivalHud.cs` | top counts; bottom bar = collected/40 |
| `SheepSpawner.cs` | `TryGetNearestAliveSheep`, `TargetCount` |
| `TreeSpawner.cs` | `TryGetNearestAliveTree` |
| `StreamingSurvivalTrajectoryRecorder.cs` | jsonl для QA |

### World registry — sheep (важный фикс)

**Было:** `RegisterSheep()` создавал ghost `sheep_0` у дома → `#do добывай еду` → бить камень.

**Стало:**
- Registry: только `SheepWander` в presentation
- `GetNearestSheep` / `FindSheep` → live `SheepSpawner` + `IsLiveHarvestSheep()` (не `SheepSpawner` GO, не viewer agents)
- `GoTarget` для food/sheep: **каждый кадр** пересчёт ближайшей овцы (wander)
- `TryBeginWorkAtResource` / `OnWorkDone`: punch/credit только у live sheep

### Fence / wood stuck (фиксы)

- `NeedsFenceGapReturn`: не гонять west yard через planks
- `FenceGapReturnWaypoint`: south gap first
- `TreeSpawner.TryGetNearestAliveTree`: prefer same side of fence
- `SteerAroundObstacles` для камней

### Campfire / water

- Campfire: stand у south door дома, collider fire off
- Water: только GoalWater3 (z≥28), credit z≥24.5
- `collect_water` route через gap, не через стены

---

## 6. Training AI (Jack / Lily / George)

**George = Gera** в коде (`GeorgeScript.cs`, `GeorgeLowLevelAgent`).

| Hero | Script | Behavior name |
|------|--------|---------------|
| Jack | `JackScript.cs` | `JackLowLevelAgent` |
| Lily | `LilyScript.cs` | `LilyLowLevelAgent` |
| George | `GeorgeScript.cs` | `GeorgeLowLevelAgent` |

YAML: `custom_configs/Jack_Lily_George.yaml` (joint), `Jack_single_agent.yaml`, etc.

**Launch train (lab):**
```bash
RUN_ID=run_85 bash train_scripts/lab_comp/run_train.bash
# → train_headless_jack_lily_george.bash
# 21 envs, presentation mode, all headless, worker0=presentation slot
```

**Stream отдельно (OBS + #join unified followers):**
```bash
RUN_ID=run_85 bash train_scripts/lab_comp/run_stream_onnx.bash
```

**SS test (без ML, полный раунд):**
```bash
bash train_scripts/lab_comp/start_streaming_survival.bash
# Unity: -forestStreamingSurvival -forestStreamOnly
```

---

## 7. Lab paths & deploy

| What | Path |
|------|------|
| Repo on lab | `~/lab_work_space/forest_survival` |
| Linux binary | `build_versions/stream_forest_survival_2_12_07_2026.x86_64` |
| Managed DLL | `build_versions/stream_forest_survival_2_12_07_2026_Data/Managed/Assembly-CSharp.dll` |
| SS log | `results/streaming_survival.log` |
| Bot log | `results/stream_bot.log` |
| Train log | `results/train_run_<RUN_ID>.log` |
| Bot HTTP | `http://127.0.0.1:8765` |

**Windows compile DLL (надёжнее full build):**
```powershell
Unity.exe -batchmode -quit -nographics -projectPath <proj> `
  -executeMethod CompileStreamingSurvivalDll.CompileAndCopyFromCommandLine `
  -buildOutputName stream_forest_survival_2_12_07_2026 `
  -logFile build_versions/compile.log
```
Если `[CompileSS] OK` нет — копировать `Library/ScriptAssemblies/Assembly-CSharp.dll` → `build_versions/.../Managed/`.

**Deploy DLL:**
```bash
scp Assembly-CSharp.dll lab_comp:~/lab_work_space/forest_survival/build_versions/stream_forest_survival_2_12_07_2026_Data/Managed/
```

**Restart SS (keep OBS/bot when possible):**
```bash
bash train_scripts/lab_comp/start_streaming_survival.bash
```
После рестарта зрители **#join** заново.

**Bot restart:** `.tmp_enable_twitch_bot.py` на lab (Helix часто не настроен → #join без Follow check).

---

## 8. Chat policy (bot)

- Успешный `#join` / `#do` / `#exit` → **тишина** в Twitch (только UDP в Unity)
- Непонятный `#do` → `@user Не понял команду. Попробуй: #do добывай воду · ...` (`UNKNOWN_DO_HINT`)
- LLM fallback: `_llm_did_not_understand()` в `main.py`

---

## 9. Movement / QA rules (не ломать)

- Normal gameplay: **no teleport** (только `join_spawn`, `manual_respawn`, scenario setup)
- Stuck → re-path / nudge / idle hint «#do перезагрузи персонажа»
- ResourceGuard: credit только в radius у live target
- Full check: `python scripts/run_streaming_survival_checks.py --full-runtime`

Старый шаблон: `docs/CURSOR_STREAMING_SURVIVAL_CONTEXT_TEMPLATE.md`

---

## 10. Состояние на Aug 18, 2026 (~02:05 MSK)

### Сделано в сессии

1. **Sheep/food fix** — не бить камни, бежать к live sheep (`SheepSpawner`, registry, player)
2. **Unknown #do** — hint в Twitch chat
3. **Unified followers** — stream/train presentation = те же персонажи что SS-тест
4. **Train trio** — `run_85` на lab (`Jack_Lily_George.yaml`, mlagents-learn running)
5. **DLL** залита на lab (~02:02 compile)

### Известные ограничения

- На lab мог одновременно крутиться **старый SS-процесс** (`-forestStreamingSurvival`) — не мешает train, но для проверки unified нужен `run_stream_onnx` (`-forestStreamOnly` only)
- Unity `CompileStreamingSurvivalDll` иногда не копирует DLL после domain reload — проверять timestamp Managed vs ScriptAssemblies
- Windows full `BuildStreamLinux` долгий; UPM иногда падает — prefer CompileSS + manual copy
- `PresentationWorldReset` использует `SheepSpawner.TargetCount` (добавлено)

### Возможные next steps

- Перезапустить stream с `run_stream_onnx` + bot, проверить `#join` / `#do добывай еду` / unknown command
- Full-runtime checks после sheep/unified changes
- Обновить `stream_bot/README.md` (там ещё «Unity только -forestStreamingSurvival»)

---

## 11. Быстрая карта файлов

```
Assets/TrainingEnvSpace.cs
Assets/Twitch/StreamCommandReceiver.cs
Assets/Twitch/StreamingSurvival/StreamingSurvivalController.cs
Assets/Twitch/StreamingSurvival/StreamingSurvivalPlayer.cs
Assets/Twitch/StreamingSurvival/StreamingSurvivalWorldRegistry.cs
Assets/Twitch/ViewerSimpleAgent.cs          # legacy sheep, если !UseUnifiedFollowers
Assets/SheepSpawner.cs
Assets/Scripts/TreeSpawner.cs
Assets/JackScript.cs  Assets/LilyScript.cs  Assets/GeorgeScript.cs
Assets/Editor/CompileStreamingSurvivalDll.cs
Assets/Editor/BuildStreamLinux.cs
stream_bot/main.py  command_parser.py  validator.py  llm_prompts.py
train_scripts/lab_comp/run_train.bash
train_scripts/lab_comp/run_stream_onnx.bash
train_scripts/lab_comp/start_streaming_survival.bash
train_headless_jack_lily_george.bash
custom_configs/Jack_Lily_George.yaml
```

---

## 12. User preferences

- Ответы **коротко по-русски**
- Не `git config`, не commit без запроса
- Не silent-Destroy деревьев без wood credit
- Verify against code, не «готово» без evidence на movement/resource changes
- Lab: compile on Windows → scp DLL → restart SS; OBS/bot по возможности не трогать
