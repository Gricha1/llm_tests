using UnityEngine;
using Unity.MLAgents;

/// <summary>
/// Сброс совместного эпизода Jack+Lily+George: агенты, спавнеры и зомби.
/// </summary>
public static class JointEpisodeReset
{
    public static void EndAllAgentEpisodes()
    {
        EndAllAgentEpisodes(null);
    }

    public static void EndAllAgentEpisodes(Transform envScope)
    {
        foreach (var spawner in FindAgentsIncludingInactive<ZombieSpawner>())
        {
            if (spawner == null)
                continue;
            if (envScope != null && !TrainingEnvSpace.IsDescendantOf(spawner.transform, envScope))
                continue;

            // JackZombie: не Clear+Bootstrap (только таймер) — иначе сносим только что
            // поднятых зомби до StartTrainingEpisode у Jack/EnvTrainingConfig.
            var envRoot = envScope != null
                ? envScope
                : TrainingEnvSpace.FindRoot(spawner.transform);
            var cfg = envRoot != null ? envRoot.GetComponent<EnvTrainingConfig>() : null;
            if (cfg != null && cfg.ResolveJackMode() == JackTrainingMode.ZombieOnly)
                continue;

            spawner.ResetForNewEpisode();
        }

        foreach (var jack in FindAgentsIncludingInactive<AgentGoToHouseDiscrete>())
        {
            if (!ShouldResetAgent(jack, envScope))
                continue;
            SafeEndEpisode(jack);
        }

        foreach (var lily in FindAgentsIncludingInactive<LilyScript>())
        {
            if (!ShouldResetAgent(lily, envScope))
                continue;
            SafeEndEpisode(lily);
        }

        EnsureAgentsRespawned(envScope);
    }

    /// <summary>Если EndEpisode не поднял OnEpisodeBegin (часто с External Brain / mid-death), воскрешаем вручную.</summary>
    public static void EnsureAgentsRespawned(Transform envScope)
    {
        if (_respawnInProgress)
            return;

        _respawnInProgress = true;
        try
        {
            foreach (var jack in FindAgentsIncludingInactive<AgentGoToHouseDiscrete>())
            {
                if (!ShouldResetAgent(jack, envScope))
                    continue;
                if (!jack.IsInDeathState)
                    continue;
                jack.ForceHardRespawnFromDeath();
            }

            foreach (var lily in FindAgentsIncludingInactive<LilyScript>())
            {
                if (!ShouldResetAgent(lily, envScope))
                    continue;
                if (!lily.IsInDeathState)
                    continue;
                lily.ForceHardRespawnFromDeath();
            }
        }
        finally
        {
            _respawnInProgress = false;
        }
    }

    /// <summary>
    /// Джек начал новый эпизод один — поднять мёртвых Lily/George в том же Env.
    /// </summary>
    public static void EnsureDeadTeammatesRespawned(Transform envScope, AgentGoToHouseDiscrete skipJack)
    {
        if (_respawnInProgress)
            return;

        _respawnInProgress = true;
        try
        {
            foreach (var jack in FindAgentsIncludingInactive<AgentGoToHouseDiscrete>())
            {
                if (jack == null || jack == skipJack)
                    continue;
                if (!ShouldResetAgent(jack, envScope))
                    continue;
                if (!jack.IsInDeathState)
                    continue;
                jack.ForceHardRespawnFromDeath();
            }

            foreach (var lily in FindAgentsIncludingInactive<LilyScript>())
            {
                if (lily == null)
                    continue;
                if (!ShouldResetAgent(lily, envScope))
                    continue;
                if (!lily.IsInDeathState)
                    continue;
                lily.ForceHardRespawnFromDeath();
            }
        }
        finally
        {
            _respawnInProgress = false;
        }
    }

    static bool _respawnInProgress;

    /// <summary>
    /// Не трогаем legacy «Jack»/«Lily»/«George» (выключены в сцене) — у них sensors=null,
    /// EndEpisode → NullReferenceException в UpdateSensors.
    /// </summary>
    static bool ShouldResetAgent(Agent agent, Transform envScope)
    {
        if (agent == null)
            return false;
        if (!agent.isActiveAndEnabled || !agent.gameObject.activeInHierarchy)
            return false;
        if (envScope != null && !TrainingEnvSpace.IsDescendantOf(agent.transform, envScope))
            return false;
        if (TwitchEphemeralEffects.IsTwitchClone(agent))
            return false;

        string n = agent.gameObject.name;
        if (n == "Jack" || n == "Lily" || n == "George")
            return false;

        return true;
    }

    static void SafeEndEpisode(Agent agent)
    {
        try
        {
            agent.EndEpisode();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[JointEpisodeReset] EndEpisode fail on {agent?.name}: {ex.Message}");
        }
    }

    static T[] FindAgentsIncludingInactive<T>() where T : Component
    {
#if UNITY_2020_1_OR_NEWER
        return Object.FindObjectsOfType<T>(true);
#else
        return Object.FindObjectsOfType<T>();
#endif
    }

    [System.Obsolete("Use EndAllAgentEpisodes")]
    public static void EndBothAgentEpisodes() => EndAllAgentEpisodes();

    [System.Obsolete("Use EndAllAgentEpisodes")]
    public static void EndBothAgentEpisodes(Transform envScope) => EndAllAgentEpisodes(envScope);

    public static void SetAgentsMovementEnabled(bool enabled)
    {
        SetAgentsMovementEnabled(enabled, null);
    }

    public static void SetAgentsMovementEnabled(bool enabled, Transform envScope)
    {
        foreach (var jack in Object.FindObjectsOfType<AgentGoToHouseDiscrete>())
        {
            if (envScope != null && !TrainingEnvSpace.IsDescendantOf(jack.transform, envScope))
                continue;
            if (jack.TryGetComponent<CharacterController>(out var jackCc))
                jackCc.enabled = enabled;
        }

        foreach (var lily in Object.FindObjectsOfType<LilyScript>())
        {
            if (envScope != null && !TrainingEnvSpace.IsDescendantOf(lily.transform, envScope))
                continue;
            if (lily.TryGetComponent<CharacterController>(out var lilyCc))
                lilyCc.enabled = enabled;
        }
    }

    public static void SetWorldSimulationEnabled(bool enabled)
    {
        SetWorldSimulationEnabled(enabled, null);
    }

    public static void SetWorldSimulationEnabled(bool enabled, Transform envScope)
    {
        SetAgentsMovementEnabled(enabled, envScope);

        foreach (var z in Object.FindObjectsOfType<ZombieChase>())
        {
            if (envScope != null && !TrainingEnvSpace.IsDescendantOf(z.transform, envScope))
                continue;
            if (z != null) z.enabled = enabled;
        }

        foreach (var z in Object.FindObjectsOfType<ZombieAttack>())
        {
            if (envScope != null && !TrainingEnvSpace.IsDescendantOf(z.transform, envScope))
                continue;
            if (z != null) z.enabled = enabled;
        }

        foreach (var s in Object.FindObjectsOfType<SheepWander>())
        {
            if (envScope != null && !TrainingEnvSpace.IsDescendantOf(s.transform, envScope))
                continue;
            if (s != null) s.enabled = enabled;
        }

        foreach (var a in Object.FindObjectsOfType<Animator>())
        {
            if (a == null)
                continue;
            if (envScope != null && !TrainingEnvSpace.IsDescendantOf(a.transform, envScope))
                continue;
            a.speed = enabled ? 1f : 0f;
        }
    }
}
