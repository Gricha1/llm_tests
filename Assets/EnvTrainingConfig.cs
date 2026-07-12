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
        ApplyAgentRoles(resolved);
        ApplyTrainingCampfire(resolved);
    }

    void ApplyAgentRoles(EnvTrainingTask resolved)
    {
        if (!TrainingEnvSpace.IsMlAgentsTrainingActive())
            return;

        var agents = GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < agents.Length; i++)
        {
            var agent = agents[i];
            if (agent == null || TwitchEphemeralEffects.IsTwitchClone(agent))
                continue;

            var role = TrainingEnvSpace.IsGeorgeAgent(agent)
                ? EnvTrainingAgentRole.George
                : EnvTrainingAgentRole.Jack;
            SetAgentTrainingEnabled(agent, ShouldAgentTrain(resolved, role));
        }

        var lilies = GetComponentsInChildren<LilyScript>(true);
        for (int i = 0; i < lilies.Length; i++)
        {
            var lily = lilies[i];
            if (lily == null || TwitchEphemeralEffects.IsTwitchClone(lily))
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
        if (resolved != EnvTrainingTask.LilyHeat && resolved != EnvTrainingTask.GeorgeHeat)
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
