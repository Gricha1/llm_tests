using Unity.MLAgents;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Actuators;
using UnityEngine;

/// <summary>
/// Профиль задачи на корне Env. Auto + copyIndex 0…N.
/// Workers 12+: фиксированный буст (2× wood/water/zombie Jack, 2× water Lily/George).
/// </summary>
public enum EnvTrainingTask
{
    Auto,
    PresentationFull,
    JackWood,
    JackFood,
    JackWater,
    JackZombie,
    LilyFood,
    LilyWater,
    LilyHeat,
    LilyFlower,
    GeorgeFood,
    GeorgeWater,
    GeorgeHeat,
}

public enum EnvTrainingAgentRole
{
    Jack,
    Lily,
    George,
}

public sealed class EnvTrainingConfig : MonoBehaviour
{
    const float TrainingCampfireSeconds = 99999f;

    [SerializeField] private EnvTrainingTask task = EnvTrainingTask.Auto;

    [Header("Simple modes")]
    [SerializeField] private int simpleMaxSteps = 1500;
    [SerializeField] private float simpleEpisodeTimeoutSeconds = 90f;
    [SerializeField] private bool endOnSuccess = true;
    [SerializeField] private bool freezeNeeds = true;
    [SerializeField] private float successReward = 5f;
    [SerializeField] private float stepPenalty = -0.001f;

    [Header("ZombieOnly (Jack Env 4)")]
    [SerializeField] private int zombieMaxSteps = 3000;
    [SerializeField] private float zombieEpisodeTimeoutSeconds = 180f;
    [SerializeField] private bool zombieEndOnKill = false;
    [SerializeField] private int zombieImmediateSpawnCount = 2;

    int _lastSetupFrame = -1;
    static int _forcedCopyIndex = -1;

    bool _heroCacheReady;
    EnvTrainingTask _visibilityTaskApplied = (EnvTrainingTask)(-1);
    Transform _jackHero;
    Transform _lilyHero;
    Transform _georgeHero;
    Transform _legacyJack;
    Transform _legacyLily;
    Transform _legacyGeorge;
    bool _initialSetupDone;

    public static void SetForcedCopyIndex(int copyIndex) => _forcedCopyIndex = copyIndex;
    public static void ClearForcedCopyIndex() => _forcedCopyIndex = -1;

    public EnvTrainingTask Task => task;
    public int SimpleMaxSteps => simpleMaxSteps;
    public float SimpleEpisodeTimeoutSeconds => simpleEpisodeTimeoutSeconds;

    /// <summary>Старые сцены могли сериализовать 400/45 — для wood/food/water этого мало.</summary>
    public int ResolveSimpleMaxSteps()
    {
        int floor = IsJackSimpleTask() || IsLilySimpleTask() || IsGeorgeSimpleTask() ? 1500 : 0;
        return Mathf.Max(simpleMaxSteps, floor);
    }

    public float ResolveSimpleEpisodeTimeoutSeconds()
    {
        float floor = IsJackSimpleTask() || IsLilySimpleTask() || IsGeorgeSimpleTask() ? 90f : 0f;
        return Mathf.Max(simpleEpisodeTimeoutSeconds, floor);
    }
    public int ZombieMaxSteps => zombieMaxSteps;
    public float ZombieEpisodeTimeoutSeconds => zombieEpisodeTimeoutSeconds;
    public bool ZombieEndOnKill => zombieEndOnKill;
    public int ZombieImmediateSpawnCount => zombieImmediateSpawnCount;
    public bool EndOnSuccess => endOnSuccess;
    public bool FreezeNeeds => freezeNeeds;
    public float SuccessReward => successReward;
    public float StepPenalty => stepPenalty;

    public EnvTrainingTask ResolveTask()
    {
        if (_forcedCopyIndex >= 0)
            return ResolveAutoTaskForCopyIndex(_forcedCopyIndex);

        if (task != EnvTrainingTask.Auto)
            return task;

        if (TrainingEnvSpace.HasMultipleTrainingEnvs()
            && TrainingEnvSpace.IsPresentationEnv(transform))
            return EnvTrainingTask.PresentationFull;

        int copyIndex = TrainingEnvSpace.GetEnvCopyIndex(transform);

        if (!TrainingEnvSpace.HasMultipleTrainingEnvs()
            && copyIndex == 0
            && TrainingEnvSpace.IsPresentationEnv(transform)
            && !TrainingEnvSpace.IsMlAgentsTrainingActive())
            return EnvTrainingTask.PresentationFull;

        return ResolveAutoTaskForCopyIndex(copyIndex);
    }

    /// <summary>
    /// w12+: 3× JackWood, 3× JackWater, 3× JackZombie, 3× LilyWater, 3× GeorgeWater (далее цикл).
    /// </summary>
    static readonly EnvTrainingTask[] FixedBoostTasks =
    {
        EnvTrainingTask.JackWood,
        EnvTrainingTask.JackWood,
        EnvTrainingTask.JackWood,
        EnvTrainingTask.JackWater,
        EnvTrainingTask.JackWater,
        EnvTrainingTask.JackWater,
        EnvTrainingTask.JackZombie,
        EnvTrainingTask.JackZombie,
        EnvTrainingTask.JackZombie,
        EnvTrainingTask.LilyWater,
        EnvTrainingTask.LilyWater,
        EnvTrainingTask.LilyWater,
        EnvTrainingTask.GeorgeWater,
        EnvTrainingTask.GeorgeWater,
        EnvTrainingTask.GeorgeWater,
    };

    /// <summary>
    /// Jack-only: wood:food:water:zombie ≈ 2:1:2:2.
    /// На 30 слотах: 9 wood, 4 food, 9 water, 8 zombie.
    /// </summary>
    public static bool IsJackOnlyTasksMode()
    {
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_JACK_ONLY_TASKS")))
            return true;

        foreach (var arg in System.Environment.GetCommandLineArgs())
        {
            if (arg == "-forestJackOnlyTasks" || arg == "--forest-jack-only-tasks")
                return true;
        }

        return false;
    }

    static bool IsTruthyEnv(string value) =>
        value == "1" || string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);

    public static EnvTrainingTask ResolveJackOnlyTaskForCopyIndex(int copyIndex)
    {
        // Период 30: 9+4+9+8 — ровно 2:1:2:2 при NUM_ENVS=30.
        int i = copyIndex < 0 ? 0 : copyIndex % 30;
        if (i < 9)
            return EnvTrainingTask.JackWood;
        if (i < 13)
            return EnvTrainingTask.JackFood;
        if (i < 22)
            return EnvTrainingTask.JackWater;
        return EnvTrainingTask.JackZombie;
    }

    public static EnvTrainingTask ResolveAutoTaskForCopyIndex(int copyIndex)
    {
        if (IsJackOnlyTasksMode())
            return ResolveJackOnlyTaskForCopyIndex(copyIndex);

        // 0 — стрим PresentationFull; 1 — train PresentationFull (все трое);
        // 2–12 — узкие; 13+ — буст.
        switch (copyIndex)
        {
            case 0: return EnvTrainingTask.PresentationFull;
            case 1: return EnvTrainingTask.PresentationFull;
            case 2: return EnvTrainingTask.JackWood;
            case 3: return EnvTrainingTask.JackFood;
            case 4: return EnvTrainingTask.JackWater;
            case 5: return EnvTrainingTask.JackZombie;
            case 6: return EnvTrainingTask.LilyFood;
            case 7: return EnvTrainingTask.LilyWater;
            case 8: return EnvTrainingTask.LilyHeat;
            case 9: return EnvTrainingTask.LilyFlower;
            case 10: return EnvTrainingTask.GeorgeFood;
            case 11: return EnvTrainingTask.GeorgeWater;
            case 12: return EnvTrainingTask.GeorgeHeat;
            default:
                if (copyIndex >= 13)
                {
                    int i = (copyIndex - 13) % FixedBoostTasks.Length;
                    return FixedBoostTasks[i];
                }
                return EnvTrainingTask.JackWood;
        }
    }

    /// <summary>Раньше фиксировал dynamic boost на эпизод; сейчас w12+ заданы жёстко в FixedBoostTasks.</summary>
    public void CommitBoostTaskIfNeeded()
    {
    }

    public JackTrainingMode ResolveJackMode()
    {
        switch (ResolveTask())
        {
            case EnvTrainingTask.JackWood: return JackTrainingMode.WoodOnly;
            case EnvTrainingTask.JackFood: return JackTrainingMode.FoodOnly;
            case EnvTrainingTask.JackWater: return JackTrainingMode.WaterOnly;
            case EnvTrainingTask.JackZombie: return JackTrainingMode.ZombieOnly;
            case EnvTrainingTask.GeorgeFood:
            case EnvTrainingTask.GeorgeWater:
            case EnvTrainingTask.GeorgeHeat:
                return JackTrainingMode.Full;
            default: return JackTrainingMode.Full;
        }
    }

    public bool IsJackSimpleTask()
    {
        var t = ResolveTask();
        return t == EnvTrainingTask.JackWood
            || t == EnvTrainingTask.JackFood
            || t == EnvTrainingTask.JackWater
            || t == EnvTrainingTask.JackZombie;
    }

    public bool IsLilySimpleTask()
    {
        var t = ResolveTask();
        return t == EnvTrainingTask.LilyFood
            || t == EnvTrainingTask.LilyWater
            || t == EnvTrainingTask.LilyHeat
            || t == EnvTrainingTask.LilyFlower;
    }

    public bool IsGeorgeSimpleTask()
    {
        var t = ResolveTask();
        return t == EnvTrainingTask.GeorgeFood
            || t == EnvTrainingTask.GeorgeWater
            || t == EnvTrainingTask.GeorgeHeat;
    }

    public static bool ShouldAgentTrain(EnvTrainingTask task, EnvTrainingAgentRole role)
    {
        // Jack-only train: в yaml только JackLowLevelAgent — Lily/George не должны
        // регистрироваться как Default, иначе TrainerConfigError.
        if (IsJackOnlyTasksMode())
            return role == EnvTrainingAgentRole.Jack;

        switch (task)
        {
            case EnvTrainingTask.PresentationFull:
                return true;
            case EnvTrainingTask.JackWood:
            case EnvTrainingTask.JackFood:
            case EnvTrainingTask.JackWater:
            case EnvTrainingTask.JackZombie:
                return role == EnvTrainingAgentRole.Jack;
            case EnvTrainingTask.LilyFood:
            case EnvTrainingTask.LilyWater:
            case EnvTrainingTask.LilyHeat:
            case EnvTrainingTask.LilyFlower:
                return role == EnvTrainingAgentRole.Lily;
            case EnvTrainingTask.GeorgeFood:
            case EnvTrainingTask.GeorgeWater:
            case EnvTrainingTask.GeorgeHeat:
                return role == EnvTrainingAgentRole.George;
            default:
                return true;
        }
    }

    public static EnvTrainingConfig Get(Transform anyInEnv)
    {
        var root = TrainingEnvSpace.FindRoot(anyInEnv);
        if (root == null)
            return null;

        var config = root.GetComponent<EnvTrainingConfig>();
        if (config == null)
            config = root.gameObject.AddComponent<EnvTrainingConfig>();
        return config;
    }

    public void ApplyForEpisodeBegin()
    {
        if (_lastSetupFrame == Time.frameCount)
            return;

        _lastSetupFrame = Time.frameCount;

        CommitBoostTaskIfNeeded();
        var resolved = ResolveTask();
        ApplyTrainingCampfire(resolved);
        // Сначала роли (HeuristicOnly / Agent.enabled), потом visibility:
        // иначе спрятанных агентов пропускают и они остаются Default → TrainerConfigError.
        bool applyRoles = TrainingEnvSpace.IsMlAgentsTrainingActive()
            || IsJackOnlyTasksMode()
            || TrainingEnvSpace.IsDebugEnvFocusActive;
        if (applyRoles)
            ApplyAgentRoles(resolved);
        ApplyAgentVisibility(resolved);
        EnsureJackZombieSpawnersRunning(resolved);
    }

    /// <summary>
    /// JackZombie: поднять спавнеры. Иначе — убить зомби и выключить спавнеры
    /// (иначе при #env_0 / меню K зомби остаются с прошлой среды).
    /// </summary>
    void EnsureJackZombieSpawnersRunning(EnvTrainingTask resolved)
    {
        if (resolved != EnvTrainingTask.JackZombie)
        {
            StopJackZombieSpawnersInThisEnv();
            return;
        }

        // Меню K: оба домовых спавнера active; hills off. Спавн — ForceStart (по 1 из дома).
        if (TrainingEnvSpace.IsDebugEnvFocusActive)
        {
            var debugSpawners = GetComponentsInChildren<ZombieSpawner>(true);
            for (int i = 0; i < debugSpawners.Length; i++)
            {
                var spawner = debugSpawners[i];
                if (spawner == null)
                    continue;
                string n = spawner.gameObject.name;
                if (n.IndexOf("Hills", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    spawner.ClearZombies(scanOrphanRoots: false);
                    if (spawner.gameObject.activeSelf)
                        spawner.gameObject.SetActive(false);
                    continue;
                }

                if (!spawner.gameObject.activeSelf)
                    spawner.gameObject.SetActive(true);
            }
            return;
        }

        int immediate = Mathf.Max(1, zombieImmediateSpawnCount);
        var spawners = GetComponentsInChildren<ZombieSpawner>(true);
        int started = 0;
        for (int i = 0; i < spawners.Length; i++)
        {
            var spawner = spawners[i];
            if (spawner == null)
                continue;
            if (spawner.gameObject.name.IndexOf("Hills", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            if (!spawner.gameObject.activeSelf)
                spawner.gameObject.SetActive(true);

            int spawned = spawner.StartTrainingEpisode(immediate);
            started++;
            if (spawned <= 0)
            {
                Debug.LogWarning(
                    $"[{name}] JackZombie: {spawner.gameObject.name} не создал зомби " +
                    $"(проверь zombiePrefab / FatZombie.prefab).",
                    spawner);
            }
            else
            {
                Debug.Log(
                    $"[{name}] JackZombie: {spawner.gameObject.name} → зомби +{spawned}",
                    spawner);
            }
        }

        if (started == 0)
        {
            Debug.LogError(
                $"[{name}] JackZombie: нет ZombieSpawner / ZombieSpawner_2 в Env — зомби не появятся.");
        }
    }

    void StopJackZombieSpawnersInThisEnv()
    {
        var spawners = GetComponentsInChildren<ZombieSpawner>(true);
        for (int i = 0; i < spawners.Length; i++)
        {
            var spawner = spawners[i];
            if (spawner == null)
                continue;
            if (spawner.gameObject.name.IndexOf("Hills", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            spawner.ClearZombies();
            if (spawner.gameObject.activeSelf)
                spawner.gameObject.SetActive(false);
        }
    }

    /// <summary>Принудительно переприменить задачу (переключение среды в Play).</summary>
    public void ForceApplyTaskSetup()
    {
        _visibilityTaskApplied = (EnvTrainingTask)(-1);
        _lastSetupFrame = -1;
        ApplyForEpisodeBegin();
    }

    public void ApplyInitialSetup()
    {
        if (_initialSetupDone)
            return;

        _initialSetupDone = true;
        EnsureHeroCache();
        HideLegacyHeroShells();

        if (TrainingEnvSpace.IsSingleEnvPlayMode()
            && TrainingEnvSpace.IsPresentationEnv(transform)
            && !TrainingEnvSpace.IsMlAgentsTrainingActive())
            return;

        if (TrainingEnvSpace.IsStreamOnlyMode && TrainingEnvSpace.IsPresentationEnv(transform))
        {
            // Jack-only stream: не показывать/не регистрировать Lily/George.
            if (IsJackOnlyTasksMode())
            {
                var jackTask = ResolveTask();
                ApplyAgentRoles(jackTask);
                ApplyAgentVisibility(jackTask);
                return;
            }
            ApplyAgentVisibility(EnvTrainingTask.PresentationFull);
            return;
        }

        if (TrainingEnvSpace.IsPresentationWorkerProcess)
        {
            ApplyAgentVisibility(EnvTrainingTask.PresentationFull);
            if (TrainingEnvSpace.IsMlAgentsTrainingActive())
                ApplyAgentRoles(EnvTrainingTask.PresentationFull);
            return;
        }

        CommitBoostTaskIfNeeded();
        var resolved = ResolveTask();
        ApplyTrainingCampfire(resolved);
        bool applyRoles = TrainingEnvSpace.IsMlAgentsTrainingActive() || IsJackOnlyTasksMode();
        if (applyRoles)
            ApplyAgentRoles(resolved);
        ApplyAgentVisibility(resolved);
    }

    void EnsureHeroCache()
    {
        if (_heroCacheReady)
            return;

        _heroCacheReady = true;
        var all = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null)
                continue;

            switch (t.name)
            {
                case "JackHero": _jackHero = t; break;
                case "LilyHero": _lilyHero = t; break;
                case "GeorgeHero": _georgeHero = t; break;
                case "Jack": _legacyJack = t; break;
                case "Lily": _legacyLily = t; break;
                case "George": _legacyGeorge = t; break;
            }
        }
    }

    void ApplyAgentVisibility(EnvTrainingTask resolved)
    {
        if (_visibilityTaskApplied == resolved)
            return;

        _visibilityTaskApplied = resolved;
        EnsureHeroCache();
        HideLegacyHeroShells();

        bool showAll = resolved == EnvTrainingTask.PresentationFull;
        SetRoleVisible(EnvTrainingAgentRole.Jack,
            showAll || ShouldAgentTrain(resolved, EnvTrainingAgentRole.Jack));
        SetRoleVisible(EnvTrainingAgentRole.Lily,
            showAll || ShouldAgentTrain(resolved, EnvTrainingAgentRole.Lily));
        SetRoleVisible(EnvTrainingAgentRole.George,
            showAll || ShouldAgentTrain(resolved, EnvTrainingAgentRole.George));
    }

    /// <summary>После миграции Jack→JackHero старый Chuby-меш «Jack» часто остаётся включённым в сцене.</summary>
    void HideLegacyHeroShells()
    {
        DisableLegacyIfHeroExists("Jack", "JackHero");
        DisableLegacyIfHeroExists("Lily", "LilyHero");
        DisableLegacyIfHeroExists("George", "GeorgeHero");
    }

    void DisableLegacyIfHeroExists(string legacyName, string heroName)
    {
        Transform hero = heroName switch
        {
            "JackHero" => _jackHero,
            "LilyHero" => _lilyHero,
            "GeorgeHero" => _georgeHero,
            _ => FindChildByName(heroName),
        };
        if (hero == null)
            return;

        Transform legacy = legacyName switch
        {
            "Jack" => _legacyJack,
            "Lily" => _legacyLily,
            "George" => _legacyGeorge,
            _ => FindChildByName(legacyName),
        };
        if (legacy == null || legacy == hero)
            return;

        if (legacy.gameObject.activeSelf)
            legacy.gameObject.SetActive(false);
    }

    Transform FindChildByName(string objectName)
    {
        var transforms = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            if (transforms[i].name == objectName)
                return transforms[i];
        }

        return null;
    }

    Transform FindRoleHero(EnvTrainingAgentRole role)
    {
        EnsureHeroCache();
        return role switch
        {
            EnvTrainingAgentRole.Jack => _jackHero,
            EnvTrainingAgentRole.Lily => _lilyHero,
            EnvTrainingAgentRole.George => _georgeHero,
            _ => null,
        };
    }

    void SetRoleVisible(EnvTrainingAgentRole role, bool visible)
    {
        var hero = FindRoleHero(role);
        if (hero != null)
        {
            if (hero.gameObject.activeSelf != visible)
                hero.gameObject.SetActive(visible);
            return;
        }

        if (role == EnvTrainingAgentRole.Jack)
        {
            var agents = GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
            for (int i = 0; i < agents.Length; i++)
            {
                var agent = agents[i];
                if (agent == null || TrainingEnvSpace.IsGeorgeAgent(agent)
                    || TwitchEphemeralEffects.IsTwitchClone(agent)
                    || agent.gameObject.name == "Jack")
                    continue;

                if (agent.gameObject.activeSelf != visible)
                    agent.gameObject.SetActive(visible);
            }
            return;
        }

        if (role == EnvTrainingAgentRole.Lily)
        {
            var lilies = GetComponentsInChildren<LilyScript>(true);
            for (int i = 0; i < lilies.Length; i++)
            {
                var lily = lilies[i];
                if (lily == null || TwitchEphemeralEffects.IsTwitchClone(lily)
                    || lily.gameObject.name == "Lily")
                    continue;

                if (lily.gameObject.activeSelf != visible)
                    lily.gameObject.SetActive(visible);
            }
            return;
        }

        var georges = GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < georges.Length; i++)
        {
            var george = georges[i];
            if (george == null || !TrainingEnvSpace.IsGeorgeAgent(george)
                || TwitchEphemeralEffects.IsTwitchClone(george)
                || george.gameObject.name == "George")
                continue;

            if (george.gameObject.activeSelf != visible)
                george.gameObject.SetActive(visible);
        }
    }

    void ApplyAgentRoles(EnvTrainingTask resolved)
    {
        bool debugView = TrainingEnvSpace.IsDebugEnvFocusActive;
        bool mlTraining = TrainingEnvSpace.IsMlAgentsTrainingActive();
        bool jackOnly = IsJackOnlyTasksMode();
        // Play без mlagents: всё равно HeuristicOnly + DecisionRequester для ручного WASD.
        if (!mlTraining && !debugView && !jackOnly)
            return;
        // Presentation worker тоже Default+train (пока нет sentis hot reload на сервере).
        if (TrainingEnvSpace.IsPresentationWorkerProcess
            && resolved != EnvTrainingTask.PresentationFull
            && !jackOnly)
            return;

        var agents = GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < agents.Length; i++)
        {
            var agent = agents[i];
            // Включая неактивных: иначе после Hide ролей Default так и остаётся.
            if (agent == null || TwitchEphemeralEffects.IsTwitchClone(agent))
                continue;

            var role = TrainingEnvSpace.IsGeorgeAgent(agent)
                ? EnvTrainingAgentRole.George
                : EnvTrainingAgentRole.Jack;
            if (agent.gameObject.name == "Jack" || agent.gameObject.name == "George")
                continue;
            bool should = ShouldAgentTrain(resolved, role);
            if (debugView && !mlTraining && !jackOnly)
                SetAgentHeuristicPlayEnabled(agent, should);
            else
                SetAgentTrainingEnabled(agent, should);
        }

        var lilies = GetComponentsInChildren<LilyScript>(true);
        for (int i = 0; i < lilies.Length; i++)
        {
            var lily = lilies[i];
            if (lily == null || TwitchEphemeralEffects.IsTwitchClone(lily)
                || lily.gameObject.name == "Lily")
                continue;

            bool should = ShouldAgentTrain(resolved, EnvTrainingAgentRole.Lily);
            if (debugView && !mlTraining && !jackOnly)
                SetAgentHeuristicPlayEnabled(lily, should);
            else
                SetAgentTrainingEnabled(lily, should);
        }
    }

    static void SetAgentTrainingEnabled(Agent agent, bool train)
    {
        var bp = agent.GetComponent<BehaviorParameters>();
        if (bp == null)
            return;

        bp.BehaviorType = train ? BehaviorType.Default : BehaviorType.HeuristicOnly;
        agent.enabled = train;
        var dr = agent.GetComponent<DecisionRequester>();
        if (dr != null)
            dr.enabled = train;
    }

    /// <summary>Play + меню K: HeuristicOnly и DecisionRequester, иначе WASD не доходит до OnActionReceived.</summary>
    static void SetAgentHeuristicPlayEnabled(Agent agent, bool enableControl)
    {
        var bp = agent.GetComponent<BehaviorParameters>();
        if (bp != null)
            bp.BehaviorType = BehaviorType.HeuristicOnly;
        var dr = agent.GetComponent<DecisionRequester>();
        if (dr == null)
            return;
        dr.enabled = enableControl;
        dr.DecisionPeriod = 1;
    }

    void ApplyTrainingCampfire(EnvTrainingTask resolved)
    {
        // Presentation: костёр только когда Jack сам подносит дрова, не автозажигание.
        if (resolved != EnvTrainingTask.LilyHeat
            && resolved != EnvTrainingTask.GeorgeHeat)
            return;

        var jacks = GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < jacks.Length; i++)
        {
            var jack = jacks[i];
            if (jack == null || TrainingEnvSpace.IsGeorgeAgent(jack))
                continue;

            jack.EnsureTrainingCampfireLit(TrainingCampfireSeconds);
            break;
        }
    }

    public int ResolveFixedJackOption()
    {
        switch (ResolveTask())
        {
            case EnvTrainingTask.JackWood: return AgentGoToHouseDiscrete.OptionWood;
            case EnvTrainingTask.JackFood: return AgentGoToHouseDiscrete.OptionFood;
            case EnvTrainingTask.JackWater: return AgentGoToHouseDiscrete.OptionWater;
            case EnvTrainingTask.JackZombie: return AgentGoToHouseDiscrete.OptionZombie;
            case EnvTrainingTask.GeorgeFood: return AgentGoToHouseDiscrete.OptionFood;
            case EnvTrainingTask.GeorgeWater: return AgentGoToHouseDiscrete.OptionWater;
            case EnvTrainingTask.GeorgeHeat: return AgentGoToHouseDiscrete.OptionHeat;
            default: return AgentGoToHouseDiscrete.OptionFood;
        }
    }

    public int ResolveFixedLilyOption()
    {
        switch (ResolveTask())
        {
            case EnvTrainingTask.LilyFood: return LilyScript.OptionFood;
            case EnvTrainingTask.LilyWater: return LilyScript.OptionWater;
            case EnvTrainingTask.LilyHeat: return LilyScript.OptionHeat;
            case EnvTrainingTask.LilyFlower: return LilyScript.OptionFlower;
            default: return LilyScript.OptionFlower;
        }
    }
}
