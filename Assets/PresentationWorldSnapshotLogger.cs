using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Периодический снимок presentation/stream Env: деревья, овцы, герои, зомби, спавнеры.
/// Пишет в results/stream_world_snapshot.log — чтобы ловить пропажи без догадок.
/// </summary>
public sealed class PresentationWorldSnapshotLogger : MonoBehaviour
{
    const string FileName = "stream_world_snapshot.log";
    const float IntervalSeconds = 10f;
    const float CountDropWarnFraction = 0.5f;

    static PresentationWorldSnapshotLogger _instance;
    static string _logPath;
    static readonly object _ioLock = new object();

    float _nextSnapshotUnscaled;
    int _prevTrees = -1;
    int _prevSheep = -1;
    int _prevZombies = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!ShouldEnable())
            return;
        if (_instance != null)
            return;

        var go = new GameObject(nameof(PresentationWorldSnapshotLogger));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<PresentationWorldSnapshotLogger>();
    }

    static bool ShouldEnable()
    {
        if (TrainingEnvSpace.IsHeadlessTrainWorkerProcess)
            return false;
        return TrainingEnvSpace.ShouldRunPresentationOnlyServices()
            || TrainingEnvSpace.IsStreamOnlyMode
            || TrainingEnvSpace.IsValidateOrInferenceMode;
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
        _nextSnapshotUnscaled = Time.unscaledTime + 2f;
        LogEvent("logger_start", $"path={ResolveLogPath()}");
    }

    void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    void Update()
    {
        if (Time.unscaledTime < _nextSnapshotUnscaled)
            return;
        _nextSnapshotUnscaled = Time.unscaledTime + IntervalSeconds;
        WriteSnapshot("tick");
    }

    /// <summary>Короткая запись без полного снимка (watchdog / refill).</summary>
    public static void Note(string kind, string detail = null)
    {
        if (!ShouldEnable() && _instance == null)
            return;

        var line = new StringBuilder(192);
        line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        line.Append(" [NOTE] ").Append(kind ?? "note");
        if (!string.IsNullOrEmpty(detail))
            line.Append(' ').Append(detail);
        AppendLine(line.ToString());
    }

    /// <summary>Событие (reset / twitch / episode) + сразу полный снимок.</summary>
    public static void LogEvent(string kind, string detail = null)
    {
        if (!ShouldEnable() && _instance == null)
            return;

        var line = new StringBuilder(256);
        line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        line.Append(" [EVENT] ").Append(kind ?? "event");
        if (!string.IsNullOrEmpty(detail))
            line.Append(' ').Append(detail);
        AppendLine(line.ToString());
        WriteSnapshot(kind ?? "event");
    }

    static void WriteSnapshot(string reason)
    {
        var env = TrainingEnvSpace.ActiveViewEnvRoot ?? TrainingEnvSpace.PresentationRoot;
        if (env == null)
        {
            AppendLine($"{Stamp()} [SNAP:{reason}] env=null");
            return;
        }

        var sb = new StringBuilder(2048);
        sb.Append(Stamp());
        sb.Append(" [SNAP:").Append(reason).Append("] env=").Append(env.name);
        sb.Append(" t=").Append(Time.time.ToString("0.0"));
        sb.Append(" ut=").Append(Time.unscaledTime.ToString("0.0"));

        var treeSpawner = env.GetComponentInChildren<TreeSpawner>(true);
        var sheepSpawner = env.GetComponentInChildren<SheepSpawner>(true);
        var flowerSpawner = env.GetComponentInChildren<FlowerSpawner>(true);
        var zombieSpawners = env.GetComponentsInChildren<ZombieSpawner>(true);

        AppendSpawner(sb, "treeSpawner", treeSpawner != null ? treeSpawner.transform : null);
        AppendSpawner(sb, "sheepSpawner", sheepSpawner != null ? sheepSpawner.transform : null);
        AppendSpawner(sb, "flowerSpawner", flowerSpawner != null ? flowerSpawner.transform : null);
        if (zombieSpawners != null)
        {
            for (int i = 0; i < zombieSpawners.Length; i++)
            {
                var zs = zombieSpawners[i];
                if (zs == null)
                    continue;
                AppendSpawner(sb, "zombieSpawner:" + zs.name, zs.transform);
            }
        }

        var jack = TrainingEnvSpace.FindPresentationPrimaryJack()
            ?? TrainingEnvSpace.FindPrimaryJackInEnv(env);
        var lily = TrainingEnvSpace.FindPresentationLily();
        var george = TrainingEnvSpace.FindPresentationGeorge();

        AppendAgent(sb, "Jack", jack != null ? jack.transform : null, jack);
        AppendAgent(sb, "Lily", lily != null ? lily.transform : null, null, lily);
        AppendAgent(sb, "George", george != null ? george.transform : null, george);

        var trees = env.GetComponentsInChildren<ChoppableTree>(true);
        int treeN = 0;
        sb.Append("\n  trees=");
        if (trees != null)
        {
            for (int i = 0; i < trees.Length; i++)
            {
                var t = trees[i];
                if (t == null || !t.gameObject.activeSelf)
                    continue;
                if (treeN == 0)
                    sb.Append('[');
                else
                    sb.Append(' ');
                AppendXz(sb, t.transform.position);
                treeN++;
            }
        }
        if (treeN == 0)
            sb.Append("[]");
        else
            sb.Append(']');
        sb.Append(" n=").Append(treeN);
        if (treeSpawner != null)
            sb.Append('/').Append(treeSpawner.TargetCount).Append("(spawnerAlive=")
                .Append(treeSpawner.AliveCount).Append(')');

        var sheep = env.GetComponentsInChildren<SheepWander>(true);
        int sheepN = 0;
        sb.Append("\n  sheep=");
        if (sheep != null)
        {
            for (int i = 0; i < sheep.Length; i++)
            {
                var s = sheep[i];
                if (s == null || !s.gameObject.activeSelf)
                    continue;
                if (sheepN == 0)
                    sb.Append('[');
                else
                    sb.Append(' ');
                AppendXz(sb, s.transform.position);
                sheepN++;
            }
        }
        if (sheepN == 0)
            sb.Append("[]");
        else
            sb.Append(']');
        sb.Append(" n=").Append(sheepN);
        if (sheepSpawner != null)
            sb.Append('/').Append(sheepSpawner.TargetCount).Append("(spawnerAlive=")
                .Append(sheepSpawner.AliveCount).Append(')');

        var zombies = env.GetComponentsInChildren<ZombieHealth>(true);
        int zN = 0;
        sb.Append("\n  zombies=");
        if (zombies != null)
        {
            for (int i = 0; i < zombies.Length; i++)
            {
                var z = zombies[i];
                if (z == null || !z.gameObject.activeSelf)
                    continue;
                if (zN == 0)
                    sb.Append('[');
                else
                    sb.Append(' ');
                AppendXz(sb, z.transform.position);
                sb.Append(" hp=").Append(z.Hp);
                zN++;
            }
        }
        if (zN == 0)
            sb.Append("[]");
        else
            sb.Append(']');
        sb.Append(" n=").Append(zN);

        // HUD «R джек/лили/гера»: ловим дубликаты / мёртвые дисплеи.
        string hud = RewardDisplay.DescribeHudHealth();
        sb.Append("\n  hud_reward ").Append(hud);
        if (CountHudRole(hud, "jack") > 1 || CountHudRole(hud, "lily") > 1
            || CountHudRole(hud, "george") > 1)
        {
            AppendLine($"{Stamp()} [WARN] hud_reward_duplicate {hud}");
            RewardDisplay.DeduplicateAll();
        }

        AppendLine(sb.ToString());

        if (_instance != null)
        {
            _instance.MaybeWarnCountDrop("trees", ref _instance._prevTrees, treeN);
            _instance.MaybeWarnCountDrop("sheep", ref _instance._prevSheep, sheepN);
            _instance.MaybeWarnCountDrop("zombies", ref _instance._prevZombies, zN);
        }
    }

    void MaybeWarnCountDrop(string label, ref int prev, int now)
    {
        if (prev > 0 && now < prev * CountDropWarnFraction)
        {
            AppendLine(
                $"{Stamp()} [WARN] {label} count drop {prev}->{now} " +
                $"(env={(TrainingEnvSpace.ActiveViewEnvRoot ?? TrainingEnvSpace.PresentationRoot)?.name})");
        }
        prev = now;
    }

    static int CountHudRole(string hud, string role)
    {
        // "counts jack=1 lily=1 george=1"
        string key = role + "=";
        int idx = hud.IndexOf(key, System.StringComparison.Ordinal);
        if (idx < 0)
            return 0;
        idx += key.Length;
        int end = idx;
        while (end < hud.Length && char.IsDigit(hud[end]))
            end++;
        if (end <= idx)
            return 0;
        return int.TryParse(hud.Substring(idx, end - idx), out int n) ? n : 0;
    }

    static void AppendAgent(
        StringBuilder sb,
        string name,
        Transform t,
        AgentGoToHouseDiscrete jackOrGeorge = null,
        LilyScript lily = null)
    {
        sb.Append('\n').Append("  ").Append(name).Append('=');
        if (t == null)
        {
            sb.Append("null");
            return;
        }

        AppendXz(sb, t.position);
        sb.Append(" y=").Append(t.position.y.ToString("0.00"));
        if (jackOrGeorge != null)
        {
            sb.Append(" hp=").Append(jackOrGeorge.hp);
            sb.Append(" sat=").Append(jackOrGeorge.satiety);
            sb.Append(" heat=").Append(jackOrGeorge.heat);
            sb.Append(" water=").Append(jackOrGeorge.water);
            sb.Append(" wood=").Append(jackOrGeorge.wood);
            if (jackOrGeorge.IsInDeathState)
                sb.Append(" DEAD");
        }
        else if (lily != null)
        {
            sb.Append(" hp=").Append(lily.Hp);
            sb.Append(" sat=").Append(lily.Satiety);
            sb.Append(" heat=").Append(lily.Heat);
            sb.Append(" water=").Append(lily.WaterCount);
            if (lily.IsInDeathState)
                sb.Append(" DEAD");
        }
    }

    static void AppendSpawner(StringBuilder sb, string label, Transform t)
    {
        sb.Append('\n').Append("  ").Append(label).Append('=');
        if (t == null)
        {
            sb.Append("null");
            return;
        }

        AppendXz(sb, t.position);
        sb.Append(" active=").Append(t.gameObject.activeInHierarchy ? 1 : 0);
    }

    static void AppendXz(StringBuilder sb, Vector3 p)
    {
        sb.Append('(').Append(p.x.ToString("0.00")).Append(',').Append(p.z.ToString("0.00")).Append(')');
    }

    static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

    static void AppendLine(string line)
    {
        try
        {
            string path = ResolveLogPath();
            if (string.IsNullOrEmpty(path))
                return;

            lock (_ioLock)
            {
                File.AppendAllText(path, line + "\n", Encoding.UTF8);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PresentationWorldSnapshotLogger] write failed: {e.Message}");
        }
    }

    static string ResolveLogPath()
    {
        if (!string.IsNullOrEmpty(_logPath))
            return _logPath;

        string dir = ResolveResultsDir();
        if (string.IsNullOrEmpty(dir))
        {
            try
            {
                dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "results"));
            }
            catch
            {
                dir = Application.persistentDataPath;
            }
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // ignore
        }

        _logPath = Path.Combine(dir, FileName);
        return _logPath;
    }

    static string ResolveResultsDir()
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
