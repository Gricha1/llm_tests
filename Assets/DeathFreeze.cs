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
        if (agent == null)
            return;

        var envRoot = TrainingEnvSpace.FindRoot(agent);
        if (_frozen && _frozenEnvRoot == envRoot)
            return;

        if (_frozen)
            UnfreezeWorld();

        _frozen = true;
        _frozenEnvRoot = envRoot;

        // OBS stream / presentation worker: не ставим Time.timeScale=0 —
        // иначе зависает ML-Agents, respawn и КД волка (Time.time).
        if (!TrainingEnvSpace.HasMultipleTrainingEnvs()
            && !TrainingEnvSpace.IsPresentationWorkerProcess
            && !TrainingEnvSpace.IsLivePresentationForObs
            && !TrainingEnvSpace.IsStreamOnlyMode)
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

        if (!TrainingEnvSpace.HasMultipleTrainingEnvs()
            && !TrainingEnvSpace.IsPresentationWorkerProcess
            && !TrainingEnvSpace.IsLivePresentationForObs
            && !TrainingEnvSpace.IsStreamOnlyMode
            && Time.timeScale == 0f)
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
