# ForestSurvival

Internal project context / engineering log for Cursor and maintainers.  
**Not** an end-user README. Update after every substantial change.

Companion handoff (also living): `docs/LAB_ARCHITECTURE.md` (may be untracked until committed).

Document snapshot context:
- Branch: `forest_survival` (tracks `main/forest_survival`)
- HEAD (committed): `883433c` — 2026-08-28 — *Add shirtless zombie apocalypse mix, wolf/death combat, and roster revive on resync.*
- Large Builder / Apocalypse / L-menu / barrier work exists mainly in the **working tree** (modified + untracked), not fully in HEAD. See §17.

---

## 1. Project Goal

ForestSurvival is an interactive survival world that can run as a long-lived **Twitch / stream presentation**.

Viewers can become characters, pick skins/roles, issue chat commands, while the world keeps living (heroes, zombies, resources, defense).

Two sides exist in one repo:

| Side | Role |
|------|------|
| **A. Presentation / Streaming** | Unity survival world, viewer characters, Twitch commands, autonomous hero policies (ONNX), zombie apocalypse, building/defense, OBS → Twitch |
| **B. AI / Training** | ML-Agents multi-env training, checkpoints, ONNX export for presentation External Brain |

**Rule:** do not mix StreamingSurvival gameplay work with Training architecture unless the task explicitly needs both.

---

## 2. Product / Stream Concept

Production stream (lab_comp reference):

1. **Jack / Lily / George** — ML heroes on the presentation Env; brain = Python ONNX External Brain (`stream_onnx_infer.py`).
2. **Streaming Survival followers** — Twitch viewers via `#join` / `#do` / `#i_*` skins.
3. Shared world: trees, sheep, water, zombies, barricades, apocalypse timeline, HUDs.
4. OBS captures the Unity window and publishes to Twitch (optional dual push via local nginx-rtmp).

Full `-forestStreamingSurvival` (SS without heroes) is a **test / alternate** mode — **not** current production stream.

---

## 3. High-Level Architecture

```
Twitch Chat
    ↓
stream_bot (HTTP :8765 — roster/stats; chat ingest)
    ↓
command_parser / validator / LLM (#do) / SQLite persistence
    ↓
UDP JSON → Unity :5055
    ↓
StreamCommandReceiver
    ↓
StreamingSurvivalController (EnsurePlayer / ApplyAction / skins)
    ↓
StreamingSurvivalPlayer
    ↓
ForestScene gameplay (presentation Env)

Presentation AI (heroes):
Unity (-forestStreamOnly -forestExternalBrain)
    ↔ ML-Agents port ~7000
    ↔ stream_onnx_infer.py
    ↔ results/<RUN_ID>/{Jack,Lily,George}LowLevelAgent/*.onnx

Training (parallel, do not kill for stream fixes):
Unity headless envs
    ↔ mlagents-learn
    ↔ train_scripts / yaml

Broadcast:
Unity window (DISPLAY=:1 on lab)
    ↓
OBS (Xcomposite + Pulse forest_stream.monitor)
    ↓
rtmp://127.0.0.1/live  (optional Docker nginx obs_dual_rtmp)
    ↓
Twitch (+ optional VK)
```

Exact transport facts (source):
- Unity UDP listen: `StreamCommandReceiver`, port **5055** (`FOREST_STREAM_BOT_PORT`).
- Bot → Unity: `stream_bot/unity_client.py`, UDP only.
- Bot HTTP: `:8765` for roster/stats/leaderboard — **not** the primary command path into Unity.
- Unity may POST stats to `http://127.0.0.1:8765/...` (`FOREST_BOT_HTTP_URL`).

---

## 4. Main Runtime Modes

| Mode | How | Purpose |
|------|-----|---------|
| **StreamOnly (prod presentation)** | `-forestStreamOnly` + `-forestExternalBrain` | Heroes + followers + OBS |
| **Editor Play ≈ stream** | Default Editor Play (`EDITOR_PLAY_AS_STREAM_V1`) | Local gameplay like stream; brain = Heuristic/local Inference unless `stream_onnx` attached |
| **Editor train layout** | `FOREST_EDITOR_TRAIN=1` or `-forestEditorTrain` | Old multi-Env train layout in Editor |
| **Full Streaming Survival** | `-forestStreamingSurvival` | Followers-focused SS; **not** current prod stream |
| **Train** | `mlagents-learn` + `launch_forest_env.bash` | Headless RL |

Flags live in `Assets/TrainingEnvSpace.cs`.

---

## 5. Production Streaming Pipeline

### Correct lab restart

```bash
cd ~/lab_work_space/forest_survival
RUN_ID=jlg_finetune_2 bash train_scripts/lab_comp/restart_stream_for_run.bash
```

Key scripts:
- `restart_stream_for_run.bash` — sticky onnx, kill **stream only**, run onnx, roster resync
- `run_stream_onnx.bash` — defaults `BUILD=stream_forest_survival_2_12_07_2026`, `DISPLAY=:1`, port `7000`
- `stream_onnx_infer.py` — loads Jack/Lily/George onnx; starts Unity with stream flags
- `run_stream_bot.bash` / `python -m stream_bot.main`
- `restart_obs.bash` / `start_obs_stream.bash` / `bind_obs_unity_window.bash`
- `setup_obs_dual_stream.bash` — OBS → local nginx-rtmp Docker `obs_dual_rtmp` → Twitch + VK

### Process statuses (do not conflate)

| Status | Meaning |
|--------|---------|
| Unity ON | Presentation binary running |
| ONNX ON | `stream_onnx_infer.py` driving External Brain |
| Bot ON | Twitch chat + roster HTTP |
| Training ON | Separate `mlagents-learn` (may run in parallel) |
| OBS ON | Capture / encode process alive |
| Twitch Stream ON | Actual RTMP publish / live channel |

**OBS ON ≠ Twitch Stream ON.** Never start a real Twitch publish only for a debug test unless the user explicitly asks.

### Kill boundaries

- Stream fix: kill `stream_onnx_infer` / `forestStreamOnly` only — **not** all Unity, **not** train.
- See `kill_mode_streaming.bash` vs `kill_mode_training.bash` / `kill_train.bash`.

---

## 6. Unity / ForestScene Development Pipeline

### Main scene

- **`Assets/ForestScene.unity`** — only enabled scene in `ProjectSettings/EditorBuildSettings.asset`.
- Development: Unity Editor → ForestScene → Play Mode.
- Scene edits (geometry, `Front_1..4`, props) happen in the Editor.

### Typical cycle

1. Change C# / scene in the Windows repo.
2. Validate in Editor Play (default = stream-like world).
3. Use **L** test menu for viewer characters / actions / camera / 1×·5× (see §9).
4. Hotpatch DLL for lab (usual path) or rare full binary rebuild.
5. Deploy to lab_comp; restart stream with correct `RUN_ID`; roster resync.
6. **Never assume** local ForestScene / working-tree DLL is already what lab is running.

### Usual deploy (hotpatch)

1. Edit `Assets/**/*.cs`.
2. Unity batchmode: `CompileStreamingSurvivalDll.CompileAndCopyFromCommandLine`.
3. Copy `Assembly-CSharp.dll` into  
   `build_versions/stream_forest_survival_2_12_07_2026_Data/Managed/`.
4. `scp` to lab same path; **verify size/md5**.
5. `RUN_ID=jlg_finetune_2 bash train_scripts/lab_comp/restart_stream_for_run.bash`.
6. Ensure `POST :8765/roster/resync` (often included in restart).

---

## lab_comp vs Editor ForestScene

### lab_comp

What is **currently running** on `lab_comp` is the working **streaming/runtime reference**.

### Editor ForestScene

Local ForestScene / working tree may contain **newer experimental** edits than the lab build/DLL.

### Do not assume byte-for-byte equality

- Editor ForestScene ≠ lab runtime build/assets.
- Lab runtime ≠ latest uncommitted Editor work.

### NOT done in this documentation task

- No ForestScene ↔ lab_comp sync.
- No copy of scene/build/assets between machines.
- No diff/repair of divergences.

### TODO (later)

- [ ] Define controlled ForestScene → lab_comp deployment/synchronization procedure

---

## 7. Viewer Character Pipeline

**Status: Implemented** (bot + Unity; builder/soldier heavily extended in working tree).

```
viewer → #join / #i_* / #do
      → stream_bot parse/validate
      → UDP streaming_survival_* 
      → StreamCommandReceiver (ApplySsJoin / ApplySsSkin / ApplySsAction)
      → StreamingSurvivalController.EnsurePlayer
      → StreamingSurvivalPlayer skin + SetAction / ApplyAction
```

### Skins (Twitch syntax — `stream_bot/command_parser.py`)

| Command | Skin | Unlock (stats thresholds) |
|---------|------|---------------------------|
| `#i_human` / `#ihuman` / `#i_jack` | human (Jack body) | after join |
| `#i_wolf` / `#iwolf` | wolf | `WOLF_SHEEP_REQUIREMENT = 30000` |
| `#i_soldier` / `#isoldier` / `#i_gun` | soldier | `SOLDIER_ZOMBIE_REQUIREMENT = 5000` |
| `#i_builder` / `#ibuilder` / `#строитель` | builder | wood **and** water `50000` each |

Also: `#join`, `#exit`/`#leave`/`#quit`, `#stats`, `#skins`, `#do <текст>`.

UDP skin tokens (Unity): `wolf` | `soldier`/`gun`/`rifle`/`ar` | `builder`/`строитель` | `human`/`jack`/`default`.

---

## 8. Actions / Commands

`#do …` → heuristic/LLM → `validator.ALLOWED_ACTIONS` → UDP `streaming_survival_action`.

Notable actions include gather/nav/combat plus **`build_walls`** (builder).

Builder chat guidance (parser strings): e.g. `#do ставь стены` / баррикады.  
Builder must not farm resources; allowed: walls + limited navigation (`#do к базе`, `#do к воде`, etc.).

---

## 9. Test & Debug Tooling

### StreamingSurvivalTestMenu (L)

**Status: Implemented in working tree** (`Assets/Twitch/StreamingSurvival/StreamingSurvivalTestMenu.cs` — **untracked** at doc time).

- Toggle: **L** (`OwnsKeyL`); Escape closes.
- Spawns test viewers: `test_human_001`, `test_wolf_001`, `test_soldier_001`, `test_builder_001`.
- Uses **production path**: `EnsurePlayer` → skin → `ApplyAction` (not a parallel fake AI).
- Action lists per role (builder includes `build_walls`).
- God mode for Jack/Lily/George; mute **M**; camera CamA/C/D/Auto; manual play **N**/**P**.
- Training safety: disabled in batchmode; Editor only when ML training not active; player builds only in StreamOnly. On disable → speed/god reset.

### Debug speed 1× / 5×

**Status: Implemented in working tree** (`PresentationTestSpeed.cs`).

- Sets `Time.timeScale` to 1 or 5; scales `fixedDeltaTime` proportionally.
- Remaps Jack survival clock elapsed so the apocalypse bar stays consistent.
- **Not** a Twitch production feature.

### Runtime test ladder (lab graphics / stack)

Terminology used in GPU/display diagnostics (`artifacts/gpu_diagnostics/`, Train Lab UI):

| Level | Meaning |
|-------|---------|
| **EASY** | Minimal OpenGL / SwapBuffers probe (no Unity) — system Present path |
| **MEDIUM** | Presentation: Unity + ONNX External Brain; Bot OFF, Training OFF, OBS OFF, Twitch OFF |
| **HARD** | Full stack: Unity + ONNX + Bot + Training (if needed) + OBS + **actual Twitch stream** |

Do **not** run HARD automatically. Do not go live on Twitch for local debug.

---

## 10. Builder & Barricades

**Status: Implemented in working tree** (many files **untracked** / large diffs on `StreamingSurvivalPlayer` / `stream_bot`). Not in HEAD `883433c` as a finished committed feature set.

### Builder

| Item | Source fact |
|------|-------------|
| Visual / Resources | `Resources/BuilderSkin`; editor source `Assets/JC_LP_MedievalCharacters_LITE/Prefabs/SM_MedievalMaleLite_01.prefab` |
| Action | `build_walls` → `TickBuildWalls` |
| Anim | Jack controller: float `Speed`, trigger `Do` |
| Build duration | `BuildDurationSec = 5f` (simulation time) |
| Cooldown | `BuildActionCooldown = 5f` |
| Progress | Label `Строит: N%` / `КД …`; partial mesh via `StreamingSurvivalBarrier.SetBuildProgress` |
| Collider / registry | Enabled on finalize |

### Barricades / fronts

| Item | Source fact |
|------|-------------|
| Prefab | `Resources/WoodenPlankBarrier_14_v1` ← `Assets/LowPolyBarriersPackFree/.../WoodenPlankBarrier_14_v1.prefab` |
| Fronts | Scene objects `Front_1` … `Front_4` |
| Slots | `FREE` / `RESERVED` / `OCCUPIED` (`BarrierFrontManager`) |
| Order | Claim Front_1 → … → Front_4 (no skipping incomplete fronts) |
| Spacing | `barrierGapFraction = 0.03f` → gap = width × fraction |
| Orientation | Face world `+X` |
| Health | `StreamingSurvivalBarrier.MaxHits = 6` |
| NavMeshObstacle | On finalize (`carving`); off while incomplete |
| Zombie targeting | `PresentationCombatTargetRegistry.RegisterBarrier` |
| Multi-builder | Shared world slots |

Round clear: `BarrierFrontManager.ClearAllForNewRound()` from `StreamingSurvivalController.StartNewRound` / `PresentationWorldReset`.

---

## 11. Zombie Apocalypse Progression

**Status: Implemented in working tree** (`Assets/ZombieApocalypseDifficulty.cs` — **untracked** at doc time). Verify DLL markers on lab before treating as production.

Single timeline (no separate learn/nightmare stages in this class):

| Phase | Ordinary HP hits | Spawn rate mult | Barrier dmg / ordinary hit |
|-------|------------------|-----------------|----------------------------|
| Early | 1 | 1× | 1 |
| PostBoss (≥1/4 or Worker dead) | 2 | 2× | 2 |
| PostGoblin (≥2/4 or Goblin dead) | 4 | 2× | 4 |

Boss slots (progress thresholds):

| Slot | Progress | Hits | Display |
|------|----------|------|---------|
| Worker «РАБОТЯГА» | 0.25 | 20 | HUD + music |
| Goblin «ГОБЛИН» | 0.50 | 40 | larger mesh, reach `3.15`, CD×0.5 |
| MysteryA «??» | 0.75 | 30 | |
| MysteryB «??» | 0.98 | 30 | |

Notes from source (not prompt guesses):
- Worker prefab path used in Resources: SuitMan-style boss assets (`Characters_Zombie_SuitMan_1` present under Resources in working tree).
- `APOCALYPSE_PROGRESS_PHASE_SYNC_V1` — phase catch-up from bar progress even if Jack is dead.
- `Tick()` early-outs when `TrainingEnvSpace.IsStreamingSurvivalMode` — pure SS flag skips apocalypse timeline.
- Apocalypse bar length: `SurvivalTotalSeconds = survivalGoalSeconds * 6` (`APOCALYPSE_BAR_6X_V1`); bar end → episode restart (`SURVIVAL_BAR_EPISODE_RESTART_V2`).

**Not** matching older “10-hit Worker / 5× final horde” prompt drafts — current code uses **20** Worker hits and **2×** spawn after PostBoss (elite HP/dmg escalate at PostGoblin).

---

## 12. Round / Episode Reset

**Status: Implemented** (working tree + earlier stream wipe logic).

| Trigger | Behavior |
|---------|----------|
| New round / world reset | Clear barricades + reservations; `ZombieApocalypseDifficulty.ResetForNewRound()`; respawn/teleport followers as coded |
| All followers dead | `FOLLOWER_WIPE_RESET_V1` — wipe overlay; heroes toast-only when followers exist |
| Survival bar complete | Overlay «Раунд окончен» → episode restart |
| Boss reset | Force despawn, clear slots, stop Worker music/HUD, phase → Early |

---

## 13. Training / ML Pipeline

- Train: `mlagents-learn` via `train_scripts/lab_comp/launch_forest_env.bash`, `mlagents_learn_forest.*`, `train_headless_*.bash`, yaml under `custom_configs/`.
- Checkpoints / onnx under `results/<RUN_ID>/`.
- Presentation loads exported onnx via **Python External Brain**, not Sentis-as-primary for prod stream.
- Editor Play mirrors stream **world**, but without `stream_onnx` heroes are not on the lab External Brain path.
- Finetune / joint Jack+Lily+George history lives in commits around `91a8207` … `346d62b` and later.

Keep Training and StreamingSurvival changes scoped.

---

## 14. OBS / Streaming

- Capture Unity window on lab (`bind_obs_unity_window.bash`).
- Audio: Unity → Pulse `forest_stream`; OBS listens on `forest_stream.monitor`.
- `restart_obs.bash` defaults `DISPLAY=:1`, sets `XAUTHORITY` from gdm/`~/.Xauthority`.
- Dual stream: OBS → `rtmp://127.0.0.1/live` → Docker `obs_dual_rtmp` pushes Twitch + VK (`setup_obs_dual_stream.bash`).
- Observed ops issue (2026-09 session): nginx-rtmp stuck `SYN_SENT` on stale Twitch ingest IP → fix by `docker restart obs_dual_rtmp` + OBS restart (DNS re-resolve). Do not paste stream keys into this doc.

---

## 15. lab_comp Production State

**Reference machine:** SSH `lab_comp` → `~/lab_work_space/forest_survival`.

### Documented prod defaults (from `docs/LAB_ARCHITECTURE.md`, context ~2026-09)

| Param | Value | Notes |
|-------|-------|-------|
| BUILD | `stream_forest_survival_2_12_07_2026` | Binary under `build_versions/` |
| RUN_ID | `jlg_finetune_2` | Override required — script default `run_80` is stale |
| Stream log | `results/stream_onnx_jlg_finetune_2.log` | |
| Bot | `python -m stream_bot.main` | HTTP `:8765`, UDP `:5055` |

Update this table when lab switches BUILD/RUN_ID.

Working tree may already contain newer gameplay than the DLL currently loaded in that build — treat lab process + DLL markers as ground truth for “what viewers see”.

---

## 16. ForestScene Editor State

- Active development scene: `Assets/ForestScene.unity` (also large **uncommitted** scene diff at doc time).
- Backup present: `Assets/ForestScene_back_up.unity` (untracked).
- Contains presentation Env, CamA/C/D, `Front_1..4`, etc.
- Day/night: working tree forces day / disables cycle (`DAY_NIGHT_CYCLE_DISABLED_V1`).
- Camera: default locked CamA; L-menu CamA/C/D/Auto (`CAM_LOCK_L_MENU_V2`).

---

## 17. Performance / NVIDIA Display Findings

**Status: Known issue / observe** — documented from `artifacts/gpu_diagnostics/*` and Train Lab UI STREAM tab.

Pathology observed:
- `xrandr`: HDMI-0 active @60 Hz
- NVIDIA: `Display Active = Disabled`, `EnabledDisplays = 0` (MISMATCH)
- Standalone SwapBuffers / Unity Present waits ~1 s → severe FPS collapse

Mitigation observed:
- Reassert exact NVIDIA `CurrentMetaMode` → Display Active Enabled, EnabledDisplays non-zero → SwapBuffers sub-ms again
- Train Lab UI: DISPLAY/NVIDIA health card + **manual** “Repair NVIDIA display” (no auto-repair)
- After clean reboot (report `post_reboot_ladder_20260908_*`): EASY GOOD, MEDIUM GOOD in the observed window — **not** proof of permanent fix

Separate issue: Unity can still stall (`Gfx.WaitForPresent`) even while NVIDIA reports Enabled — display bug ≠ full frame-health explanation.

---

## 18. Recent Git / Development History

### Committed (selected, real hashes)

| Commit | Date | Title / impact |
|--------|------|----------------|
| `883433c` | 2026-08-28 | Shirtless zombie apocalypse mix, wolf/death combat, roster revive on resync |
| `8b8e043` | 2026-08-20 | Tree spawn wipe fix; spawn/OBS repair tooling; Train Lab UI |
| `30b52ad` … `0d2b451` | 2026-08-14 | Live Stress ×3, wood stuck retarget, run-list UI |
| `4a0bf0f` | 2026-08-13 | SS wood farming / forest exit chops / QA charts |
| `7d18839` | 2026-08-12 | Streaming Survival water corridor, QA charts, lab UI |
| `346d62b` | 2026-08-02 | Train Lab UI; joint finetune + stream presentation fixes |
| `91a8207` | 2026-07-29 | Jack/Lily/George two-stage training |
| `b8bac58` | 2026-07-25 | Jack train/stream pipeline, stream bot, lab sync |
| `aad1677` … `e7a5265` | 2026-07 | ONNX stream, lab_comp multi-env, presentation display |
| `76f6d9e` … | 2026 | ForestScene single presentation Env integration chunks |

Older roots: Twitch overlays, city scene, validation/training fixes (`d9ccfe4`, `9895293`, …).

### Current working tree / not yet committed (as of this document)

**Do not treat as “in git production” until committed + deployed.**

Large modified areas include (non-exhaustive):
- `StreamingSurvivalController/Player/Hud`, `StreamCommandReceiver`, `AgentDeathOverlay`, `JackScript`, `ZombieChase/Attack/Health/Spawner`, `TrainingEnvSpace`, `CamAbSwitcher`, `DayNightCycle`, `JointEpisodeReset`, `PresentationWorldReset`, `stream_bot/*`, `stream_onnx_infer.py`, `train_lab_ui.py`, `ForestScene.unity`, …

Notable **untracked** feature files:
- `BarrierFrontManager.cs`, `StreamingSurvivalBarrier.cs`, `StreamingSurvivalTestMenu.cs`
- `ZombieApocalypseDifficulty.cs`, `WorkerBossZombie.cs`, `WorkerBossHud.cs`, `WorkerBossMusic.cs`
- `PresentationTestSpeed.cs`, `PresentationTestGodMode.cs`, `PresentationCombatTargetRegistry.cs`
- `UnityWebRequestDomainSafe.cs`
- Builder/barrier/soldier Resources prefabs; medieval + barrier asset packs
- `docs/LAB_ARCHITECTURE.md`

Feature themes in that tree (Partially tested / needs Play Mode + lab marker verify):
- L-menu test characters + 1×/5× + camera lock
- Builder + `build_walls` + Front_1..4 multi-builder slots
- Apocalypse bosses + phase sync + bar×6 + bar-end restart
- Follower wipe reset; straight zombie chase editor sync; webrequest domain-safe; Editor Play as stream

---

## 19. Known Issues / Open Work

| Item | State |
|------|-------|
| Editor ForestScene / working tree may diverge from lab_comp build/DLL | **Known** — sync procedure TODO |
| Controlled ForestScene → lab deployment | **Open** — not formalized |
| Builder / Apocalypse / L-menu largely uncommitted | **Open** — need commit + Play Mode + MEDIUM/HARD as appropriate |
| NVIDIA/Xorg Present MISMATCH | **Observe** — may return after Unity/ONNX start |
| Twitch push via nginx-rtmp can stick on bad ingest IP | **Ops** — restart `obs_dual_rtmp` + OBS |
| Script default `RUN_ID=run_80` if unset | **Trap** — always set `jlg_finetune_2` for current prod |
| Mixing train kills with stream fixes | **Convention** — avoid |
| HARD / live Twitch without explicit ask | **Forbidden** for casual debug |
| Pure `-forestStreamingSurvival` vs prod StreamOnly | Easy to confuse — prod = StreamOnly + heroes |
| Growth bottlenecks (acquisition / early retention / watch depth) | See **Growth / Current Product Bottlenecks — Sep 2026** below |

---

## Growth / Current Product Bottlenecks — Sep 2026

Snapshot basis (read-only):
- Viewer funnel: `artifacts/analytics/viewer_character_funnel_20260912_121056.csv` (+ events / summary same stamp)
- Twitch weekly Insights: **manual numbers below** (no Twitch weekly CSV found in repo as of 2026-09-12)
- Do **not** quote raw SS `UNIQUE VIEWERS=91` as product metric without CLEAN filter

### Clean viewer metrics rule

| Set | Count | Rule |
|-----|------:|------|
| **RAW** SS creators | **91** | all `streaming_survival_users` rows |
| **Excluded** | **34** | test/automation + owner |
| **CLEAN real viewers** | **57** | product funnel |

**Exclusion (documented):**
- prefixes / patterns: `debug*`, `test*`, `stress_user_*` / `stress_*`, `e2e*`, `pond_*`, `ui_*`, `coord_*`, `ck_*`, `ep_*`, `u_*` (numeric), plus obvious lab ids in this export (`ck_back`…, `pond_fix*`, `stress_user_0*`, …)
- **owner/internal:** `mysticggx` excluded from customer funnel (only CLEAN builder unlock was owner)
- Ambiguous usernames: **none excluded silently** in this pass (0 ambiguous kept flags)

Recompute CLEAN from the funnel CSV before citing newer numbers.

### Current priority

| Priority | Area | Role |
|----------|------|------|
| **P1** | Acquisition | Primary growth bottleneck |
| **P2** | Early retention | Primary growth bottleneck |
| **P3** | Engagement / watch depth | Confirmed symptom; **root cause unknown** |

P1 + P2 drive growth. P3 is measured decline, not yet a proven gameplay verdict.

---

### 1. Acquisition: insufficient new-user flow

**Problem**  
ForestSurvival still depends almost entirely on organic Twitch discovery. Independent external acquisition is nearly absent. Recent weeks show weaker Twitch-native inflow and fewer new CLEAN character creators.

**Evidence — Twitch weekly (channel Insights)**

| Week start | Unique viewers | Watch minutes | Chatters | Chat messages | +Followers |
|------------|---------------:|--------------:|---------:|--------------:|-----------:|
| Aug 23 | 202 | 8061 | 24 | 714 | +9 |
| Aug 30 | 158 | 4006 | 16 | 445 | +2 |
| Sep 06 | 152 | 1362 | 14 | 149 | +0 |

**Evidence — CLEAN new character creators / ISO week** (`first_seen` cohort)

| Cohort week | New CLEAN creators |
|-------------|-------------------:|
| 2026-W32 | 18 |
| 2026-W34 | 20 |
| 2026-W35 | 11 |
| 2026-W36 | 6 |
| 2026-W37 | 2 |

Trend after W34: declining new CLEAN character creations (small absolute counts — treat carefully).

**Current interpretation**  
Native Twitch acquisition is **limited / recently declining**. That is **not** proof that “the Twitch algorithm stopped recommending” the channel. Separately: there is almost no scalable **external** acquisition channel yet.

**Main controllable growth lever:** external short-form video (YouTube Shorts / TikTok) → measure new CLEAN character creators / week.  
Twitch-native levers (title / category / tags / schedule) are cheap to tune but **not** the primary scale path.

**Unknowns**  
Share of uniques from browse/recommendations vs returning vs host raids; how many Twitch uniques ever `#join`.

**Metrics to watch**  
Twitch weekly uniques / +followers; CLEAN new creators / week; `#join` rate among chatters.

**Next experiment**  
Ship a Shorts/TikTok clip pipeline; tag UTM/period and compare CLEAN creators/week. Light Twitch packaging pass without treating it as the scale channel.

---

### 2. Early retention: most character creators do not return

**Problem**

```
viewer enters → creates Human → WHY SHOULD THEY RETURN?
→ only a minority returns
→ those who return often start progressing further
```

**Evidence — CLEAN real creators (n=57)**

| Metric | Value |
|--------|------:|
| Returned after 1d | **8 / 57 = 14.0%** |
| One-day (no 1d return) | **49 / 57 = 86.0%** |
| Returned after 3d | **8 / 57 = 14.0%** |
| Returned after 7d | **7 / 57 = 12.3%** |
| Reached wolf | **6 / 57 = 10.5%** |
| Reached soldier | **3 / 57 = 5.3%** |
| Reached builder | **0 / 57 = 0%** (owner-only in RAW) |

Among CLEAN `returned_after_1d=1` (n=8): wolf 6, soldier 3.  
Among CLEAN non-returners (n=49): wolf 0, soldier 0.  
→ progression after Human is concentrated in the small returner set (association, not proven causality).

Retention method: `events` (`ss_join|ss_action|ss_stats|ss_skin`) vs `first_seen_at` (same export as funnel summary).

**Current interpretation**  
First→second visit is the product bottleneck after acquisition. Manual “~20% / ~80%” estimate is **close but superseded** by this CLEAN recompute (**14% / 86%**).

**48h inactivity soft-hide vs schedule (hypothesis)**  
Actual rule: `INACTIVE_HIDE_SECONDS = 48h` (soft `is_active=0`, row kept). Regular hosted streams: **Tue 17:00** and **Fri 17:00** (~72h apart). A character can leave the active world **before** the next hosted stream.  
Status: **HYPOTHESIS / retention risk**, not a proven cause of the 14% 1d return.

**Unknowns**  
How many non-returners never saw a clear next unlock; hosted vs 24/7 first session mix; effect of 48h hide on Tue↔Fri returners.

**Metrics to watch**  
CLEAN `returned_after_1d/3d/7d`; wolf/soldier conversion among returners; time from first `#join` to second activity day.

**Next experiment**  
Make first-session “why return / next unlock” explicit; A/B or staged test of hide window vs Tue/Fri loop (e.g. ≥72h) without claiming fix until measured.

---

### 3. Engagement / watch depth is declining

**Problem**  
Weekly unique volume between Aug 30 and Sep 06 is similar, but **minutes watched per unique** and chat volume fell sharply.

**Evidence**

| Week | Min / unique | Chatters | Chat msgs |
|------|-------------:|---------:|----------:|
| Aug 23 | 8061/202 ≈ **39.9** | 24 | 714 |
| Aug 30 | 4006/158 ≈ **25.4** | 16 | 445 |
| Sep 06 | 1362/152 ≈ **9.0** | 14 | 149 |

**Current interpretation**  
Confirmed engagement **symptom**. Root cause = **UNKNOWN**. Do **not** state gameplay as proven cause.

**Hypotheses (unproven)**  
- less relevant incoming traffic  
- long passive stretches in autonomous 24/7 mode  
- unclear objective for a new viewer in the first seconds  
- technical / performance issues  
- mix of autonomous 24/7 vs hosted Tue/Fri sessions inside the same Twitch week

**Analytics limitation**  
Twitch weekly Insights **blend** autonomous 24/7 and hosted live-dev streams. Cannot yet attribute watch-depth drop to one mode.

**Unknowns**  
Per-mode averages; whether drop is new vs returning viewers; correlation with lab FPS/Present incidents.

**Metrics to watch**  
Min/unique weekly; chatters / msgs; ideally split autonomous vs hosted (manual or future logging).

**Next experiment**  
Clarify autonomous loop in-HUD (AI goal / apocalypse progress / next beat); start separating hosted vs 24/7 analytics before changing core gameplay for “engagement”.

---

### Next experiments (short)

| Area | Direction |
|------|-----------|
| Acquisition | Shorts/TikTok pipeline; CLEAN creators/week |
| Retention | First→second visit loop; test/reconsider 48h active-world hide vs Tue/Fri; explicit progression/next unlock |
| Engagement | Instantly readable autonomous loop; HUD: AI goal / apocalypse / next event; split autonomous vs hosted metrics |

---

## 20. Project Conventions

1. Investigate current pipeline before large edits.
2. Prefer production viewer path (`EnsurePlayer` / skins / `ApplyAction`) over parallel fake systems.
3. Debug characters must use the same path as Twitch viewers.
4. ForestScene edits go through Unity Editor.
5. Local ForestScene is not automatically deployed.
6. Do not overwrite a working lab build without an explicit task.
7. OBS ON ≠ Twitch Stream ON.
8. Do not start Twitch stream for local checks unless asked.
9. Do not change Training when the task is Presentation-only.
10. Behavioral changes get Play Mode and/or EASY / MEDIUM / HARD as needed.
11. After substantial completed work, **update this file**.
12. Prefer marker strings in code (`*_V1`) for deploy verification.
13. Never commit secrets (`.env`, stream keys, tokens). This document must stay secret-free.

---

## 21. How To Update This Document

After each substantial change:

1. Check `git status` / `git diff` (and lab markers if relevant).
2. Update architecture / pipeline sections if behavior changed.
3. Add a short entry under §18 (Committed vs Working tree).
4. Refresh §19 Known Issues.
5. Mark feature state: **Implemented** / **Tested** / **Partially tested** / **Planned** / **Known issue**.
6. Preserve useful history; do not dump raw logs.
7. Keep BUILD / RUN_ID / scene / transport facts accurate — no invented hashes or dates.

Status vocabulary for features:
- **Implemented** — code exists
- **Tested** — validated in the stated environment
- **Partially tested** — some paths checked
- **Planned** — not in code yet
- **Known issue** — observed limitation

---

## Related paths (quick map)

```
Assets/ForestScene.unity
Assets/TrainingEnvSpace.cs
Assets/Twitch/StreamCommandReceiver.cs
Assets/Twitch/StreamingSurvival/*
Assets/ZombieApocalypseDifficulty.cs          # often untracked until commit
Assets/JackScript.cs / LilyScript.cs / GeorgeScript.cs
Assets/Editor/CompileStreamingSurvivalDll.cs
stream_bot/
train_scripts/lab_comp/restart_stream_for_run.bash
train_scripts/lab_comp/stream_onnx_infer.py
train_scripts/lab_comp/restart_obs.bash
docs/LAB_ARCHITECTURE.md
build_versions/stream_forest_survival_2_12_07_2026*
results/jlg_finetune_2/
```
