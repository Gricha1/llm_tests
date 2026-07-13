using System.Collections.Generic;
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

    struct PresentationSpawnSnapshot
    {
        public Vector3 LocalPos;
        public Quaternion LocalRot;
    }

    static readonly Dictionary<int, PresentationSpawnSnapshot> PresentationSpawns = new();

    enum EnvRunMode
    {
        All,
        StreamOnly,
        TrainCopiesOnly,
        SingleEnvByPort,
        TrainWithPresentation,
    }

    static EnvRunMode _runMode = EnvRunMode.All;
    static int _singleEnvTaskCopyIndex = -1;
    static bool _presentationWorkerZero;
    static string _streamWeightsDirectory;
    static bool _cachedHasMultipleEnvs;
    static bool _envCountsCached;

    public static bool IsStreamOnlyMode => _runMode == EnvRunMode.StreamOnly;
    public static string StreamWeightsDirectory => _streamWeightsDirectory;
    public static bool IsTrainCopiesOnlyMode => _runMode == EnvRunMode.TrainCopiesOnly;
    public static bool IsSingleEnvByPortMode => _runMode == EnvRunMode.SingleEnvByPort;
    public static int SingleEnvWorkerCopyIndex => _singleEnvTaskCopyIndex;
    public static bool IsTrainWithPresentationMode => _runMode == EnvRunMode.TrainWithPresentation;
    /// <summary>SingleEnvByPort worker 0: presentation для OBS, workers 1–11 headless.</summary>
    public static bool IsPresentationWorkerProcess =>
        IsSingleEnvByPortMode && _presentationWorkerZero && _singleEnvTaskCopyIndex == 0;
    /// <summary>Train worker: SingleEnvByPort без presentation (workers 1–11 или все 11 без OBS).</summary>
    public static bool IsHeadlessTrainWorkerProcess =>
        IsTrainCopiesOnlyMode || (IsSingleEnvByPortMode && !IsPresentationWorkerProcess);
    public static bool IsLivePresentationForObs =>
        IsStreamOnlyMode || IsTrainWithPresentationMode || IsPresentationWorkerProcess;

    /// <summary>Twitch, HUD стрима — только presentation worker (не train workers 1–11).</summary>
    public static bool ShouldRunPresentationOnlyServices()
    {
        if (IsHeadlessTrainWorkerProcess)
            return false;
        if (IsPresentationWorkerProcess || IsStreamOnlyMode)
            return true;
#if UNITY_EDITOR
        return !IsMlAgentsTrainingActive();
#else
        return false;
#endif
    }

    static bool IsTruthyEnv(string value) =>
        value == "1" || string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);

    /// Stream: только Env (presentation).
    /// TrainCopiesOnly: Env (1)…(11) в одном процессе.
    /// SingleEnvByPort: один Env в процессе, задача по (--mlagents-port - forestBasePort).
    static EnvRunMode ResolveEnvRunMode()
    {
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_TRAIN_WITH_PRESENTATION")))
            return EnvRunMode.TrainWithPresentation;
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
            if (arg == "-forestTrainWithPresentation" || arg == "--forest-train-with-presentation")
                return EnvRunMode.TrainWithPresentation;
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

    static bool IsPresentationWorkerZeroRequested()
    {
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_PRESENTATION_WORKER")))
            return true;

        foreach (var arg in System.Environment.GetCommandLineArgs())
        {
            if (arg == "-forestPresentationWorker0" || arg == "--forest-presentation-worker0")
                return true;
        }

        return false;
    }

    static int ResolveSingleEnvTaskCopyIndex()
    {
        int mlPort = ReadMlAgentsPortFromArgs();
        int basePort = ReadForestBasePortFromArgs();
        if (mlPort < 0 || basePort < 0)
            return -1;

        int worker = mlPort - basePort;

        if (_presentationWorkerZero)
        {
            if (worker < 0 || worker > 11)
                return -1;
            // worker 0 → PresentationFull, workers 1–11 → train tasks
            return worker;
        }

        if (worker < 0 || worker > 10)
            return -1;

        // legacy 11 workers: worker 0 → JackWood … worker 10 → GeorgeHeat
        return worker + 1;
    }

    static void ApplyEnvRunMode()
    {
        _runMode = ResolveEnvRunMode();
        _singleEnvTaskCopyIndex = -1;
        _presentationWorkerZero = _runMode == EnvRunMode.SingleEnvByPort
            && IsPresentationWorkerZeroRequested();
        _streamWeightsDirectory = _runMode == EnvRunMode.StreamOnly
            ? ReadForestStreamWeightsDirFromArgs()
            : null;
        EnvTrainingConfig.ClearForcedCopyIndex();
        InvalidateEnvCountsCache();

        if (_runMode == EnvRunMode.All || _runMode == EnvRunMode.TrainWithPresentation)
        {
            if (_runMode == EnvRunMode.TrainWithPresentation)
                EnsureTrainEnvCopies(11);
            CacheEnvCounts();
            return;
        }

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
                Debug.Log($"[TrainingEnvSpace] SingleEnvByPort worker={ReadMlAgentsPortFromArgs() - ReadForestBasePortFromArgs()} copyIndex={_singleEnvTaskCopyIndex} presentation={IsPresentationWorkerProcess} task={cfg.ResolveTask()}");
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
        Debug.Log($"[TrainingEnvSpace] EnvRunMode={_runMode} activeEnvs={CountActiveEnvRoots()}");
        CacheEnvCounts();
    }

    static int CountActiveEnvRoots()
    {
        int n = 0;
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot != null && envRoot.gameObject.activeInHierarchy)
                n++;
        }
        return n;
    }

    static void InvalidateEnvCountsCache() => _envCountsCached = false;

    static void CacheEnvCounts()
    {
        int active = 0;
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot != null && envRoot.gameObject.activeInHierarchy)
                active++;
        }

        _cachedHasMultipleEnvs = active > 1;
        _envCountsCached = true;
    }

    /// <summary>Editor Play с одной Env — без train/stream флагов.</summary>
    public static bool IsSingleEnvPlayMode() =>
        _runMode == EnvRunMode.All && _envCountsCached && !_cachedHasMultipleEnvs && !IsStreamOnlyMode;

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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InitializeRunModeEarly()
    {
        _runMode = ResolveEnvRunMode();
        _presentationWorkerZero = _runMode == EnvRunMode.SingleEnvByPort
            && IsPresentationWorkerZeroRequested();
        _singleEnvTaskCopyIndex = _runMode == EnvRunMode.SingleEnvByPort
            ? ResolveSingleEnvTaskCopyIndex()
            : -1;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ConfigureParallelEnvPresentation()
    {
        _presentationRoot = null;
        ApplyEnvRunMode();
        _parallelEnvsVisible = IsShowParallelEnvsRequested();
        ApplyParallelEnvPresentation();
        ApplyTrainProcessSilence();
        EnsureTrainingConfigs();
        EnsureEnvLocalHierarchyComponents();
    }

    static void ApplyTrainProcessSilence()
    {
        if (IsPresentationWorkerProcess)
            return;
        if (!IsHeadlessTrainWorkerProcess)
            return;

        EnforceHeadlessSilence();
    }

    public static void EnforceHeadlessSilence()
    {
        AudioListener.volume = 0f;
        AudioListener.pause = true;

        foreach (var listener in Object.FindObjectsByType<AudioListener>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (listener != null)
                listener.enabled = false;
        }

        foreach (var audio in Object.FindObjectsByType<AudioSource>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (audio == null)
                continue;
            audio.mute = true;
            audio.volume = 0f;
            if (audio.isPlaying)
                audio.Stop();
        }
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
                if (IsPresentationWorkerProcess && envRoot == presentation)
                    UnmuteEnvPresentation(envRoot, enableCameraAndAudio: true);
                else
                    MuteEnvPresentation(envRoot);
                continue;
            }

            if (IsTrainWithPresentationMode)
            {
                if (envRoot == presentation)
                    UnmuteEnvPresentation(envRoot, enableCameraAndAudio: true);
                else
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

        if (IsSingleEnvPlayMode() && !IsMlAgentsTrainingActive())
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
        if (t == null)
            return !IsMlAgentsTrainingActive();

        // Быстрый путь: одна Env в Play — как до chunk 5, без ResolveTask/обхода сцены.
        if (IsSingleEnvPlayMode() && !IsMlAgentsTrainingActive())
        {
            var presentation = PresentationRoot;
            if (presentation == null)
                return true;
            return IsDescendantOf(t, presentation);
        }

        var root = FindRoot(t);
        if (root == null)
            return !IsMlAgentsTrainingActive();

        if (!IsPresentationStreamEnv(root))
            return false;

        var presentationRoot = PresentationRoot;
        if (presentationRoot == null)
            return true;
        return IsDescendantOf(t, presentationRoot);
    }

    public static bool ShouldPlayFeedback(Transform source)
    {
        if (IsHeadlessTrainWorkerProcess)
            return false;

        if ((IsTrainCopiesOnlyMode || IsSingleEnvByPortMode) && !IsPresentationWorkerProcess)
            return false;

        var presentationRoot = PresentationRoot;
        if (presentationRoot == null)
            return true;
        if (source == null)
            return false;
        return IsDescendantOf(source, presentationRoot);
    }

    public static bool ShouldPlayAmbientAudio() =>
        !IsHeadlessTrainWorkerProcess
        && (IsPresentationWorkerProcess
            || (!IsTrainCopiesOnlyMode && !IsSingleEnvByPortMode));

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

    static void EnsureTrainEnvCopies(int trainCopyCount)
    {
        var presentation = ResolvePresentationRoot();
        if (presentation == null)
        {
            Debug.LogWarning("[TrainingEnvSpace] TrainWithPresentation: нет корня Env.");
            return;
        }

        const float spacing = 300f;
        var basePos = presentation.position;

        for (int i = 1; i <= trainCopyCount; i++)
        {
            string copyName = $"Env ({i})";
            if (FindEnvRootByName(copyName) != null)
                continue;

            var clone = Object.Instantiate(presentation.gameObject);
            clone.name = copyName;
            clone.transform.SetParent(null, true);
            clone.transform.position = basePos + new Vector3(i * spacing, -400f, 0f);
            Debug.Log($"[TrainingEnvSpace] создан {copyName} @ {clone.transform.position}");
        }
    }

    static Transform FindEnvRootByName(string objectName)
    {
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot != null && envRoot.name == objectName)
                return envRoot;
        }

        return null;
    }

    public static bool HasMultipleTrainingEnvs()
    {
        if (!_envCountsCached)
            CacheEnvCounts();
        return _cachedHasMultipleEnvs;
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

    /// <summary>Запомнить стартовую позу героя в presentation Env (из сцены).</summary>
    public static void CapturePresentationSpawn(Transform agent)
    {
        if (agent == null || !IsPresentationTransform(agent))
            return;
        if (agent.GetComponent<TwitchJackCloneMarker>() != null)
            return;

        int id = agent.GetInstanceID();
        if (PresentationSpawns.ContainsKey(id))
            return;

        var envRoot = FindRoot(agent);
        if (envRoot == null)
            return;

        PresentationSpawns[id] = new PresentationSpawnSnapshot
        {
            LocalPos = envRoot.InverseTransformPoint(agent.position),
            LocalRot = Quaternion.Inverse(envRoot.rotation) * agent.rotation,
        };
    }

    /// <summary>Вернуть героя на стартовую позицию presentation Env.</summary>
    public static bool TryRestorePresentationSpawn(Transform agent, CharacterController controller)
    {
        if (agent == null || !IsPresentationTransform(agent))
            return false;
        if (agent.GetComponent<TwitchJackCloneMarker>() != null)
            return false;

        int id = agent.GetInstanceID();
        if (!PresentationSpawns.TryGetValue(id, out var snap))
        {
            CapturePresentationSpawn(agent);
            if (!PresentationSpawns.TryGetValue(id, out snap))
                return false;
        }

        var envRoot = FindRoot(agent);
        if (envRoot == null)
            return false;

        bool ccWasEnabled = controller != null && controller.enabled;
        if (controller != null)
            controller.enabled = false;

        agent.SetPositionAndRotation(
            envRoot.TransformPoint(snap.LocalPos),
            envRoot.rotation * snap.LocalRot);

        if (controller != null)
            controller.enabled = ccWasEnabled;

        return true;
    }
}
