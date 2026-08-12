using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Runs a command plan in Streaming Survival and records trajectory via existing recorder.
/// Invoked via scenario runner / UDP — not ML-Agents.
/// </summary>
public class StreamingSurvivalCommandRuntimeTester : MonoBehaviour
{
    public static StreamingSurvivalCommandRuntimeTester Instance { get; private set; }

    void Awake()
    {
        Instance = this;
    }

    public IEnumerator RunPlanCoroutine(
        string scenarioId,
        string username,
        List<(string action, int count)> plan,
        float maxSeconds,
        string outDir)
    {
        var ctrl = StreamingSurvivalController.Instance;
        if (ctrl == null || plan == null || plan.Count == 0)
            yield break;

        username = string.IsNullOrEmpty(username) ? "cmd_tester" : username;
        var player = ctrl.EnsurePlayer(username);
        if (player == null)
            yield break;

        string queue = string.Join(";", plan.ConvertAll(s => $"{s.action}:{Mathf.Max(1, s.count)}"));
        player.SetAction(plan[0].action, null, Mathf.Max(1, plan[0].count), queue);

        var rec = StreamingSurvivalTrajectoryRecorder.Instance;
        rec?.BeginScenario(scenarioId, username);
        float t0 = Time.time;
        while (Time.time - t0 < maxSeconds)
        {
            if (player == null) break;
            yield return null;
            if (IsPlanIdleDone(player) && Time.time - t0 > 1.5f)
                break;
        }
        rec?.EndScenario();

        try
        {
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                string path = Path.Combine(outDir, scenarioId + "_plan_runtime.json");
                File.WriteAllText(path,
                    $"{{\"scenario_id\":\"{scenarioId}\",\"username\":\"{username}\",\"queue\":\"{queue}\"}}");
            }
        }
        catch { /* ignore IO */ }
    }

    static bool IsPlanIdleDone(StreamingSurvivalPlayer p)
    {
        if (p == null) return true;
        return p.Action == "idle" && (p.ActionName != null && p.ActionName.IndexOf("Ждёт") >= 0);
    }
}
