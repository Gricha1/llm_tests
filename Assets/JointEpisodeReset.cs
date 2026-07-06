using UnityEngine;

/// <summary>
/// Сброс совместного эпизода Jack+Lily: оба агента, спавнеры и зомби.
/// </summary>
public static class JointEpisodeReset
{
    public static void EndBothAgentEpisodes()
    {
        EndBothAgentEpisodes(null);
    }

    public static void EndBothAgentEpisodes(Transform envScope)
    {
        foreach (var spawner in Object.FindObjectsOfType<ZombieSpawner>())
        {
            if (envScope != null && !TrainingEnvSpace.IsDescendantOf(spawner.transform, envScope))
                continue;
            spawner.ResetForNewEpisode();
        }

        foreach (var jack in Object.FindObjectsOfType<AgentGoToHouseDiscrete>())
        {
            if (envScope != null && !TrainingEnvSpace.IsDescendantOf(jack.transform, envScope))
                continue;
            jack.EndEpisode();
        }

        foreach (var lily in Object.FindObjectsOfType<LilyScript>())
        {
            if (envScope != null && !TrainingEnvSpace.IsDescendantOf(lily.transform, envScope))
                continue;
            lily.EndEpisode();
        }
    }

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
