using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Внешний watchdog (watch_stream_spawn_repair.py) / UI «Починить спавн»
/// пишет флаг → Unity форсирует ResetTrees/ResetSheep без полного #reset эпизода.
/// </summary>
public sealed class StreamSpawnRepairWatcher : MonoBehaviour
{
    public const string RepairBuildId = "repair_v2_20260821_force";
    const string FlagFileName = ".forest_repair_spawners";
    const string ResultFileName = ".forest_repair_spawners_result";
    const float PollSeconds = 1f;
    const float CooldownSeconds = 8f;

    static StreamSpawnRepairWatcher _instance;
    float _nextPollUnscaled;
    float _lastRepairUnscaled = -999f;
    float _nextHeartbeatUnscaled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!ShouldEnable())
            return;
        EnsureInstance();
    }

    static bool ShouldEnable()
    {
        if (TrainingEnvSpace.IsStreamOnlyMode)
            return true;
        if (TrainingEnvSpace.IsPresentationFullTrainWorker
            && TrainingEnvSpace.IsTrainAllHeadlessRequested())
            return false;
        if (TrainingEnvSpace.IsPresentationFullTrainWorker)
            return true;
        if (TrainingEnvSpace.IsHeadlessTrainWorkerProcess)
            return false;
        return TrainingEnvSpace.ShouldRunPresentationOnlyServices()
            || TrainingEnvSpace.IsValidateOrInferenceMode;
    }

    static void EnsureInstance()
    {
        if (_instance != null)
            return;
        var go = new GameObject(nameof(StreamSpawnRepairWatcher));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<StreamSpawnRepairWatcher>();
    }

    /// <summary>Вызывается из snapshot-logger, если Bootstrap не сработал.</summary>
    public static void EnsureAlive()
    {
        if (!ShouldEnable())
            return;
        EnsureInstance();
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
        _nextPollUnscaled = Time.unscaledTime + 0.5f;
        _nextHeartbeatUnscaled = Time.unscaledTime + 5f;
        Debug.Log($"[SpawnRepair] watcher online flag={ResolveFlagPath()}");
        PresentationWorldSnapshotLogger.Note("spawn_repair_watcher", $"online flag={ResolveFlagPath()}");
    }

    void Update()
    {
        if (_instance != this)
            return;

        float now = Time.unscaledTime;
        if (now >= _nextHeartbeatUnscaled)
        {
            _nextHeartbeatUnscaled = now + 60f;
            PresentationWorldSnapshotLogger.Note(
                "spawn_repair_hb",
                $"flag_exists={File.Exists(ResolveFlagPath())} path={ResolveFlagPath()}");
        }

        if (now < _nextPollUnscaled)
            return;
        _nextPollUnscaled = now + PollSeconds;
        TryConsumeFlag();
    }

    void TryConsumeFlag()
    {
        string flag = FindExistingFlagPath();
        if (string.IsNullOrEmpty(flag))
            return;

        float now = Time.unscaledTime;
        if (now - _lastRepairUnscaled < CooldownSeconds)
        {
            WriteResult($"cooldown remaining={CooldownSeconds - (now - _lastRepairUnscaled):0.0}s flag={flag}");
            return;
        }

        string reason = "flag";
        try
        {
            reason = (File.ReadAllText(flag) ?? "").Trim();
            if (reason.Length > 200)
                reason = reason.Substring(0, 200);
        }
        catch
        {
            // ignore
        }

        // Снести все копии флага (repo root / cwd / results).
        foreach (var p in CandidateFlagPaths())
        {
            try
            {
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    File.Delete(p);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SpawnRepair] cannot delete {p}: {e.Message}");
            }
        }

        _lastRepairUnscaled = now;
        string state = RunRepair(reason);
        WriteResult(state);
        PresentationWorldSnapshotLogger.Note("spawn_repair", state);
        Debug.Log($"[SpawnRepair] {state}");
    }

    static string RunRepair(string reason)
    {
        var envRoot = TrainingEnvSpace.ActiveViewEnvRoot ?? TrainingEnvSpace.PresentationRoot;
        if (envRoot == null)
            return $"fail reason={reason} env=null";

        if (!envRoot.gameObject.activeSelf)
            envRoot.gameObject.SetActive(true);

        PresentationWorldReset.ResetSpawners(envRoot, force: true);

        var trees = envRoot.GetComponentInChildren<TreeSpawner>(true);
        var sheep = envRoot.GetComponentInChildren<SheepSpawner>(true);
        // Если ResetTrees ушёл в early-return из‑за _resetInProgress — форсим ещё раз.
        if (trees != null && trees.TargetCount > 0 && trees.AliveCount == 0)
            trees.ForceResetTrees();
        for (int i = 0; i < 4; i++)
        {
            bool needTrees = trees != null && trees.TargetCount > 0
                && trees.AliveCount < Mathf.Max(1, Mathf.RoundToInt(trees.TargetCount * 0.55f));
            bool needSheep = sheep != null && sheep.TargetCount > 0
                && sheep.AliveCount < Mathf.Max(1, sheep.TargetCount / 3);
            if (!needTrees && !needSheep)
                break;
            if (needTrees)
                trees.ForceResetTrees();
            if (needSheep)
                sheep.ResetSheep();
        }

        string after = PresentationWorldReset.DescribeState(envRoot);
        string health = trees != null ? trees.DescribePrefabHealth() : "no_trees";
        int alive = trees != null ? trees.AliveCount : -1;
        if (trees != null && trees.TargetCount > 0 && alive == 0)
            return $"FAILTREE build={RepairBuildId} alive={alive} reason={Sanitize(reason)} {after} | {health}";
        return $"OKTREE build={RepairBuildId} alive={alive} reason={Sanitize(reason)} {after} | {health}";
    }

    static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "flag";
        return s.Replace('\n', ' ').Replace('\r', ' ');
    }

    static void WriteResult(string state)
    {
        try
        {
            string path = ResolveResultPath();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, $"utc={DateTime.UtcNow:o}\n{state}\n");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SpawnRepair] result write failed: {e.Message}");
        }
    }

    static string FindExistingFlagPath()
    {
        foreach (var p in CandidateFlagPaths())
        {
            try
            {
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    return p;
            }
            catch
            {
                // ignore
            }
        }
        return null;
    }

    static string[] CandidateFlagPaths()
    {
        var list = new System.Collections.Generic.List<string>(8);
        void Add(string p)
        {
            if (string.IsNullOrEmpty(p))
                return;
            try
            {
                p = Path.GetFullPath(p);
            }
            catch
            {
                return;
            }
            if (!list.Contains(p))
                list.Add(p);
        }

        string results = TryResolveResultsDir();
        if (!string.IsNullOrEmpty(results))
        {
            // results/run_X → repo root
            Add(Path.Combine(results, "..", "..", FlagFileName));
            // на всякий случай рядом с run
            Add(Path.Combine(results, FlagFileName));
            Add(Path.Combine(results, "..", FlagFileName));
        }

        try
        {
            Add(Path.Combine(Directory.GetCurrentDirectory(), FlagFileName));
        }
        catch
        {
            // ignore
        }

        try
        {
            // build_versions/<name>_Data → repo root
            Add(Path.Combine(Application.dataPath, "..", "..", FlagFileName));
            Add(Path.Combine(Application.dataPath, "..", FlagFileName));
        }
        catch
        {
            // ignore
        }

        try
        {
            Add(Path.Combine(Application.persistentDataPath, FlagFileName));
        }
        catch
        {
            // ignore
        }

        return list.ToArray();
    }

    public static string ResolveFlagPath()
    {
        var all = CandidateFlagPaths();
        return all.Length > 0 ? all[0] : FlagFileName;
    }

    static string ResolveResultPath()
    {
        string flag = ResolveFlagPath();
        string dir = Path.GetDirectoryName(flag) ?? ".";
        return Path.Combine(dir, ResultFileName);
    }

    static string TryResolveResultsDir()
    {
        var env = Environment.GetEnvironmentVariable("FOREST_RESULTS_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "-forestResultsDir" || args[i] == "--forest-results-dir")
                && i + 1 < args.Length
                && !string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1].Trim();
        }

        return null;
    }
}
