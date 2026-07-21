using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.MLAgents;
using UnityEngine;

/// <summary>
/// Success rate по простым задачам (wood / sheep / fire / …).
/// Пишет JSONL в results/&lt;run_id&gt;/ — все headless worker'ы и стрим читают один файл.
/// При обучении шлёт TaskSuccess/… в TensorBoard (0..1).
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

    static readonly Dictionary<Metric, List<float>> RateHistory = new();
    static readonly Dictionary<Metric, List<float>> RawSamples = new();
    static readonly Dictionary<Metric, float> LastRate = new();
    static readonly Dictionary<EnvTrainingTask, List<float>> TaskRawSamples = new();
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

        AppendToDisk(metric.Value, success);
        PushLocalSample(metric.Value, success ? 1f : 0f);
        PushTaskSample(task, success ? 1f : 0f);
        ReportToTensorBoard(task, metric.Value);
    }

    public static float GetTaskLastRate(EnvTrainingTask task)
    {
        return TaskLastRate.TryGetValue(task, out float v) ? v : 0f;
    }

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

    public static float GetLastRate(Metric metric)
    {
        MaybeRefreshFromDisk();
        return LastRate.TryGetValue(metric, out float v) ? v : 0f;
    }

    public static int GetSampleCount(Metric metric)
    {
        MaybeRefreshFromDisk();
        return RawSamples.TryGetValue(metric, out var list) ? list.Count : 0;
    }

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

        _nextDiskReadTime = Time.unscaledTime + 2f;
        RefreshFromDisk(force: true);
    }

    static void MaybeRefreshFromDisk()
    {
        RefreshFromDisk(force: false);
    }

    static void AppendToDisk(Metric metric, bool success)
    {
        var path = GetStatsFilePath();
        if (path == null)
            return;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var line = $"{{\"m\":\"{metric}\",\"s\":{(success ? 1 : 0)},\"t\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}\n";
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
        if (raw.Count > 4096)
            raw.RemoveAt(0);

        RebuildMetricHistory(metric, raw);
    }

    static void PushTaskSample(EnvTrainingTask task, float sample)
    {
        if (!TaskRawSamples.TryGetValue(task, out var raw))
            raw = TaskRawSamples[task] = new List<float>();

        raw.Add(sample);
        if (raw.Count > 4096)
            raw.RemoveAt(0);

        TaskLastRate[task] = ComputeRollingRate(raw, raw.Count - 1, RollingWindow);
    }

    static string TaskTensorBoardKey(EnvTrainingTask task)
    {
        switch (task)
        {
            case EnvTrainingTask.JackWood: return "Jack/Wood";
            case EnvTrainingTask.JackFood: return "Jack/Food";
            case EnvTrainingTask.JackWater: return "Jack/Water";
            case EnvTrainingTask.JackZombie: return "Jack/Zombie";
            case EnvTrainingTask.LilyFood: return "Lily/Food";
            case EnvTrainingTask.LilyWater: return "Lily/Water";
            case EnvTrainingTask.LilyHeat: return "Lily/Heat";
            case EnvTrainingTask.LilyFlower: return "Lily/Flower";
            case EnvTrainingTask.GeorgeFood: return "George/Food";
            case EnvTrainingTask.GeorgeWater: return "George/Water";
            case EnvTrainingTask.GeorgeHeat: return "George/Heat";
            default: return null;
        }
    }

    static void ReportToTensorBoard(EnvTrainingTask task, Metric metric)
    {
        if (!Academy.IsInitialized || !Academy.Instance.IsCommunicatorOn)
            return;

        var stats = Academy.Instance.StatsRecorder;
        float taskRate = GetTaskLastRate(task);
        string taskKey = TaskTensorBoardKey(task);
        if (!string.IsNullOrEmpty(taskKey))
            stats.Add($"TaskSuccess/{taskKey}", taskRate, StatAggregationMethod.Average);

        float metricRate = GetLastRate(metric);
        stats.Add($"TaskSuccess/Metric/{metric}", metricRate, StatAggregationMethod.Average);
    }

    static void RefreshFromDisk(bool force)
    {
        var path = GetStatsFilePath();
        if (path == null || !File.Exists(path))
            return;

        try
        {
            long size = new FileInfo(path).Length;
            if (!force && size == _lastFileSize && RateHistory[Metric.Wood].Count > 0)
                return;

            _lastFileSize = size;
            var tail = ReadTailLines(path, 4096);
            foreach (Metric m in Enum.GetValues(typeof(Metric)))
            {
                var samples = new List<float>();
                for (int i = 0; i < tail.Count; i++)
                    TryParseSample(tail[i], m, samples);
                RawSamples[m] = samples;
                RebuildMetricHistory(m, samples);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TaskSuccess] read fail: {ex.Message}");
        }
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

    static void TryParseSample(string line, Metric metric, List<float> dst)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        int mIdx = line.IndexOf("\"m\":\"", StringComparison.Ordinal);
        if (mIdx < 0)
            return;

        mIdx += 5;
        int mEnd = line.IndexOf('"', mIdx);
        if (mEnd < 0 || !Enum.TryParse(line.Substring(mIdx, mEnd - mIdx), out Metric parsed) || parsed != metric)
            return;

        int sIdx = line.IndexOf("\"s\":", StringComparison.Ordinal);
        if (sIdx < 0)
            return;

        sIdx += 4;
        int sEnd = line.IndexOfAny(new[] { ',', '}' }, sIdx);
        if (sEnd < 0 || !int.TryParse(line.Substring(sIdx, sEnd - sIdx), out int s))
            return;

        dst.Add(s == 1 ? 1f : 0f);
    }

    static List<string> ReadTailLines(string path, int maxLines)
    {
        var lines = new List<string>();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs, Encoding.UTF8))
        {
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                lines.Add(line);
                if (lines.Count > maxLines)
                    lines.RemoveAt(0);
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
        var path = WaterGoalPath.Get(jack.transform);
        return path != null && path.HasCompletedPath(jack.transform);
    }

    static bool IsLilyWaterComplete(LilyScript lily)
    {
        var path = WaterGoalPath.Get(lily.transform);
        return path != null && path.HasCompletedPath(lily.transform);
    }
}
