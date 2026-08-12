# Streaming Survival — автопроверка

Отдельный **стримовый survival mode**: зрители создают scripted follower-персонажей через `#join` и задают действия через `#do`.

Это **не** Training AI Forest Survival, **не** обучение и **не** ML-Agents.  
Training AI остаётся на своей вкладке и сюда не входит.

## После любых изменений Streaming Survival

### Parser only (можно без Unity)

```bash
python scripts/run_streaming_survival_checks.py --parser-only
```

В `summary.json`:
- `mode`: `parser_only`
- `parser_only_status`: PASS/FAIL
- `full_runtime_status`: `NOT_RUN`
- `overall` может быть PASS **только** в этом режиме (runtime не проверялся)

### Full runtime (нужен запущенный Streaming Survival)

```bash
python scripts/run_streaming_survival_checks.py --full-runtime
```

`overall=PASS` **только если**:
- `parser=PASS`
- `world_registry=PASS` (реальный JSON от Unity)
- `runtime_scenarios=PASS` (есть trajectory `*.jsonl` + checkers)
- `e2e=PASS` или `SKIP` (optional, если bot не запущен)

Если runtime недоступен при `--full-runtime` → `overall=FAIL`.

### Default (auto)

```bash
python scripts/run_streaming_survival_checks.py
```

- если Unity Streaming Survival отвечает на UDP ping → гоняет full runtime;
- иначе → `overall=INCOMPLETE` (не PASS).

## Что проверяет full runtime

Полный цикл scripted-персонажа:

`#do` → plan → WorldRegistry target → движение → trajectory → resource

Обязательные scenarios: water / wood / stone / water+campfire / chain / circle / patrol / idle / join idempotent / cooldown.

Checkers:
- `target_type` верный
- distance до target уменьшается
- target достигнут
- ресурс начислен (water не у спавна)
- initial direction (dot) не «ушёл от цели»

## Артефакты

`artifacts/streaming_survival/test_runs/<run_id>/`

- `summary.json` — с `parser_only_status` и `full_runtime_status`
- `world_registry_results.json`
- `scenario_results.json`
- `trajectories/<id>_trajectory.jsonl`
- `<id>_result.json`

Python передаёт Unity `run_id` + `output_dir` по UDP; Unity пишет сюда же  
(или в `STREAMING_SURVIVAL_TEST_ARTIFACTS_DIR`).

## UI Diagnostics

Вкладка **Streaming Survival** (не Train):

1. **Run Parser Checks** — parser-only  
2. **Run Full Runtime Checks** — требует запущенный Streaming Survival  
3. **Export Core Files Zip**

В статусе явно:
- Parser checks: PASS / FAIL  
- Runtime checks: NOT RUN / PASS / FAIL / INCOMPLETE  

## Export zip

```bash
python scripts/export_streaming_survival_core_zip.py
```

## Деплой lab_comp

- Sync DLL после Unity-правок: `train_scripts/lab_comp/sync_dll_only.bash`
- Sync bot: `sync_stream_bot_only.bash`
- Start: `start_streaming_survival.bash` + `llm_bot_lab.bash`

## Resource credit

Ресурс начисляется в `OnWorkDone` у валидного target (вода: `z ≥ ~22`).  
Checker падает, если water credit около спавна.
