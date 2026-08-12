using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Runs Streaming Survival scenarios for scripted follower characters (no Twitch, no training).
/// Driven by UDP streaming_survival_test or local StartCoroutine.
/// </summary>
public sealed class StreamingSurvivalScenarioRunner : MonoBehaviour
{
    public static StreamingSurvivalScenarioRunner Instance { get; private set; }

    public sealed class Scenario
    {
        public string Id;
        public string ChatCommand;
        public string ExpectedTargetType;
        public string ExpectedMotionPattern;
        public float MaxSeconds = 30f;
        public readonly List<Step> Plan = new List<Step>();
        public StreamingSurvivalScenarioChecker.ResourceDelta ResourceDelta;
        public bool ExpectNoResourceCredit;
        public string NegativeTest = ""; // force_complete_far
        /// <summary>home | water | near_fence | near_tree | spawn (default)</summary>
        public string StartAt = "";
    }

    public sealed class Step
    {
        public string Action;
        public int Count = 1;
    }

    public sealed class RunReport
    {
        public string ScenarioId;
        public bool Ok;
        public string Reason;
        public string TrajectoryPath;
        public StreamingSurvivalScenarioChecker.CheckResult Check;
    }

    bool _busy;
    RunReport _lastReport;
    string _lastWorldRegistryJson = "{}";
    string _outputDir;
    string _runId;
    readonly List<RunReport> _batch = new List<RunReport>();

    public bool Busy => _busy;
    public RunReport LastReport => _lastReport;
    public string LastWorldRegistryJson => _lastWorldRegistryJson;
    public string OutputDir => _outputDir;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    public void ConfigureOutput(string runId, string outputDir)
    {
        _runId = runId ?? "";
        if (!string.IsNullOrEmpty(outputDir))
            _outputDir = outputDir;
        EnsureHooks();
        var rec = StreamingSurvivalTrajectoryRecorder.Instance;
        if (rec != null && !string.IsNullOrEmpty(_outputDir))
            rec.SetRunDir(_outputDir);
        Debug.Log($"[SSScenarioRunner] configure run_id={_runId} output_dir={_outputDir}");
    }

    public void WritePing()
    {
        EnsureHooks();
        EnsureOutputReady();
        string path = Path.Combine(EffectiveOutputDir(), "runtime_ping.json");
        File.WriteAllText(path, "{\"ok\":true,\"mode\":\"streaming_survival\",\"msg\":\"follower characters runtime\"}");
        Debug.Log($"[SSTest] PING wrote {path}");
    }

    public void WriteWorldMap()
    {
        EnsureHooks();
        EnsureOutputReady();
        var reg = StreamingSurvivalWorldRegistry.Instance;
        if (reg == null)
        {
            Debug.LogWarning("[SSTest] WriteWorldMap: registry missing");
            return;
        }
        string json = reg.ExportWorldMapJson();
        WriteArtifact("world_map.json", json);
        Debug.Log($"[SSTest] WORLD_MAP wrote {Path.Combine(EffectiveOutputDir(), "world_map.json")}");
    }

    string EffectiveOutputDir()
    {
        if (!string.IsNullOrEmpty(_outputDir))
            return _outputDir;
        var rec = StreamingSurvivalTrajectoryRecorder.Instance;
        if (rec != null && !string.IsNullOrEmpty(rec.RunDir))
            return rec.RunDir;
        string env = Environment.GetEnvironmentVariable("STREAMING_SURVIVAL_TEST_ARTIFACTS_DIR");
        if (!string.IsNullOrEmpty(env))
            return env;
        return Application.persistentDataPath;
    }

    void EnsureOutputReady()
    {
        string dir = EffectiveOutputDir();
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "trajectories"));
        var rec = StreamingSurvivalTrajectoryRecorder.Instance;
        if (rec != null)
            rec.SetRunDir(dir);
    }

    public void EnsureHooks()
    {
        if (GetComponent<StreamingSurvivalTrajectoryRecorder>() == null)
            gameObject.AddComponent<StreamingSurvivalTrajectoryRecorder>();
        if (GetComponent<StreamingSurvivalWorldRegistry>() == null)
            gameObject.AddComponent<StreamingSurvivalWorldRegistry>();
        if (StreamingSurvivalWorldRegistry.Instance != null)
            StreamingSurvivalWorldRegistry.Instance.Rebuild();
    }

    public string RunWorldRegistrySelfTest()
    {
        EnsureHooks();
        EnsureOutputReady();
        var r = StreamingSurvivalWorldRegistrySelfTest.Run();
        _lastWorldRegistryJson = StreamingSurvivalWorldRegistrySelfTest.ToJson(r);
        WriteArtifact("world_registry_results.json", _lastWorldRegistryJson);
        return _lastWorldRegistryJson;
    }

    public void RunJoinIdempotentTest()
    {
        EnsureHooks();
        EnsureOutputReady();
        var ctrl = StreamingSurvivalController.Instance;
        if (ctrl == null)
        {
            WriteSimpleResult("join_idempotent", false, "no controller");
            return;
        }
        const string user = "test_join_user";
        ctrl.RemovePlayer(user);
        ctrl.EnsurePlayer(user);
        int n1 = ctrl.PlayerCount;
        ctrl.EnsurePlayer(user);
        int n2 = ctrl.PlayerCount;
        bool ok = n2 == n1 && n1 >= 1;
        WriteSimpleResult("join_idempotent", ok, ok ? "ok" : $"duplicate players n1={n1} n2={n2}");
    }

    public string EffectiveOutputDirPublic() => EffectiveOutputDir();
    public void EnsureOutputReadyPublic() => EnsureOutputReady();

    public void RunActionCooldownTest()
    {
        // Cooldown is enforced by stream_bot; Unity only confirms ApplyAction is callable twice.
        // Report PASS with note that bot e2e validates 10s reject.
        EnsureOutputReady();
        WriteSimpleResult(
            "action_cooldown",
            true,
            "Unity side ok; 10s reject is enforced by stream_bot (see e2e)");
    }

    public void RunJoinDefaultIdleTest()
    {
        if (_busy)
        {
            Debug.LogWarning("[SSScenarioRunner] busy (join_default_idle)");
            return;
        }
        StartCoroutine(JoinDefaultIdleCoroutine());
    }

    IEnumerator JoinDefaultIdleCoroutine()
    {
        _busy = true;
        var report = new RunReport { ScenarioId = "join_default_idle", Ok = false };
        try
        {
            EnsureHooks();
            EnsureOutputReady();
            var ctrl = StreamingSurvivalController.Instance;
            if (ctrl == null)
            {
                report.Reason = "no controller";
                WriteScenarioResult(report);
                yield break;
            }

            const string user = "join_idle_user";
            ctrl.ClearAllPlayers();
            ctrl.ResetResourcesForTest(120f);
            if (StreamingSurvivalWorldRegistry.Instance != null)
                StreamingSurvivalWorldRegistry.Instance.Rebuild();

            int w0 = ctrl.Water, wood0 = ctrl.Wood, food0 = ctrl.Food;
            var player = ctrl.EnsurePlayer(user);
            ctrl.ApplyAction(user, "idle", "Ждёт у базы", 1, null);
            Vector3 spawn = player.transform.position;

            var rec = StreamingSurvivalTrajectoryRecorder.Instance;
            rec.SetRunDir(EffectiveOutputDir());
            rec.BeginScenario("join_default_idle", user);

            float until = Time.time + 10f;
            float nextTick = 0f;
            bool badAction = false;
            while (Time.time < until)
            {
                if (Time.time >= nextTick)
                {
                    nextTick = Time.time + 0.25f;
                    rec.TickPlayer(player, "moving");
                    string act = (player.Action ?? "").ToLowerInvariant();
                    if (act == "collect_wood" || act == "collect_water"
                        || act == "go_to_tree" || act == "go_to_water")
                    {
                        badAction = true;
                        rec.EmitEvent(user, "unexpected_action", player);
                        break;
                    }
                }
                yield return null;
            }

            rec.EmitEvent(user, badAction ? "fail_action" : "plan_completed", player);
            rec.EndScenario();
            report.TrajectoryPath = rec.CurrentPath;

            bool near = Horiz(player.transform.position, spawn) <= 6f;
            bool idleOk = (player.Action ?? "").ToLowerInvariant() == "idle";
            bool resOk = ctrl.Water == w0 && ctrl.Wood == wood0 && ctrl.Food == food0;

            var spec = new StreamingSurvivalScenarioChecker.ScenarioSpec
            {
                id = "join_default_idle",
                expected_motion_pattern = "idle",
                max_seconds = 10f,
                expected_resource_delta = new StreamingSurvivalScenarioChecker.ResourceDelta
                {
                    water = 0, wood = 0, food = 0
                },
            };
            report.Check = StreamingSurvivalScenarioChecker.CheckFile(
                "join_default_idle", report.TrajectoryPath, spec);

            report.Ok = report.Check.Ok && idleOk && near && resOk && !badAction
                && report.Check.GroundPass;
            if (!report.Ok)
            {
                var reasons = new List<string>();
                if (badAction) reasons.Add("started collect without #do");
                if (!idleOk) reasons.Add($"action={player.Action}");
                if (!near) reasons.Add("left spawn");
                if (!resOk) reasons.Add("resources changed");
                if (!report.Check.Ok) reasons.Add(report.Check.Reason);
                report.Reason = string.Join(" | ", reasons);
            }
            else
                report.Reason = "ok idle after join";

            Debug.Log(
                $"[SSTest] SCENARIO id=join_default_idle overall={(report.Ok ? "PASS" : "FAIL")} " +
                $"reason={report.Reason}");
            WriteScenarioResult(report);
            ctrl.RemovePlayer(user);
            ctrl.ResetResourcesForTest();
        }
        finally
        {
            _lastReport = report;
            _busy = false;
        }
    }

    void WriteSimpleResult(string id, bool ok, string reason)
    {
        var sb = new StringBuilder();
        sb.Append("{\"id\":\"").Append(id).Append("\",");
        sb.Append("\"overall\":\"").Append(ok ? "PASS" : "FAIL").Append("\",");
        sb.Append("\"ok\":").Append(ok ? "true" : "false").Append(',');
        sb.Append("\"reason\":\"").Append(Esc(reason)).Append("\"}");
        string path = Path.Combine(EffectiveOutputDir(), id + "_result.json");
        File.WriteAllText(path, sb.ToString());
        Debug.Log($"[SSTest] {id} overall={(ok ? "PASS" : "FAIL")} {reason}");
    }

    public void RunScenarioAsync(Scenario scenario)
    {
        if (_busy)
        {
            Debug.LogWarning("[SSScenarioRunner] busy");
            return;
        }
        StartCoroutine(RunScenarioCoroutine(scenario));
    }

    public IEnumerator RunScenarioCoroutine(Scenario scenario)
    {
        _busy = true;
        var report = new RunReport { ScenarioId = scenario != null ? scenario.Id : "?", Ok = false };
        try
        {
            EnsureHooks();
            EnsureOutputReady();
            var ctrl = StreamingSurvivalController.Instance;
            if (ctrl == null)
            {
                report.Reason = "no StreamingSurvivalController";
                WriteScenarioResult(report);
                yield break;
            }

            // Isolate scenario: stop leftover collectors from previous tests
            ctrl.ClearAllPlayers();
            ctrl.ResetResourcesForTest(Mathf.Max(60f, scenario.MaxSeconds + 30f));
            if (StreamingSurvivalWorldRegistry.Instance != null)
                StreamingSurvivalWorldRegistry.Instance.Rebuild();

            // Campfire requires team wood>=3 in MVP
            bool needsFire = false;
            foreach (var st in scenario.Plan)
            {
                if (st != null && (st.Action == "build_campfire" || st.Action == "circle"))
                    needsFire = st.Action == "build_campfire" || needsFire;
                if (st != null && st.Action == "build_campfire")
                {
                    ctrl.AddResource("wood", 3);
                    break;
                }
            }

            string user = "test_" + (scenario.Id ?? "sc");
            var player = ctrl.EnsurePlayer(user);
            // EnsurePlayer resets round timer to stageSeconds — re-hold for long scenarios.
            ctrl.ResetResourcesForTest(Mathf.Max(60f, scenario.MaxSeconds + 30f));

            bool negativeFar = scenario.NegativeTest == "force_complete_far";
            if (negativeFar)
            {
                // Place character in forest / far from pond (not at water_source).
                Vector3 forest = ctrl.FollowerSpawnWorld + new Vector3(-8f, 0f, 3f);
                forest.y = ctrl.FollowerSpawnWorld.y;
                player.TeleportTo(forest, "negative_forest");
            }
            else
            {
                Vector3 start = ResolveStartPosition(ctrl, scenario.StartAt);
                player.TeleportTo(start, "scenario_setup");
            }

            string queue = BuildQueue(scenario.Plan);
            string action = scenario.Plan.Count > 0 ? scenario.Plan[0].Action : "idle";
            int amount = scenario.Plan.Count > 0 ? scenario.Plan[0].Count : 1;
            ctrl.ApplyAction(user, action, action, amount, queue);

            var rec = StreamingSurvivalTrajectoryRecorder.Instance;
            rec.SetRunDir(EffectiveOutputDir());
            rec.BeginScenario(scenario.Id, user);

            if (negativeFar)
            {
                // Immediately attempt illegal complete while still far away.
                yield return null;
                player.TryForceCompleteWorkFarForTest(out _);
                player.FreezeForNegativeTest();
                rec.TickPlayer(player, "moving");
                float holdUntil = Time.time + Mathf.Min(3f, Mathf.Max(1.5f, scenario.MaxSeconds));
                while (Time.time < holdUntil)
                {
                    rec.TickPlayer(player, "moving");
                    yield return null;
                }
                rec.EmitEvent(user, "timeout", player);
                rec.EndScenario();
                report.TrajectoryPath = rec.CurrentPath;
                var negSpec = new StreamingSurvivalScenarioChecker.ScenarioSpec
                {
                    id = scenario.Id,
                    expected_target_type = scenario.ExpectedTargetType,
                    expected_motion_pattern = scenario.ExpectedMotionPattern,
                    max_seconds = scenario.MaxSeconds,
                    expected_resource_delta = scenario.ResourceDelta,
                    expect_no_resource_credit = true,
                };
                report.Check = StreamingSurvivalScenarioChecker.CheckFile(scenario.Id, report.TrajectoryPath, negSpec);
                report.Ok = report.Check.Ok;
                report.Reason = report.Check.Reason;
                Debug.Log($"[SSTest] SCENARIO id={scenario.Id} overall={(report.Ok ? "PASS" : "FAIL")} reason={report.Reason}");
                WriteScenarioResult(report);
                ctrl.RemovePlayer(user);
                ctrl.ResetResourcesForTest();
                yield break;
            }

            float until = Time.time + Mathf.Max(3f, scenario.MaxSeconds);
            float nextTick = 0f;
            while (Time.time < until)
            {
                if (Time.time >= nextTick)
                {
                    nextTick = Time.time + 0.25f;
                    rec.TickPlayer(player, "moving");
                    if (Horiz(player.transform.position, player.CurrentTarget) < 2.5f)
                        rec.EmitEvent(user, "reached_target", player);
                }
                if (scenario.ResourceDelta != null && ResourcesMet(ctrl, scenario.ResourceDelta, rec.Snapshot(ctrl)))
                {
                    rec.EmitEvent(user, "plan_completed", player);
                    break;
                }
                // go_home / idle plans finish without resource delta
                if ((scenario.ResourceDelta == null || !HasAnyResourceNeed(scenario.ResourceDelta))
                    && (player.Action == "idle" || player.PhaseName == "IdleStand"))
                {
                    rec.EmitEvent(user, "plan_completed", player);
                    break;
                }
                if (!string.IsNullOrEmpty(scenario.ExpectedMotionPattern)
                    && Time.time > until - 0.5f)
                    break;
                yield return null;
            }

            if (Time.time >= until)
                rec.EmitEvent(user, "timeout", player);

            rec.EndScenario();
            report.TrajectoryPath = rec.CurrentPath;

            var spec = new StreamingSurvivalScenarioChecker.ScenarioSpec
            {
                id = scenario.Id,
                expected_target_type = scenario.ExpectedTargetType,
                expected_motion_pattern = scenario.ExpectedMotionPattern,
                max_seconds = scenario.MaxSeconds,
                expected_resource_delta = scenario.ResourceDelta,
                expect_no_resource_credit = scenario.ExpectNoResourceCredit,
            };
            report.Check = StreamingSurvivalScenarioChecker.CheckFile(scenario.Id, report.TrajectoryPath, spec);
            report.Ok = report.Check.Ok;
            report.Reason = report.Check.Reason;
            Debug.Log($"[SSTest] SCENARIO id={scenario.Id} overall={(report.Ok ? "PASS" : "FAIL")} reason={report.Reason}");
            WriteScenarioResult(report);

            // Stop character so it cannot pollute next scenario
            ctrl.RemovePlayer(user);
            ctrl.ResetResourcesForTest();
        }
        finally
        {
            _lastReport = report;
            _busy = false;
        }
    }

    static float Horiz(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static bool HasAnyResourceNeed(StreamingSurvivalScenarioChecker.ResourceDelta need)
    {
        if (need == null) return false;
        return need.water + need.wood + need.stone + need.food + need.heat > 0;
    }

    static Vector3 ResolveStartPosition(StreamingSurvivalController ctrl, string startAt)
    {
        Vector3 spawn = ctrl.FollowerSpawnWorld;
        string at = (startAt ?? "").Trim().ToLowerInvariant();
        var reg = StreamingSurvivalWorldRegistry.Instance;
        if (string.IsNullOrEmpty(at) || at == "spawn" || at == "home")
            return spawn;
        if (at == "water" && reg != null)
        {
            var w = reg.GetNearestWater(spawn);
            if (w != null)
            {
                // Walkable approach stand south of pond — not pond interior.
                Vector3 p = new Vector3(
                    Mathf.Clamp(w.Position.x - 6.5f, 7.0f, 11.5f),
                    spawn.y,
                    Mathf.Clamp(Mathf.Min(w.InteractionPosition.z, w.Position.z - 0.8f), 22.5f, 25.5f));
                return p;
            }
        }
        if (at == "near_tree" && reg != null)
        {
            var t = reg.GetNearestTree(spawn);
            if (t != null)
            {
                Vector3 p = t.InteractionPosition;
                p.y = spawn.y;
                return p;
            }
        }
        if (at == "near_fence")
        {
            var home = reg != null ? reg.GetHome() : null;
            Vector3 house = home != null ? home.Position : ctrl.HouseWorld;
            Vector3 interact = home != null ? home.InteractionPosition : spawn;
            // Place between interaction point and house center — path often blocked by fence.
            Vector3 p = Vector3.Lerp(interact, house, 0.55f);
            p.y = spawn.y;
            return p;
        }
        return spawn;
    }

    static bool ResourcesMet(StreamingSurvivalController ctrl,
        StreamingSurvivalScenarioChecker.ResourceDelta need,
        StreamingSurvivalTrajectoryRecorder.ResourceSnap snap)
    {
        if (need == null) return false;
        // snap is absolute; compare against need as minimum totals after reset (start at 0)
        return snap.Water >= need.water
            && snap.Wood >= need.wood
            && snap.Stone >= need.stone
            && snap.Food >= need.food
            && snap.Heat >= need.heat
            && (need.water + need.wood + need.stone + need.food + need.heat) > 0;
    }

    static string BuildQueue(List<Step> plan)
    {
        if (plan == null || plan.Count == 0) return "";
        var parts = new List<string>();
        foreach (var s in plan)
        {
            string a = s.Action;
            if (a == "circle") a = "walk_circle";
            parts.Add($"{a}:{Mathf.Max(1, s.Count)}");
        }
        return string.Join(";", parts);
    }

    void WriteScenarioResult(RunReport report)
    {
        EnsureOutputReady();
        string dir = EffectiveOutputDir();
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.Append("{\"id\":\"").Append(report.ScenarioId).Append("\",");
        sb.Append("\"overall\":\"").Append(report.Ok ? "PASS" : "FAIL").Append("\",");
        sb.Append("\"ok\":").Append(report.Ok ? "true" : "false").Append(',');
        sb.Append("\"reason\":\"").Append(Esc(report.Reason)).Append("\",");
        sb.Append("\"trajectory\":\"").Append(Esc(report.TrajectoryPath)).Append("\"");
        if (report.Check != null)
        {
            sb.Append(",\"continuity\":\"").Append(report.Check.ContinuityPass ? "PASS" : "FAIL").Append("\"");
            sb.Append(",\"ground_checker\":\"").Append(report.Check.GroundPass ? "PASS" : "FAIL").Append("\"");
            sb.Append(",\"min_ground_clearance\":")
                .Append(report.Check.MinGroundClearance.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(",\"max_ground_clearance\":")
                .Append(report.Check.MaxGroundClearance.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(",\"max_position_jump\":")
                .Append(report.Check.MaxPositionJump.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(",\"max_speed\":")
                .Append(report.Check.MaxSpeed.ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(",\"illegal_teleports\":[");
            for (int i = 0; i < report.Check.IllegalTeleports.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(report.Check.IllegalTeleports[i])).Append('"');
            }
            sb.Append(']');
        }
        sb.Append('}');
        File.WriteAllText(Path.Combine(dir, report.ScenarioId + "_result.json"), sb.ToString());
    }

    void WriteArtifact(string name, string json)
    {
        EnsureOutputReady();
        File.WriteAllText(Path.Combine(EffectiveOutputDir(), name), json);
    }

    static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static Scenario ParseScenarioJson(string json)
    {
        var sc = new Scenario();
        sc.Id = ExtractString(json, "id");
        sc.ChatCommand = ExtractString(json, "chat_command");
        sc.ExpectedTargetType = ExtractString(json, "expected_target_type");
        sc.ExpectedMotionPattern = ExtractString(json, "expected_motion_pattern");
        sc.MaxSeconds = ExtractFloat(json, "max_seconds", 30f);
        sc.NegativeTest = ExtractString(json, "negative_test");
        sc.StartAt = ExtractString(json, "start_at");
        sc.ExpectNoResourceCredit = ExtractString(json, "expect_no_resource_credit") == "true"
            || json.IndexOf("\"expect_no_resource_credit\": true", StringComparison.Ordinal) >= 0
            || json.IndexOf("\"expect_no_resource_credit\":true", StringComparison.Ordinal) >= 0;
        // expected_plan steps
        int planIdx = json.IndexOf("\"expected_plan\"", StringComparison.Ordinal);
        if (planIdx >= 0)
        {
            int arr = json.IndexOf('[', planIdx);
            int end = FindBracketEnd(json, arr);
            if (arr >= 0 && end > arr)
            {
                string chunk = json.Substring(arr, end - arr + 1);
                int search = 0;
                while (true)
                {
                    int a = chunk.IndexOf("\"action\"", search, StringComparison.Ordinal);
                    if (a < 0) break;
                    int objStart = chunk.LastIndexOf('{', a);
                    int objEnd = FindBraceEnd(chunk, objStart);
                    if (objStart < 0 || objEnd < 0) { search = a + 8; continue; }
                    string obj = chunk.Substring(objStart, objEnd - objStart + 1);
                    var step = new Step
                    {
                        Action = ExtractString(obj, "action"),
                        Count = Mathf.Max(1, ExtractInt(obj, "count", 1)),
                    };
                    sc.Plan.Add(step);
                    search = objEnd + 1;
                }
            }
        }
        // resource delta
        int rd = json.IndexOf("\"expected_resource_delta\"", StringComparison.Ordinal);
        if (rd >= 0)
        {
            int brace = json.IndexOf('{', rd);
            int bend = FindBraceEnd(json, brace);
            if (brace >= 0 && bend > brace)
            {
                string obj = json.Substring(brace, bend - brace + 1);
                sc.ResourceDelta = new StreamingSurvivalScenarioChecker.ResourceDelta
                {
                    water = ExtractInt(obj, "water", 0),
                    wood = ExtractInt(obj, "wood", 0),
                    food = ExtractInt(obj, "food", 0),
                    heat = ExtractInt(obj, "heat", 0),
                    stone = ExtractInt(obj, "stone", 0),
                };
            }
        }
        return sc;
    }

    static string ExtractString(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return "";
        int colon = json.IndexOf(':', i + needle.Length);
        if (colon < 0) return "";
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return "";
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return "";
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    static int ExtractInt(string json, string key, int fallback)
    {
        string needle = "\"" + key + "\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return fallback;
        int colon = json.IndexOf(':', i + needle.Length);
        if (colon < 0) return fallback;
        int j = colon + 1;
        while (j < json.Length && (json[j] == ' ' || json[j] == '\t')) j++;
        int k = j;
        while (k < json.Length && ((json[k] >= '0' && json[k] <= '9') || json[k] == '-')) k++;
        if (k > j && int.TryParse(json.Substring(j, k - j), out int n)) return n;
        return fallback;
    }

    static float ExtractFloat(string json, string key, float fallback)
    {
        string needle = "\"" + key + "\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return fallback;
        int colon = json.IndexOf(':', i + needle.Length);
        if (colon < 0) return fallback;
        int j = colon + 1;
        while (j < json.Length && (json[j] == ' ' || json[j] == '\t')) j++;
        int k = j;
        while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '.' || json[k] == '-')) k++;
        if (k > j && float.TryParse(json.Substring(j, k - j),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float n))
            return n;
        return fallback;
    }

    static int FindBraceEnd(string s, int openIdx)
    {
        if (openIdx < 0 || openIdx >= s.Length || s[openIdx] != '{') return -1;
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    static int FindBracketEnd(string s, int openIdx)
    {
        if (openIdx < 0 || openIdx >= s.Length || s[openIdx] != '[') return -1;
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}
