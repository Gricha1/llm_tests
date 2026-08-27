using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Журнал стрим-фолловеров по эпизодам: состояние / мёртв / координаты / телепорты.
/// Хранит последние <see cref="KeepEpisodes"/> эпизодов в results/follower_episodes/.
/// </summary>
public static class FollowerEpisodeJournal
{
    public const int KeepEpisodes = 10;
    const string FolderName = "follower_episodes";

    static readonly object IoLock = new object();
    static int _episode = -1;
    static string _dir;
    static string _currentPath;

    public static void OnEpisodeBegin(int episodeIndex)
    {
        if (episodeIndex < 1)
            episodeIndex = 1;
        lock (IoLock)
        {
            _episode = episodeIndex;
            EnsureDir();
            _currentPath = Path.Combine(_dir, $"ep_{episodeIndex:D5}.jsonl");
            try
            {
                File.AppendAllText(
                    _currentPath,
                    $"{{\"ts\":\"{Stamp()}\",\"kind\":\"episode_begin\",\"ep\":{episodeIndex}}}\n",
                    Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FollowerEpisodeJournal] begin failed: {e.Message}");
            }
            PruneOldEpisodes(episodeIndex);
        }
    }

    public static void OnEpisodeEnd(int episodeIndex)
    {
        lock (IoLock)
        {
            if (string.IsNullOrEmpty(_currentPath))
                return;
            try
            {
                File.AppendAllText(
                    _currentPath,
                    $"{{\"ts\":\"{Stamp()}\",\"kind\":\"episode_end\",\"ep\":{Mathf.Max(1, episodeIndex)}}}\n",
                    Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>Периодический снимок всех фолловеров (из SNAP tick).</summary>
    public static void SnapshotAll(int episodeIndex)
    {
        var ctrl = StreamingSurvivalController.Instance;
        if (ctrl == null)
            return;
        EnsureEpisode(episodeIndex);
        var lines = new List<string>(16);
        ctrl.ForEachPlayer(p =>
        {
            if (p == null) return;
            lines.Add(FormatSample("tick", p, null));
        });
        if (lines.Count == 0)
            return;
        AppendLines(lines);
    }

    public static void NoteEvent(StreamingSurvivalPlayer p, string kind, string detail = null)
    {
        if (p == null || string.IsNullOrEmpty(kind))
            return;
        EnsureEpisode(Mathf.Max(1, PresentationWorldSnapshotLogger.CurrentEpisodeIndex));
        AppendLines(new List<string> { FormatSample(kind, p, detail) });
    }

    public static int CurrentEpisode => _episode;

    static void EnsureEpisode(int episodeIndex)
    {
        if (episodeIndex < 1)
            episodeIndex = 1;
        if (_episode == episodeIndex && !string.IsNullOrEmpty(_currentPath))
            return;
        OnEpisodeBegin(episodeIndex);
    }

    static string FormatSample(string kind, StreamingSurvivalPlayer p, string detail)
    {
        Vector3 pos = p.transform.position;
        var sb = new StringBuilder(256);
        sb.Append('{');
        sb.Append("\"ts\":\"").Append(Stamp()).Append('"');
        sb.Append(",\"kind\":\"").Append(Escape(kind)).Append('"');
        sb.Append(",\"ep\":").Append(Mathf.Max(1, _episode));
        sb.Append(",\"user\":\"").Append(Escape(p.Username ?? "")).Append('"');
        sb.Append(",\"skin\":\"").Append(p.IsWolfSkin ? "wolf" : "human").Append('"');
        sb.Append(",\"action\":\"").Append(Escape(p.Action ?? "")).Append('"');
        sb.Append(",\"action_name\":\"").Append(Escape(p.ActionName ?? "")).Append('"');
        sb.Append(",\"x\":").Append(pos.x.ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append(",\"z\":").Append(pos.z.ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append(",\"y\":").Append(pos.y.ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append(",\"hp\":").Append(p.Hp);
        sb.Append(",\"max_hp\":").Append(p.MaxHp);
        sb.Append(",\"zombie_hits\":").Append(p.ZombieHitsTaken);
        sb.Append(",\"dead\":").Append(p.IsDeadToZombies ? "true" : "false");
        sb.Append(",\"wolf_cd\":").Append(p.WolfCdLeftPublic.ToString("0.##", CultureInfo.InvariantCulture));
        sb.Append(",\"wolf_hits_ep\":").Append(p.WolfHitsThisEpisode);
        sb.Append(",\"teleport\":\"").Append(Escape(p.LastTeleportReason ?? "")).Append('"');
        if (!string.IsNullOrEmpty(detail))
            sb.Append(",\"detail\":\"").Append(Escape(detail)).Append('"');
        sb.Append('}');
        return sb.ToString();
    }

    static void AppendLines(List<string> lines)
    {
        if (lines == null || lines.Count == 0)
            return;
        lock (IoLock)
        {
            if (string.IsNullOrEmpty(_currentPath))
                return;
            try
            {
                var sb = new StringBuilder(lines.Count * 200);
                for (int i = 0; i < lines.Count; i++)
                    sb.Append(lines[i]).Append('\n');
                File.AppendAllText(_currentPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FollowerEpisodeJournal] write failed: {e.Message}");
            }
        }
    }

    static void EnsureDir()
    {
        if (!string.IsNullOrEmpty(_dir) && Directory.Exists(_dir))
            return;
        string results = ResolveResultsDir();
        if (string.IsNullOrEmpty(results))
        {
            try
            {
                results = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "results"));
            }
            catch
            {
                results = Application.persistentDataPath;
            }
        }
        _dir = Path.Combine(results, FolderName);
        Directory.CreateDirectory(_dir);
        Debug.Log($"[FollowerEpisodeJournal] JOURNAL_DIR={_dir}");
    }

    static string ResolveResultsDir()
    {
        string env = Environment.GetEnvironmentVariable("FOREST_RESULTS_DIR");
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

    static void PruneOldEpisodes(int currentEpisode)
    {
        try
        {
            EnsureDir();
            var files = Directory.GetFiles(_dir, "ep_*.jsonl");
            if (files == null || files.Length <= KeepEpisodes)
                return;
            var list = new List<(int ep, string path)>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]); // ep_00012
                if (name == null || name.Length < 4) continue;
                if (!int.TryParse(name.Substring(3), out int ep))
                    continue;
                list.Add((ep, files[i]));
            }
            list.Sort((a, b) => a.ep.CompareTo(b.ep));
            int drop = list.Count - KeepEpisodes;
            for (int i = 0; i < drop; i++)
            {
                try { File.Delete(list[i].path); }
                catch { /* ignore */ }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FollowerEpisodeJournal] prune failed: {e.Message}");
        }
    }

    static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

    static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
