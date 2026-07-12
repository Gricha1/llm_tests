using System.Collections.Generic;
using System.IO;
using Unity.InferenceEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>
/// Стрим: Unity один раз, веса подтягиваются из stream_weights/&lt;RUN_ID&gt;/*.sentis без перезапуска.
/// </summary>
public sealed class StreamWeightsHotReload : MonoBehaviour
{
    static readonly string[] BehaviorNames =
    {
        "JackLowLevelAgent",
        "LilyLowLevelAgent",
        "GeorgeLowLevelAgent",
    };

    const float PollSeconds = 3f;

    string _weightsDir;
    float _nextPollTime;
    readonly Dictionary<string, string> _loadedSignature = new Dictionary<string, string>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.IsStreamOnlyMode)
            return;

        var go = new GameObject(nameof(StreamWeightsHotReload));
        DontDestroyOnLoad(go);
        go.AddComponent<StreamWeightsHotReload>();
    }

    void Start()
    {
        var timeScaleEnv = System.Environment.GetEnvironmentVariable("FOREST_TIME_SCALE");
        if (float.TryParse(timeScaleEnv, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float timeScale)
            && timeScale > 0f)
            Time.timeScale = timeScale;

        _weightsDir = TrainingEnvSpace.StreamWeightsDirectory;
        if (string.IsNullOrEmpty(_weightsDir))
        {
            Debug.LogWarning("[StreamWeightsHotReload] каталог весов не задан (-forestStreamWeightsDir)");
            enabled = false;
            return;
        }

        Directory.CreateDirectory(_weightsDir);
        Debug.Log($"[StreamWeightsHotReload] watch dir={_weightsDir}");
        ConfigurePresentationAgents();
        TryReloadAll(force: true);
    }

    void Update()
    {
        if (Time.unscaledTime < _nextPollTime)
            return;

        _nextPollTime = Time.unscaledTime + PollSeconds;
        TryReloadAll(force: false);
    }

    void TryReloadAll(bool force)
    {
        foreach (var behaviorName in BehaviorNames)
        {
            var sentisPath = Path.Combine(_weightsDir, behaviorName + ".sentis");
            if (!File.Exists(sentisPath))
                continue;

            string signature;
            try
            {
                var info = new FileInfo(sentisPath);
                signature = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }
            catch (IOException)
            {
                continue;
            }

            if (!force
                && _loadedSignature.TryGetValue(behaviorName, out var prev)
                && prev == signature)
                continue;

            if (!TryLoadStableFile(sentisPath))
                continue;

            var model = StreamModelAssetLoader.LoadFromSentisFile(sentisPath);
            if (model == null)
            {
                Debug.LogWarning($"[StreamWeightsHotReload] не удалось прочитать {sentisPath}");
                continue;
            }

            if (!ApplyModel(behaviorName, model))
                continue;

            _loadedSignature[behaviorName] = signature;
            Debug.Log($"[StreamWeightsHotReload] hot reload {behaviorName} <- {sentisPath}");
        }
    }

    static bool TryLoadStableFile(string path)
    {
        long prev = -1;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var size = new FileInfo(path).Length;
                if (size > 0 && size == prev)
                    return true;
                prev = size;
            }
            catch (IOException)
            {
                return false;
            }

            System.Threading.Thread.Sleep(100);
        }

        return false;
    }

    bool ApplyModel(string behaviorName, ModelAsset model)
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root == null)
            return false;

        bool applied = false;
        foreach (var bp in root.GetComponentsInChildren<BehaviorParameters>(true))
        {
            if (bp == null || bp.BehaviorName != behaviorName || TwitchEphemeralEffects.IsTwitchClone(bp))
                continue;

            var agent = bp.GetComponent<Agent>();
            if (agent == null)
                continue;

            agent.LazyInitialize();
            bp.BehaviorType = BehaviorType.InferenceOnly;
            agent.SetModel(behaviorName, model, InferenceDevice.Burst);
            applied = true;
        }

        return applied;
    }

    static void ConfigurePresentationAgents()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root == null)
            return;

        foreach (var jack in root.GetComponentsInChildren<AgentGoToHouseDiscrete>(true))
        {
            if (jack != null && jack.gameObject.activeInHierarchy)
                jack.EnsureRuntimeAnimator();
        }

        foreach (var lily in root.GetComponentsInChildren<LilyScript>(true))
        {
            if (lily == null || !lily.gameObject.activeInHierarchy)
                continue;

            var animator = lily.GetComponent<Animator>() ?? lily.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.runtimeAnimatorController == null)
            {
                var fallback = DefaultHeroAnimatorController.ForAgent(lily);
                if (fallback != null)
                    animator.runtimeAnimatorController = fallback;
            }
        }

        foreach (var bp in root.GetComponentsInChildren<BehaviorParameters>(true))
        {
            if (bp == null || TwitchEphemeralEffects.IsTwitchClone(bp))
                continue;

            var agent = bp.GetComponent<Agent>();
            if (agent == null || !agent.gameObject.activeInHierarchy)
                continue;

            agent.LazyInitialize();
            bp.BehaviorType = BehaviorType.InferenceOnly;
        }
    }
}
