using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Linux Player-билд для стрима (batchmode: -executeMethod BuildStreamLinux.BuildFromCommandLine).</summary>
public static class BuildStreamLinux
{
    const string DefaultOutputName = "stream_forest_survival";

    public static void BuildFromCommandLine()
    {
        string outputName = GetArg("-buildOutputName") ?? DefaultOutputName;
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string buildDir = Path.Combine(projectRoot, "build_versions");
        Directory.CreateDirectory(buildDir);

        // Unity добавит .x86_64 и папку _Data
        string locationPath = Path.Combine(buildDir, outputName);

        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            Debug.LogError("[BuildStreamLinux] No enabled scenes in Build Settings.");
            EditorApplication.Exit(1);
            return;
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = locationPath,
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Player,
            options = BuildOptions.None,
        };

        Debug.Log($"[BuildStreamLinux] Building Linux Player -> {locationPath}");
        BuildReport report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"[BuildStreamLinux] FAILED: {report.summary.result}");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"[BuildStreamLinux] OK: {locationPath}.x86_64");
        EditorApplication.Exit(0);
    }

    static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }

        return null;
    }
}
