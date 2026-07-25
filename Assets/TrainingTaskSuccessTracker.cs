using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.MLAgents;
using UnityEngine;

/// <summary>
/// Success rate по задачам героев (Jack/Lily/George).
/// Пишет JSONL в results/&lt;run_id&gt;/ — все headless worker'ы и стрим читают один файл.
/// В TensorBoard: SuccessRate/Jack|Lily|George/… и EpisodeReward/….
/// </summary>
public static class TrainingTaskSuccessTracker
{
    public const int SheepGoal = 10;

    public enum Metric
    {
        Wood,
        Sheep,
        Fire,
        Water,
        Zombie,
        Flower,
    }

    const int MaxHistoryPoints = 512;
    const int RollingWindow = 32;
    const int BoostSlotCount = 4;
    const float BoostRankingRefreshSeconds = 30f;
    const string FileName = "simple_task_success.jsonl";

    const int DiskTailMaxLines = 32000;
    const int MaxRawSamples = 4096;

    static readonly Dictionary<Metric, List<float>> RateHistory = new();
    static readonly Dictionary<Metric, List<float>> RawSamples = new();
    static readonly Dictionary<Metric, float> LastRate = new();
    static readonly Dictionary<EnvTrainingTask, List<float>> TaskRawSamples = new();
    static readonly Dictionary<EnvTrainingTask, List<float>> TaskRateHistory = new();
    static readonly Dictionary<EnvTrainingTask, float> TaskLastRate = new();
    static readonly EnvTrainingTask[] _boostRankedTasks = new EnvTrainingTask[BoostSlotCount];
    static string _resultsDir;
    static float _nextDiskReadTime;
    static float _nextBoostRankingTime;
    static long _lastFileSize = -1;
    static bool _boostRankingReady;
    static bool _hasCommittedBoost;
    static int _committedBoostSlot = -1;
    static EnvTrainingTask _committedBoostTask = EnvTrainingTask.JackWood;

    static TrainingTaskSuccessTracker()
    {
        foreach (Metric m in Enum.GetValues(typeof(Metric)))
        {
            RateHistory[m] = new List<float>();
            RawSamples[m] = new List<float>();
            LastRate[m] = 0f;
        }

        // Cold start: wood / sheep / water / zombie, пока нет статистики.
        _boostRankedTasks[0] = EnvTrainingTask.JackWood;
        _boostRankedTasks[1] = EnvTrainingTask.JackFood;
        _boostRankedTasks[2] = EnvTrainingTask.JackWater;
        _boostRankedTasks[3] = EnvTrainingTask.JackZombie;
        _boostRankingReady = true;
    }

    static EnvTrainingTask MetricToBoostTask(Metric metric)
    {
        switch (metric)
        {
            case Metric.Wood: return EnvTrainingTask.JackWood;
            case Metric.Sheep: return EnvTrainingTask.JackFood;
            case Metric.Fire: return EnvTrainingTask.LilyHeat;
            case Metric.Water: return EnvTrainingTask.JackWater;
            case Metric.Zombie: return EnvTrainingTask.JackZombie;
            case Metric.Flower: return EnvTrainingTask.LilyFlower;
            default: return EnvTrainingTask.JackWood;
        }
    }

    static void RefreshBoostRankingIfNeeded(bool force)
    {
        if (!force && _boostRankingReady && Time.unscaledTime < _nextBoostRankingTime)
            return;

        MaybeRefreshFromDisk();
        _nextBoostRankingTime = Time.unscaledTime + BoostRankingRefreshSeconds;

        var ranked = new List<(Metric metric, float rate, int samples)>();
        foreach (Metric m in Enum.GetValues(typeof(Metric)))
        {
            float rate = LastRate.TryGetValue(m, out float r) ? r : 0f;
            int samples = RawSamples.TryGetValue(m, out var list) ? list.Count : 0;
            ranked.Add((m, rate, samples));
        }

        // Сначала низкий success rate; при равенстве — меньше сэмплов (нужно больше данных).
        ranked.Sort((a, b) =>
        {
            int byRate = a.rate.CompareTo(b.rate);
            if (byRate != 0)
                return byRate;
            int bySamples = a.samples.CompareTo(b.samples);
            if (bySamples != 0)
                return bySamples;
            return a.metric.CompareTo(b.metric);
        });

        for (int i = 0; i < BoostSlotCount; i++)
            _boostRankedTasks[i] = MetricToBoostTask(ranked[i].metric);

        _boostRankingReady = true;
    }

    public static Metric? TaskToMetric(EnvTrainingTask task)
    {
        switch (task)
        {
            case EnvTrainingTask.JackWood: return Metric.Wood;
            case EnvTrainingTask.JackFood:
            case EnvTrainingTask.LilyFood:
            case EnvTrainingTask.GeorgeFood: return Metric.Sheep;
            case EnvTrainingTask.LilyHeat:
            case EnvTrainingTask.GeorgeHeat: return Metric.Fire;
            case EnvTrainingTask.JackWater:
            case EnvTrainingTask.LilyWater:
            case EnvTrainingTask.GeorgeWater: return Metric.Water;
            case EnvTrainingTask.JackZombie: return Metric.Zombie;
            case EnvTrainingTask.LilyFlower: return Metric.Flower;
            default: return null;
        }
    }

    public static bool ShouldTrack(EnvTrainingTask task) => TaskToMetric(task).HasValue;

    public static void Record(EnvTrainingTask task, bool success)
    {
        var metric = TaskToMetric(task);
        if (!metric.HasValue)
            return;

        AppendToDisk(metric.Value, task, success);
        PushLocalSample(metric.Value, success ? 1f : 0f);
        PushTaskSample(task, success ? 1f : 0f);
        ReportToTensorBoard(task, metric.Value);
    }

    public static float GetTaskLastRate(EnvTrainingTask task)
    {
        MaybeRefreshFromDisk();
        return TaskLastRate.TryGetValue(task, out float v) ? v : 0f;
    }

    public static float GetTaskLastRateCached(EnvTrainingTask task) =>
        TaskLastRate.TryGetValue(task, out float v) ? v : 0f;

    public static IReadOnlyList<float> GetTaskRateSeriesCached(EnvTrainingTask task) =>
        TaskRateHistory.TryGetValue(task, out var list) ? list : Array.Empty<float>();

    public static int GetTaskSampleCountCached(EnvTrainingTask task) =>
        TaskRawSamples.TryGetValue(task, out var list) ? list.Count : 0;

    public static bool EvaluateJackSuccess(EnvTrainingTask task, AgentGoToHouseDiscrete jack, int episodeSheepEaten, int episodeZombiesKilled)
    {
        if (jack == null)
            return false;

        switch (task)
        {
            case EnvTrainingTask.JackWood:
                return jack.HasCompletedWoodDeliveryGoal;
            case EnvTrainingTask.JackFood:
                return episodeSheepEaten >= SheepGoal;
            case EnvTrainingTask.JackWater:
                return IsJackWaterComplete(jack);
            case EnvTrainingTask.JackZombie:
                return episodeZombiesKilled > 0;
            case EnvTrainingTask.GeorgeFood:
                return episodeSheepEaten >= SheepGoal;
            case EnvTrainingTask.GeorgeWater:
                return IsJackWaterComplete(jack);
            case EnvTrainingTask.GeorgeHeat:
                return jack.IsGeorgeNearBurningCampfirePublic();
            default:
                return false;
        }
    }

    public static bool EvaluateLilySuccess(EnvTrainingTask task, LilyScript lily, int episodeSheepEaten)
    {
        if (lily == null)
            return false;

        switch (task)
        {
            case EnvTrainingTask.LilyFood:
                return episodeSheepEaten >= SheepGoal;
            case EnvTrainingTask.LilyWater:
                return IsLilyWaterComplete(lily);
            case EnvTrainingTask.LilyHeat:
                return lily.IsOnHousePublic() && lily.IsCampfireBurningInEnvPublic();
            case EnvTrainingTask.LilyFlower:
                return lily.FlowerCount > 0;
            default:
                return false;
        }
    }

    public static IReadOnlyList<float> GetRateSeries(Metric metric)
    {
        MaybeRefreshFromDisk();
        return RateHistory.TryGetValue(metric, out var list) ? list : Array.Empty<float>();
    }

    /// <summary>Без чтения диска — для UI после TickOverlayRefresh.</summary>
    public static IReadOnlyList<float> GetRateSeriesCached(Metric metric) =>
        RateHistory.TryGetValue(metric, out var list) ? list : Array.Empty<float>();

    public static float GetLastRate(Metric metric)
    {
        MaybeRefreshFromDisk();
        return LastRate.TryGetValue(metric, out float v) ? v : 0f;
    }

    public static float GetLastRateCached(Metric metric) =>
        LastRate.TryGetValue(metric, out float v) ? v : 0f;

    public static int GetSampleCount(Metric metric)
    {
        MaybeRefreshFromDisk();
        return RawSamples.TryGetValue(metric, out var list) ? list.Count : 0;
    }

    public static int GetSampleCountCached(Metric metric) =>
        RawSamples.TryGetValue(metric, out var list) ? list.Count : 0;

    /// <summary>
    /// Workers 12–15: фиксируем задачу на эпизод (slot 0..3 = 4 самых слабых метрики).
    /// </summary>
    public static void CommitBoostSlot(int slot)
    {
        slot = Mathf.Clamp(slot, 0, BoostSlotCount - 1);
        RefreshBoostRankingIfNeeded(force: true);
        _committedBoostSlot = slot;
        _committedBoostTask = _boostRankedTasks[slot];
        _hasCommittedBoost = true;
    }

    public static EnvTrainingTask GetBoostTaskForSlot(int slot)
    {
        slot = Mathf.Clamp(slot, 0, BoostSlotCount - 1);
        if (_hasCommittedBoost && _committedBoostSlot == slot)
            return _committedBoostTask;

        RefreshBoostRankingIfNeeded(force: false);
        return _boostRankedTasks[slot];
    }

    public static void TickOverlayRefresh()
    {
        if (Time.unscaledTime < _nextDiskReadTime)
            return;

        // Реже читаем диск: 32k строк хвоста, иначе #show metrics снова подвешивает стрим.
        _nextDiskReadTime = Time.unscaledTime + 4f;
        RefreshFromDisk(force: true);
    }

    static void MaybeRefreshFromDisk()
    {
        RefreshFromDisk(force: false);
    }

    static void AppendToDisk(Metric metric, EnvTrainingTask task, bool success)
    {
        var path = GetStatsFilePath();
        if (path == null)
            return;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // task нужен стриму: иначе wood/zombie тонут в хвосте sheep/fire.
            var line =
                $"{{\"m\":\"{metric}\",\"task\":\"{task}\",\"s\":{(success ? 1 : 0)},\"t\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}\n";
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs, Encoding.UTF8))
                sw.Write(line);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TaskSuccess] write fail: {ex.Message}");
        }
    }

    static void PushLocalSample(Metric metric, float sample)
    {
        if (!RawSamples.TryGetValue(metric, out var raw))
            raw = RawSamples[metric] = new List<float>();

        raw.Add(sample);
        if (raw.Count > MaxRawSamples)
            raw.RemoveAt(0);

        RebuildMetricHistory(metric, raw);
    }

    static void PushTaskSample(EnvTrainingTask task, float sample)
    {
        if (!TaskRawSamples.TryGetValue(task, out var raw))
            raw = TaskRawSamples[task] = new List<float>();

        raw.Add(sample);
        if (raw.Count > MaxRawSamples)
            raw.RemoveAt(0);

        RebuildTaskHistory(task, raw);
    }

    static string TaskTensorBoardKey(EnvTrainingTask task)
    {
        // Без префикса героя: StatsRecorder пишет в папку текущего Behavior
        // (JackLowLevelAgent / Lily… / George…) — там уже ясно чей лог.
        switch (task)
        {
            case EnvTrainingTask.JackWood: return "Wood";
            case EnvTrainingTask.JackFood:
            case EnvTrainingTask.LilyFood:
            case EnvTrainingTask.GeorgeFood: return "Sheep";
            case EnvTrainingTask.JackWater:
            case EnvTrainingTask.LilyWater:
            case EnvTrainingTask.GeorgeWater: return "Water";
            case EnvTrainingTask.JackZombie: return "Zombie";
            case EnvTrainingTask.LilyHeat:
            case EnvTrainingTask.GeorgeHeat: return "Fire";
            case EnvTrainingTask.LilyFlower: return "Flower";
            default: return null;
        }
    }

    static string HeroNameForTask(EnvTrainingTask task)
    {
        switch (task)
        {
            case EnvTrainingTask.JackWood:
            case EnvTrainingTask.JackFood:
            case EnvTrainingTask.JackWater:
            case EnvTrainingTask.JackZombie:
                return "Jack";
            case EnvTrainingTask.LilyFood:
            case EnvTrainingTask.LilyWater:
            case EnvTrainingTask.LilyHeat:
            case EnvTrainingTask.LilyFlower:
                return "Lily";
            case EnvTrainingTask.GeorgeFood:
            case EnvTrainingTask.GeorgeWater:
            case EnvTrainingTask.GeorgeHeat:
                return "George";
            default:
                return null;
        }
    }

    static EnvTrainingTask[] TasksForHero(string hero)
    {
        switch (hero)
        {
            case "Jack":
                return new[]
                {
                    EnvTrainingTask.JackWood,
                    EnvTrainingTask.JackFood,
                    EnvTrainingTask.JackWater,
                    EnvTrainingTask.JackZombie,
                };
            case "Lily":
                return new[]
                {
                    EnvTrainingTask.LilyFood,
                    EnvTrainingTask.LilyWater,
                    EnvTrainingTask.LilyHeat,
                    EnvTrainingTask.LilyFlower,
                };
            case "George":
                return new[]
                {
                    EnvTrainingTask.GeorgeFood,
                    EnvTrainingTask.GeorgeWater,
                    EnvTrainingTask.GeorgeHeat,
                };
            default:
                return Array.Empty<EnvTrainingTask>();
        }
    }

    static void ReportToTensorBoard(EnvTrainingTask task, Metric metric)
    {
        if (!Academy.IsInitialized || !Academy.Instance.IsCommunicatorOn)
            return;

        var stats = Academy.Instance.StatsRecorder;
        // Пишется в summary текущего Behavior (кто вызвал Record).
        string taskKey = TaskTensorBoardKey(task);
        if (!string.IsNullOrEmpty(taskKey))
            stats.Add($"SuccessRate/{taskKey}", GetTaskLastRateCached(task), StatAggregationMethod.Average);

        string hero = HeroNameForTask(task);
        if (string.IsNullOrEmpty(hero))
            return;

        float sum = 0f;
        int n = 0;
        var heroTasks = TasksForHero(hero);
        for (int i = 0; i < heroTasks.Length; i++)
        {
            if (GetTaskSampleCountCached(heroTasks[i]) <= 0)
                continue;
            sum += GetTaskLastRateCached(heroTasks[i]);
            n++;
        }

        if (n > 0)
            stats.Add("SuccessRate/Mean", sum / n, StatAggregationMethod.Average);
    }

    /// <summary>
    /// Суммарный reward эпизода в папку текущего Behavior (Jack/Lily/George).
    /// Один тег EpisodeReward — return одного эпизода (через anchor в агенте).
    /// В TB при выборе 3 runs будет 1 график с 3 линиями.
    /// </summary>
    public static void ReportAgentEpisodeReward(float episodeReward)
    {
        if (!Academy.IsInitialized || !Academy.Instance.IsCommunicatorOn)
            return;

        if (float.IsNaN(episodeReward) || float.IsInfinity(episodeReward))
            return;

        Academy.Instance.StatsRecorder.Add(
            "EpisodeReward",
            episodeReward,
            StatAggregationMethod.Average);
    }

    static void RefreshFromDisk(bool force)
    {
        var path = GetStatsFilePath();
        if (path == null || !File.Exists(path))
            return;

        try
        {
            long size = new FileInfo(path).Length;
            if (!force && size == _lastFileSize)
                return;

            _lastFileSize = size;
            // Большой хвост: Wood/Zombie пишутся реже Sheep/Fire и иначе выпадают из окна.
            var tail = ReadTailLines(path, DiskTailMaxLines);
            var metricSamples = new Dictionary<Metric, List<float>>();
            var taskSamples = new Dictionary<EnvTrainingTask, List<float>>();
            foreach (Metric m in Enum.GetValues(typeof(Metric)))
                metricSamples[m] = new List<float>();

            for (int i = 0; i < tail.Count; i++)
                TryParseLine(tail[i], metricSamples, taskSamples);

            foreach (Metric m in Enum.GetValues(typeof(Metric)))
            {
                TrimToLast(metricSamples[m], MaxRawSamples);
                RawSamples[m] = metricSamples[m];
                RebuildMetricHistory(m, metricSamples[m]);
            }

            TaskRawSamples.Clear();
            TaskRateHistory.Clear();
            TaskLastRate.Clear();
            foreach (var kv in taskSamples)
            {
                TrimToLast(kv.Value, MaxRawSamples);
                TaskRawSamples[kv.Key] = kv.Value;
                RebuildTaskHistory(kv.Key, kv.Value);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TaskSuccess] read fail: {ex.Message}");
        }
    }

    static void TrimToLast(List<float> list, int max)
    {
        if (list == null || list.Count <= max)
            return;
        list.RemoveRange(0, list.Count - max);
    }

    static void RebuildMetricHistory(Metric metric, List<float> samples01)
    {
        if (!RateHistory.TryGetValue(metric, out var history))
            return;

        history.Clear();
        for (int i = 0; i < samples01.Count; i++)
        {
            float rolling = ComputeRollingRate(samples01, i, RollingWindow);
            history.Add(rolling);
        }

        while (history.Count > MaxHistoryPoints)
            history.RemoveAt(0);

        LastRate[metric] = history.Count > 0 ? history[history.Count - 1] : 0f;
    }

    static void RebuildTaskHistory(EnvTrainingTask task, List<float> samples01)
    {
        if (!TaskRateHistory.TryGetValue(task, out var history))
            history = TaskRateHistory[task] = new List<float>();

        history.Clear();
        for (int i = 0; i < samples01.Count; i++)
            history.Add(ComputeRollingRate(samples01, i, RollingWindow));

        while (history.Count > MaxHistoryPoints)
            history.RemoveAt(0);

        TaskLastRate[task] = history.Count > 0 ? history[history.Count - 1] : 0f;
    }

    static float ComputeRollingRate(List<float> samples, int endIndex, int window)
    {
        int start = Mathf.Max(0, endIndex - window + 1);
        int count = endIndex - start + 1;
        if (count <= 0)
            return 0f;

        float sum = 0f;
        for (int i = start; i <= endIndex; i++)
            sum += samples[i];
        return sum / count;
    }

    static void TryParseLine(string line,
        Dictionary<Metric, List<float>> metricSamples,
        Dictionary<EnvTrainingTask, List<float>> taskSamples)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        int mIdx = line.IndexOf("\"m\":\"", StringComparison.Ordinal);
        if (mIdx < 0)
            return;

        mIdx += 5;
        int mEnd = line.IndexOf('"', mIdx);
        if (mEnd < 0 || !Enum.TryParse(line.Substring(mIdx, mEnd - mIdx), out Metric metric))
            return;

        int sIdx = line.IndexOf("\"s\":", StringComparison.Ordinal);
        if (sIdx < 0)
            return;

        sIdx += 4;
        int sEnd = line.IndexOfAny(new[] { ',', '}' }, sIdx);
        if (sEnd < 0 || !int.TryParse(line.Substring(sIdx, sEnd - sIdx), out int s))
            return;

        float sample = s == 1 ? 1f : 0f;
        if (metricSamples.TryGetValue(metric, out var mList))
            mList.Add(sample);

        EnvTrainingTask task = MetricToBoostTask(metric);
        int tIdx = line.IndexOf("\"task\":\"", StringComparison.Ordinal);
        if (tIdx >= 0)
        {
            tIdx += 8;
            int tEnd = line.IndexOf('"', tIdx);
            if (tEnd > tIdx && Enum.TryParse(line.Substring(tIdx, tEnd - tIdx), out EnvTrainingTask parsedTask))
                task = parsedTask;
        }

        if (!taskSamples.TryGetValue(task, out var tList))
            tList = taskSamples[task] = new List<float>();
        tList.Add(sample);
    }

    static List<string> ReadTailLines(string path, int maxLines)
    {
        var lines = new List<string>(maxLines + 1);
        // НЕ читать весь файл с начала: jsonl на стриме легко >100MB, и это вешает Unity
        // (после #show metrics #restart_stream уже не обрабатывается).
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            long len = fs.Length;
            if (len <= 0)
                return lines;

            // ~200 байт на строку → запас под maxLines
            long approx = Math.Min(len, (long)maxLines * 256L + 4096L);
            fs.Seek(Math.Max(0L, len - approx), SeekOrigin.Begin);

            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                // Первая строка после seek может быть обрезана — пропускаем.
                if (fs.Position > 0)
                    sr.ReadLine();

                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    lines.Add(line);
                    if (lines.Count > maxLines)
                        lines.RemoveAt(0);
                }
            }
        }

        return lines;
    }

    static string GetStatsFilePath()
    {
        var dir = ResolveResultsDir();
        return dir == null ? null : Path.Combine(dir, FileName);
    }

    static string ResolveResultsDir()
    {
        if (!string.IsNullOrWhiteSpace(_resultsDir))
            return _resultsDir;

        var env = Environment.GetEnvironmentVariable("FOREST_RESULTS_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            _resultsDir = env.Trim();
            return _resultsDir;
        }

        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "-forestResultsDir" || args[i] == "--forest-results-dir")
                && i + 1 < args.Length
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                _resultsDir = args[i + 1].Trim();
                return _resultsDir;
            }
        }

        return null;
    }

    static bool IsJackWaterComplete(AgentGoToHouseDiscrete jack)
    {
        // Раньше success = прошли GoalWater1..3 (кубы), без обязательного сбора воды у озера.
        // Из‑за этого метрики/boost думали, что «вода ок», а агент воду не качал.
        return jack != null && jack.EpisodeWaterCollected > 0;
    }

    static bool IsLilyWaterComplete(LilyScript lily)
    {
        return lily != null && lily.EpisodeWaterCollected > 0;
    }
}
