using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Windows Player для локального теста presentation (без Linux/стрима).</summary>
public static class BuildPresentationWindows
{
    const string DefaultOutputName = "presentation_manual_win";

    public static void BuildFromCommandLine()
    {
        string outputName = GetArg("-buildOutputName") ?? DefaultOutputName;
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string buildDir = Path.Combine(projectRoot, "build_versions");
        Directory.CreateDirectory(buildDir);

        string locationPath = Path.Combine(buildDir, outputName, outputName + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(locationPath)!);

        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            Debug.LogError("[BuildPresentationWindows] No enabled scenes in Build Settings.");
            EditorApplication.Exit(1);
            return;
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = locationPath,
            target = BuildTarget.StandaloneWindows64,
            subtarget = (int)StandaloneBuildSubtarget.Player,
            options = BuildOptions.None,
        };

        Debug.Log($"[BuildPresentationWindows] Building Windows Player -> {locationPath}");
        BuildReport report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"[BuildPresentationWindows] FAILED: {report.summary.result}");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"[BuildPresentationWindows] OK: {locationPath}");
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
