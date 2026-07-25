using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>Эффекты Twitch до конца эпизода: клоны Jack, масштаб.</summary>
public static class TwitchEphemeralEffects
{
    sealed class ScaleState
    {
        public Vector3 BaseLocalScale;
        public float BaseHeight;
        public float BaseRadius;
        public Vector3 BaseCenter;
        public bool Captured;
    }

    const int MaxTotalClones = 1;

    /// <summary>Во время SpawnJackClones не даём OnEpisodeBegin оригинала чистить клонов/мир.</summary>
    public static bool IsSpawningJackClone { get; private set; }

    static readonly List<GameObject> Clones = new List<GameObject>();
    static readonly Dictionary<int, ScaleState> ScaleByJackId = new Dictionary<int, ScaleState>();

    public static bool IsTwitchClone(Component c) =>
        c != null && (c.GetComponent<TwitchJackCloneMarker>() != null
            || (c.gameObject != null && c.gameObject.name.StartsWith("Jack_TwitchClone", System.StringComparison.Ordinal)));

    public static void OnPresentationJackEpisodeBegin(AgentGoToHouseDiscrete jack)
    {
        if (IsSpawningJackClone)
            return;

        ClearClones();
        if (jack != null)
        {
            ResetJackScale(jack);
            ResetJackSpeed(jack);
        }
    }

    public static void OnPresentationJackDeath()
    {
        ClearClones();
    }

    public static int ActiveCloneCount
    {
        get
        {
            RemoveDeadClones();
            return Clones.Count;
        }
    }

    public static int SpawnJackClones(AgentGoToHouseDiscrete source, int count)
    {
        if (source == null || !TrainingEnvSpace.IsPresentationTransform(source.transform))
            return 0;

        if (IsTwitchClone(source))
            return 0;

        if (!source.IsAliveForTwitch)
            return 0;

        count = 1;
        RemoveDeadClones();

        if (Clones.Count >= MaxTotalClones)
            return 0;

        int slotsLeft = MaxTotalClones - Clones.Count;
        if (slotsLeft <= 0)
            return 0;

        count = Mathf.Min(count, slotsLeft);

        var sourceBehavior = source.GetComponent<BehaviorParameters>();
        int spawned = 0;
        IsSpawningJackClone = true;
        try
        {
            for (int i = 0; i < count; i++)
            {
                float angle = (Clones.Count + i) * 72f * Mathf.Deg2Rad;
                var offset = new Vector3(Mathf.Cos(angle) * 1.8f, 0f, Mathf.Sin(angle) * 1.8f);
                Vector3 spawnPos = source.transform.position + offset;
                Quaternion spawnRot = source.transform.rotation;

                // Нельзя SetActive(false) на живом Jack: Agent.OnDisable → Done → при
                // включениях OnEpisodeBegin сбрасывает мир. Instantiate под неактивный
                // holder: Awake есть, OnEnable/LazyInitialize — нет.
                var holder = new GameObject("__TwitchCloneSpawnHolder");
                holder.SetActive(false);

                var cloneGo = Object.Instantiate(source.gameObject, holder.transform);
                cloneGo.SetActive(false);
                cloneGo.name = $"Jack_TwitchClone_{Clones.Count + 1}";

                if (cloneGo.GetComponent<TwitchJackCloneMarker>() == null)
                    cloneGo.AddComponent<TwitchJackCloneMarker>();

                cloneGo.transform.SetParent(source.transform.parent, false);
                cloneGo.transform.SetPositionAndRotation(spawnPos, spawnRot);
                Object.Destroy(holder);

                var cloneBehavior = cloneGo.GetComponent<BehaviorParameters>();
                var sourceDecision = source.GetComponent<Unity.MLAgents.DecisionRequester>();
                bool policyFromTrainer = Academy.IsInitialized && Academy.Instance.IsCommunicatorOn;
                if (sourceBehavior != null && cloneBehavior != null)
                {
                    cloneBehavior.BehaviorName = sourceBehavior.BehaviorName;
                    cloneBehavior.Model = sourceBehavior.Model;
                    cloneBehavior.TeamId = sourceBehavior.TeamId;
                    cloneBehavior.DeterministicInference = false;
                    if (policyFromTrainer)
                        cloneBehavior.BehaviorType = BehaviorType.Default;
                    else if (sourceBehavior.Model != null)
                        cloneBehavior.BehaviorType = BehaviorType.InferenceOnly;
                    else
                        cloneBehavior.BehaviorType = BehaviorType.Default;
                }

                var cloneAgent = cloneGo.GetComponent<Agent>();
                var cloneJack = cloneGo.GetComponent<AgentGoToHouseDiscrete>();
                if (cloneAgent != null)
                    cloneAgent.enabled = true;
                if (cloneJack != null)
                {
                    cloneJack.enabled = true;
                    cloneJack.BootstrapTwitchCloneFrom(source, spawnPos, spawnRot);
                }

                cloneGo.SetActive(true);

                if (cloneAgent != null)
                    cloneAgent.LazyInitialize();

                var cloneDecision = cloneGo.GetComponent<Unity.MLAgents.DecisionRequester>();
                if (cloneDecision != null)
                {
                    cloneDecision.enabled = true;
                    if (sourceDecision != null && sourceDecision.DecisionPeriod > 0)
                        cloneDecision.DecisionPeriod = sourceDecision.DecisionPeriod + (i + 1);
                }

                Clones.Add(cloneGo);
                spawned++;
            }
        }
        finally
        {
            IsSpawningJackClone = false;
        }

        return spawned;
    }

    public static void NotifyCloneDestroyed(GameObject cloneGo)
    {
        Clones.Remove(cloneGo);
    }

    public static void ApplyJackSize(AgentGoToHouseDiscrete jack, int sizeLevel)
    {
        if (jack == null || !CanApplyBodyFx(jack))
            return;

        sizeLevel = Mathf.Clamp(sizeLevel, 1, 5);
        float mult = SizeLevelToMultiplier(sizeLevel);

        var state = GetOrCaptureScaleState(jack);
        jack.transform.localScale = state.BaseLocalScale * mult;

        var cc = jack.GetComponent<CharacterController>();
        if (cc != null)
        {
            cc.height = state.BaseHeight * mult;
            cc.radius = state.BaseRadius * mult;
            cc.center = state.BaseCenter * mult;
        }

        jack.SetTwitchReachMultiplier(mult);
    }

    public static void ResetJackScale(AgentGoToHouseDiscrete jack)
    {
        if (jack == null)
            return;

        int id = jack.GetInstanceID();
        if (!ScaleByJackId.TryGetValue(id, out var state) || !state.Captured)
            return;

        jack.transform.localScale = state.BaseLocalScale;
        var cc = jack.GetComponent<CharacterController>();
        if (cc != null)
        {
            cc.height = state.BaseHeight;
            cc.radius = state.BaseRadius;
            cc.center = state.BaseCenter;
        }

        jack.SetTwitchReachMultiplier(1f);
    }

    public static void ApplyJackSpeed(AgentGoToHouseDiscrete jack, int speedMultiplier)
    {
        if (jack == null || !CanApplyBodyFx(jack))
            return;

        if (IsTwitchClone(jack))
            return;

        speedMultiplier = Mathf.Clamp(speedMultiplier, 1, 5);
        jack.SetTwitchMoveSpeedMultiplier(speedMultiplier);
    }

    public static void ResetJackSpeed(AgentGoToHouseDiscrete jack)
    {
        if (jack == null)
            return;

        jack.ResetTwitchMoveSpeed();
    }

    /// <summary>Сброс #size/#speed у всех Jack (смена среды меню K).</summary>
    public static void ResetAllJackBodyFx()
    {
        var jacks = Object.FindObjectsByType<AgentGoToHouseDiscrete>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < jacks.Length; i++)
        {
            var jack = jacks[i];
            if (jack == null || IsTwitchClone(jack) || TrainingEnvSpace.IsGeorgeAgent(jack))
                continue;
            ResetJackScale(jack);
            ResetJackSpeed(jack);
        }
    }

    static bool CanApplyBodyFx(AgentGoToHouseDiscrete jack)
    {
        if (jack == null || IsTwitchClone(jack) || TrainingEnvSpace.IsGeorgeAgent(jack))
            return false;

        // Стрим / presentation Env.
        if (TrainingEnvSpace.IsPresentationTransform(jack.transform))
            return true;

        // Меню K: только Jack в текущей среде просмотра.
        if (!TrainingEnvSpace.IsDebugEnvFocusActive)
            return false;

        var focused = TrainingEnvSpace.GetDebugFocusedEnvRoot();
        return focused != null && TrainingEnvSpace.IsDescendantOf(jack.transform, focused);
    }

    public static float SpeedLevelToMultiplier(int speedLevel)
    {
        return Mathf.Clamp(speedLevel, 1, 5);
    }

    static ScaleState GetOrCaptureScaleState(AgentGoToHouseDiscrete jack)
    {
        int id = jack.GetInstanceID();
        if (!ScaleByJackId.TryGetValue(id, out var state))
        {
            state = new ScaleState();
            ScaleByJackId[id] = state;
        }

        if (!state.Captured)
        {
            state.BaseLocalScale = jack.transform.localScale;
            var cc = jack.GetComponent<CharacterController>();
            if (cc != null)
            {
                state.BaseHeight = cc.height;
                state.BaseRadius = cc.radius;
                state.BaseCenter = cc.center;
            }

            state.Captured = true;
        }

        return state;
    }

    public static float SizeLevelToMultiplier(int sizeLevel)
    {
        sizeLevel = Mathf.Clamp(sizeLevel, 1, 5);
        return sizeLevel;
    }

    static void ClearClones()
    {
        for (int i = Clones.Count - 1; i >= 0; i--)
        {
            if (Clones[i] != null)
                Object.Destroy(Clones[i]);
        }

        Clones.Clear();
    }

    static void RemoveDeadClones()
    {
        Clones.RemoveAll(go => go == null);
    }
}
