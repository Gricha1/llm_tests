using UnityEngine;

/// <summary>
/// Ручное управление presentation: кнопка / M / флаг -forestPresentationManual.
/// На стриме с onnx по умолчанию выкл — включается только явно (чтобы проверить костёр локально).
/// </summary>
public static class ManualPlayControl
{
    public static bool GeorgeManualActive { get; private set; }

    /// <summary>Явно включён режим «хожу сам» (Heuristic), даже при -forestStreamOnly без ML.</summary>
    public static bool PresentationManualRequested { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void BootstrapFromArgs()
    {
        if (!IsPresentationManualPlayActive())
            return;
        PresentationManualRequested = true;
        GeorgeManualActive = true;
        // Чуть позже — агенты уже в сцене.
        var go = new GameObject("_ManualPlayBootstrap");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<_ManualPlayBootstrap>();
    }

    sealed class _ManualPlayBootstrap : MonoBehaviour
    {
        void Start() => ApplyHeuristicIfNeeded();
    }

    public static void ToggleGeorgeManual() =>
        GeorgeManualActive = !GeorgeManualActive;

    public static void SetGeorgeManual(bool on) =>
        GeorgeManualActive = on;

    public static void TogglePresentationManual()
    {
        PresentationManualRequested = !PresentationManualRequested;
        if (PresentationManualRequested && !GeorgeManualActive)
            GeorgeManualActive = true;
        ApplyHeuristicIfNeeded();
    }

    public static void SetPresentationManual(bool on)
    {
        PresentationManualRequested = on;
        if (on)
            GeorgeManualActive = true;
        ApplyHeuristicIfNeeded();
    }

    public static bool IsPresentationManualPlayActive()
    {
        if (PresentationManualRequested)
            return true;
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_PRESENTATION_MANUAL")))
            return true;
        foreach (var arg in System.Environment.GetCommandLineArgs())
        {
            if (arg == "-forestPresentationManual" || arg == "--forest-presentation-manual")
                return true;
        }
        return false;
    }

    static bool IsTruthyEnv(string value) =>
        value == "1" || string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);

    static void ApplyHeuristicIfNeeded()
    {
        if (!IsPresentationManualPlayActive())
            return;

        var root = TrainingEnvSpace.PresentationRoot;
        if (root == null)
            return;

        foreach (var agent in root.GetComponentsInChildren<Unity.MLAgents.Agent>(true))
        {
            if (agent == null)
                continue;
            var bp = agent.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>();
            if (bp != null)
                bp.BehaviorType = Unity.MLAgents.Policies.BehaviorType.HeuristicOnly;
            var dr = agent.GetComponent<Unity.MLAgents.DecisionRequester>();
            if (dr != null)
            {
                dr.enabled = true;
                dr.DecisionPeriod = 1;
            }
            agent.enabled = true;
        }
    }
}
