using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>
/// Локальные координаты первой среды (Env) → мировые для дубликатов Env (1), Env (2)…
/// Звук, HUD и рендер — только у основной среды «Env» (первая копия).
/// </summary>
public static class TrainingEnvSpace
{
    static Transform _presentationRoot;
    static bool _parallelEnvsVisible;
    static int _debugFocusedCopyIndex = -1;

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
    static int _singleEnvWorkerIndex = -1;
    static bool _presentationWorkerZero;
    static string _streamWeightsDirectory;
    static bool _cachedHasMultipleEnvs;
    static bool _envCountsCached;

    public static bool IsStreamOnlyMode => _runMode == EnvRunMode.StreamOnly;
    /// <summary>Отдельный стрим без ML-Agents: зрители #join/#do. Не трогает train.</summary>
    public static bool IsStreamingSurvivalMode { get; private set; }
    public static string StreamWeightsDirectory => _streamWeightsDirectory;

    /// <summary>
    /// Validate / mlagents --inference: PresentationFull без MaxStep (бесконечный эпизод для видео).
    /// Train — наоборот, с лимитом шагов.
    /// </summary>
    public static bool IsValidateOrInferenceMode
    {
        get
        {
            if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_INFERENCE")))
                return true;
            if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_VALIDATE")))
                return true;
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (arg == "-forestValidate" || arg == "--forest-validate"
                    || arg == "-forestInference" || arg == "--forest-inference")
                    return true;
            }
            return false;
        }
    }

    /// <summary>Стрим или validate: PresentationFull без обрыва по MaxStep.</summary>
    public static bool AllowInfinitePresentationEpisode =>
        IsStreamOnlyMode || IsValidateOrInferenceMode;

    public static bool IsTrainCopiesOnlyMode => _runMode == EnvRunMode.TrainCopiesOnly;
    public static bool IsSingleEnvByPortMode => _runMode == EnvRunMode.SingleEnvByPort;
    public static int SingleEnvWorkerCopyIndex => _singleEnvTaskCopyIndex;
    public static int SingleEnvWorkerIndex => _singleEnvWorkerIndex;
    public static bool IsTrainWithPresentationMode => _runMode == EnvRunMode.TrainWithPresentation;
    /// <summary>worker 0: OBS stream, InferenceOnly + hot reload.</summary>
    /// <summary>worker 0 с графикой для OBS (не используется, если train all-headless + stream отдельно).</summary>
    public static bool IsPresentationWorkerProcess =>
        IsSingleEnvByPortMode
        && _presentationWorkerZero
        && _singleEnvWorkerIndex == 0
        && !IsTrainAllHeadlessRequested();

    /// <summary>Train: все SingleEnvByPort без окна; стрим — отдельный процесс.</summary>
    public static bool IsTrainAllHeadlessRequested()
    {
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_TRAIN_ALL_HEADLESS")))
            return true;
        foreach (var arg in System.Environment.GetCommandLineArgs())
        {
            if (arg == "-forestTrainAllHeadless" || arg == "--forest-train-all-headless")
                return true;
        }
        return false;
    }

    /// <summary>Headless PresentationFull (worker 0 при all-headless train).</summary>
    public static bool IsPresentationFullTrainWorker =>
        IsSingleEnvByPortMode
        && _presentationWorkerZero
        && _singleEnvWorkerIndex == 0;

    /// <summary>Стрим с Python onnxruntime: без Sentis hot reload, Behavior=Default.</summary>
    public static bool IsExternalPythonBrainStream =>
        IsStreamOnlyMode && IsExternalPythonBrainRequested();

    static bool IsExternalPythonBrainRequested()
    {
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_STREAM_EXTERNAL_BRAIN")))
            return true;
        foreach (var arg in System.Environment.GetCommandLineArgs())
        {
            if (arg == "-forestExternalBrain" || arg == "--forest-external-brain")
                return true;
        }
        return false;
    }

    /// <summary>Train worker: не graphics OBS (при all-headless — все включая w0).</summary>
    public static bool IsHeadlessTrainWorkerProcess =>
        IsTrainCopiesOnlyMode
        || (IsSingleEnvByPortMode && !IsPresentationWorkerProcess);
    public static bool IsLivePresentationForObs =>
        IsStreamOnlyMode || IsTrainWithPresentationMode || IsPresentationWorkerProcess;

    /// <summary>
    /// Presentation/stream/train worker 0: #join → Jack-like SS followers (не овечки ViewerSimpleAgent).
    /// Полный раунд SS (ресурсы, таймер) только при IsStreamingSurvivalMode.
    /// </summary>
    public static bool UseUnifiedFollowers =>
        IsStreamingSurvivalMode || IsLivePresentationForObs;

    /// <summary>Twitch, HUD стрима — только presentation worker (не train workers 1–15).</summary>
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

    static bool DetectStreamingSurvivalFlag()
    {
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_STREAMING_SURVIVAL")))
            return true;
        foreach (var arg in System.Environment.GetCommandLineArgs())
        {
            if (arg == "-forestStreamingSurvival" || arg == "--forest-streaming-survival")
                return true;
        }
        return false;
    }

    /// Stream: только Env (presentation).
    /// TrainCopiesOnly: Env (1)…(11) в одном процессе.
    /// SingleEnvByPort: один Env в процессе, задача по (--mlagents-port - forestBasePort).
    static EnvRunMode ResolveEnvRunMode()
    {
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_TRAIN_WITH_PRESENTATION")))
            return EnvRunMode.TrainWithPresentation;
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_STREAM_ONLY")))
            return EnvRunMode.StreamOnly;
        if (IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_STREAMING_SURVIVAL")))
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
            if (arg == "-forestStreamingSurvival" || arg == "--forest-streaming-survival")
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

    static int ResolveSingleEnvWorkerIndex()
    {
        int mlPort = ReadMlAgentsPortFromArgs();
        int basePort = ReadForestBasePortFromArgs();
        if (mlPort < 0 || basePort < 0)
            return -1;

        return mlPort - basePort;
    }

    static int ResolveTaskCopyIndexForWorker(int worker)
    {
        if (worker < 0)
            return -1;

        // Solo hero: worker = слот задачи.
        if (EnvTrainingConfig.IsJackOnlyTasksMode()
            || EnvTrainingConfig.IsLilyOnlyTasksMode()
            || EnvTrainingConfig.IsGeorgeOnlyTasksMode())
        {
            const int MaxSoloWorker = 63;
            if (worker > MaxSoloWorker)
                return -1;
            return worker;
        }

        if (_presentationWorkerZero)
        {
            // Train: worker 0–9 = PresentationFull×10, 10–13 Jack, 14–17 Lily, 18–20 George.
            // Stream — отдельный процесс (не в num-envs).
            const int MaxTrainWorker = 63;
            if (worker < 0 || worker > MaxTrainWorker)
                return -1;
            return worker;
        }

        if (worker < 0 || worker > 10)
            return -1;

        return worker + 1;
    }

    static int ResolveSingleEnvTaskCopyIndex() =>
        ResolveTaskCopyIndexForWorker(_singleEnvWorkerIndex);

    static void RefreshStreamWeightsDirectory()
    {
        if (_runMode == EnvRunMode.StreamOnly
            || (_presentationWorkerZero && _singleEnvWorkerIndex == 0))
            _streamWeightsDirectory = ReadForestStreamWeightsDirFromArgs();
        else
            _streamWeightsDirectory = null;
    }

    static void ApplyEnvRunMode()
    {
        IsStreamingSurvivalMode = DetectStreamingSurvivalFlag();
        _runMode = ResolveEnvRunMode();
        if (IsStreamingSurvivalMode)
            _runMode = EnvRunMode.StreamOnly;
        _singleEnvTaskCopyIndex = -1;
        _singleEnvWorkerIndex = -1;
        _presentationWorkerZero = _runMode == EnvRunMode.SingleEnvByPort
            && IsPresentationWorkerZeroRequested();
        _streamWeightsDirectory = null;
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
            _singleEnvWorkerIndex = ResolveSingleEnvWorkerIndex();
            _singleEnvTaskCopyIndex = ResolveSingleEnvTaskCopyIndex();
            RefreshStreamWeightsDirectory();
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
                // До первого шага Academy: выключить Lily/George (Jack-only yaml).
                cfg.ForceApplyTaskSetup();
                Debug.Log($"[TrainingEnvSpace] SingleEnvByPort worker={_singleEnvWorkerIndex} copyIndex={_singleEnvTaskCopyIndex} presentation={IsPresentationWorkerProcess} fullTrain={IsPresentationFullTrainWorker} task={cfg.ResolveTask()} weightsDir={_streamWeightsDirectory ?? "(none)"}");
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

    /// <summary>
    /// Меню K / #menu / #env_N: Editor, train+presentation и стрим OBS.
    /// В StreamOnly раньше было выключено — из‑за этого #add_fire работал, а #menu/#env_N нет.
    /// </summary>
    public static bool IsEnvViewSwitcherAllowed() =>
        _runMode == EnvRunMode.All
        || _runMode == EnvRunMode.TrainWithPresentation
        || _runMode == EnvRunMode.StreamOnly;

    public static bool IsDebugEnvFocusActive => _debugFocusedCopyIndex >= 0;

    public static int DebugFocusedCopyIndex => _debugFocusedCopyIndex;

    /// <summary>Меню K смотрит JackZombie (мульти #env_5 / Jack-only #env_4).</summary>
    public static bool IsJackZombieDebugFocus =>
        IsDebugEnvFocusActive && IsJackZombieCopyIndex(_debugFocusedCopyIndex);

    /// <summary>Корень Env под фокусом меню K / #env_N, иначе null.</summary>
    public static Transform GetDebugFocusedEnvRoot()
    {
        if (_debugFocusedCopyIndex < 0)
            return null;
        return FindEnvByCopyIndex(_debugFocusedCopyIndex);
    }

    public static Transform FindEnvByCopyIndex(int copyIndex)
    {
        if (copyIndex <= 0)
            return PresentationRoot;

        // Мультигерой: Env 1 = PresentationFull без клона. Jack-only: Env 1 = дрова.
        if (IsPresentationFullViewIndex(copyIndex))
            return PresentationRoot;

        return FindEnvRootByName($"Env ({copyIndex})");
    }

    /// <summary>Просмотр PresentationFull без Instantiation полной копии Env.</summary>
    public static bool IsPresentationFullViewIndex(int copyIndex)
    {
        if (copyIndex < 0)
            return false;
        // Меню 0/1 — не train-слоты JackWoodFoodOnly (там 0…11 = wood).
        return EnvTrainingConfig.ResolveActiveMenuTaskForCopyIndex(copyIndex)
            == EnvTrainingTask.PresentationFull;
    }

    public static void EnsureTrainEnvCopiesForViewing(int trainCopyCount = 11)
    {
        if (!IsEnvViewSwitcherAllowed())
            return;

        EnsureTrainEnvCopies(trainCopyCount);
        InvalidateEnvCountsCache();
        CacheEnvCounts();
        EnsureTrainingConfigs();
        EnsureEnvLocalHierarchyComponents();
    }

    /// <summary>Для меню K: создать только нужную копию Env (N), не все 11 сразу.</summary>
    public static void EnsureSingleTrainEnvCopyForViewing(int copyIndex)
    {
        if (!IsEnvViewSwitcherAllowed() || copyIndex <= 0)
            return;
        if (IsPresentationFullViewIndex(copyIndex))
            return;

        var presentation = ResolvePresentationRoot();
        if (presentation == null)
            return;

        string copyName = $"Env ({copyIndex})";
        if (FindEnvRootByName(copyName) != null)
            return;

        // Сразу на месте presentation: иначе овцы спавнятся на +300*N и после переноса
        // бегут «вправо» к старому spawnCenter.
        var clone = InstantiateEnvCopy(
            presentation,
            copyName,
            presentation.position);
        if (clone == null)
            return;

        if (clone.GetComponent<EnvTrainingConfig>() == null)
            clone.AddComponent<EnvTrainingConfig>();
        if (clone.GetComponent<EnvLocalHierarchy>() == null)
            clone.AddComponent<EnvLocalHierarchy>();

        InvalidateEnvCountsCache();
        CacheEnvCounts();
        clone.GetComponent<EnvTrainingConfig>()?.ApplyInitialSetup();
        Debug.Log($"[TrainingEnvSpace] просмотр: создан {copyName}");
    }

    public static void SetDebugFocusedEnv(int copyIndex)
    {
        if (!IsEnvViewSwitcherAllowed())
            return;

        // Сменить среду → убрать зомби только если уходим/заходим в JackZombie
        // (иначе Clear+FindObjects на каждом #env_1 = лишний hitch).
        PresentationEnvSwitcher.CancelDelayedZombieSpawnerStart();
        int prevFocus = _debugFocusedCopyIndex;
        // Смена среды: сбросить #size/#speed (иначе «прилипает» к presentation Jack).
        if (prevFocus != copyIndex)
            TwitchEphemeralEffects.ResetAllJackBodyFx();

        bool clearZombies =
            IsJackZombieCopyIndex(prevFocus) || IsJackZombieCopyIndex(copyIndex);
        if (clearZombies)
            ZombieSpawner.ClearZombiesInAllEnvs();

        // Фокус до Ensure/ApplyInitialSetup: иначе при JackWoodFoodOnly
        // ResolveAutoTask(3) = Wood (train-слот), а не JackFood из меню.
        _debugFocusedCopyIndex = copyIndex;
        _presentationAgentsFrame = -1;

        bool viewOnPresentation = IsPresentationFullViewIndex(copyIndex);
        if (copyIndex > 0 && !viewOnPresentation)
            EnsureSingleTrainEnvCopyForViewing(copyIndex);

        // Не поднимаем ВСЕ Env: массовый SetActive → OnEnable/Update всех копий = hitch.
        // Достаточно целевой (и presentation при выходе из фокуса).
        if (copyIndex < 0 || viewOnPresentation)
        {
            var presentation = PresentationRoot;
            if (presentation != null && !presentation.gameObject.activeSelf)
                presentation.gameObject.SetActive(true);
        }
        if (copyIndex >= 0)
        {
            var env = FindEnvByCopyIndex(copyIndex);
            var presentation = PresentationRoot;
            // Просмотр: копия на месте presentation, иначе камера/мир «пустые», а Env (N) уезжает на -400Y.
            // CharacterController не следует за parent.position — без Resync Jack «пропадает».
            if (!viewOnPresentation && copyIndex > 0 && env != null && presentation != null)
            {
                MoveEnvRootKeepingCharacterControllers(env, presentation.position);
                RepairDetachedHeroesInEnv(env);
                // Старые клоны с offset + овцы с world-центром справа.
                var sheepFix = env.GetComponentInChildren<SheepSpawner>(true);
                sheepFix?.RefreshSpawnAreas();
            }

            if (env != null)
            {
                if (!env.gameObject.activeSelf)
                    env.gameObject.SetActive(true);
                var cfg = env.GetComponent<EnvTrainingConfig>();

                // Env 0/1 = один и тот же PresentationRoot: без EndEpisode/ResetTrees (это и есть лаг).
                if (viewOnPresentation && env == presentation)
                {
                    cfg?.ForceApplyTaskSetup();
                    Debug.Log(
                        $"[EnvSwitcher] focus={copyIndex} task={cfg?.ResolveTask()} " +
                        $"(presentation, без клона)");
                }
                else
                {
                    cfg?.ForceApplyTaskSetup();
                    // JackZombie: без ResetTrees/Sheep — лишний hitch, деревья для задачи не нужны.
                    if (cfg == null || cfg.ResolveJackMode() != JackTrainingMode.ZombieOnly)
                    {
                        var trees = env.GetComponentInChildren<TreeSpawner>(true);
                        var sheep = env.GetComponentInChildren<SheepSpawner>(true);
                        if (trees != null && trees.AliveCount <= 0)
                            trees.ResetTrees();
                        if (sheep != null && sheep.AliveCount <= 0)
                            sheep.ResetSheep();
                    }

                    Debug.Log(
                        $"[EnvSwitcher] focus={copyIndex} task={cfg?.ResolveTask()} " +
                        $"jackMode={cfg?.ResolveJackMode()}");
                }
            }
        }

        InvalidateEnvCountsCache();
        CacheEnvCounts();
        ApplyParallelEnvPresentation();
        // CamAbSwitcher только на env 0 (валидация/стрим со звуком).
        SetCamAbSwitcherEnabled(copyIndex == 0);
        EnsureSingleActiveEventSystem();

        // Не копить Env (2)+Env (3)+… в памяти — отсюда лаги после нескольких переключений.
        if (copyIndex > 1)
            UnloadOtherTrainEnvCopies(copyIndex);

        // Зомби — ПОСЛЕ ApplyParallel (Env активен).
        if (IsJackZombieCopyIndex(copyIndex))
        {
            var zombieEnv = FindEnvByCopyIndex(copyIndex);
            ForceStartJackZombieSpawners(zombieEnv);
        }

        EnsureActiveViewCamera();
    }

    /// <summary>Удалить чужие train-копии Env (N), кроме focused — меньше RAM/CPU.</summary>
    static void UnloadOtherTrainEnvCopies(int keepCopyIndex)
    {
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot == null)
                continue;
            int idx = GetEnvCopyIndex(envRoot);
            if (idx <= 1)
                continue;
            if (idx == keepCopyIndex)
                continue;
            Object.Destroy(envRoot.gameObject);
        }

        InvalidateEnvCountsCache();
    }

    /// <summary>
    /// Сдвиг Env: CharacterController (enabled) не едет с родителем.
    /// Только выключаем CC → двигаем Env → включаем обратно.
    /// Нельзя SetPositionAndRotation по InverseTransform — при «отлипших» CC
    /// получаются огромные координаты → Invalid localAABB / worldAABB.
    /// </summary>
    static void MoveEnvRootKeepingCharacterControllers(Transform env, Vector3 worldPos)
    {
        if (env == null)
            return;
        if ((env.position - worldPos).sqrMagnitude < 0.0001f)
            return;

        var ccs = env.GetComponentsInChildren<CharacterController>(true);
        var wasEnabled = new bool[ccs.Length];
        for (int i = 0; i < ccs.Length; i++)
        {
            if (ccs[i] == null)
                continue;
            wasEnabled[i] = ccs[i].enabled;
            ccs[i].enabled = false;
        }

        env.position = worldPos;

        for (int i = 0; i < ccs.Length; i++)
        {
            if (ccs[i] == null)
                continue;
            ccs[i].enabled = wasEnabled[i];
        }
    }

    /// <summary>Если герой уже «отлип» от Env (после старого бага) — вернуть в зону спавна.</summary>
    static void RepairDetachedHeroesInEnv(Transform env)
    {
        if (env == null)
            return;

        const float maxDist = 80f;
        float maxDistSq = maxDist * maxDist;
        // Как в OnEpisodeBegin у Jack — центр поляны.
        var jackLocal = new Vector3(6f, -5.228786f, 13.5f);

        void Snap(Transform agent, Vector3 local)
        {
            if (agent == null || !agent.gameObject.activeInHierarchy)
                return;
            if ((agent.position - env.position).sqrMagnitude <= maxDistSq
                && IsFinite(agent.position))
                return;

            var cc = agent.GetComponent<CharacterController>();
            bool on = cc != null && cc.enabled;
            if (cc != null)
                cc.enabled = false;
            var world = env.TransformPoint(local);
            if (!IsFinite(world))
                world = env.position + Vector3.up;
            agent.SetPositionAndRotation(world, env.rotation);
            if (cc != null)
                cc.enabled = on;
        }

        var agents = env.GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < agents.Length; i++)
        {
            var a = agents[i];
            if (a == null || TwitchEphemeralEffects.IsTwitchClone(a)
                || a.gameObject.name == "Jack" || a.gameObject.name == "George")
                continue;
            Snap(a.transform, jackLocal);
        }

        var lilies = env.GetComponentsInChildren<LilyScript>(true);
        for (int i = 0; i < lilies.Length; i++)
        {
            var lily = lilies[i];
            if (lily == null || TwitchEphemeralEffects.IsTwitchClone(lily)
                || lily.gameObject.name == "Lily")
                continue;
            Snap(lily.transform, jackLocal);
        }
    }

    static bool IsFinite(Vector3 v) =>
        !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
          || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)
          || Mathf.Abs(v.x) > 100000f || Mathf.Abs(v.y) > 100000f || Mathf.Abs(v.z) > 100000f);

    /// <summary>
    /// Ровно одна камера в focused Env.
    /// AudioListener — только для env 0 (валидация); train env 1+ без звука.
    /// </summary>
    static void EnsureSingleCameraAndListener(Transform envRoot, bool enableAudio)
    {
        if (envRoot == null)
            return;

        Camera keepCam = null;
        var cams = envRoot.GetComponentsInChildren<Camera>(true);
        for (int i = 0; i < cams.Length; i++)
        {
            var cam = cams[i];
            if (cam == null)
                continue;
            string n = cam.gameObject.name;
            if (n.IndexOf("CamA", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Main Camera", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                keepCam = cam;
                break;
            }
        }

        if (keepCam == null)
        {
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] != null)
                {
                    keepCam = cams[i];
                    break;
                }
            }
        }

        for (int i = 0; i < cams.Length; i++)
        {
            var cam = cams[i];
            if (cam == null)
                continue;
            bool keep = keepCam != null && cam == keepCam;
            if (keep && !cam.gameObject.activeSelf)
                cam.gameObject.SetActive(true);
            cam.enabled = keep;
        }

        // Все слушатели в сцене сначала гасим — иначе при смене Env остаются 0 или 2+.
        var allListeners = Object.FindObjectsByType<AudioListener>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < allListeners.Length; i++)
        {
            if (allListeners[i] != null)
                allListeners[i].enabled = false;
        }

        // Всегда ровно один AudioListener (иначе Unity спамит «no audio listeners»).
        // Train env 1+: listener есть, volume=0 + mute источников.
        AudioListener keepListener = null;
        if (keepCam != null)
        {
            keepListener = keepCam.GetComponent<AudioListener>();
            if (keepListener == null)
                keepListener = keepCam.gameObject.AddComponent<AudioListener>();
        }
        else
        {
            keepListener = envRoot.GetComponent<AudioListener>();
            if (keepListener == null)
                keepListener = envRoot.gameObject.AddComponent<AudioListener>();
        }

        keepListener.enabled = true;
        // Headless: volume=0 и pause — иначе следующий кадр снова слышно до LateUpdate silence.
        if (IsHeadlessTrainWorkerProcess)
            enableAudio = false;
        AudioListener.volume = enableAudio ? 1f : 0f;
        AudioListener.pause = !enableAudio;
    }

    /// <summary>Камера+listener на текущей среде просмотра (после #reset / если Display пустой).</summary>
    public static void EnsureActiveViewCamera()
    {
        var root = ActiveViewEnvRoot ?? PresentationRoot;
        if (root == null)
            return;
        if (!root.gameObject.activeSelf)
            root.gameObject.SetActive(true);
        EnsureSingleCameraAndListener(root, enableAudio: WantAudioForFocusedEnv());
        if (WantAudioForFocusedEnv())
            UnmuteFocusedEnvAudioSources(root);
        else
            MuteFocusedEnvAudioSources(root);
    }

    /// <summary>Jack для hotkeys/#команд: сначала текущая среда меню K, иначе presentation.</summary>
    public static AgentGoToHouseDiscrete FindViewJack(bool preferActiveInHierarchy = true)
    {
        var env = ActiveViewEnvRoot ?? PresentationRoot;
        var jack = FindPrimaryJackInEnv(env);
        if (jack != null)
        {
            if (!preferActiveInHierarchy || jack.gameObject.activeInHierarchy)
                return jack;
        }

        jack = FindPresentationPrimaryJack();
        if (jack != null && (!preferActiveInHierarchy || jack.gameObject.activeInHierarchy))
            return jack;

        return FindPresentationJack();
    }

    /// <summary>Звук на presentation (стрим/validate/env 0). Headless train — никогда.</summary>
    public static bool WantAudioForFocusedEnv()
    {
        if (IsHeadlessTrainWorkerProcess)
            return false;
        // Stream + validate: mlagents communicator включён, но это не «тихий train».
        // Иначе GameSfx (дрова/шаги) молчат, а CampfireLoopAudio (без этого гейта) играет.
        if (IsStreamOnlyMode || IsValidateOrInferenceMode)
            return true;
        return _debugFocusedCopyIndex == 0
            || (_debugFocusedCopyIndex < 0 && !IsMlAgentsTrainingActive());
    }

    public static void ForceStartJackZombieSpawners(Transform env)
    {
        if (env == null)
            return;
        // Streaming Survival: зомби-этапы только для train.
        if (IsStreamingSurvivalMode)
            return;

        if (!IsDebugEnvFocusActive)
        {
            var jacks = env.GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
            for (int i = 0; i < jacks.Length; i++)
            {
                var jack = jacks[i];
                if (jack == null || IsGeorgeAgent(jack) || TwitchEphemeralEffects.IsTwitchClone(jack))
                    continue;
                if (!jack.gameObject.activeInHierarchy)
                    continue;
                jack.ForceStartZombieSpawnersForDebug();
                break;
            }
            return;
        }

        // Меню K: оба домовых спавнера (ZombieSpawner + ZombieSpawner_2), по 1 зомби из дома.
        // Hills не трогаем. Не спавним «у Jack» — точка спавна = дом.
        var houseSpawners = FindHouseZombieSpawners(env);
        if (houseSpawners.Count == 0)
        {
            Debug.LogError($"[JackZombie] в {env.name} нет домовых ZombieSpawner — зомби не появятся.");
            return;
        }

        int total = 0;
        for (int i = 0; i < houseSpawners.Count; i++)
        {
            var spawner = houseSpawners[i];
            if (spawner == null)
                continue;
            if (!spawner.gameObject.activeSelf)
                spawner.gameObject.SetActive(true);
            if (spawner.AliveCount > 0)
            {
                total += spawner.AliveCount;
                continue;
            }

            int spawned = spawner.StartTrainingEpisode(1);
            total += spawner.AliveCount;
            if (spawned <= 0)
                Debug.LogWarning($"[JackZombie] {spawner.name} не создал зомби (prefab?)", spawner);
        }

        Debug.Log(
            $"[JackZombie] {env.name}: домов={houseSpawners.Count}, зомби={total} (по 1 из каждого дома)");
    }

    /// <summary>ZombieSpawner + ZombieSpawner_2 у домов; без hills.</summary>
    static System.Collections.Generic.List<ZombieSpawner> FindHouseZombieSpawners(Transform env)
    {
        var list = new System.Collections.Generic.List<ZombieSpawner>(2);
        if (env == null)
            return list;

        var spawners = env.GetComponentsInChildren<ZombieSpawner>(true);
        ZombieSpawner primary = null;
        ZombieSpawner secondary = null;
        for (int i = 0; i < spawners.Length; i++)
        {
            var s = spawners[i];
            if (s == null)
                continue;
            string n = s.gameObject.name;
            if (n.IndexOf("Hills", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                s.ClearZombies(scanOrphanRoots: false);
                if (s.gameObject.activeSelf)
                    s.gameObject.SetActive(false);
                continue;
            }

            if (string.Equals(n, "ZombieSpawner", System.StringComparison.OrdinalIgnoreCase))
                primary = s;
            else if (string.Equals(n, "ZombieSpawner_2", System.StringComparison.OrdinalIgnoreCase)
                     || string.Equals(n, "zombie_spawner_2", System.StringComparison.OrdinalIgnoreCase))
                secondary = s;
        }

        if (primary != null)
            list.Add(primary);
        if (secondary != null)
            list.Add(secondary);

        // Fallback: любые не-hills, если имена другие.
        if (list.Count == 0)
        {
            for (int i = 0; i < spawners.Length; i++)
            {
                var s = spawners[i];
                if (s == null)
                    continue;
                if (s.gameObject.name.IndexOf("Hills", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                list.Add(s);
                if (list.Count >= 2)
                    break;
            }
        }

        return list;
    }

    /// <summary>Env, на которую смотрим: фокус из меню K, иначе presentation.</summary>
    public static Transform ActiveViewEnvRoot
    {
        get
        {
            if (_debugFocusedCopyIndex >= 0)
            {
                var focused = FindEnvByCopyIndex(_debugFocusedCopyIndex);
                if (focused != null)
                    return focused;
            }

            return PresentationRoot;
        }
    }

    /// <summary>Нужен ли HUD роли в текущей среде просмотра (меню K).</summary>
    public static bool ShouldShowHudForRole(EnvTrainingAgentRole role)
    {
        var root = ActiveViewEnvRoot;
        if (root == null)
            return true;

        var cfg = root.GetComponent<EnvTrainingConfig>();
        if (cfg == null)
            return true;

        var task = cfg.ResolveTask();
        if (task == EnvTrainingTask.PresentationFull || task == EnvTrainingTask.Auto)
        {
            // Solo validate: HUD только активного героя (не Лили/Гера у Jack-only).
            if (EnvTrainingConfig.IsJackOnlyTasksMode())
                return role == EnvTrainingAgentRole.Jack;
            if (EnvTrainingConfig.IsLilyOnlyTasksMode())
                return role == EnvTrainingAgentRole.Lily;
            if (EnvTrainingConfig.IsGeorgeOnlyTasksMode())
                return role == EnvTrainingAgentRole.George;
            return true;
        }

        return EnvTrainingConfig.ShouldAgentTrain(task, role);
    }

    public static void ClearDebugFocusedEnv() => SetDebugFocusedEnv(-1);

    static bool IsJackZombieCopyIndex(int copyIndex)
    {
        if (copyIndex < 0)
            return false;
        // Меню: Jack-only #env_4, иначе #env_5 — не train-слот WoodFoodOnly.
        return EnvTrainingConfig.ResolveActiveMenuTaskForCopyIndex(copyIndex)
            == EnvTrainingTask.JackZombie;
    }

    /// <summary>
    /// Lily есть в presentation (даже inactive) — для меню K.
    /// Не смотреть activeInHierarchy: Jack Food прячет Lily, но пункты меню должны остаться.
    /// </summary>
    public static bool HasLilyHeroConfiguredInScene()
    {
        var root = PresentationRoot;
        if (root != null)
        {
            var lilies = root.GetComponentsInChildren<LilyScript>(true);
            for (int i = 0; i < lilies.Length; i++)
            {
                var lily = lilies[i];
                if (lily == null || lily.gameObject.name == "Lily")
                    continue;
                if (TwitchEphemeralEffects.IsTwitchClone(lily))
                    continue;
                return true;
            }
        }

        var all = Object.FindObjectsByType<LilyScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            var lily = all[i];
            if (lily == null || lily.gameObject.name == "Lily")
                continue;
            if (TwitchEphemeralEffects.IsTwitchClone(lily))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>George есть в presentation (даже inactive) — для меню K.</summary>
    public static bool HasGeorgeHeroConfiguredInScene()
    {
        var root = PresentationRoot;
        if (root != null)
        {
            var agents = root.GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
            for (int i = 0; i < agents.Length; i++)
            {
                var agent = agents[i];
                if (agent == null || !IsGeorgeAgent(agent) || agent.gameObject.name == "George")
                    continue;
                if (TwitchEphemeralEffects.IsTwitchClone(agent))
                    continue;
                return true;
            }
        }

        var all = Object.FindObjectsByType<AgentGoToHouseDiscrete>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            var agent = all[i];
            if (agent == null || !IsGeorgeAgent(agent) || agent.gameObject.name == "George")
                continue;
            if (TwitchEphemeralEffects.IsTwitchClone(agent))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>Есть активный LilyHero / LilyScript (не legacy «Lily»).</summary>
    public static bool HasActiveLilyHeroInScene()
    {
        var lilies = Object.FindObjectsByType<LilyScript>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < lilies.Length; i++)
        {
            var lily = lilies[i];
            if (lily == null || lily.gameObject.name == "Lily")
                continue;
            if (TwitchEphemeralEffects.IsTwitchClone(lily))
                continue;
            if (lily.gameObject.activeInHierarchy)
                return true;
        }
        return false;
    }

    /// <summary>Есть активный GeorgeHero (не legacy «George»).</summary>
    public static bool HasActiveGeorgeHeroInScene()
    {
        var agents = Object.FindObjectsByType<AgentGoToHouseDiscrete>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < agents.Length; i++)
        {
            var agent = agents[i];
            if (agent == null || !IsGeorgeAgent(agent) || agent.gameObject.name == "George")
                continue;
            if (TwitchEphemeralEffects.IsTwitchClone(agent))
                continue;
            if (agent.gameObject.activeInHierarchy)
                return true;
        }
        return false;
    }

    static void SetCamAbSwitcherEnabled(bool enabled)
    {
        foreach (var sw in Object.FindObjectsByType<CamAbSwitcher>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (sw != null)
                sw.enabled = enabled;
        }
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
        IsStreamingSurvivalMode = DetectStreamingSurvivalFlag();
        _runMode = ResolveEnvRunMode();
        if (IsStreamingSurvivalMode)
            _runMode = EnvRunMode.StreamOnly;
        _presentationWorkerZero = _runMode == EnvRunMode.SingleEnvByPort
            && IsPresentationWorkerZeroRequested();
        _singleEnvWorkerIndex = _runMode == EnvRunMode.SingleEnvByPort
            ? ResolveSingleEnvWorkerIndex()
            : -1;
        _singleEnvTaskCopyIndex = _runMode == EnvRunMode.SingleEnvByPort
            ? ResolveTaskCopyIndexForWorker(_singleEnvWorkerIndex)
            : -1;
        RefreshStreamWeightsDirectory();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ConfigureParallelEnvPresentation()
    {
        _presentationRoot = null;
        ApplyEnvRunMode();
        _parallelEnvsVisible = IsShowParallelEnvsRequested();
        TryApplyValidateTaskFocus();
        ApplyParallelEnvPresentation();
        ApplyTrainProcessSilence();
        EnsureTrainingConfigs();
        EnsureEnvLocalHierarchyComponents();
    }

    /// <summary>
    /// Validate UI / CLI: -forestValidateTask wood|food|water|zombie|stream
    /// Ставит фокус меню K на нужную задачу (Jack-only: 0..4).
    /// </summary>
    static void TryApplyValidateTaskFocus()
    {
        string task = ReadValidateTaskFromArgs();
        if (string.IsNullOrEmpty(task))
            return;

        int idx = MapValidateTaskToMenuIndex(task);
        if (idx < 0)
        {
            Debug.LogWarning($"[TrainingEnvSpace] неизвестный -forestValidateTask={task}");
            return;
        }

        Debug.Log($"[TrainingEnvSpace] validateTask={task} → debugFocus={idx}");
        if (IsEnvViewSwitcherAllowed())
            SetDebugFocusedEnv(idx);
        else
            _debugFocusedCopyIndex = idx;
    }

    static string ReadValidateTaskFromArgs()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-forestValidateTask" || a == "--forest-validate-task")
            {
                if (i + 1 < args.Length)
                    return args[i + 1].Trim().ToLowerInvariant();
            }
            const string prefix = "-forestValidateTask=";
            const string prefix2 = "--forest-validate-task=";
            if (a.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                return a.Substring(prefix.Length).Trim().ToLowerInvariant();
            if (a.StartsWith(prefix2, System.StringComparison.OrdinalIgnoreCase))
                return a.Substring(prefix2.Length).Trim().ToLowerInvariant();
        }
        return null;
    }

    static int MapValidateTaskToMenuIndex(string task)
    {
        // Solo-меню: 0 = стрим, дальше узкие задачи выбранного героя.
        bool jackOnly = EnvTrainingConfig.IsJackOnlyTasksMode();
        bool lilyOnly = EnvTrainingConfig.IsLilyOnlyTasksMode();
        bool georgeOnly = EnvTrainingConfig.IsGeorgeOnlyTasksMode();
        switch (task)
        {
            case "stream":
            case "full":
            case "presentation":
                return 0;
            case "wood":
            case "tree":
            case "jackwood":
                return jackOnly ? 1 : 2;
            case "food":
            case "sheep":
            case "jackfood":
                if (lilyOnly || georgeOnly)
                    return 1;
                return jackOnly ? 2 : 3;
            case "water":
            case "jackwater":
                if (lilyOnly || georgeOnly)
                    return 2;
                return jackOnly ? 3 : 4;
            case "zombie":
            case "jackzombie":
                return jackOnly ? 4 : 5;
            case "heat":
            case "fire":
            case "lilyheat":
            case "georgeheat":
                if (lilyOnly)
                    return 3;
                if (georgeOnly)
                    return 3;
                return -1;
            case "flower":
            case "lilyflower":
                return lilyOnly ? 4 : -1;
            default:
                if (int.TryParse(task, out int n) && n >= 0 && n <= 20)
                    return n;
                return -1;
        }
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
        if (_debugFocusedCopyIndex >= 0 && IsEnvViewSwitcherAllowed())
        {
            var focused = FindEnvByCopyIndex(_debugFocusedCopyIndex);
            // Копия удалена (Unload) / сброс — иначе все Env выключатся → No cameras rendering.
            if (focused == null)
            {
                _debugFocusedCopyIndex = 0;
                focused = PresentationRoot;
                if (focused != null && !focused.gameObject.activeSelf)
                    focused.gameObject.SetActive(true);
            }

            bool anyCamera = false;
            foreach (var envRoot in FindAllEnvRoots())
            {
                if (envRoot == null)
                    continue;

                bool isFocused = envRoot == focused;
                if (envRoot.gameObject.activeSelf != isFocused)
                    envRoot.gameObject.SetActive(isFocused);

                if (isFocused)
                {
                    bool wantAudio = WantAudioForFocusedEnv();
                    EnsureSingleCameraAndListener(envRoot, enableAudio: wantAudio);
                    if (!wantAudio)
                        MuteFocusedEnvAudioSources(envRoot);
                    else
                        UnmuteFocusedEnvAudioSources(envRoot);
                    anyCamera = true;
                }
            }

            if (!anyCamera && PresentationRoot != null)
            {
                _debugFocusedCopyIndex = 0;
                if (!PresentationRoot.gameObject.activeSelf)
                    PresentationRoot.gameObject.SetActive(true);
                EnsureSingleCameraAndListener(PresentationRoot, enableAudio: true);
                UnmuteFocusedEnvAudioSources(PresentationRoot);
            }

            return;
        }

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

    /// <summary>
    /// Смягчение нужд только на стрим/ручной presentation — не на train (в т.ч. PresentationFull train).
    /// Ресурсы ×2 дольше, HP от голода/жажды/холода ×2 реже, тепло у костра ×2 быстрее.
    /// </summary>
    public static bool UsesStreamPresentationSurvivalPace(Transform agent)
    {
        if (agent == null || IsHeadlessTrainWorkerProcess)
            return false;
        // Train с communicator: даже если Env = PresentationRoot / PresentationFull — не трогаем темп.
        if (IsMlAgentsTrainingActive() && !IsStreamOnlyMode && !IsValidateOrInferenceMode)
            return false;
        return IsPresentationTransform(agent);
    }

    public const float StreamPresentationNeedsDecayIntervalMul = 2f;
    public const float StreamPresentationHpDamageIntervalMul = 2f;
    public const float StreamPresentationWarmthGainIntervalMul = 0.5f;
    /// <summary>Jack на стриме греется почти сразу у костра — политика не умеет ждать.</summary>
    public const float StreamPresentationJackWarmthGainIntervalMul = 0.06f;
    public const int StreamPresentationJackWarmthPerTick = 5;
    public const int StreamPresentationJackWarmthOnDeposit = 10;

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
        // Jack-only / all-headless: Env = PresentationRoot по слоту, но это train — не стрим.
        if (IsHeadlessTrainWorkerProcess)
            return false;

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
        if (!WantAudioForFocusedEnv())
            return false;

        if ((IsTrainCopiesOnlyMode || IsSingleEnvByPortMode) && !IsPresentationWorkerProcess)
            return false;

        // Стрим/validate: SFX всегда (даже если source=null или вне presentation).
        if (IsStreamOnlyMode || IsValidateOrInferenceMode)
            return true;

        var presentationRoot = PresentationRoot;
        if (presentationRoot == null)
            return true;
        if (source == null)
            return false;
        return IsDescendantOf(source, presentationRoot);
    }

    public static bool ShouldPlayAmbientAudio() =>
        !IsHeadlessTrainWorkerProcess
        && WantAudioForFocusedEnv()
        && (IsStreamOnlyMode
            || IsPresentationWorkerProcess
            || IsLivePresentationForObs
            || (!IsTrainCopiesOnlyMode && !IsSingleEnvByPortMode));

    public static T FindInPresentation<T>() where T : Component
    {
        var root = PresentationRoot;
        if (root == null)
            return Object.FindFirstObjectByType<T>();

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

        var root = ActiveViewEnvRoot;
        if (root == null)
        {
            _cachedLily = Object.FindFirstObjectByType<LilyScript>();
            var anyJack = Object.FindFirstObjectByType<AgentGoToHouseDiscrete>();
            if (anyJack != null && !IsGeorgeAgent(anyJack))
                _cachedFallbackJack = anyJack;

            // Раньше George здесь не искали → FindPresentationGeorge()=null → Reward EMA пустой.
            var all = Object.FindObjectsByType<AgentGoToHouseDiscrete>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && IsGeorgeAgent(all[i]))
                {
                    _cachedGeorge = all[i];
                    if (all[i].gameObject.name.IndexOf("Hero", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        break;
                }
            }
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

            bool isHero = agent.gameObject.name.IndexOf("Hero", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (isHero && agent.gameObject.activeInHierarchy)
                georgeHero = agent;
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

    /// <summary>Оригинальный Jack в указанном Env (включая inactive).</summary>
    public static AgentGoToHouseDiscrete FindPrimaryJackInEnv(Transform envRoot)
    {
        if (envRoot == null)
            return null;

        var jacks = envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < jacks.Length; i++)
        {
            var jack = jacks[i];
            if (jack == null || TwitchEphemeralEffects.IsTwitchClone(jack) || IsGeorgeAgent(jack))
                continue;
            if (jack.gameObject.name == "Jack")
                continue;
            return jack;
        }

        return FindFirstJackAgent(jacks);
    }

    /// <summary>Оригинальный Jack (не Twitch-клон) в presentation Env.</summary>
    public static AgentGoToHouseDiscrete FindPresentationPrimaryJack()
    {
        var root = PresentationRoot;
        if (root == null)
        {
            var all = Object.FindObjectsByType<AgentGoToHouseDiscrete>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && !TwitchEphemeralEffects.IsTwitchClone(all[i]) && !IsGeorgeAgent(all[i]))
                    return all[i];
            }

            return FindFirstJackAgent(all);
        }

        return FindPrimaryJackInEnv(root);
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

            var clone = InstantiateEnvCopy(presentation, copyName,
                basePos + new Vector3(i * spacing, -400f, 0f));
            if (clone != null)
                Debug.Log($"[TrainingEnvSpace] создан {copyName} @ {clone.transform.position}");
        }
    }

    /// <summary>
    /// Клон Env с правильным именем ДО Awake спавнеров.
    /// Иначе Unity зовёт объект Env(Clone) → FindRoot=null → деревья/овцы спавнятся в чужих координатах.
    /// </summary>
    static GameObject InstantiateEnvCopy(Transform presentation, string copyName, Vector3 worldPos)
    {
        if (presentation == null)
            return null;

        var source = presentation.gameObject;
        // Missing (Mono Script) на Env → при Instantiate сыпется
        // "The referenced script ... Game Object '<null>'". Чистим источник в Editor.
        StripMissingScriptsRecursive(source, "presentation Env before clone");

        bool sourceWasActive = source.activeSelf;
        source.SetActive(false);

        var clone = Object.Instantiate(source);
        clone.name = copyName;
        clone.transform.SetParent(null, true);
        clone.transform.position = worldPos;

        StripMissingScriptsRecursive(clone, copyName);
        // Клон Env тащит свой EventSystem → "There can be only one active Event System".
        DisableEventSystemsUnder(clone.transform, destroy: true);

        source.SetActive(sourceWasActive);
        clone.SetActive(true);
        EnsureSingleActiveEventSystem();
        return clone;
    }

    /// <summary>Оставляет один EventSystem (предпочтительно под presentation Env).</summary>
    public static void EnsureSingleActiveEventSystem()
    {
        var systems = Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (systems == null || systems.Length <= 1)
            return;

        EventSystem keep = null;
        var presentation = PresentationRoot;
        for (int i = 0; i < systems.Length; i++)
        {
            var es = systems[i];
            if (es == null)
                continue;
            if (presentation != null && es.transform.IsChildOf(presentation))
            {
                keep = es;
                break;
            }
        }

        if (keep == null)
        {
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] != null && systems[i].isActiveAndEnabled)
                {
                    keep = systems[i];
                    break;
                }
            }
        }

        if (keep == null)
            keep = systems[0];

        for (int i = 0; i < systems.Length; i++)
        {
            var es = systems[i];
            if (es == null || es == keep)
                continue;

            // Копии Env: выключаем целиком GO, чтобы SetActive родителя не поднимал второй ES.
            if (presentation != null && !es.transform.IsChildOf(presentation))
            {
                es.gameObject.SetActive(false);
                es.enabled = false;
            }
            else
            {
                es.enabled = false;
                es.gameObject.SetActive(false);
            }
        }

        if (keep != null)
        {
            if (!keep.gameObject.activeSelf)
                keep.gameObject.SetActive(true);
            keep.enabled = true;
        }
    }

    static void DisableEventSystemsUnder(Transform root, bool destroy)
    {
        if (root == null)
            return;

        var systems = root.GetComponentsInChildren<EventSystem>(true);
        for (int i = 0; i < systems.Length; i++)
        {
            var es = systems[i];
            if (es == null)
                continue;
            if (destroy)
                Object.Destroy(es.gameObject);
            else
            {
                es.enabled = false;
                es.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Логирует объекты с missing script; в Editor ещё и снимает битые MonoBehaviour.
    /// </summary>
    static void StripMissingScriptsRecursive(GameObject root, string context)
    {
        if (root == null)
            return;

        var transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            var t = transforms[i];
            if (t == null)
                continue;

            var comps = t.GetComponents<Component>();
            bool hasMissing = false;
            for (int c = 0; c < comps.Length; c++)
            {
                if (comps[c] != null)
                    continue;
                hasMissing = true;
                break;
            }

            if (!hasMissing)
                continue;

#if UNITY_EDITOR
            int removed = UnityEditor.GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            if (removed > 0)
                Debug.Log($"[TrainingEnvSpace] снято missing scripts×{removed} с '{GetTransformPath(t)}' ({context})");
#else
            Debug.LogWarning(
                $"[TrainingEnvSpace] missing script на '{GetTransformPath(t)}' ({context}) — почини в Editor и пересобери билд.");
#endif
        }
    }

    static string GetTransformPath(Transform t)
    {
        if (t == null)
            return "<null>";
        var sb = new System.Text.StringBuilder(t.name);
        var p = t.parent;
        while (p != null)
        {
            sb.Insert(0, '/');
            sb.Insert(0, p.name);
            p = p.parent;
        }
        return sb.ToString();
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
            if (renderer == null || !renderer.gameObject.activeInHierarchy)
                continue;
            if (IsWaterGoalRenderer(renderer.transform))
                continue;
            renderer.enabled = true;
        }

        WaterGoalPath.HideGoalsInEnv(envRoot);
    }

    /// <summary>Train-просмотр (env 1+): камера есть, источники звука молчат.</summary>
    static void MuteFocusedEnvAudioSources(Transform envRoot)
    {
        if (envRoot == null)
            return;

        foreach (var audio in envRoot.GetComponentsInChildren<AudioSource>(true))
        {
            if (audio == null)
                continue;
            audio.mute = true;
            if (audio.isPlaying)
                audio.Stop();
        }
    }

    static void UnmuteFocusedEnvAudioSources(Transform envRoot)
    {
        if (envRoot == null)
            return;

        foreach (var audio in envRoot.GetComponentsInChildren<AudioSource>(true))
        {
            if (audio == null)
                continue;
            audio.mute = false;
        }
    }

    static bool IsWaterGoalRenderer(Transform t)
    {
        while (t != null)
        {
            if (t.name.StartsWith("GoalWater", System.StringComparison.Ordinal))
                return true;
            if (IsEnvRootName(t.name))
                break;
            t = t.parent;
        }

        return false;
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
