using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>Offline checkers over recorded JSONL trajectories.</summary>
public sealed class StreamingSurvivalScenarioChecker : MonoBehaviour
{
    public sealed class CheckResult
    {
        public bool Ok = true;
        public string ScenarioId;
        public bool ParserPass = true;
        public bool TargetPass = true;
        public bool TrajectoryPass = true;
        public bool ResourcePass = true;
        public bool DirectionPass = true;
        public bool ContinuityPass = true;
        public bool GroundPass = true;
        public float MaxPositionJump;
        public float MaxSpeed;
        public float MinGroundClearance;
        public float MaxGroundClearance;
        public readonly List<string> IllegalTeleports = new List<string>();
        public string Reason = "";
        public readonly List<string> Notes = new List<string>();
    }

    [Serializable]
    public class ScenarioSpec
    {
        public string id;
        public string expected_target_type;
        public string expected_motion_pattern;
        public float max_seconds = 30f;
        public ResourceDelta expected_resource_delta;
        public bool expect_no_resource_credit;
    }

    [Serializable]
    public class ResourceDelta
    {
        public int water;
        public int wood;
        public int food;
        public int heat;
        public int stone;
    }

    public static CheckResult CheckFile(string scenarioId, string jsonlPath, ScenarioSpec spec)
    {
        var result = new CheckResult { ScenarioId = scenarioId };
        if (string.IsNullOrEmpty(jsonlPath) || !File.Exists(jsonlPath))
        {
            Fail(result, "trajectory", $"missing trajectory file: {jsonlPath}");
            return result;
        }

        var lines = File.ReadAllLines(jsonlPath);
        if (lines.Length < 2)
        {
            Fail(result, "trajectory", "trajectory too short");
            return result;
        }

        var samples = new List<Sample>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            samples.Add(ParseSample(line));
        }

        string expectType = spec != null ? spec.expected_target_type : null;
        string pattern = spec != null ? spec.expected_motion_pattern : null;
        float maxSec = spec != null ? Mathf.Max(1f, spec.max_seconds) : 30f;
        bool noCredit = spec != null && spec.expect_no_resource_credit;

        // A) Target correctness (skip for pure negative no-credit tests)
        if (!noCredit && !string.IsNullOrEmpty(expectType))
        {
            bool saw = false;
            foreach (var s in samples)
            {
                if (!string.IsNullOrEmpty(s.TargetType) && s.TargetType == expectType)
                {
                    saw = true;
                    break;
                }
            }
            if (!saw)
            {
                result.TargetPass = false;
                Fail(result, "target", $"expected target_type={expectType} never observed");
            }
        }

        // B) Distance decreases + reached
        if (!noCredit && (!string.IsNullOrEmpty(expectType) || string.IsNullOrEmpty(pattern)))
        {
            CheckApproach(result, samples, maxSec);
        }

        // C) Resources
        if (noCredit)
            CheckNoResourceCredit(result, samples);
        else if (spec != null && spec.expected_resource_delta != null)
            CheckResources(result, samples, spec.expected_resource_delta);

        // D) Credit not at spawn / too far
        CheckCreditNotAtSpawn(result, samples);
        CheckResourceGuardEvents(result, samples);

        // E) Patterns
        if (!string.IsNullOrEmpty(pattern))
            CheckPattern(result, samples, pattern, maxSec);

        // F) Wrong-direction smoke
        if (!noCredit)
            CheckInitialDirection(result, samples);

        // G) Continuity — catch hidden Warp / teleport (stuck_recovery never excuses large jumps)
        CheckContinuity(result, samples, scenarioId);

        // H) collect_water event order + smooth approach
        if (!noCredit && (expectType == "water_source"
            || (spec != null && !string.IsNullOrEmpty(spec.id) && spec.id.Contains("collect_water"))))
            CheckCollectWaterOrder(result, samples);

        // I) Ground / Y — never below sampled ground_y
        CheckGround(result, samples);

        if (result.Ok)
            result.Notes.Add("all checks passed");
        return result;
    }

    const float GroundBelowTolerance = 0.25f;

    static void CheckGround(CheckResult result, List<Sample> samples)
    {
        float minClr = float.PositiveInfinity;
        float maxClr = float.NegativeInfinity;
        int below = 0;
        Sample? firstBad = null;
        bool any = false;
        foreach (var s in samples)
        {
            if (!s.HasGround) continue;
            any = true;
            float clr = s.GroundClearance;
            if (clr < minClr) minClr = clr;
            if (clr > maxClr) maxClr = clr;
            if (s.IsBelowGround || clr < -GroundBelowTolerance)
            {
                below++;
                if (!firstBad.HasValue) firstBad = s;
            }
        }
        if (!any) return;
        if (!float.IsInfinity(minClr)) result.MinGroundClearance = minClr;
        if (!float.IsInfinity(maxClr) && !float.IsNegativeInfinity(maxClr))
            result.MaxGroundClearance = maxClr;
        if (below > 0 && firstBad.HasValue)
        {
            var s = firstBad.Value;
            result.GroundPass = false;
            result.TrajectoryPass = false;
            string pos = s.Pos.HasValue
                ? $"({s.Pos.Value.x:F1},{s.Pos.Value.y:F1},{s.Pos.Value.z:F1})"
                : "?";
            Fail(result, "ground",
                $"character below ground clearance={s.GroundClearance:F2} pos={pos} " +
                $"ground_y={s.GroundY:F2} n={below}");
        }
        else
            result.Notes.Add($"ground ok min_clearance={result.MinGroundClearance:F2}");
    }

    static readonly HashSet<string> SetupTeleportEvents = new HashSet<string>
    {
        "join_spawn",
        "scenario_setup",
        "scenario_reset",
        "manual_respawn_command",
        "round_reset",
        "round_start",
        "negative_forest",
    };

    /// <summary>Events that must never excuse a large jump in normal gameplay.</summary>
    static readonly HashSet<string> ForbiddenNormalTeleportEvents = new HashSet<string>
    {
        "stuck_recovery",
        "respawn_to_spawn",
        "controlled_teleport",
        "recovery_attempt",
    };

    const float MaxAllowedPositionJump = 3.0f;
    const float MaxStuckNudgeJump = 1.5f;
    const float MaxAllowedSpeed = 8.0f;

    static bool IsStrictNoTeleportScenario(string scenarioId)
    {
        if (string.IsNullOrEmpty(scenarioId)) return false;
        return scenarioId == "collect_water_no_teleport"
            || scenarioId == "go_home_no_teleport"
            || scenarioId == "go_home_no_teleport_strict"
            || scenarioId == "collect_wood_simple"
            || scenarioId == "water_then_campfire"
            || scenarioId == "water_home_wood_chain"
            || scenarioId == "stuck_near_fence_no_target_teleport";
    }

    static void CheckContinuity(CheckResult result, List<Sample> samples)
    {
        CheckContinuity(result, samples, null);
    }

    static void CheckContinuity(CheckResult result, List<Sample> samples, string scenarioId)
    {
        bool strict = IsStrictNoTeleportScenario(scenarioId)
            || (scenarioId != null && scenarioId.Contains("no_teleport"));
        float maxJump = 0f;
        float maxSpeed = 0f;
        const float MinDtForSpeed = 0.08f;
        Sample? prev = null;
        foreach (var s in samples)
        {
            if (!s.Pos.HasValue) continue;
            if (prev == null || !prev.Value.Pos.HasValue)
            {
                prev = s;
                continue;
            }
            float rawDt = s.T - prev.Value.T;
            float jump = Horiz(prev.Value.Pos.Value, s.Pos.Value);
            if (jump > maxJump) maxJump = jump;

            bool setupNearby = HasNearbyEvent(samples, s.T, prev.Value.T, SetupTeleportEvents);
            bool forbiddenNearby = HasNearbyEvent(samples, s.T, prev.Value.T, ForbiddenNormalTeleportEvents);

            if (rawDt < MinDtForSpeed)
            {
                // Same-frame: allow tiny nudge, never large jump unless setup teleport.
                if (jump > MaxAllowedPositionJump)
                {
                    if (!setupNearby || (strict && forbiddenNearby && !setupNearby))
                    {
                        string msg =
                            $"Illegal teleport detected: jump={jump:F1} speed=n/a " +
                            $"from=({prev.Value.Pos.Value.x:F1},{prev.Value.Pos.Value.z:F1}) " +
                            $"to=({s.Pos.Value.x:F1},{s.Pos.Value.z:F1})" +
                            (forbiddenNearby ? " (stuck_recovery/controlled not allowed)" : " no setup teleport event");
                        result.ContinuityPass = false;
                        result.TrajectoryPass = false;
                        result.IllegalTeleports.Add(msg);
                        Fail(result, "continuity", msg);
                    }
                }
                else if (strict && jump > MaxStuckNudgeJump + 0.05f && !setupNearby)
                {
                    string msg =
                        $"Illegal teleport detected: jump={jump:F1} speed=n/a " +
                        $"from=({prev.Value.Pos.Value.x:F1},{prev.Value.Pos.Value.z:F1}) " +
                        $"to=({s.Pos.Value.x:F1},{s.Pos.Value.z:F1}) exceeds nudge limit";
                    result.ContinuityPass = false;
                    result.TrajectoryPass = false;
                    result.IllegalTeleports.Add(msg);
                    Fail(result, "continuity", msg);
                }
                prev = s;
                continue;
            }
            float speed = jump / rawDt;
            if (speed > maxSpeed) maxSpeed = speed;

            // Hard rule: jump > MAX is FAIL even with stuck_recovery nearby.
            if (jump > MaxAllowedPositionJump)
            {
                if (!setupNearby)
                {
                    string msg =
                        $"Illegal teleport detected: jump={jump:F1} speed={speed:F1} " +
                        $"from=({prev.Value.Pos.Value.x:F1},{prev.Value.Pos.Value.z:F1}) " +
                        $"to=({s.Pos.Value.x:F1},{s.Pos.Value.z:F1}) max_jump exceeded" +
                        (forbiddenNearby ? " (stuck_recovery not allowed)" : "");
                    result.ContinuityPass = false;
                    result.TrajectoryPass = false;
                    result.IllegalTeleports.Add(msg);
                    Fail(result, "continuity", msg);
                }
            }
            else if (jump > MaxStuckNudgeJump + 0.05f && speed > MaxAllowedSpeed && !setupNearby)
            {
                string msg =
                    $"Illegal teleport detected: jump={jump:F1} speed={speed:F1} " +
                    $"from=({prev.Value.Pos.Value.x:F1},{prev.Value.Pos.Value.z:F1}) " +
                    $"to=({s.Pos.Value.x:F1},{s.Pos.Value.z:F1}) no setup teleport event";
                result.ContinuityPass = false;
                result.TrajectoryPass = false;
                result.IllegalTeleports.Add(msg);
                Fail(result, "continuity", msg);
            }
            prev = s;
        }
        result.MaxPositionJump = maxJump;
        result.MaxSpeed = maxSpeed;

        // Strict scenarios: any stuck_recovery / respawn_to_spawn / mid-run controlled_teleport = FAIL
        if (strict)
            CheckForbiddenTeleports(result, samples, scenarioId);

        if (strict && maxJump > MaxAllowedPositionJump)
        {
            string msg =
                $"max_position_jump={maxJump:F3} exceeds MAX_ALLOWED_POSITION_JUMP={MaxAllowedPositionJump:F1}";
            result.ContinuityPass = false;
            result.TrajectoryPass = false;
            if (!result.IllegalTeleports.Contains(msg))
                result.IllegalTeleports.Add(msg);
            Fail(result, "continuity", msg);
        }

        if (result.ContinuityPass)
            result.Notes.Add($"continuity ok max_jump={maxJump:F2} max_speed={maxSpeed:F2}");
    }

    static void CheckForbiddenTeleports(CheckResult result, List<Sample> samples, string scenarioId)
    {
        bool ultraStrict = scenarioId == "go_home_no_teleport_strict";
        foreach (var s in samples)
        {
            if (string.IsNullOrEmpty(s.Event)) continue;
            // scenario_setup at t~0 is outside recording usually; if present, allow only setup reasons.
            if (SetupTeleportEvents.Contains(s.Event))
                continue;
            if (s.Event == "stuck_recovery" || s.Event == "respawn_to_spawn")
            {
                string msg = $"forbidden teleport event={s.Event} t={s.T:F2}";
                result.ContinuityPass = false;
                result.TrajectoryPass = false;
                result.IllegalTeleports.Add(msg);
                Fail(result, "continuity", msg);
            }
            if (ultraStrict && s.Event == "controlled_teleport")
            {
                string msg = $"controlled_teleport forbidden in {scenarioId} t={s.T:F2}";
                result.ContinuityPass = false;
                result.TrajectoryPass = false;
                result.IllegalTeleports.Add(msg);
                Fail(result, "continuity", msg);
            }
        }
    }

    static bool HasNearbyEvent(List<Sample> samples, float t1, float t0, HashSet<string> events)
    {
        foreach (var e in samples)
        {
            if (string.IsNullOrEmpty(e.Event)) continue;
            if (!events.Contains(e.Event)) continue;
            if (Mathf.Abs(e.T - t1) <= 0.55f || Mathf.Abs(e.T - t0) <= 0.55f)
                return true;
        }
        return false;
    }

    static void CheckCollectWaterOrder(CheckResult result, List<Sample> samples)
    {
        int idxTarget = -1, idxReached = -1, idxAdded = -1;
        float startDist = -1f;
        int movingSamples = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.Event == "target_resolved" || s.Event == "started_action" || s.TargetType == "water_source")
            {
                if (idxTarget < 0 && s.TargetType == "water_source") idxTarget = i;
            }
            if (s.Event == "reached_resource" || s.Event == "work_started")
                if (idxReached < 0) idxReached = i;
            if (s.Event == "resource_added")
                if (idxAdded < 0) idxAdded = i;
            if (s.Dist.HasValue && startDist < 0f && s.T < 2f && s.TargetType == "water_source")
                startDist = s.Dist.Value;
            if (s.TargetType == "water_source" && s.Dist.HasValue && s.Dist.Value > 2.5f)
                movingSamples++;
        }
        if (idxAdded >= 0 && idxReached >= 0 && idxAdded < idxReached)
        {
            result.TrajectoryPass = false;
            Fail(result, "trajectory", "resource_added before reached_resource");
        }
        if (startDist > 6f && idxAdded >= 0 && movingSamples < 3)
        {
            result.TrajectoryPass = false;
            Fail(result, "trajectory",
                $"collect_water jumped to interaction: start_dist={startDist:F1} moving_samples={movingSamples}");
        }
        // Sudden arrive only counts if the character also jumped in world position.
        // Waypoint→stand retarget drops distance without a teleport.
        float prevDist = -1f;
        Vector3? prevPos = null;
        foreach (var s in samples)
        {
            if (s.TargetType != "water_source" || !s.Dist.HasValue) continue;
            float d = s.Dist.Value;
            if (prevDist > 8f && d <= 2.0f)
            {
                bool jumped = false;
                if (prevPos.HasValue && s.Pos.HasValue)
                {
                    float dx = s.Pos.Value.x - prevPos.Value.x;
                    float dz = s.Pos.Value.z - prevPos.Value.z;
                    jumped = Mathf.Sqrt(dx * dx + dz * dz) > 2.5f;
                }
                bool allowed = HasNearbyEvent(samples, s.T, s.T, SetupTeleportEvents);
                if (jumped && !allowed)
                {
                    result.ContinuityPass = false;
                    result.TrajectoryPass = false;
                    string msg =
                        $"Illegal teleport to water: dist {prevDist:F1} -> {d:F1} without movement";
                    result.IllegalTeleports.Add(msg);
                    Fail(result, "continuity", msg);
                }
            }
            prevDist = d;
            if (s.Pos.HasValue) prevPos = s.Pos;
        }
    }

    static void CheckApproach(CheckResult result, List<Sample> samples, float maxSec)
    {
        float startDist = -1f;
        float midDist = -1f;
        bool reached = false;
        bool progressed = false;
        foreach (var s in samples)
        {
            if (s.Event == "reached_target" || s.Event == "reached_resource"
                || s.Event == "work_finished"
                || s.Event == "work_started" || s.Event == "plan_completed"
                || s.Event == "resource_added")
            {
                reached = true;
                if (s.Event == "reached_resource" || s.Event == "work_started"
                    || s.Event == "resource_added" || s.Event == "work_finished")
                    progressed = true;
            }
            if (s.Dist.HasValue && s.Dist.Value < 3.5f)
                reached = true;
            if (!s.Dist.HasValue) continue;
            if (startDist < 0f && s.T < 1.5f) startDist = s.Dist.Value;
            if (s.T >= 1.5f && s.T <= Mathf.Min(maxSec, 8f)) midDist = s.Dist.Value;
        }
        // Water corridor / pathfinding may increase distance temporarily.
        bool isWater = false;
        foreach (var s in samples)
        {
            if (s.TargetType == "water_source") { isWater = true; break; }
        }
        if (!isWater && !progressed && startDist > 3f && midDist > 0f && midDist > startDist * 1.15f)
        {
            result.TrajectoryPass = false;
            Fail(result, "trajectory", $"distance increased start={startDist:F1} mid={midDist:F1}");
        }
        if (!reached && startDist > 2f)
        {
            bool hadStuckIdle = false;
            foreach (var s in samples)
            {
                if (s.Event == "character_stuck")
                {
                    hadStuckIdle = true;
                    break;
                }
            }
            if (hadStuckIdle)
            {
                // Stuck without teleport is acceptable for go_home / no-teleport scenarios.
                result.Notes.Add("target not reached but character_stuck (idle, no teleport)");
            }
            else
            {
                result.TrajectoryPass = false;
                Fail(result, "trajectory", "target not reached within max_seconds");
            }
        }
    }

    static void CheckResources(CheckResult result, List<Sample> samples, ResourceDelta delta)
    {
        if (samples.Count == 0) return;
        var first = samples[0];
        var last = samples[samples.Count - 1];
        void Need(string name, int need, int before, int after)
        {
            if (need <= 0) return;
            if (after - before < need)
            {
                result.ResourcePass = false;
                Fail(result, "resource", $"{name} delta={after - before} need>={need}");
            }
        }
        Need("water", delta.water, first.WaterBefore, last.WaterAfter);
        Need("wood", delta.wood, first.WoodBefore, last.WoodAfter);
        Need("stone", delta.stone, first.StoneBefore, last.StoneAfter);
        Need("food", delta.food, first.FoodBefore, last.FoodAfter);
        Need("heat", delta.heat, first.HeatBefore, last.HeatAfter);
    }

    static void CheckNoResourceCredit(CheckResult result, List<Sample> samples)
    {
        if (samples.Count == 0) return;
        var first = samples[0];
        var last = samples[samples.Count - 1];
        if (last.WaterAfter > first.WaterBefore
            || last.WoodAfter > first.WoodBefore
            || last.FoodAfter > first.FoodBefore
            || last.StoneAfter > first.StoneBefore)
        {
            result.ResourcePass = false;
            Fail(result, "resource",
                $"resource credited far from target w={last.WaterAfter - first.WaterBefore} " +
                $"wood={last.WoodAfter - first.WoodBefore} food={last.FoodAfter - first.FoodBefore} " +
                $"stone={last.StoneAfter - first.StoneBefore}");
        }
        bool sawDenied = false;
        foreach (var s in samples)
        {
            if (s.Event == "resource_added")
            {
                result.ResourcePass = false;
                Fail(result, "resource", "resource_added event while expect_no_resource_credit");
            }
            if (s.Event == "resource_guard_denied" || s.Event == "invalid_resource_completion")
                sawDenied = true;
        }
        if (!sawDenied)
        {
            result.Notes.Add("warn: no resource_guard_denied event (still ok if delta=0)");
        }
    }

    static void CheckResourceGuardEvents(CheckResult result, List<Sample> samples)
    {
        bool sawReached = false;
        bool sawWorkStarted = false;
        bool sawWorkFinished = false;
        bool sawGuardPass = false;
        foreach (var s in samples)
        {
            if (s.Event == "reached_resource") sawReached = true;
            if (s.Event == "work_started") sawWorkStarted = true;
            if (s.Event == "work_finished") sawWorkFinished = true;
            if (s.Event == "resource_guard_pass") sawGuardPass = true;

            if (s.Event != "resource_added") continue;
            if (!sawReached || !sawWorkStarted)
            {
                result.ResourcePass = false;
                Fail(result, "resource",
                    "resource_added without reached_resource/work_started order");
            }
            if (!sawGuardPass && !sawWorkFinished)
            {
                result.Notes.Add("warn: resource_added without prior resource_guard_pass");
            }
            if (s.Dist.HasValue && s.Dist.Value > 2.05f)
            {
                result.ResourcePass = false;
                Fail(result, "resource",
                    $"resource_added too far d={s.Dist.Value:F1} action={s.Action}");
            }
            if (s.CanComplete.HasValue && s.CanComplete.Value == false)
            {
                result.ResourcePass = false;
                Fail(result, "resource", "resource_added while can_complete_resource=false");
            }
        }
    }

    static void CheckCreditNotAtSpawn(CheckResult result, List<Sample> samples)
    {
        foreach (var s in samples)
        {
            if (s.Event != "resource_added")
                continue;
            if (!s.Pos.HasValue) continue;
            // spawn/forest/gate are south; real pond approach is z>=24.5
            if (s.Action == "collect_water" && s.Pos.Value.z < 24.5f)
            {
                result.ResourcePass = false;
                Fail(result, "resource", $"water credited near spawn/forest z={s.Pos.Value.z:F1}");
            }
        }
    }

    static void CheckPattern(CheckResult result, List<Sample> samples, string pattern, float maxSec)
    {
        if (samples.Count < 3)
        {
            result.TrajectoryPass = false;
            Fail(result, "trajectory", $"pattern={pattern} not enough samples");
            return;
        }
        // First jsonl event is often started_action without pos — never treat missing as (0,0,0).
        Vector3? origin = null;
        foreach (var s in samples)
        {
            if (s.Pos.HasValue)
            {
                origin = s.Pos;
                break;
            }
        }
        if (!origin.HasValue)
        {
            result.TrajectoryPass = false;
            Fail(result, "trajectory", $"pattern={pattern} no positions");
            return;
        }
        if (pattern == "idle")
        {
            Vector3 c = origin.Value;
            float maxD = 0f;
            foreach (var s in samples)
            {
                if (!s.Pos.HasValue) continue;
                float d = Horiz(c, s.Pos.Value);
                if (d > maxD) maxD = d;
            }
            if (maxD > 3.5f)
            {
                result.TrajectoryPass = false;
                Fail(result, "trajectory", $"idle wandered too far d={maxD:F1}");
            }
            return;
        }
        if (pattern == "circle")
        {
            Vector3 center = origin.Value;
            float sumAbs = 0f;
            float prev = 0f;
            bool first = true;
            foreach (var s in samples)
            {
                if (!s.Pos.HasValue) continue;
                // Ignore post-demo idle samples near center noise
                if (s.Action == "idle") continue;
                float ang = Mathf.Atan2(s.Pos.Value.z - center.z, s.Pos.Value.x - center.x);
                if (first) { prev = ang; first = false; continue; }
                float d = Mathf.DeltaAngle(prev * Mathf.Rad2Deg, ang * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                sumAbs += Mathf.Abs(d);
                prev = ang;
            }
            float totalDeg = sumAbs * Mathf.Rad2Deg;
            if (totalDeg < 120f)
            {
                result.TrajectoryPass = false;
                Fail(result, "trajectory", $"circle angle only {totalDeg:F0} deg");
            }
            return;
        }
        if (pattern == "patrol")
        {
            int reversals = 0;
            Vector3? prevDir = null;
            for (int i = 1; i < samples.Count; i++)
            {
                if (!samples[i].Pos.HasValue || !samples[i - 1].Pos.HasValue) continue;
                Vector3 d = samples[i].Pos.Value - samples[i - 1].Pos.Value;
                d.y = 0f;
                if (d.sqrMagnitude < 0.01f) continue;
                d.Normalize();
                if (prevDir.HasValue && Vector3.Dot(prevDir.Value, d) < -0.2f)
                    reversals++;
                prevDir = d;
            }
            if (reversals < 1)
            {
                result.TrajectoryPass = false;
                Fail(result, "trajectory", "patrol: no direction reversal");
            }
        }
    }

    static void CheckInitialDirection(CheckResult result, List<Sample> samples)
    {
        Sample? start = null;
        Sample? after = null;
        foreach (var s in samples)
        {
            if (!s.Pos.HasValue) continue;
            if (start == null) start = s;
            if (s.T >= 2f) { after = s; break; }
        }
        if (start == null || after == null || !start.Value.TargetPos.HasValue || !start.Value.Pos.HasValue || !after.Value.Pos.HasValue)
            return;
        // Water uses a corridor route (west then north) — not a straight line to lake.
        if (start.Value.Action == "collect_water")
        {
            float dz = after.Value.Pos.Value.z - start.Value.Pos.Value.z;
            if (dz < -1.5f)
            {
                result.DirectionPass = false;
                result.TrajectoryPass = false;
                Fail(result, "direction", $"collect_water moved south away from pond dz={dz:F1}");
            }
            return;
        }
        // go_home from water uses east/south corridor around the fence.
        if (start.Value.Action == "go_home")
            return;
        Vector3 toTarget = start.Value.TargetPos.Value - start.Value.Pos.Value;
        Vector3 moved = after.Value.Pos.Value - start.Value.Pos.Value;
        toTarget.y = 0f;
        moved.y = 0f;
        if (toTarget.sqrMagnitude < 1f || moved.sqrMagnitude < 0.25f) return;
        float dot = Vector3.Dot(toTarget.normalized, moved.normalized);
        if (dot < 0f)
        {
            result.DirectionPass = false;
            result.TrajectoryPass = false;
            Fail(result, "direction", $"Character initially moved away from target. dot={dot:F2}");
        }
        else if (dot < 0.2f)
        {
            result.Notes.Add($"weak initial alignment dot={dot:F2}");
        }
    }

    static void Fail(CheckResult r, string kind, string reason)
    {
        r.Ok = false;
        if (string.IsNullOrEmpty(r.Reason))
            r.Reason = reason;
        else
            r.Reason += " | " + reason;
        r.Notes.Add($"[{kind}] {reason}");
    }

    struct Sample
    {
        public float T;
        public string Action;
        public string TargetType;
        public string Event;
        public Vector3? Pos;
        public Vector3? TargetPos;
        public float? Dist;
        public bool? CanComplete;
        public int WaterBefore, WoodBefore, FoodBefore, HeatBefore, StoneBefore;
        public int WaterAfter, WoodAfter, FoodAfter, HeatAfter, StoneAfter;
        public bool HasGround;
        public float GroundY;
        public float GroundClearance;
        public bool IsBelowGround;
    }

    static Sample ParseSample(string line)
    {
        var s = new Sample();
        s.T = GetFloat(line, "\"t\":");
        s.Action = GetString(line, "\"action\"");
        if (string.IsNullOrEmpty(s.Action))
            s.Action = GetString(line, "\"current_action\"");
        s.TargetType = GetString(line, "\"target_type\"");
        s.Event = GetString(line, "\"event\"");
        s.Dist = GetFloatOpt(line, "\"distance_to_target\":");
        s.Pos = GetVec(line, "\"pos\":");
        s.TargetPos = GetVec(line, "\"target_pos\":");
        if (line.IndexOf("\"can_complete_resource\":true", StringComparison.Ordinal) >= 0)
            s.CanComplete = true;
        else if (line.IndexOf("\"can_complete_resource\":false", StringComparison.Ordinal) >= 0)
            s.CanComplete = false;
        s.WaterBefore = GetNestedInt(line, "resource_before", "water");
        s.WoodBefore = GetNestedInt(line, "resource_before", "wood");
        s.StoneBefore = GetNestedInt(line, "resource_before", "stone");
        s.FoodBefore = GetNestedInt(line, "resource_before", "food");
        s.HeatBefore = GetNestedInt(line, "resource_before", "heat");
        s.WaterAfter = GetNestedInt(line, "resource_after", "water");
        s.WoodAfter = GetNestedInt(line, "resource_after", "wood");
        s.StoneAfter = GetNestedInt(line, "resource_after", "stone");
        s.FoodAfter = GetNestedInt(line, "resource_after", "food");
        s.HeatAfter = GetNestedInt(line, "resource_after", "heat");
        if (line.IndexOf("\"ground_y\":", StringComparison.Ordinal) >= 0
            || line.IndexOf("\"ground_clearance\":", StringComparison.Ordinal) >= 0)
        {
            s.HasGround = true;
            s.GroundY = GetFloat(line, "\"ground_y\":");
            s.GroundClearance = GetFloat(line, "\"ground_clearance\":");
            s.IsBelowGround = line.IndexOf("\"is_below_ground\":true", StringComparison.Ordinal) >= 0
                || s.GroundClearance < -GroundBelowTolerance;
        }
        return s;
    }

    static float GetFloat(string line, string key)
    {
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return 0f;
        i += key.Length;
        int j = i;
        while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.' || line[j] == '-')) j++;
        if (float.TryParse(line.Substring(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            return v;
        return 0f;
    }

    static float? GetFloatOpt(string line, string key)
    {
        if (line.IndexOf(key, StringComparison.Ordinal) < 0) return null;
        return GetFloat(line, key);
    }

    static string GetString(string line, string key)
    {
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        int colon = line.IndexOf(':', i + key.Length);
        if (colon < 0) return null;
        int q1 = line.IndexOf('"', colon + 1);
        if (q1 < 0) return null;
        int q2 = line.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return line.Substring(q1 + 1, q2 - q1 - 1);
    }

    static Vector3? GetVec(string line, string key)
    {
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        float x = GetFloat(line.Substring(i), "\"x\":");
        float y = GetFloat(line.Substring(i), "\"y\":");
        float z = GetFloat(line.Substring(i), "\"z\":");
        return new Vector3(x, y, z);
    }

    static int GetNestedInt(string line, string obj, string field)
    {
        int i = line.IndexOf("\"" + obj + "\"", StringComparison.Ordinal);
        if (i < 0) return 0;
        string slice = line.Substring(i, Math.Min(120, line.Length - i));
        return Mathf.RoundToInt(GetFloat(slice, "\"" + field + "\":"));
    }

    static float Horiz(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
