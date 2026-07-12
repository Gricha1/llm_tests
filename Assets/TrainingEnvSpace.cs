using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Локальные координаты первой среды (Env) → мировые для дубликатов Env (1), Env (2)…
/// Звук, HUD и рендер — только у основной среды «Env» (первая копия).
/// </summary>
public static class TrainingEnvSpace
{
    static Transform _presentationRoot;
    static bool _parallelEnvsVisible;

    static int _presentationAgentsFrame = -1;
    static AgentGoToHouseDiscrete _cachedPrimaryJack;
    static AgentGoToHouseDiscrete _cachedLivingJackClone;
    static AgentGoToHouseDiscrete _cachedFallbackJack;
    static LilyScript _cachedLily;
    static AgentGoToHouseDiscrete _cachedGeorge;

    enum EnvRunMode
    {
        All,
        StreamOnly,
        TrainCopiesOnly,
        SingleEnvByPort,
    }

    static EnvRunMode _runMode = EnvRunMode.All;
    static int _singleEnvTaskCopyIndex = -1;
    static string _streamWeightsDirectory;

    public static bool IsStreamOnlyMode => _runMode == EnvRunMode.StreamOnly;
    public static string StreamWeightsDirectory => _streamWeightsDirectory;
    public static bool IsTrainCopiesOnlyMode => _runMode == EnvRunMode.TrainCopiesOnly;
    public static bool IsSingleEnvByPortMode => _runMode == EnvRunMode.SingleEnvByPort;

    static bool IsTruthyEnv(string value) =>
        value == "1" || string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);

    /// Stream: только Env (presentation).
    /// TrainCopiesOnly: Env (1)…(11) в одном процессе.
    /// SingleEnvByPort: один Env в процессе, задача по (--mlagents-port - forestBasePort).
    static EnvRunMode ResolveEnvRunMode()
    {
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_STREAM_ONLY")))
            return EnvRunMode.StreamOnly;
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_TRAIN_COPIES_ONLY")))
            return EnvRunMode.TrainCopiesOnly;
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_SINGLE_ENV_BY_PORT")))
            return EnvRunMode.SingleEnvByPort;

        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-forestStreamOnly" || arg == "--forest-stream-only")
                return EnvRunMode.StreamOnly;
            if (arg == "-forestTrainCopiesOnly" || arg == "--forest-train-copies-only")
                return EnvRunMode.TrainCopiesOnly;
            if (arg == "-forestSingleEnvByPort" || arg == "--forest-single-env-by-port")
                return EnvRunMode.SingleEnvByPort;
        }

        return EnvRunMode.All;
    }

    static int ReadMlAgentsPortFromArgs()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--mlagents-port" && i + 1 < args.Length
                && int.TryParse(args[i + 1], out int port))
                return port;
        }

        return -1;
    }

    static string ReadForestStreamWeightsDirFromArgs()
    {
        var env = System.Environment.GetEnvironmentVariable("FOREST_STREAM_WEIGHTS_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "-forestStreamWeightsDir" || args[i] == "--forest-stream-weights-dir")
                && i + 1 < args.Length)
                return args[i + 1].Trim();
        }

        return null;
    }

    static int ReadForestBasePortFromArgs()
    {
        var env = System.Environment.GetEnvironmentVariable("FOREST_BASE_PORT");
        if (int.TryParse(env, out int fromEnv))
            return fromEnv;

        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "-forestBasePort" || args[i] == "--forest-base-port")
                && i + 1 < args.Length
                && int.TryParse(args[i + 1], out int port))
                return port;
        }

        return -1;
    }

    static int ResolveSingleEnvTaskCopyIndex()
    {
        int mlPort = ReadMlAgentsPortFromArgs();
        int basePort = ReadForestBasePortFromArgs();
        if (mlPort < 0 || basePort < 0)
            return -1;

        int worker = mlPort - basePort;
        if (worker < 0 || worker > 10)
            return -1;

        // worker 0 → Env (1) JackWood … worker 10 → Env (11) GeorgeHeat
        return worker + 1;
    }

    static void ApplyEnvRunMode()
    {
        _runMode = ResolveEnvRunMode();
        _singleEnvTaskCopyIndex = -1;
        _streamWeightsDirectory = _runMode == EnvRunMode.StreamOnly
            ? ReadForestStreamWeightsDirFromArgs()
            : null;
        EnvTrainingConfig.ClearForcedCopyIndex();

        if (_runMode == EnvRunMode.All)
            return;

        _presentationRoot = null;
        var presentation = PresentationRoot;

        if (_runMode == EnvRunMode.SingleEnvByPort)
        {
            _singleEnvTaskCopyIndex = ResolveSingleEnvTaskCopyIndex();
            if (_singleEnvTaskCopyIndex < 0)
                Debug.LogWarning("[TrainingEnvSpace] SingleEnvByPort: не удалось определить задачу (mlagents-port / forestBasePort).");

            foreach (var envRoot in FindAllEnvRoots())
            {
                if (envRoot == null)
                    continue;

                bool keep = presentation != null && envRoot == presentation;
                if (envRoot.gameObject.activeSelf != keep)
                    envRoot.gameObject.SetActive(keep);
            }

            if (_singleEnvTaskCopyIndex >= 0 && presentation != null)
            {
                EnvTrainingConfig.SetForcedCopyIndex(_singleEnvTaskCopyIndex);
                var cfg = presentation.GetComponent<EnvTrainingConfig>();
                if (cfg == null)
                    cfg = presentation.gameObject.AddComponent<EnvTrainingConfig>();
                Debug.Log($"[TrainingEnvSpace] SingleEnvByPort copyIndex={_singleEnvTaskCopyIndex} task={cfg.ResolveTask()}");
            }

            _presentationRoot = null;
            return;
        }

        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot == null)
                continue;

            bool isPresentation = presentation != null && envRoot == presentation;
            bool keep = _runMode == EnvRunMode.StreamOnly ? isPresentation : !isPresentation;
            if (envRoot.gameObject.activeSelf != keep)
                envRoot.gameObject.SetActive(keep);
        }

        _presentationRoot = null;
        Debug.Log($"[TrainingEnvSpace] EnvRunMode={_runMode}");
    }

    public static bool ParallelEnvsVisible => _parallelEnvsVisible;

    /// <summary>Показать рендер копий Env (1)… для отладки в Play. Env var FOREST_SHOW_PARALLEL_ENVS=1 или -forestShowParallelEnvs.</summary>
    public static bool IsShowParallelEnvsRequested()
    {
        var env = System.Environment.GetEnvironmentVariable("FOREST_SHOW_PARALLEL_ENVS");
        if (env == "1" || string.Equals(env, "true", System.StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var arg in System.Environment.GetCommandLineArgs())
        {
            if (arg == "-forestShowParallelEnvs" || arg == "--forest-show-parallel-envs")
                return true;
        }

        return false;
    }

    public static void SetParallelEnvsVisible(bool visible)
    {
        _parallelEnvsVisible = visible;
        ApplyParallelEnvPresentation();
    }

    public static void ToggleParallelEnvsVisible()
    {
        SetParallelEnvsVisible(!_parallelEnvsVisible);
    }

    /// <summary>Presentation Env на стриме: Full-режим Jack при нескольких Env в сцене.</summary>
    public static bool IsPresentationOnlyRequested()
    {
        return HasMultipleTrainingEnvs() && PresentationRoot != null;
    }

    public static bool IsMlAgentsTrainingActive()
    {
        if (!Unity.MLAgents.Academy.IsInitialized)
            return false;
        return Unity.MLAgents.Academy.Instance.IsCommunicatorOn;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ConfigureParallelEnvPresentation()
    {
        _presentationRoot = null;
        ApplyEnvRunMode();
        _parallelEnvsVisible = IsShowParallelEnvsRequested();
        ApplyParallelEnvPresentation();
        EnsureTrainingConfigs();
        EnsureEnvLocalHierarchyComponents();
    }

    static void ApplyParallelEnvPresentation()
    {
        var presentation = PresentationRoot;

        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot == null || !envRoot.gameObject.activeInHierarchy)
                continue;

            if (IsStreamOnlyMode)
            {
                if (envRoot == presentation)
                    UnmuteEnvPresentation(envRoot, enableCameraAndAudio: true);
                else
                    MuteEnvPresentation(envRoot);
                continue;
            }

            if (IsTrainCopiesOnlyMode || IsSingleEnvByPortMode)
            {
                MuteEnvPresentation(envRoot);
                continue;
            }

            // All / Play в Editor: presentation Env не трогаем (как раньше — без лишнего включения мешей).
            if (envRoot == presentation)
                continue;

            if (_parallelEnvsVisible)
                UnmuteEnvPresentation(envRoot, enableCameraAndAudio: false);
            else
                MuteEnvPresentation(envRoot);
        }
    }

    /// <summary>Стрим / PresentationFull — HUD, звук, все три героя. Train-среды — нет.</summary>
    public static bool IsPresentationStreamEnv(Transform envRoot)
    {
        if (envRoot == null)
            return false;

        if (IsStreamOnlyMode)
            return envRoot == PresentationRoot;

        var cfg = envRoot.GetComponent<EnvTrainingConfig>();
        if (cfg != null)
            return cfg.ResolveTask() == EnvTrainingTask.PresentationFull;

        return envRoot == PresentationRoot && !IsMlAgentsTrainingActive();
    }

    public static bool IsPresentationEnv(Transform envRoot) =>
        envRoot != null && envRoot == PresentationRoot;

    /// <summary>0 = Env (presentation), 1 = Env (1), 2 = Env (2), …</summary>
    public static int GetEnvCopyIndex(Transform envRoot)
    {
        if (envRoot == null)
            return 0;

        string name = envRoot.name;
        if (name == "Env")
            return 0;

        if (name.StartsWith("Env (") && name.EndsWith(")"))
        {
            string inner = name.Substring(5, name.Length - 6);
            if (int.TryParse(inner, out int n))
                return n;
        }

        return 0;
    }

    static void EnsureTrainingConfigs()
    {
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot.GetComponent<EnvTrainingConfig>() == null)
                envRoot.gameObject.AddComponent<EnvTrainingConfig>();

            if (!envRoot.gameObject.activeInHierarchy)
                continue;

            var cfg = envRoot.GetComponent<EnvTrainingConfig>();
            cfg?.ApplyInitialSetup();
        }
    }

    public static Transform PresentationRoot
    {
        get
        {
            if (_presentationRoot == null)
                _presentationRoot = ResolvePresentationRoot();
            return _presentationRoot;
        }
    }

    public static bool IsPresentationTransform(Transform t)
    {
        var root = FindRoot(t);
        if (root == null)
            return !IsMlAgentsTrainingActive();

        if (!IsPresentationStreamEnv(root))
            return false;

        var presentation = PresentationRoot;
        if (presentation == null || t == null)
            return true;
        return IsDescendantOf(t, presentation);
    }

    public static bool ShouldPlayFeedback(Transform source)
    {
        var root = PresentationRoot;
        if (root == null)
            return true;
        if (source == null)
            return false;
        return IsDescendantOf(source, root);
    }

    public static T FindInPresentation<T>() where T : Component
    {
        var root = PresentationRoot;
        if (root == null)
            return Object.FindObjectOfType<T>();

        T inactiveFallback = null;
        foreach (var c in root.GetComponentsInChildren<T>(true))
        {
            if (c == null)
                continue;
            if (c.gameObject.activeInHierarchy)
                return c;
            if (inactiveFallback == null)
                inactiveFallback = c;
        }

        return inactiveFallback;
    }

    public static LilyScript FindPresentationLily()
    {
        EnsurePresentationAgentsCached();
        return _cachedLily;
    }

    static void EnsurePresentationAgentsCached()
    {
        if (_presentationAgentsFrame == Time.frameCount)
            return;

        _presentationAgentsFrame = Time.frameCount;
        _cachedPrimaryJack = null;
        _cachedLivingJackClone = null;
        _cachedFallbackJack = null;
        _cachedLily = null;
        _cachedGeorge = null;

        var root = PresentationRoot;
        if (root == null)
        {
            _cachedLily = Object.FindObjectOfType<LilyScript>();
            var anyJack = Object.FindObjectOfType<AgentGoToHouseDiscrete>();
            if (anyJack != null && !IsGeorgeAgent(anyJack))
                _cachedFallbackJack = anyJack;
            return;
        }

        LilyScript lilyHero = null;
        LilyScript lilyAny = null;
        foreach (var lily in root.GetComponentsInChildren<LilyScript>(false))
        {
            if (lily == null || !lily.isActiveAndEnabled)
                continue;

            lilyAny ??= lily;
            if (lily.gameObject.name.IndexOf("Hero", System.StringComparison.OrdinalIgnoreCase) >= 0)
                lilyHero = lily;
        }

        _cachedLily = lilyHero != null ? lilyHero : lilyAny;

        AgentGoToHouseDiscrete[] jacks = root.GetComponentsInChildren<AgentGoToHouseDiscrete>(false);
        for (int i = 0; i < jacks.Length; i++)
        {
            var jack = jacks[i];
            if (jack == null || IsGeorgeAgent(jack))
                continue;

            if (TwitchEphemeralEffects.IsTwitchClone(jack))
            {
                if (_cachedLivingJackClone == null && jack.IsAliveForTwitch)
                    _cachedLivingJackClone = jack;
            }
            else if (_cachedPrimaryJack == null)
            {
                _cachedPrimaryJack = jack;
            }

            if (_cachedFallbackJack == null)
                _cachedFallbackJack = jack;
        }

        AgentGoToHouseDiscrete georgeHero = null;
        AgentGoToHouseDiscrete georgeAny = null;
        foreach (var agent in jacks)
        {
            if (agent == null || !IsGeorgeAgent(agent))
                continue;

            if (agent.gameObject.name == "GeorgeHero" && agent.gameObject.activeInHierarchy)
                georgeHero = agent;
            else if (agent.gameObject.name == "George")
                georgeAny = agent;
            else if (georgeAny == null)
                georgeAny = agent;
        }

        _cachedGeorge = georgeHero != null ? georgeHero : georgeAny;
    }

    public static bool IsGeorgeAgent(AgentGoToHouseDiscrete agent)
    {
        if (agent == null)
            return false;
        if (agent is GeorgeScript)
            return true;

        var go = agent.gameObject;
        if (go.CompareTag("George"))
            return true;

        return go.name.IndexOf("George", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Живой Jack для HUD и Twitch: оригинал, иначе клон с HP &gt; 0.</summary>
    public static AgentGoToHouseDiscrete FindPresentationJack()
    {
        EnsurePresentationAgentsCached();

        if (_cachedPrimaryJack != null && _cachedPrimaryJack.IsAliveForTwitch)
            return _cachedPrimaryJack;
        if (_cachedLivingJackClone != null)
            return _cachedLivingJackClone;
        if (_cachedPrimaryJack != null)
            return _cachedPrimaryJack;
        return _cachedFallbackJack;
    }

    static AgentGoToHouseDiscrete FindFirstJackAgent(AgentGoToHouseDiscrete[] agents)
    {
        if (agents == null)
            return null;
        for (int i = 0; i < agents.Length; i++)
        {
            if (agents[i] != null && !IsGeorgeAgent(agents[i]))
                return agents[i];
        }

        return null;
    }

    /// <summary>Оригинальный Jack (не Twitch-клон) в presentation Env.</summary>
    public static AgentGoToHouseDiscrete FindPresentationPrimaryJack()
    {
        var root = PresentationRoot;
        if (root == null)
        {
            var all = Object.FindObjectsOfType<AgentGoToHouseDiscrete>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && !TwitchEphemeralEffects.IsTwitchClone(all[i]) && !IsGeorgeAgent(all[i]))
                    return all[i];
            }

            return FindFirstJackAgent(all);
        }

        var jacks = root.GetComponentsInChildren<AgentGoToHouseDiscrete>(false);
        for (int i = 0; i < jacks.Length; i++)
        {
            if (jacks[i] != null && !TwitchEphemeralEffects.IsTwitchClone(jacks[i]) && !IsGeorgeAgent(jacks[i]))
                return jacks[i];
        }

        return FindFirstJackAgent(jacks);
    }

    public static AgentGoToHouseDiscrete FindPresentationGeorge()
    {
        EnsurePresentationAgentsCached();
        return _cachedGeorge;
    }

    public static Transform FindRoot(Transform from)
    {
        if (from == null)
            return null;

        Transform t = from;
        while (t != null)
        {
            if (IsEnvRootName(t.name))
                return t;
            t = t.parent;
        }

        return null;
    }

    public static bool IsEnvRootName(string objectName)
    {
        return objectName == "Env" || objectName.StartsWith("Env (");
    }

    public static bool IsEnvRootTransform(Transform t)
    {
        return t != null && IsEnvRootName(t.name)
            && (t.parent == null || !IsEnvRootName(t.parent.name));
    }

    public static bool IsDescendantOf(Transform child, Transform ancestor)
    {
        if (child == null || ancestor == null)
            return false;

        Transform t = child;
        while (t != null)
        {
            if (t == ancestor)
                return true;
            t = t.parent;
        }

        return false;
    }

    public static Vector3 LocalToWorld(Transform from, Vector3 localPosition)
    {
        var root = FindRoot(from);
        return root != null ? root.TransformPoint(localPosition) : localPosition;
    }

    public static Quaternion LocalToWorldRotation(Transform from, Vector3 localEuler)
    {
        var root = FindRoot(from);
        if (root == null)
            return Quaternion.Euler(localEuler);

        return root.rotation * Quaternion.Euler(localEuler);
    }

    /// <summary>Локальная позиция anchor внутри envRoot + смещение.</summary>
    public static Vector3 AnchorLocalPosition(Transform envRoot, Transform anchor, Vector3 localOffset)
    {
        if (envRoot == null)
            return localOffset;
        if (anchor == null)
            return localOffset;

        return envRoot.InverseTransformPoint(anchor.position) + localOffset;
    }

    /// <summary>Гарантирует, что объект — потомок envRoot (контекст — любой Transform внутри Env).</summary>
    public static bool EnsureDescendantOfEnv(Transform objectTransform, Transform context, bool preserveWorldPosition = true)
    {
        if (objectTransform == null)
            return false;

        var envRoot = FindRoot(context != null ? context : objectTransform);
        if (envRoot == null)
            return false;

        if (IsDescendantOf(objectTransform, envRoot))
            return true;

        objectTransform.SetParent(envRoot, preserveWorldPosition);
        Debug.LogWarning($"[{objectTransform.name}] перенесён под {envRoot.name} — объект должен жить в локальной иерархии Env.");
        return true;
    }

    static void EnsureEnvLocalHierarchyComponents()
    {
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot == null)
                continue;

            if (envRoot.GetComponent<EnvLocalHierarchy>() == null)
                envRoot.gameObject.AddComponent<EnvLocalHierarchy>();
        }
    }

    static Transform ResolvePresentationRoot()
    {
        Transform fallback = null;
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot.name == "Env")
                return envRoot;
            if (fallback == null)
                fallback = envRoot;
        }

        return fallback;
    }

    static Transform[] FindAllEnvRoots()
    {
        var scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
            return System.Array.Empty<Transform>();

        var roots = scene.GetRootGameObjects();
        var list = new System.Collections.Generic.List<Transform>(4);
        for (int i = 0; i < roots.Length; i++)
            CollectEnvRoots(roots[i].transform, list);
        return list.ToArray();
    }

    public static bool HasMultipleTrainingEnvs()
    {
        int active = 0;
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot != null && envRoot.gameObject.activeInHierarchy)
                active++;
        }

        return active > 1;
    }

    public static Transform[] GetAllEnvRoots()
    {
        return FindAllEnvRoots();
    }

    static void CollectEnvRoots(Transform t, System.Collections.Generic.List<Transform> list)
    {
        if (t == null)
            return;

        if (IsEnvRootTransform(t))
            list.Add(t);

        for (int i = 0; i < t.childCount; i++)
            CollectEnvRoots(t.GetChild(i), list);
    }

    static void MuteEnvPresentation(Transform envRoot)
    {
        foreach (var canvas in envRoot.GetComponentsInChildren<Canvas>(true))
        {
            if (canvas != null && canvas.gameObject.activeInHierarchy)
                canvas.enabled = false;
        }

        foreach (var listener in envRoot.GetComponentsInChildren<AudioListener>(true))
        {
            if (listener != null && listener.gameObject.activeInHierarchy)
                listener.enabled = false;
        }

        foreach (var audio in envRoot.GetComponentsInChildren<AudioSource>(true))
        {
            if (audio != null && audio.gameObject.activeInHierarchy)
                audio.mute = true;
        }

        foreach (var cam in envRoot.GetComponentsInChildren<Camera>(true))
        {
            if (cam != null && cam.gameObject.activeInHierarchy)
                cam.enabled = false;
        }

        foreach (var light in envRoot.GetComponentsInChildren<Light>(true))
        {
            if (light != null && light.gameObject.activeInHierarchy)
                light.enabled = false;
        }

        foreach (var renderer in envRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer != null && renderer.gameObject.activeInHierarchy)
                renderer.enabled = false;
        }
    }

    static void UnmuteEnvPresentation(Transform envRoot, bool enableCameraAndAudio)
    {
        foreach (var canvas in envRoot.GetComponentsInChildren<Canvas>(true))
        {
            if (canvas != null && canvas.gameObject.activeInHierarchy)
                canvas.enabled = true;
        }

        foreach (var audio in envRoot.GetComponentsInChildren<AudioSource>(true))
        {
            if (audio != null && audio.gameObject.activeInHierarchy)
                audio.mute = false;
        }

        foreach (var cam in envRoot.GetComponentsInChildren<Camera>(true))
        {
            if (cam != null && cam.gameObject.activeInHierarchy)
                cam.enabled = enableCameraAndAudio;
        }

        foreach (var listener in envRoot.GetComponentsInChildren<AudioListener>(true))
        {
            if (listener != null && listener.gameObject.activeInHierarchy)
                listener.enabled = enableCameraAndAudio;
        }

        foreach (var light in envRoot.GetComponentsInChildren<Light>(true))
        {
            if (light != null && light.gameObject.activeInHierarchy)
                light.enabled = true;
        }

        foreach (var renderer in envRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer != null && renderer.gameObject.activeInHierarchy)
                renderer.enabled = true;
        }
    }
}
