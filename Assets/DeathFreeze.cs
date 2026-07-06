using UnityEngine;

/// <summary>
/// Пауза симуляции при смерти агента. При нескольких Env — только внутри одной среды.
/// </summary>
public static class DeathFreeze
{
    static bool _frozen;
    static float _savedTimeScale = 1f;
    static Transform _frozenEnvRoot;

    public static bool IsFrozen => _frozen;

    public static void FreezeForAgent(Transform agent)
    {
        if (_frozen || agent == null)
            return;

        _frozen = true;
        _frozenEnvRoot = TrainingEnvSpace.FindRoot(agent);

        if (!TrainingEnvSpace.HasMultipleTrainingEnvs())
        {
            _savedTimeScale = Time.timeScale;
            Time.timeScale = 0f;
        }

        JointEpisodeReset.SetWorldSimulationEnabled(false, _frozenEnvRoot);
    }

    public static void UnfreezeWorld()
    {
        if (!_frozen)
            return;

        _frozen = false;
        var envRoot = _frozenEnvRoot;
        _frozenEnvRoot = null;

        if (!TrainingEnvSpace.HasMultipleTrainingEnvs() && Time.timeScale == 0f)
            Time.timeScale = _savedTimeScale > 0.001f ? _savedTimeScale : 1f;

        JointEpisodeReset.SetWorldSimulationEnabled(true, envRoot);
    }

    /// <summary>Снять паузу после смерти, если она зависла.</summary>
    public static void EnsureEnvSimulationRunning()
    {
        if (_frozen)
            UnfreezeWorld();
        else if (Time.timeScale < 0.01f)
            Time.timeScale = _savedTimeScale > 0.001f ? _savedTimeScale : 1f;
    }
}
