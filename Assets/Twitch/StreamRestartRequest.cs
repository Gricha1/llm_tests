using System;
using System.IO;
using UnityEngine;

/// <summary>
/// #restart_stream → файл-флаг; bash run_stream_supervised перезапускает только стрим
/// (stream_onnx_infer + Unity -forestStreamOnly), train/mlagents не трогает.
/// </summary>
public static class StreamRestartRequest
{
    public const string FlagFileName = ".stream_restart_request";
    const float CooldownSeconds = 45f;

    static float _lastRequestUnscaledTime = -999f;

    public static string ResolveFlagPath()
    {
        // results/run_XX → корень репо
        var results = TryResolveResultsDir();
        if (!string.IsNullOrEmpty(results))
        {
            try
            {
                var root = Path.GetFullPath(Path.Combine(results, "..", ".."));
                return Path.Combine(root, FlagFileName);
            }
            catch
            {
                // fall through
            }
        }

        // build_versions/xxx_Data → корень репо
        try
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", FlagFileName));
        }
        catch
        {
            return Path.Combine(Application.persistentDataPath, FlagFileName);
        }
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

    public static bool TryRequest(string byDisplayName, out string message)
    {
        if (!TrainingEnvSpace.IsStreamOnlyMode)
        {
            message = "только на стрим-процессе (-forestStreamOnly)";
            return false;
        }

        float now = Time.unscaledTime;
        float left = CooldownSeconds - (now - _lastRequestUnscaledTime);
        if (left > 0f)
        {
            message = $"подожди ещё {left:0}с";
            return false;
        }

        string path = ResolveFlagPath();
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(
                path,
                $"utc={DateTime.UtcNow:o}\nby={byDisplayName}\npid={System.Diagnostics.Process.GetCurrentProcess().Id}\n");
            _lastRequestUnscaledTime = now;
            message = path;
            return true;
        }
        catch (Exception e)
        {
            message = e.Message;
            return false;
        }
    }
}
