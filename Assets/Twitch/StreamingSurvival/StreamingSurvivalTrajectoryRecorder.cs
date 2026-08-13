using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>JSONL trajectory recorder for Streaming Survival scenario tests.</summary>
public sealed class StreamingSurvivalTrajectoryRecorder : MonoBehaviour
{
    public static StreamingSurvivalTrajectoryRecorder Instance { get; private set; }

    string _runDir;
    string _scenarioId;
    string _path;
    StreamWriter _writer;
    float _t0;
    readonly Dictionary<string, ResourceSnap> _before = new Dictionary<string, ResourceSnap>();

    public struct ResourceSnap
    {
        public int Water, Wood, Food, Heat, Stone;
    }

    public string CurrentPath => _path;
    public string RunDir => _runDir;
    public bool IsRecording => _writer != null;

    bool _liveTick;
    string _liveUser;
    float _nextLiveTickUnscaled;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    public void BeginRun(string artifactsRoot = null)
    {
        // Exact output dir from Python (run_id folder) takes priority
        if (!string.IsNullOrEmpty(artifactsRoot))
        {
            _runDir = artifactsRoot;
            Directory.CreateDirectory(_runDir);
            Directory.CreateDirectory(Path.Combine(_runDir, "trajectories"));
            Debug.Log($"[SSTrajectory] run_dir={_runDir} (explicit)");
            return;
        }
        string root = Environment.GetEnvironmentVariable("STREAMING_SURVIVAL_TEST_ARTIFACTS_DIR");
        if (string.IsNullOrEmpty(root))
            root = Environment.GetEnvironmentVariable("STREAMING_SURVIVAL_ARTIFACTS");
        if (string.IsNullOrEmpty(root))
            root = Path.Combine(Application.persistentDataPath, "streaming_survival_test_runs");
        // If env already points at a concrete run folder, use it as-is
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STREAMING_SURVIVAL_TEST_ARTIFACTS_DIR")))
        {
            _runDir = root;
            Directory.CreateDirectory(_runDir);
            Directory.CreateDirectory(Path.Combine(_runDir, "trajectories"));
            Debug.Log($"[SSTrajectory] run_dir={_runDir} (env TEST_ARTIFACTS_DIR)");
            return;
        }
        string ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        _runDir = Path.Combine(root, ts);
        Directory.CreateDirectory(_runDir);
        Directory.CreateDirectory(Path.Combine(_runDir, "trajectories"));
        Debug.Log($"[SSTrajectory] run_dir={_runDir}");
    }

    public void SetRunDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        BeginRun(dir);
    }

    public void BeginScenario(string scenarioId, string username)
    {
        if (string.IsNullOrEmpty(_runDir))
            BeginRun();
        _scenarioId = scenarioId ?? "scenario";
        _path = Path.Combine(_runDir, "trajectories", $"{_scenarioId}_trajectory.jsonl");
        _writer?.Dispose();
        _writer = new StreamWriter(_path, false, Encoding.UTF8) { AutoFlush = true };
        _t0 = Time.time;
        _liveTick = false;
        _liveUser = username ?? "";
        var ctrl = StreamingSurvivalController.Instance;
        _before[username ?? ""] = Snapshot(ctrl);
        Emit(username, null, null, null, null, "started_action", null);
    }

    /// <summary>Live stress / bot-driven recording with automatic Update ticks.</summary>
    public void BeginLiveTrajectory(string attemptId, string username)
    {
        BeginScenario(attemptId, username);
        _liveTick = true;
        _liveUser = username ?? "";
        _nextLiveTickUnscaled = 0f;
        var ctrl = StreamingSurvivalController.Instance;
        var p = ctrl != null ? ctrl.GetPlayer(username) : null;
        if (p != null)
            TickPlayer(p, "live_begin");
    }

    public void EndLiveTrajectory(string evt = "live_end")
    {
        if (_writer != null && !string.IsNullOrEmpty(_liveUser))
        {
            var ctrl = StreamingSurvivalController.Instance;
            var p = ctrl != null ? ctrl.GetPlayer(_liveUser) : null;
            EmitEvent(_liveUser, evt, p);
        }
        _liveTick = false;
        EndScenario();
    }

    public void EndScenario()
    {
        _liveTick = false;
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
    }

    void Update()
    {
        if (!_liveTick || _writer == null || string.IsNullOrEmpty(_liveUser))
            return;
        // Wall-clock cadence: under Time.timeScale>1, game-time ticks would skip
        // and look like teleports to the continuity checker.
        if (Time.unscaledTime < _nextLiveTickUnscaled)
            return;
        _nextLiveTickUnscaled = Time.unscaledTime + 0.25f;
        var ctrl = StreamingSurvivalController.Instance;
        var p = ctrl != null ? ctrl.GetPlayer(_liveUser) : null;
        if (p != null)
            TickPlayer(p, "moving");
    }

    public ResourceSnap Snapshot(StreamingSurvivalController ctrl)
    {
        if (ctrl == null) return default;
        return new ResourceSnap
        {
            Water = ctrl.Water,
            Wood = ctrl.Wood,
            Food = ctrl.Food,
            Heat = ctrl.Heat,
            Stone = ctrl.Stone,
        };
    }

    public void TickPlayer(StreamingSurvivalPlayer p, string evt = "moving")
    {
        if (_writer == null || p == null) return;
        Vector3 pos = p.transform.position;
        Vector3 tgt = p.CurrentTarget;
        float dist = Horiz(pos, tgt);
        EmitFull(p, evt, dist, null, null);
    }

    public void EmitEvent(string username, string evt, StreamingSurvivalPlayer p = null)
    {
        if (_writer == null) return;
        // Live stress records ONE attempt/user at a time — never mix other agents
        // into the same jsonl (that fabricated 15–20m "max jump" between users).
        if (_liveTick && !string.IsNullOrEmpty(_liveUser))
        {
            string u = p != null ? p.Username : username;
            if (!string.Equals(u, _liveUser, StringComparison.OrdinalIgnoreCase))
                return;
        }
        if (p != null)
        {
            float dist = Horiz(p.transform.position, p.CurrentTarget);
            EmitFull(p, evt, dist, null, null);
            return;
        }
        Emit(username, null, null, null, null, evt, null, null);
    }

    public void EmitGuard(
        string username,
        string evt,
        StreamingSurvivalPlayer p,
        StreamingSurvivalResourceGuard.GuardResult guard)
    {
        if (_writer == null || p == null || guard == null) return;
        if (_liveTick && !string.IsNullOrEmpty(_liveUser)
            && !string.Equals(p.Username, _liveUser, StringComparison.OrdinalIgnoreCase))
            return;
        EmitFull(p, evt, guard.Distance, guard.Ok ? "pass" : "denied", guard.Reason);
    }

    void EmitFull(
        StreamingSurvivalPlayer p,
        string evt,
        float dist,
        string guardResult,
        string guardReason)
    {
        if (p == null || _writer == null) return;
        if (_liveTick && !string.IsNullOrEmpty(_liveUser)
            && !string.Equals(p.Username, _liveUser, StringComparison.OrdinalIgnoreCase))
            return;
        float radius = StreamingSurvivalResourceGuard.RadiusForAction(p.Action);
        bool inside = radius > 0f && dist <= radius;
        var ctrl = StreamingSurvivalController.Instance;
        var after = Snapshot(ctrl);
        _before.TryGetValue(p.Username ?? "", out var before);

        Vector3 pos = p.transform.position;
        SampleGround(p, out float groundY, out float bottomY, out float clearance,
            out bool below, out bool grounded, out string groundCol);

        var sb = new StringBuilder(512);
        sb.Append('{');
        sb.Append("\"t\":").Append((Time.time - _t0).ToString("F3")).Append(',');
        sb.Append("\"username\":\"").Append(Esc(p.Username)).Append("\",");
        sb.Append("\"pos\":{\"x\":").Append(pos.x.ToString("F2"))
            .Append(",\"y\":").Append(pos.y.ToString("F2"))
            .Append(",\"z\":").Append(pos.z.ToString("F2")).Append("},");
        sb.Append("\"ground_y\":").Append(groundY.ToString("F2")).Append(',');
        sb.Append("\"character_bottom_y\":").Append(bottomY.ToString("F2")).Append(',');
        sb.Append("\"ground_clearance\":").Append(clearance.ToString("F2")).Append(',');
        sb.Append("\"is_below_ground\":").Append(below ? "true" : "false").Append(',');
        sb.Append("\"is_grounded\":").Append(grounded ? "true" : "false").Append(',');
        if (!string.IsNullOrEmpty(groundCol))
            sb.Append("\"ground_collider_name\":\"").Append(Esc(groundCol)).Append("\",");
        if (!string.IsNullOrEmpty(p.Action))
            sb.Append("\"action\":\"").Append(Esc(p.Action)).Append("\",");
        sb.Append("\"current_action\":\"").Append(Esc(p.Action)).Append("\",");
        sb.Append("\"current_phase\":\"").Append(Esc(p.PhaseName)).Append("\",");
        sb.Append("\"state\":\"").Append(Esc(p.CollectState.ToString())).Append("\",");
        if (!string.IsNullOrEmpty(p.CurrentTargetType))
            sb.Append("\"target_type\":\"").Append(Esc(p.CurrentTargetType)).Append("\",");
        if (!string.IsNullOrEmpty(p.CurrentTargetId))
            sb.Append("\"target_id\":\"").Append(Esc(p.CurrentTargetId)).Append("\",");
        Vector3 tgt = p.CurrentTarget;
        sb.Append("\"target_pos\":{\"x\":").Append(tgt.x.ToString("F2"))
            .Append(",\"y\":").Append(tgt.y.ToString("F2"))
            .Append(",\"z\":").Append(tgt.z.ToString("F2")).Append("},");
        sb.Append("\"distance_to_target\":").Append(dist.ToString("F2")).Append(',');
        sb.Append("\"interaction_radius\":").Append(radius.ToString("F2")).Append(',');
        sb.Append("\"is_inside_interaction_radius\":").Append(inside ? "true" : "false").Append(',');
        sb.Append("\"can_complete_resource\":").Append(p.CanCompleteResourceNow ? "true" : "false").Append(',');
        if (!string.IsNullOrEmpty(guardResult))
            sb.Append("\"resource_guard_result\":\"").Append(Esc(guardResult)).Append("\",");
        if (!string.IsNullOrEmpty(guardReason))
            sb.Append("\"resource_guard_reason\":\"").Append(Esc(guardReason)).Append("\",");
        sb.Append("\"resource_before\":{\"water\":").Append(before.Water)
            .Append(",\"wood\":").Append(before.Wood)
            .Append(",\"food\":").Append(before.Food)
            .Append(",\"heat\":").Append(before.Heat)
            .Append(",\"stone\":").Append(before.Stone).Append("},");
        sb.Append("\"resource_after\":{\"water\":").Append(after.Water)
            .Append(",\"wood\":").Append(after.Wood)
            .Append(",\"food\":").Append(after.Food)
            .Append(",\"heat\":").Append(after.Heat)
            .Append(",\"stone\":").Append(after.Stone).Append("},");
        sb.Append("\"event\":\"").Append(Esc(evt ?? "moving")).Append('"');
        sb.Append('}');
        _writer.WriteLine(sb.ToString());
        if (below)
            Debug.LogWarning(
                $"[SSTrajectory] ground_check_fail user={p.Username} pos=({pos.x:F1},{pos.y:F1},{pos.z:F1}) " +
                $"ground_y={groundY:F1} clearance={clearance:F1} col={groundCol}");
        else
            Debug.Log($"[SSTrajectory] {sb}");
    }

    public const float GroundCheckRaycastHeight = 50f;
    public const float GroundCheckRaycastDistance = 120f;
    public const float GroundBelowTolerance = 0.25f;

    public static void SampleGround(
        StreamingSurvivalPlayer p,
        out float groundY,
        out float bottomY,
        out float clearance,
        out bool below,
        out bool grounded,
        out string colliderName)
    {
        Vector3 pos = p != null ? p.transform.position : Vector3.zero;
        float half = 0f;
        float height = 1.8f;
        var cc = p != null ? p.GetComponent<CharacterController>() : null;
        if (cc != null)
        {
            height = Mathf.Max(0.5f, cc.height);
            // Feet ≈ transform.y when center.y ≈ height/2.
            half = Mathf.Max(0f, height * 0.5f - cc.center.y);
        }
        bottomY = pos.y - half;
        groundY = bottomY;
        colliderName = "";
        grounded = false;

        // Cast from high above; ignore canopy/roof hits above the character body.
        Vector3 origin = new Vector3(pos.x, pos.y + GroundCheckRaycastHeight, pos.z);
        RaycastHit[] hits = Physics.RaycastAll(
            origin, Vector3.down, GroundCheckRaycastDistance,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float maxAcceptY = bottomY + height + 0.35f; // allow standing on rocks/slopes
        float bestY = float.NegativeInfinity;
        Collider bestCol = null;
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (h.collider == null) continue;
            if (p != null && h.collider.transform.IsChildOf(p.transform)) continue;
            if (h.collider.GetComponentInParent<StreamingSurvivalPlayer>() != null) continue;
            if (h.collider.GetComponentInParent<CharacterController>() != null) continue;
            // Skip overhead foliage / roofs above the character.
            if (h.point.y > maxAcceptY) continue;
            if (h.point.y > bestY)
            {
                bestY = h.point.y;
                bestCol = h.collider;
            }
        }
        if (bestCol != null && !float.IsNegativeInfinity(bestY))
        {
            groundY = bestY;
            colliderName = bestCol.gameObject.name;
            grounded = true;
        }
        clearance = bottomY - groundY;
        below = grounded && clearance < -GroundBelowTolerance;
    }

    void Emit(
        string username,
        string action,
        string targetType,
        string targetId,
        Vector3? targetPos,
        string evt,
        Vector3? pos,
        float? dist = null)
    {
        if (_writer == null) return;
        if (_liveTick && !string.IsNullOrEmpty(_liveUser)
            && !string.Equals(username, _liveUser, StringComparison.OrdinalIgnoreCase))
            return;
        var ctrl = StreamingSurvivalController.Instance;
        var after = Snapshot(ctrl);
        _before.TryGetValue(username ?? "", out var before);

        var sb = new StringBuilder(256);
        sb.Append('{');
        sb.Append("\"t\":").Append((Time.time - _t0).ToString("F3")).Append(',');
        sb.Append("\"username\":\"").Append(Esc(username)).Append("\",");
        if (pos.HasValue)
            sb.Append("\"pos\":{\"x\":").Append(pos.Value.x.ToString("F2"))
                .Append(",\"y\":").Append(pos.Value.y.ToString("F2"))
                .Append(",\"z\":").Append(pos.Value.z.ToString("F2")).Append("},");
        if (!string.IsNullOrEmpty(action))
            sb.Append("\"action\":\"").Append(Esc(action)).Append("\",");
        if (!string.IsNullOrEmpty(targetType))
            sb.Append("\"target_type\":\"").Append(Esc(targetType)).Append("\",");
        if (!string.IsNullOrEmpty(targetId))
            sb.Append("\"target_id\":\"").Append(Esc(targetId)).Append("\",");
        if (targetPos.HasValue)
            sb.Append("\"target_pos\":{\"x\":").Append(targetPos.Value.x.ToString("F2"))
                .Append(",\"y\":").Append(targetPos.Value.y.ToString("F2"))
                .Append(",\"z\":").Append(targetPos.Value.z.ToString("F2")).Append("},");
        if (dist.HasValue)
            sb.Append("\"distance_to_target\":").Append(dist.Value.ToString("F2")).Append(',');
        sb.Append("\"resource_before\":{\"water\":").Append(before.Water)
            .Append(",\"wood\":").Append(before.Wood)
            .Append(",\"food\":").Append(before.Food)
            .Append(",\"heat\":").Append(before.Heat)
            .Append(",\"stone\":").Append(before.Stone).Append("},");
        sb.Append("\"resource_after\":{\"water\":").Append(after.Water)
            .Append(",\"wood\":").Append(after.Wood)
            .Append(",\"food\":").Append(after.Food)
            .Append(",\"heat\":").Append(after.Heat)
            .Append(",\"stone\":").Append(after.Stone).Append("},");
        sb.Append("\"event\":\"").Append(Esc(evt ?? "moving")).Append('"');
        sb.Append('}');
        _writer.WriteLine(sb.ToString());
        Debug.Log($"[SSTrajectory] {sb}");
    }

    static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    static float Horiz(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    void OnDestroy()
    {
        _writer?.Dispose();
        _writer = null;
        if (Instance == this) Instance = null;
    }
}
