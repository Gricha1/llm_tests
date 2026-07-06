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

    static readonly List<GameObject> Clones = new List<GameObject>();
    static readonly Dictionary<int, ScaleState> ScaleByJackId = new Dictionary<int, ScaleState>();

    public static bool IsTwitchClone(Component c) =>
        c != null && c.GetComponent<TwitchJackCloneMarker>() != null;

    public static void OnPresentationJackEpisodeBegin(AgentGoToHouseDiscrete jack)
    {
        ClearClones();
        if (jack != null)
            ResetJackScale(jack);
    }

    public static void SpawnJackClones(AgentGoToHouseDiscrete source, int count)
    {
        if (source == null || !TrainingEnvSpace.IsPresentationTransform(source.transform))
            return;

        if (IsTwitchClone(source))
            return;

        count = Mathf.Clamp(count, 1, 5);
        RemoveDeadClones();

        var sourceAgent = source.GetComponent<Agent>();
        var sourceBehavior = source.GetComponent<BehaviorParameters>();

        for (int i = 0; i < count; i++)
        {
            float angle = (Clones.Count + i) * 72f * Mathf.Deg2Rad;
            var offset = new Vector3(Mathf.Cos(angle) * 1.8f, 0f, Mathf.Sin(angle) * 1.8f);
            Vector3 spawnPos = source.transform.position + offset;
            Quaternion spawnRot = source.transform.rotation;

            var cloneGo = Object.Instantiate(
                source.gameObject,
                spawnPos,
                spawnRot,
                source.transform.parent);

            cloneGo.name = $"Jack_TwitchClone_{Clones.Count + 1}";

            if (cloneGo.GetComponent<TwitchJackCloneMarker>() == null)
                cloneGo.AddComponent<TwitchJackCloneMarker>();

            var cloneBehavior = cloneGo.GetComponent<BehaviorParameters>();
            if (sourceBehavior != null && cloneBehavior != null)
            {
                cloneBehavior.BehaviorName = sourceBehavior.BehaviorName;
                cloneBehavior.BehaviorType = sourceBehavior.BehaviorType;
                cloneBehavior.Model = sourceBehavior.Model;
                cloneBehavior.TeamId = sourceBehavior.TeamId;
                cloneBehavior.DeterministicInference = sourceBehavior.DeterministicInference;
            }

            var cloneJack = cloneGo.GetComponent<AgentGoToHouseDiscrete>();
            var cloneAgent = cloneGo.GetComponent<Agent>();
            if (cloneAgent != null)
                cloneAgent.enabled = true;
            if (cloneJack != null)
                cloneJack.enabled = true;

            if (cloneAgent != null)
            {
                cloneAgent.EndEpisode();
                cloneGo.transform.SetPositionAndRotation(spawnPos, spawnRot);
            }

            Clones.Add(cloneGo);
        }
    }

    public static void NotifyCloneDestroyed(GameObject cloneGo)
    {
        Clones.Remove(cloneGo);
    }

    public static void ApplyJackSize(AgentGoToHouseDiscrete jack, int sizeLevel)
    {
        if (jack == null || !TrainingEnvSpace.IsPresentationTransform(jack.transform))
            return;

        if (IsTwitchClone(jack))
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
