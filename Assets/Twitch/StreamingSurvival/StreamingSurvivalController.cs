using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;

/// <summary>
/// Streaming Survival: раунды Water/Wood/Food/Heat со scripted follower-персонажами.
/// Включается только при -forestStreamingSurvival / FOREST_STREAMING_SURVIVAL.
/// (Training AI / ML-Agents герои в этом режиме отключаются.)
/// </summary>
public sealed class StreamingSurvivalController : MonoBehaviour
{
    public static StreamingSurvivalController Instance { get; private set; }

    const int Goal = 10;
    const float InterRoundPause = 5f;

    [SerializeField] float stageSeconds = 240f;
    [SerializeField] float workDuration = 1.4f;

    int _water;
    int _wood;
    int _food;
    int _heat;
    int _stone;
    float _roundEndsAt;
    float _pauseUntil;
    bool _inPause;
    string _banner = "";
    float _bannerUntil;
    readonly Dictionary<string, StreamingSurvivalPlayer> _players =
        new Dictionary<string, StreamingSurvivalPlayer>();

    StreamingSurvivalHud _hud;
    StreamingSurvivalPlayersTable _table;
    Transform _basePoint;
    Transform _housePoint;
    Vector3 _followerSpawnWorld;
    bool _followerSpawnReady;
    float _cleanupUntil;
    bool _worldReady;
    GameObject _jackVisualTemplate;

    public int Water => _water;
    public int Wood => _wood;
    public int Food => _food;
    public int Heat => _heat;
    public int Stone => _stone;
    public int GoalAmount => Goal;
    public int PlayerCount => _players.Count;
    public float StageSeconds => stageSeconds;
    public float RoundSecondsLeft => Mathf.Max(0f, _roundEndsAt - Time.time);
    public float RoundProgress01 =>
        stageSeconds <= 0.01f ? 0f : 1f - (RoundSecondsLeft / stageSeconds);
    public string Banner => Time.time < _bannerUntil ? _banner : "";
    public string RoundTaskText =>
        _inPause
            ? "Пауза между раундами…"
            : $"Соберите {Goal} воды, {Goal} дерева, {Goal} еды и {Goal} тепла";
    public float WorkDuration => workDuration;
    public Transform BasePoint => _basePoint;
    /// <summary>Дом / костёр Jack (HomeSpot/Fire), не зона цветов.</summary>
    public Vector3 HouseWorld
    {
        get
        {
            ResolveHousePoint();
            if (_housePoint != null)
                return _housePoint.position;
            ResolveFollowerSpawn(force: false);
            return _followerSpawnReady ? _followerSpawnWorld : Vector3.zero;
        }
    }
    /// <summary>Точка спавна фолловеров — центр зоны цветов (не дом/забор).</summary>
    public Vector3 FollowerSpawnWorld
    {
        get
        {
            ResolveFollowerSpawn(force: false);
            return _followerSpawnReady ? _followerSpawnWorld : (_basePoint != null ? _basePoint.position : Vector3.zero);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.IsStreamingSurvivalMode)
            return;
        if (Instance != null)
            return;
        HudHpBars.SetGlobalEnabled(false);
        var go = new GameObject(nameof(StreamingSurvivalController));
        DontDestroyOnLoad(go);
        go.AddComponent<StreamingSurvivalController>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        string envSec = System.Environment.GetEnvironmentVariable("STREAMING_SURVIVAL_STAGE_SECONDS");
        if (!string.IsNullOrEmpty(envSec) && float.TryParse(envSec, out float s) && s > 5f)
            stageSeconds = s;

        HudHpBars.SetGlobalEnabled(false);
        _hud = gameObject.AddComponent<StreamingSurvivalHud>();
        _table = gameObject.AddComponent<StreamingSurvivalPlayersTable>();
        if (GetComponent<StreamingSurvivalWorldRegistry>() == null)
            gameObject.AddComponent<StreamingSurvivalWorldRegistry>();
        if (GetComponent<StreamingSurvivalTrajectoryRecorder>() == null)
            gameObject.AddComponent<StreamingSurvivalTrajectoryRecorder>();
        if (GetComponent<StreamingSurvivalScenarioRunner>() == null)
            gameObject.AddComponent<StreamingSurvivalScenarioRunner>();
        _cleanupUntil = Time.unscaledTime + 8f;
        StartNewRound("Раунд начался");
    }

    void Start()
    {
        TryPrepareWorld(force: true);
        SetupCamAOnly();
    }

    void Update()
    {
        if (!_worldReady || Time.unscaledTime < _cleanupUntil)
            TryPrepareWorld(force: false);

        // Drive viewers even when a player GO sits under an inactive Env root
        // (Player.Update would not run → frozen traj with unchanged target).
        foreach (var kv in _players)
        {
            var p = kv.Value;
            if (p != null)
                p.TickGameplay();
        }

        if (_inPause)
        {
            if (Time.time >= _pauseUntil)
            {
                _inPause = false;
                StartNewRound("Раунд начался");
            }
            return;
        }

        if (Time.time >= _roundEndsAt)
        {
            EndRound(success: false);
            return;
        }

        if (_water >= Goal && _wood >= Goal && _food >= Goal && _heat >= Goal)
            EndRound(success: true);
    }

    void TryPrepareWorld(bool force)
    {
        ResolveBasePoint();
        ResolveFollowerSpawn(force: force);
        CacheJackVisualTemplate();
        ExtinguishWorldFires();
        HideAllHeroCharacters();
        HideLegacyHud();
        DisableZombieApocalypse();
        SetupCamAOnly();
        if (_basePoint != null || force)
            _worldReady = true;
    }

    void ExtinguishWorldFires()
    {
        TwitchPermanentFire.Disable();
        TwitchPermanentFire.ExtinguishAllPresentationJacks();

        var jacks = Object.FindObjectsByType<AgentGoToHouseDiscrete>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < jacks.Length; i++)
            jacks[i]?.ExtinguishCampfire();

        // убрать SS-костры прошлых раундов
        var old = GameObject.Find("SS_Campfire");
        if (old != null)
            Destroy(old);

        var loops = Object.FindObjectsByType<CampfireLoopAudio>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < loops.Length; i++)
        {
            if (loops[i] == null) continue;
            loops[i].enabled = false;
            loops[i].gameObject.SetActive(false);
        }
    }

    void DisableZombieApocalypse()
    {
        PresentationEnvSwitcher.CancelDelayedZombieSpawnerStart();

        var spawners = Object.FindObjectsByType<ZombieSpawner>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < spawners.Length; i++)
        {
            var s = spawners[i];
            if (s == null) continue;
            s.enabled = false;
            s.ClearZombies(scanOrphanRoots: false);
            if (s.gameObject.activeSelf)
                s.gameObject.SetActive(false);
        }

        ZombieSpawner.ClearZombiesInAllEnvs();

        var chases = Object.FindObjectsByType<ZombieChase>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < chases.Length; i++)
        {
            if (chases[i] == null) continue;
            chases[i].enabled = false;
            chases[i].gameObject.SetActive(false);
        }
    }

    void HideAllHeroCharacters()
    {
        var jacks = Object.FindObjectsByType<AgentGoToHouseDiscrete>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < jacks.Length; i++)
        {
            if (jacks[i] == null) continue;
            if (IsSsClone(jacks[i].gameObject)) continue;
            jacks[i].enabled = false;
            jacks[i].gameObject.SetActive(false);
        }

        var lilies = Object.FindObjectsByType<LilyScript>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < lilies.Length; i++)
        {
            if (lilies[i] == null) continue;
            if (IsSsClone(lilies[i].gameObject)) continue;
            lilies[i].enabled = false;
            lilies[i].gameObject.SetActive(false);
        }

        var agents = Object.FindObjectsByType<Agent>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < agents.Length; i++)
        {
            if (agents[i] == null) continue;
            if (IsSsClone(agents[i].gameObject)) continue;
            agents[i].enabled = false;
            agents[i].gameObject.SetActive(false);
        }

        var dr = Object.FindObjectsByType<DecisionRequester>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < dr.Length; i++)
            if (dr[i] != null && !IsSsClone(dr[i].gameObject)) dr[i].enabled = false;

        var bp = Object.FindObjectsByType<BehaviorParameters>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < bp.Length; i++)
            if (bp[i] != null && !IsSsClone(bp[i].gameObject)) bp[i].enabled = false;
    }

    static bool IsSsClone(GameObject go)
    {
        return go != null && go.name.StartsWith("SSPlayer_");
    }

    void HideLegacyHud()
    {
        HudHpBars.SetGlobalEnabled(false);
        DisableAllOfTypeGlobal<HudHpBars>();
        DisableAllOfTypeGlobal<SatietyDisplay>();
        DisableAllOfTypeGlobal<HeatDisplay>();
        DisableAllOfTypeGlobal<WaterDisplay>();
        DisableAllOfTypeGlobal<TreeDisplay>();
        DisableAllOfTypeGlobal<JackSurvivalTaskHud>();
        DisableAllOfTypeGlobal<RewardDisplay>();
        DisableAllOfTypeGlobal<TrainingGraphOverlay>();
        DisableAllOfTypeGlobal<LilySatietyDisplay>();
        DisableAllOfTypeGlobal<LilyHeatDisplay>();
        DisableAllOfTypeGlobal<LilyWaterDisplay>();
        DisableAllOfTypeGlobal<GeorgeSatietyDisplay>();
        DisableAllOfTypeGlobal<GeorgeHeatDisplay>();
        DisableAllOfTypeGlobal<GeorgeWaterDisplay>();
        DisableAllOfTypeGlobal<GeorgeWoodDisplay>();
        DisableAllOfTypeGlobal<FlowerDisplay>();
        DisableAllOfTypeGlobal<CampfireBurnTimerDisplay>();
        DisableAllOfTypeGlobal<SurvivalPhaseAnnouncement>();
        DisableAllOfTypeGlobal<AgentDeathOverlay>();
        DisableAllOfTypeGlobal<FloatingRewardPopup>();
        DisableAllOfTypeGlobal<PresentationManualHud>();

        // TMP "New Text" leftovers
        var tmps = Object.FindObjectsByType<TMPro.TMP_Text>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < tmps.Length; i++)
        {
            var t = tmps[i];
            if (t == null) continue;
            if (t.GetComponentInParent<StreamingSurvivalHud>() != null) continue;
            if (t.GetComponentInParent<StreamingSurvivalPlayersTable>() != null) continue;
            string s = t.text ?? "";
            if (s.IndexOf("New Text", System.StringComparison.OrdinalIgnoreCase) >= 0
                || string.IsNullOrWhiteSpace(s))
            {
                t.enabled = false;
                t.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>Только CamA (perspective). CamB и top-down — off.</summary>
    void SetupCamAOnly()
    {
        var switchers = Object.FindObjectsByType<CamAbSwitcher>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < switchers.Length; i++)
            if (switchers[i] != null) switchers[i].enabled = false;

        var follows = Object.FindObjectsByType<FollowTargetCamera>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < follows.Length; i++)
            if (follows[i] != null) follows[i].enabled = false;

        var paths = Object.FindObjectsByType<CameraPathFollower>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < paths.Length; i++)
            if (paths[i] != null) paths[i].enabled = false;

        var cams = Object.FindObjectsByType<Camera>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        Camera camA = null;
        for (int i = 0; i < cams.Length; i++)
        {
            var c = cams[i];
            if (c == null) continue;
            string n = c.gameObject.name;
            if (n.IndexOf("CamA", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Main Camera", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                camA = c;
                break;
            }
        }
        if (camA == null)
        {
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] != null && cams[i].CompareTag("MainCamera"))
                {
                    camA = cams[i];
                    break;
                }
            }
        }
        if (camA == null)
        {
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] != null)
                {
                    camA = cams[i];
                    break;
                }
            }
        }

        for (int i = 0; i < cams.Length; i++)
        {
            var c = cams[i];
            if (c == null) continue;
            string n = c.gameObject.name;
            bool isB = n.IndexOf("CamB", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool keep = c == camA && !isB;
            c.enabled = keep;
            if (isB)
                c.gameObject.SetActive(false);
        }

        if (camA != null)
        {
            camA.gameObject.SetActive(true);
            camA.enabled = true;
            camA.orthographic = false; // не top-down
            if (!camA.CompareTag("MainCamera"))
                camA.tag = "MainCamera";
            var listeners = Object.FindObjectsByType<AudioListener>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < listeners.Length; i++)
            {
                if (listeners[i] == null) continue;
                listeners[i].enabled = listeners[i].GetComponent<Camera>() == camA;
            }
            if (camA.GetComponent<AudioListener>() == null)
                camA.gameObject.AddComponent<AudioListener>();
            Debug.Log($"[StreamingSurvival] CamA only: {camA.gameObject.name}");
        }
    }

    void CacheJackVisualTemplate()
    {
        if (_jackVisualTemplate != null)
            return;
        var jacks = Object.FindObjectsByType<AgentGoToHouseDiscrete>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        // сначала именно JackHero (не Chubby/Jack)
        for (int i = 0; i < jacks.Length; i++)
        {
            var j = jacks[i];
            if (j == null || TrainingEnvSpace.IsGeorgeAgent(j)) continue;
            if (IsSsClone(j.gameObject)) continue;
            if (j.gameObject.name == "JackHero")
            {
                _jackVisualTemplate = j.gameObject;
                Debug.Log("[StreamingSurvival] template = JackHero");
                return;
            }
        }
        for (int i = 0; i < jacks.Length; i++)
        {
            var j = jacks[i];
            if (j == null || TrainingEnvSpace.IsGeorgeAgent(j)) continue;
            if (IsSsClone(j.gameObject)) continue;
            if (j.gameObject.name.IndexOf("JackHero", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _jackVisualTemplate = j.gameObject;
                Debug.Log($"[StreamingSurvival] template = {j.gameObject.name}");
                return;
            }
        }
        // fallback только если JackHero нет в сцене
        for (int i = 0; i < jacks.Length; i++)
        {
            var j = jacks[i];
            if (j == null || TrainingEnvSpace.IsGeorgeAgent(j)) continue;
            if (IsSsClone(j.gameObject)) continue;
            string n = j.gameObject.name;
            if (n == "Jack" || n.IndexOf("Chubby", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Chuby", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            _jackVisualTemplate = j.gameObject;
            Debug.LogWarning($"[StreamingSurvival] JackHero не найден, template={n}");
            return;
        }
    }

    void ResolveHousePoint()
    {
        if (_housePoint != null && IsPlayableGround(_housePoint.position)
            && _housePoint.name != "SS_FollowerBase")
            return;

        string[] exact = { "HomeSpot", "Fire", "House", "Cabin" };
        var root = TrainingEnvSpace.PresentationRoot;
        Transform[] all = root != null
            ? root.GetComponentsInChildren<Transform>(true)
            : Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int n = 0; n < exact.Length; n++)
        {
            string want = exact[n];
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null || t.name != want) continue;
                if (!IsPlayableGround(t.position)) continue;
                _housePoint = t;
                Debug.Log($"[StreamingSurvival] housePoint={t.name} pos={t.position}");
                return;
            }
        }

        var named = FindNearestName("HomeSpot", "House", "Cabin", "Fire");
        if (named != null && IsPlayableGround(named.position)
            && named.name.IndexOf("Follower", System.StringComparison.OrdinalIgnoreCase) < 0)
        {
            _housePoint = named;
            Debug.Log($"[StreamingSurvival] housePoint~={named.name} pos={named.position}");
        }
    }

    void ResolveBasePoint()
    {
        ResolveHousePoint();
        if (_housePoint != null && IsPlayableGround(_housePoint.position))
        {
            _basePoint = _housePoint;
            return;
        }

        if (_basePoint != null && IsPlayableGround(_basePoint.position)
            && _basePoint.name != "SS_FollowerBase")
            return;

        ResolveFollowerSpawn(force: true);
        if (_followerSpawnReady)
        {
            var go = GameObject.Find("SS_FollowerBase");
            if (go == null)
            {
                go = new GameObject("SS_FollowerBase");
                var root = TrainingEnvSpace.PresentationRoot;
                if (root != null)
                    go.transform.SetParent(root, false);
            }
            go.transform.position = _followerSpawnWorld;
            _basePoint = go.transform;
            Debug.LogWarning($"[StreamingSurvival] basePoint=flowerCenter (no HomeSpot) {_followerSpawnWorld}");
        }
    }

    public static bool IsPlayableGround(Vector3 world)
    {
        // цветы ~z=13, деревья ~z=18..32; всё что z<8 — за нижним забором
        return world.z >= 8f;
    }

    void ResolveFollowerSpawn(bool force)
    {
        if (_followerSpawnReady && !force)
            return;

        var flowers = FlowerSpawner.FindInPresentation();
        if (flowers != null)
        {
            _followerSpawnWorld = flowers.GetAreaCenterWorld();
            _followerSpawnReady = true;
            Debug.Log($"[StreamingSurvival] followerSpawn(flowerCenter)={_followerSpawnWorld}");
            return;
        }

        // fallback: рядом с базой, но не Jack под забором
        ResolveBasePoint();
        if (_basePoint != null)
        {
            _followerSpawnWorld = _basePoint.position + new Vector3(0f, 0f, 8f);
            _followerSpawnReady = true;
            Debug.LogWarning($"[StreamingSurvival] FlowerSpawner не найден, spawn≈base+Z {_followerSpawnWorld}");
        }
    }

    /// <summary>Кольцо у цветов: все на одной высоте Y, без наложения CharacterController.</summary>
    public Vector3 NextFollowerSpawnPos(int slotIndex)
    {
        ResolveFollowerSpawn(force: false);
        Vector3 c = FollowerSpawnWorld;
        // Wider ring so 5+ viewers do not jam each other's CharacterController.
        float ang = slotIndex * 1.15f;
        float r = 1.6f + (slotIndex % 4) * 0.55f;
        Vector3 p = c + new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
        p.y = c.y;
        return p;
    }

    void TeleportAllToFollowerSpawn()
    {
        ResolveFollowerSpawn(force: true);
        int i = 0;
        foreach (var kv in _players)
        {
            var p = kv.Value;
            if (p == null) continue;
            // Do NOT yank agents mid #do (water/wood/home). That created 15–20m
            // "max jump" teleports every stageSeconds and failed continuity checks.
            string act = (p.Action ?? "").ToLowerInvariant();
            if (!string.IsNullOrEmpty(act)
                && act != "idle"
                && act != "idle_stand"
                && act != "idle_wander")
            {
                Debug.Log(
                    $"[StreamingSurvival] round_start skip teleport user={p.Username} action={act}");
                i++;
                continue;
            }
            Vector3 pos = NextFollowerSpawnPos(i++);
            p.TeleportTo(pos, "round_start");
        }
    }

    public void ClearAllPlayers()
    {
        var keys = new List<string>(_players.Keys);
        for (int i = 0; i < keys.Count; i++)
            RemovePlayer(keys[i]);
        RefreshTable();
        Debug.Log("[StreamingSurvival] ClearAllPlayers");
    }

    void StartNewRound(string banner)
    {
        _water = 0;
        _wood = 0;
        _food = 0;
        _heat = 0;
        _stone = 0;
        _roundEndsAt = Time.time + stageSeconds;
        ShowBanner(banner, 3f);
        ExtinguishWorldFires();
        TeleportAllToFollowerSpawn();
        foreach (var kv in _players)
        {
            if (kv.Value != null)
                kv.Value.ResumeLastAction();
        }
        RefreshTable();
    }

    /// <summary>Test-only: zero resources without ending the round.</summary>
    public void ResetResourcesForTest()
    {
        ResetResourcesForTest(stageSeconds);
    }

    public void ResetResourcesForTest(float holdSeconds)
    {
        _water = 0;
        _wood = 0;
        _food = 0;
        _heat = 0;
        _stone = 0;
        _inPause = false;
        _roundEndsAt = Time.time + Mathf.Max(30f, holdSeconds);
        LogResources("test_reset");
    }

    void EndRound(bool success)
    {
        if (_inPause)
            return;
        _inPause = true;
        _pauseUntil = Time.time + InterRoundPause;
        ShowBanner(
            success ? "Этап пройден. Вы выжили." : "Конец этапа, вы не справились.",
            InterRoundPause);
        Debug.Log(
            $"[SSRes] ROUND_{(success ? "WIN" : "LOSE")} water={_water} wood={_wood} food={_food} heat={_heat} goal={Goal}");
        LogResources(success ? "round_win" : "round_lose");
    }

    public void ShowBanner(string text, float seconds)
    {
        _banner = text ?? "";
        _bannerUntil = Time.time + Mathf.Max(0.5f, seconds);
    }

    public void AddResource(string kind, int amount = 1)
    {
        if (amount <= 0 || _inPause)
            return;
        switch ((kind ?? "").ToLowerInvariant())
        {
            case "water": _water = Mathf.Min(Goal * 2, _water + amount); break;
            case "wood": _wood = Mathf.Min(Goal * 2, _wood + amount); break;
            case "food": _food = Mathf.Min(Goal * 2, _food + amount); break;
            case "heat": _heat = Mathf.Min(Goal * 2, _heat + amount); break;
            case "stone": _stone = Mathf.Min(Goal * 2, _stone + amount); break;
        }
        Debug.Log($"[SSRes] water={_water} wood={_wood} food={_food} heat={_heat} stone={_stone} goal={Goal} (+{kind}:{amount})");
        // Do not emit trajectory here — player emits resource_added after ResourceGuard.
    }

    public void LogResources(string tag)
    {
        Debug.Log($"[SSRes] tag={tag} water={_water} wood={_wood} food={_food} heat={_heat} stone={_stone} goal={Goal} players={_players.Count}");
    }

    public StreamingSurvivalPlayer EnsurePlayer(string username)
    {
        string key = (username ?? "viewer").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
            key = "viewer";
        if (_players.TryGetValue(key, out var existing) && existing != null)
            return existing;

        ResolveFollowerSpawn(force: false);
        CacheJackVisualTemplate();
        GameObject go = SpawnJackLikeBody(key);
        int slot = _players.Count;
        bool firstPlayer = _players.Count == 0;
        Vector3 spawn = NextFollowerSpawnPos(slot);
        go.transform.position = spawn;

        // Parent under an active hierarchy. Inactive PresentationRoot kills Update().
        var root = TrainingEnvSpace.PresentationRoot;
        if (root != null && root.gameObject.activeInHierarchy)
            go.transform.SetParent(root, true);
        else
            go.transform.SetParent(transform, true);
        if (!go.activeSelf)
            go.SetActive(true);

        var cc = go.GetComponent<CharacterController>();
        if (cc == null)
        {
            cc = go.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.35f;
            cc.center = new Vector3(0f, 0.9f, 0f);
        }
        cc.enabled = true;

        var player = go.GetComponent<StreamingSurvivalPlayer>();
        if (player == null)
            player = go.AddComponent<StreamingSurvivalPlayer>();
        player.enabled = true;
        player.Setup(username, "idle", "Ждёт у базы");
        // Setup мог сдвинуть Y raycast'ом — вернём на высоту спавна цветов
        player.TeleportTo(spawn, "join_spawn");
        _players[key] = player;
        IgnoreCollisionsWithOtherPlayers(player);
        // всегда обновляем таймер раунда при новом игроке (не телепортируем остальных)
        _inPause = false;
        _roundEndsAt = Time.time + stageSeconds;
        if (firstPlayer)
        {
            ShowBanner("Команда в игре — раунд!", 3f);
            Debug.Log($"[SSRes] ROUND_START_FIRST_JOIN stage={stageSeconds}s");
        }
        else
            Debug.Log($"[SSRes] ROUND_TIMER_REFRESH players={_players.Count} left={RoundSecondsLeft:F0}s");
        RefreshTable();
        Debug.Log($"[StreamingSurvival] spawned {go.name} at {spawn} flowerCenter={FollowerSpawnWorld}");
        return player;
    }

    GameObject SpawnJackLikeBody(string key)
    {
        if (_jackVisualTemplate != null)
        {
            bool wasActive = _jackVisualTemplate.activeSelf;
            _jackVisualTemplate.SetActive(false);
            var go = Object.Instantiate(_jackVisualTemplate);
            _jackVisualTemplate.SetActive(wasActive);
            go.name = "SSPlayer_" + key;
            go.SetActive(false);
            StripTrainingComponents(go);
            foreach (var an in go.GetComponentsInChildren<Animator>(true))
            {
                if (an == null) continue;
                an.applyRootMotion = false;
                an.SetFloat("Speed", 0f);
            }
            go.SetActive(true);
            return go;
        }

        var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        capsule.name = "SSPlayer_" + key;
        Object.Destroy(capsule.GetComponent<Collider>());
        capsule.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
        return capsule;
    }

    static void StripTrainingComponents(GameObject go)
    {
        if (go == null) return;

        // Отключаем training/policy компоненты на клоне Jack (иначе клон сам бежит)
        foreach (var a in go.GetComponentsInChildren<Agent>(true))
            if (a != null) { a.enabled = false; Object.Destroy(a); }
        foreach (var d in go.GetComponentsInChildren<DecisionRequester>(true))
            if (d != null) { d.enabled = false; Object.Destroy(d); }
        foreach (var b in go.GetComponentsInChildren<BehaviorParameters>(true))
            if (b != null) { b.enabled = false; Object.Destroy(b); }
        foreach (var l in go.GetComponentsInChildren<LilyScript>(true))
            if (l != null) { l.enabled = false; Object.Destroy(l); }

        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
        {
            if (rb == null) continue;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.useGravity = false;
        }

        foreach (var nav in go.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true))
        {
            if (nav == null) continue;
            nav.enabled = false;
            Object.Destroy(nav);
        }

        foreach (var an in go.GetComponentsInChildren<Animator>(true))
        {
            if (an == null) continue;
            an.applyRootMotion = false;
            an.SetFloat("Speed", 0f);
        }

        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            if (mb is StreamingSurvivalPlayer) continue;
            string tn = mb.GetType().Name;
            if (tn.IndexOf("Sensor", System.StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("RayPerception", System.StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("Lidar", System.StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("Observation", System.StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("Reward", System.StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("Training", System.StringComparison.OrdinalIgnoreCase) >= 0
                || tn.IndexOf("ViewerSimple", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                mb.enabled = false;
                Object.Destroy(mb);
            }
        }
    }

    public void RemovePlayer(string username)
    {
        string key = (username ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
            return;
        if (_players.TryGetValue(key, out var p) && p != null)
        {
            Destroy(p.gameObject);
        }
        _players.Remove(key);
        RefreshTable();
        Debug.Log($"[StreamingSurvival] removed {key}");
    }

    /// <summary>
    /// Viewer capsules must not block each other near spawn (CC.Move freezes otherwise).
    /// Terrain / fence collisions stay enabled.
    /// </summary>
    void IgnoreCollisionsWithOtherPlayers(StreamingSurvivalPlayer newbie)
    {
        if (newbie == null) return;
        var aCols = newbie.GetComponentsInChildren<Collider>(true);
        if (aCols == null || aCols.Length == 0) return;
        foreach (var kv in _players)
        {
            var other = kv.Value;
            if (other == null || other == newbie) continue;
            var bCols = other.GetComponentsInChildren<Collider>(true);
            if (bCols == null) continue;
            for (int i = 0; i < aCols.Length; i++)
            {
                if (aCols[i] == null) continue;
                for (int j = 0; j < bCols.Length; j++)
                {
                    if (bCols[j] == null) continue;
                    Physics.IgnoreCollision(aCols[i], bCols[j], true);
                }
            }
        }
        // CharacterControllers also collide with each other via the physics engine.
        var aCc = newbie.GetComponent<CharacterController>();
        if (aCc == null) return;
        foreach (var kv in _players)
        {
            var other = kv.Value;
            if (other == null || other == newbie) continue;
            var bCc = other.GetComponent<CharacterController>();
            if (bCc == null) continue;
            Physics.IgnoreCollision(aCc, bCc, true);
        }
    }

    public StreamingSurvivalPlayer GetPlayer(string username)
    {
        string key = (username ?? "viewer").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
            key = "viewer";
        if (_players.TryGetValue(key, out var p) && p != null)
            return p;
        return null;
    }

    public void ApplyAction(string username, string action, string actionName, int amount = 1, string actionQueue = null)
    {
        var p = EnsurePlayer(username);
        p.SetAction(action, actionName, amount, actionQueue);
        RefreshTable();
    }

    public void SyncUsers(List<StreamingSurvivalUserDto> users)
    {
        // только состав игроков: кто есть / кого убрать.
        // НЕ перезатирать SetAction — иначе сбрасывается очередь и прогресс добычи.
        var keep = new HashSet<string>();
        if (users != null)
        {
            for (int i = 0; i < users.Count; i++)
            {
                var u = users[i];
                if (u == null || string.IsNullOrEmpty(u.username))
                    continue;
                string key = u.username.Trim().ToLowerInvariant();
                keep.Add(key);
                EnsurePlayer(u.username);
            }
        }

        var toRemove = new List<string>();
        foreach (var kv in _players)
        {
            if (!keep.Contains(kv.Key))
                toRemove.Add(kv.Key);
        }
        for (int i = 0; i < toRemove.Count; i++)
            RemovePlayer(toRemove[i]);

        RefreshTable();
    }

    public List<(string user, string actionName)> SnapshotPlayers()
    {
        var list = new List<(string, string)>();
        foreach (var kv in _players)
        {
            if (kv.Value == null) continue;
            list.Add((kv.Value.Username, kv.Value.ActionName));
        }
        return list;
    }

    void RefreshTable()
    {
        if (_table != null)
            _table.Rebuild(SnapshotPlayers());
    }

    static Transform FindNearestName(params string[] needles)
    {
        var root = TrainingEnvSpace.PresentationRoot;
        Transform[] all;
        Vector3 origin;
        if (root != null)
        {
            all = root.GetComponentsInChildren<Transform>(true);
            origin = root.position;
        }
        else
        {
            all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            origin = Vector3.zero;
        }

        Transform best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < all.Length; i++)
        {
            var tr = all[i];
            if (tr == null) continue;
            string n = tr.name;
            bool hit = false;
            for (int k = 0; k < needles.Length; k++)
            {
                if (n.IndexOf(needles[k], System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) continue;
            float d = (tr.position - origin).sqrMagnitude;
            if (d < bestD)
            {
                bestD = d;
                best = tr;
            }
        }
        return best;
    }

    static void DisableAllOfTypeGlobal<T>() where T : Behaviour
    {
        var arr = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i] == null) continue;
            arr[i].enabled = false;
            arr[i].gameObject.SetActive(false);
        }
    }
}

public sealed class StreamingSurvivalUserDto
{
    public string username;
    public string action;
    public string action_name;
}
