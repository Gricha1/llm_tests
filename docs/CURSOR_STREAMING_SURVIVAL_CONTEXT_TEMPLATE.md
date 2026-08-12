==================================================
CURSOR CONTEXT TEMPLATE — FOREST SURVIVAL PROJECT
==================================================

# Project context

Проект: Unity / Python / localhost dashboard для стримового survival-эксперимента.

Есть две независимые части:

1. Training AI Forest Survival
2. Streaming Survival

Они должны быть строго разделены.

==================================================
1. Training AI Forest Survival
==================================================

Это старая вкладка/режим с обучением AI.

Там есть:
- текущая среда обучения;
- ML-Agents;
- training scripts;
- validation scripts;
- sensors/lidar;
- текущие графики;
- текущий pipeline обучения/валидации.

ВАЖНО:
Эту часть НЕ трогать, если задача явно не про Training AI.

Нельзя случайно ломать:
- ML-Agents;
- обучение;
- текущую сцену;
- старые validation scripts;
- текущие launch scripts;
- старую вкладку UI.

==================================================
2. Streaming Survival
==================================================

Это отдельная вкладка в localhost dashboard.

Цель Streaming Survival:
сделать простой Twitch/local survival-режим без обучения, где зрители создают своих scripted-персонажей.

Это НЕ AI-обучение.
Это НЕ ML-Agents.
Это НЕ inference.
Это НЕ RL.

Это отдельная стримовая сцена:

- зритель пишет #join;
- появляется персонаж Jack / humanoid placeholder с никнеймом;
- зритель пишет #do <текст>;
- bot/parser/LLM превращает текст в безопасный action plan;
- персонаж выполняет scripted-поведение;
- все персонажи вместе собирают общие ресурсы;
- цель раунда — собрать Water/Wood/Food/Campfire до конца таймера.

Главная пользовательская цель:
сделать понятный survival-стрим, где фолловеры заходят, добавляют персонажа, задают ему действия и вместе пытаются пройти раунд.

==================================================
3. Current Streaming Survival commands
==================================================

Публичные команды должны быть минимальными:

#join
#do <описание действия>

Примеры:

#join
#do добывай воду
#do добывай дерево
#do убивай овечек
#do поставь костер
#do ходи по кругу
#do иди домой
#do перезагрузи персонажа
#do атакуй mysticggx

Не надо возвращать старые лишние команды:

#zombie
#food
#night
#chaos
#reset
#vote
#profile
#top
#ask
#why
#suggest

Если эти команды где-то есть для старого режима, в Streaming Survival они не должны отображаться и не должны работать как публичный gameplay API.

==================================================
4. Current resources / gameplay
==================================================

Активные ресурсы:

- Water
- Wood
- Food
- Campfire

Stone сейчас отключён из gameplay.

Важно:
- collect_stone code можно оставить в проекте, но gameplay/UI/parser не должны активно использовать stone, пока STREAMING_SURVIVAL_STONE_ENABLED=false.
- #do добывай камень не должен ломать систему.
- Stone не должен быть в win condition.
- Stone не должен отображаться в overlay, если отключён.

HP bars не нужны.

Персонажи пользователей не умирают.

Если раунд проигран:
- показать "Конец этапа, вы не справились."
- затем новый раунд.

Если раунд выигран:
- показать "Этап пройден. Вы выжили."
- затем новый раунд.

Персонажи остаются в игре между раундами.
Последний action plan пользователя сохраняется и повторяется в следующих раундах.

==================================================
5. Architecture
==================================================

Компоненты:

A) localhost dashboard
- вкладка Training AI Forest Survival;
- вкладка Streaming Survival;
- кнопки запуска/остановки Streaming Survival;
- LLM Bot controls;
- Local Debug Chat;
- Diagnostics.

B) stream_bot
- command parser;
- deterministic parser;
- LLM fallback;
- validator;
- FastAPI/local API;
- Twitch/local mode;
- SQLite state;
- user/action plans.

C) Unity Streaming Survival runtime
- separate scene/mode;
- WorldObjectRegistry;
- CharacterController;
- ResourceManager;
- RoundManager;
- CommandReceiver;
- TrajectoryRecorder;
- ScenarioRunner;
- ScenarioChecker;
- SelfTests.

D) test pipeline
- parser tests;
- world registry tests;
- runtime scenario tests;
- trajectory checks;
- E2E;
- export core zip.

==================================================
6. WorldObjectRegistry rule
==================================================

LLM and parser must NOT know or guess Unity coordinates.

Correct rule:

User text
→ parser/LLM creates high-level action plan
→ Unity WorldObjectRegistry resolves real targets

WorldObjectRegistry owns:
- home / home_interaction_point;
- water_source;
- tree;
- sheep;
- campfire_slot;
- spawn points.

Resource actions must use actual registered world objects.

Wrong:
- hardcoded "go left";
- screen-space coordinates;
- teleport to resource;
- LLM choosing coordinates;
- water interaction point accidentally near home/forest.

Correct:
- collect_water resolves water_source through registry;
- collect_wood resolves tree through registry;
- collect_food/kill_sheep resolves sheep/food through registry;
- go_home resolves home_interaction_point.

==================================================
7. ResourceGuard rule
==================================================

Resources must only be added when the character is physically near the correct target.

collect_water:
- target_type must be water_source;
- target_id must exist;
- character must be inside WATER_INTERACTION_RADIUS;
- work must start only after reaching interaction radius;
- Water += 1 only after ResourceGuard PASS.

collect_wood:
- same for tree.

collect_food / kill_sheep:
- same for sheep/food.

Stone:
- disabled when STREAMING_SURVIVAL_STONE_ENABLED=false.

Forbidden:
- adding water while character is in forest;
- adding resources merely because current action is collect_water;
- OnWorkDone without valid target;
- resource credit from wrong location.

Trajectory must include:
- reached_resource;
- work_started;
- work_finished;
- resource_guard_pass / resource_guard_denied;
- resource_added;
- invalid_resource_completion.

==================================================
8. Movement / teleport policy
==================================================

Normal gameplay must use smooth movement.

Allowed direct position changes only:
- join_spawn;
- scenario_setup;
- round_reset if explicitly needed;
- manual_respawn_command through #do перезагрузи персонажа.

Strict rule:
Normal movement actions must NOT teleport:
- collect_water;
- collect_wood;
- collect_food;
- kill_sheep;
- build_campfire;
- go_home;
- attack_user;
- patrol;
- circle.

Stuck recovery in normal gameplay must NOT silently teleport.

Allowed stuck recovery:
- path recompute;
- small safe nudge within threshold;
- stop action and set idle;
- mark character as stuck;
- tell user to use #do перезагрузи персонажа.

Teleporting due to stuck is only acceptable if it is explicit manual respawn or specifically allowed in a dedicated test scenario, not as normal movement.

Any direct move must go through ControlledTeleport(reason).
Any direct position change without allowed reason is a bug.

==================================================
9. Current known issue to avoid
==================================================

There was a bug:
character stood near home/fence, then suddenly appeared near water and collected water.

This is unacceptable.

The tests must detect:
- hidden Warp;
- large position jump;
- speed spike;
- resource_added after teleport;
- go_home teleporting to home;
- stuck recovery teleport hidden as allowed movement.

Movement scenarios must pass strict continuity.

==================================================
10. Testing discipline
==================================================

Never say "готово" after only parser checks.

Parser-only PASS is not full PASS.

Required after any Streaming Survival change:

python scripts/run_streaming_survival_checks.py --full-runtime

Full-runtime PASS only if:

overall=PASS
full_runtime_status=PASS
parser=PASS
world_registry=PASS
runtime_scenarios=PASS
e2e=PASS where required
hidden Unity FAIL=0
INCOMPLETE=0
illegal_teleports=0
controlled_teleports_in_normal_gameplay=0
continuity=PASS

If Unity runtime is unavailable:
- default should be INCOMPLETE or FAIL;
- never full PASS.

Reports must include:
- summary.json;
- scenario_results.json;
- trajectories/*.jsonl;
- world_registry_results.json;
- *_result.json.

Scenario PASS only if:
- parser PASS;
- Unity result PASS;
- Python checker PASS;
- no hidden nested FAIL;
- no illegal teleport;
- trajectory checks pass;
- resource guard checks pass.

==================================================
11. Existing tests / artifacts
==================================================

Existing files may include:

streaming_survival_tests/scenarios.json
stream_bot/tests/test_streaming_survival_parser.py
scripts/run_streaming_survival_checks.py
scripts/test_streaming_survival_e2e.py
scripts/export_streaming_survival_core_zip.py
README_STREAMING_SURVIVAL.md

Unity Streaming Survival scripts:
- StreamingSurvivalWorldRegistry.cs
- StreamingSurvivalWorldRegistrySelfTest.cs
- StreamingSurvivalScenarioRunner.cs
- StreamingSurvivalScenarioChecker.cs
- StreamingSurvivalTrajectoryRecorder.cs
- StreamingSurvivalCharacterController.cs
- StreamingSurvivalRoundManager.cs
- StreamingSurvivalResourceManager.cs
- StreamingSurvivalCommandReceiver.cs

After successful full-runtime:
run export:

python scripts/export_streaming_survival_core_zip.py

Zip must include:
- stream_bot/*.py;
- stream_bot/tests/*.py;
- scenarios.json;
- scripts;
- Unity Streaming Survival core scripts;
- localhost UI Streaming Survival tab files;
- latest full-runtime report;
- trajectories;
- README.

Do not include:
- Library;
- Temp;
- builds;
- checkpoints;
- huge assets;
- videos;
- cache.

==================================================
12. Current working pipeline
==================================================

For any new Streaming Survival task:

1. Read this context template.
2. Identify whether task touches Training AI or Streaming Survival.
3. If Streaming Survival:
   - do not touch Training AI.
4. Modify code.
5. Run parser checks if parser changed.
6. Run full runtime checks on lab.
7. Inspect summary and scenario_results for hidden nested FAIL.
8. Inspect trajectories if movement changed.
9. Fix until full-runtime PASS.
10. Export core zip.
11. Report:
   - command run;
   - machine;
   - latest report path;
   - summary;
   - failed/fixed issues;
   - export zip path.

==================================================
13. Reporting format
==================================================

Final Cursor report must be concrete:

Full-runtime checks:
overall=PASS
parser=PASS
world_registry=PASS
runtime_scenarios=PASS
full_runtime_status=PASS
illegal_teleports=0
controlled_teleports_in_normal_gameplay=0

Latest report:
artifacts/streaming_survival/test_runs/<timestamp>/summary.json

Export:
artifacts/streaming_survival/exports/<zip_name>.zip

Do not write "готово" without full-runtime evidence.
