using Unity.MLAgents;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Actuators;
using UnityEngine;

/// <summary>
/// Профиль задачи на корне Env. Auto + переименование копий Env (1)…Env (11).
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
    [SerializeField] private int simpleMaxSteps = 400;
    [SerializeField] private float simpleEpisodeTimeoutSeconds = 45f;
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

    public static EnvTrainingTask ResolveAutoTaskForCopyIndex(int copyIndex)
    {
        switch (copyIndex)
        {
            case 0: return EnvTrainingTask.PresentationFull;
            case 1: return EnvTrainingTask.JackWood;
            case 2: return EnvTrainingTask.JackFood;
            case 3: return EnvTrainingTask.JackWater;
            case 4: return EnvTrainingTask.JackZombie;
            case 5: return EnvTrainingTask.LilyFood;
            case 6: return EnvTrainingTask.LilyWater;
            case 7: return EnvTrainingTask.LilyHeat;
            case 8: return EnvTrainingTask.LilyFlower;
            case 9: return EnvTrainingTask.GeorgeFood;
            case 10: return EnvTrainingTask.GeorgeWater;
            case 11: return EnvTrainingTask.GeorgeHeat;
            default:
                return copyIndex % 2 == 0 ? EnvTrainingTask.JackWood : EnvTrainingTask.JackFood;
        }
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

        var resolved = ResolveTask();
        ApplyTrainingCampfire(resolved);
        ApplyAgentVisibility(resolved);
        ApplyAgentRoles(resolved);
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

        var resolved = ResolveTask();
        ApplyTrainingCampfire(resolved);
        ApplyAgentVisibility(resolved);
        if (TrainingEnvSpace.IsMlAgentsTrainingActive())
            ApplyAgentRoles(resolved);
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
        if (!TrainingEnvSpace.IsMlAgentsTrainingActive())
            return;
        // Presentation worker тоже Default+train (пока нет sentis hot reload на сервере).
        if (TrainingEnvSpace.IsPresentationWorkerProcess
            && resolved != EnvTrainingTask.PresentationFull)
            return;

        var agents = GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < agents.Length; i++)
        {
            var agent = agents[i];
            if (agent == null || !agent.gameObject.activeInHierarchy
                || TwitchEphemeralEffects.IsTwitchClone(agent))
                continue;

            var role = TrainingEnvSpace.IsGeorgeAgent(agent)
                ? EnvTrainingAgentRole.George
                : EnvTrainingAgentRole.Jack;
            if (agent.gameObject.name == "Jack" || agent.gameObject.name == "George")
                continue;
            SetAgentTrainingEnabled(agent, ShouldAgentTrain(resolved, role));
        }

        var lilies = GetComponentsInChildren<LilyScript>(true);
        for (int i = 0; i < lilies.Length; i++)
        {
            var lily = lilies[i];
            if (lily == null || !lily.gameObject.activeInHierarchy
                || TwitchEphemeralEffects.IsTwitchClone(lily)
                || lily.gameObject.name == "Lily")
                continue;

            SetAgentTrainingEnabled(lily, ShouldAgentTrain(resolved, EnvTrainingAgentRole.Lily));
        }
    }

    static void SetAgentTrainingEnabled(Agent agent, bool train)
    {
        var bp = agent.GetComponent<BehaviorParameters>();
        if (bp == null)
            return;

        bp.BehaviorType = train ? BehaviorType.Default : BehaviorType.HeuristicOnly;
        var dr = agent.GetComponent<DecisionRequester>();
        if (dr != null)
            dr.enabled = train;
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
