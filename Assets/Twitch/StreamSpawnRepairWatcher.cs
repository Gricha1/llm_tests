using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Внешний watchdog (watch_stream_spawn_repair.py) пишет флаг → Unity
/// форсирует ResetTrees/ResetSheep без полного #reset эпизода.
/// </summary>
public sealed class StreamSpawnRepairWatcher : MonoBehaviour
{
    const string FlagFileName = ".forest_repair_spawners";
    const string ResultFileName = ".forest_repair_spawners_result";
    const float PollSeconds = 2f;
    const float CooldownSeconds = 20f;

    static StreamSpawnRepairWatcher _instance;
    float _nextPollUnscaled;
    float _lastRepairUnscaled = -999f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.ShouldRunPresentationOnlyServices())
            return;
        if (_instance != null)
            return;
        var go = new GameObject(nameof(StreamSpawnRepairWatcher));
        DontDestroyOnLoad(go);
        go.AddComponent<StreamSpawnRepairWatcher>();
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
        _nextPollUnscaled = Time.unscaledTime + 1f;
    }

    void Update()
    {
        if (Time.unscaledTime < _nextPollUnscaled)
            return;
        _nextPollUnscaled = Time.unscaledTime + PollSeconds;
        TryConsumeFlag();
    }

    void TryConsumeFlag()
    {
        string flag = ResolveFlagPath();
        if (string.IsNullOrEmpty(flag) || !File.Exists(flag))
            return;

        float now = Time.unscaledTime;
        if (now - _lastRepairUnscaled < CooldownSeconds)
            return;

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

        try
        {
            File.Delete(flag);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SpawnRepair] cannot delete flag: {e.Message}");
            return;
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

        // Только спавнеры — без ForceFullEpisodeRestart (стрим не дёргаем целиком).
        PresentationWorldReset.ResetSpawners(envRoot, force: true);

        // Доп. попытки, если лес/овцы всё ещё пустые.
        var trees = envRoot.GetComponentInChildren<TreeSpawner>(true);
        var sheep = envRoot.GetComponentInChildren<SheepSpawner>(true);
        for (int i = 0; i < 2; i++)
        {
            bool needTrees = trees != null && trees.TargetCount > 0
                && trees.AliveCount < Mathf.Max(1, trees.TargetCount / 3);
            bool needSheep = sheep != null && sheep.TargetCount > 0
                && sheep.AliveCount < Mathf.Max(1, sheep.TargetCount / 3);
            if (!needTrees && !needSheep)
                break;
            if (needTrees)
                trees.ResetTrees();
            if (needSheep)
                sheep.ResetSheep();
        }

        string after = PresentationWorldReset.DescribeState(envRoot);
        return $"ok reason={Sanitize(reason)} {after}";
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

    public static string ResolveFlagPath()
    {
        string results = TryResolveResultsDir();
        if (!string.IsNullOrEmpty(results))
        {
            try
            {
                // results/run_X → repo root
                string root = Path.GetFullPath(Path.Combine(results, "..", ".."));
                return Path.Combine(root, FlagFileName);
            }
            catch
            {
                // fall through
            }
        }

        try
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", FlagFileName));
        }
        catch
        {
            return Path.Combine(Application.persistentDataPath, FlagFileName);
        }
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
