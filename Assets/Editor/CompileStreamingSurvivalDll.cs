using System;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

/// <summary>Batchmode: compile scripts only, then copy Bee Assembly-CSharp into Managed.</summary>
public static class CompileStreamingSurvivalDll
{
    const string DefaultBuildName = "stream_forest_survival_2_12_07_2026";

    public static void CompileAndCopyFromCommandLine()
    {
        string buildName = GetArg("-buildOutputName") ?? DefaultBuildName;
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string managed = Path.Combine(
            projectRoot, "build_versions", buildName + "_Data", "Managed", "Assembly-CSharp.dll");
        string beeP = Path.Combine(
            projectRoot, "Library", "Bee", "artifacts", "2400b0aP.dag", "post-processed", "Assembly-CSharp.dll");
        string beePAlt = Path.Combine(
            projectRoot, "Library", "Bee", "artifacts", "2400b0aP.dag", "Assembly-CSharp.dll");
        string beeE = Path.Combine(
            projectRoot, "Library", "Bee", "artifacts", "2400b0aE.dag", "post-processed", "Assembly-CSharp.dll");
        string beeEAlt = Path.Combine(
            projectRoot, "Library", "Bee", "artifacts", "2400b0aE.dag", "Assembly-CSharp.dll");
        string scriptAsm = Path.Combine(
            projectRoot, "Library", "ScriptAssemblies", "Assembly-CSharp.dll");
        string[] candidates = { beeE, beeEAlt, beeP, beePAlt, scriptAsm };

        // Prefer ScriptAssemblies mtime — Bee artifacts are often stale and would
        // cause an immediate false-positive copy of the previous DLL.
        DateTime before = File.Exists(scriptAsm)
            ? File.GetLastWriteTime(scriptAsm)
            : DateTime.MinValue;
        foreach (var cand in candidates)
        {
            if (!File.Exists(cand)) continue;
            DateTime t = File.GetLastWriteTime(cand);
            if (t > before) before = t;
        }
        Debug.Log($"[CompileSS] RequestScriptCompilation before={before:O}");

        // Persist across domain reload (CleanBuildCache triggers one).
        SessionState.SetString("CompileSS.managed", managed);
        SessionState.SetString("CompileSS.before", before.ToString("O"));
        SessionState.EraseFloat("CompileSS.deadline");
        SessionState.SetBool("CompileSS.pending", true);

        CompilationPipeline.compilationFinished += OnFinished;
        CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
        EditorApplication.update += PollCopy;
    }

    [InitializeOnLoadMethod]
    static void ResumeAfterDomainReload()
    {
        if (!SessionState.GetBool("CompileSS.pending", false)) return;
        Debug.Log("[CompileSS] resume after domain reload");
        EditorApplication.update += PollCopy;
    }

    static void OnFinished(object _)
    {
        Debug.Log("[CompileSS] compilationFinished");
    }

    static void PollCopy()
    {
        if (!SessionState.GetBool("CompileSS.pending", false))
        {
            EditorApplication.update -= PollCopy;
            return;
        }
        if (EditorApplication.isCompiling) return;

        string managed = SessionState.GetString("CompileSS.managed", "");
        if (string.IsNullOrEmpty(managed))
        {
            SessionState.SetBool("CompileSS.pending", false);
            EditorApplication.update -= PollCopy;
            EditorApplication.Exit(1);
            return;
        }

        DateTime before = DateTime.MinValue;
        DateTime.TryParse(
            SessionState.GetString("CompileSS.before", ""),
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out before);

        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string beeP = Path.Combine(
            projectRoot, "Library", "Bee", "artifacts", "2400b0aP.dag", "post-processed", "Assembly-CSharp.dll");
        string beePAlt = Path.Combine(
            projectRoot, "Library", "Bee", "artifacts", "2400b0aP.dag", "Assembly-CSharp.dll");
        string beeE = Path.Combine(
            projectRoot, "Library", "Bee", "artifacts", "2400b0aE.dag", "post-processed", "Assembly-CSharp.dll");
        string beeEAlt = Path.Combine(
            projectRoot, "Library", "Bee", "artifacts", "2400b0aE.dag", "Assembly-CSharp.dll");
        string scriptAsm = Path.Combine(
            projectRoot, "Library", "ScriptAssemblies", "Assembly-CSharp.dll");
        string[] candidates = { beeE, beeEAlt, beeP, beePAlt, scriptAsm };

        string src = null;
        DateTime newest = before;
        foreach (var cand in candidates)
        {
            if (!File.Exists(cand)) continue;
            DateTime t = File.GetLastWriteTime(cand);
            if (t > before.AddSeconds(2) && t >= newest)
            {
                newest = t;
                src = cand;
            }
        }
        if (src != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(managed));
            File.Copy(src, managed, true);
            Debug.Log($"[CompileSS] OK copied {src} -> {managed} mtime={newest:O}");
            SessionState.SetBool("CompileSS.pending", false);
            EditorApplication.update -= PollCopy;
            EditorApplication.Exit(0);
            return;
        }

        // Give Bee a few seconds after isCompiling=false, then fail.
        float started = (float)EditorApplication.timeSinceStartup;
        // deadline stored once
        float deadline = SessionState.GetFloat("CompileSS.deadline", 0f);
        if (deadline <= 0f)
        {
            deadline = started + 120f;
            SessionState.SetFloat("CompileSS.deadline", deadline);
        }
        if (started > deadline)
        {
            Debug.LogError("[CompileSS] TIMEOUT waiting for Assembly-CSharp.dll");
            SessionState.SetBool("CompileSS.pending", false);
            EditorApplication.update -= PollCopy;
            EditorApplication.Exit(1);
        }
    }

    static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
