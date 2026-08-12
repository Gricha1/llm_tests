using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-action checkers for Streaming Survival command runtime QA.
/// Used by ScenarioChecker / CommandRuntimeTester — not ML-Agents.
/// </summary>
public static class StreamingSurvivalCommandCheckers
{
    public sealed class Result
    {
        public bool Ok = true;
        public string Checker = "";
        public string Reason = "ok";
        public readonly List<string> Notes = new List<string>();
    }

    public static Result CheckAction(
        string action,
        string expectedTargetType,
        string actualTargetType,
        string actualTargetId,
        float finalDistance,
        int resourceDeltaWater,
        int resourceDeltaWood,
        int resourceDeltaFood,
        int expectedCount,
        bool hadIllegalTeleport,
        bool hadControlledTeleportInGameplay)
    {
        action = (action ?? "").ToLowerInvariant();
        expectedCount = expectedCount < 1 ? 1 : expectedCount;
        var r = new Result { Checker = "check_" + action };

        if (hadIllegalTeleport)
        {
            r.Ok = false;
            r.Reason = "illegal_teleport";
            return r;
        }
        if (hadControlledTeleportInGameplay
            && (StreamingSurvivalResourceGuard.IsMovementOnlyAction(action)
                || StreamingSurvivalResourceGuard.IsResourceAction(action)))
        {
            r.Ok = false;
            r.Reason = "controlled_teleport_in_gameplay";
            return r;
        }

        string wantType = string.IsNullOrEmpty(expectedTargetType)
            ? StreamingSurvivalResourceGuard.ExpectedTargetType(action)
            : expectedTargetType;
        float radius = StreamingSurvivalResourceGuard.RadiusForAction(action);

        switch (action)
        {
            case "go_to_water":
                return CheckNearTarget(r, wantType, actualTargetType, finalDistance, radius,
                    expectResourceChange: false, resourceDeltaWater, 0);
            case "collect_water":
                return CheckCollect(r, "water_source", actualTargetType, finalDistance, radius,
                    resourceDeltaWater, expectedCount);
            case "go_home":
            case "go_to_base":
                return CheckNearTarget(r, "home_interaction_point", actualTargetType, finalDistance, 3.2f,
                    expectResourceChange: false, 0, 0);
            case "go_to_campfire":
                return CheckNearTarget(r, "campfire_slot", actualTargetType, finalDistance, 3.2f,
                    expectResourceChange: false, 0, 0);
            case "collect_wood":
                return CheckCollect(r, "tree", actualTargetType, finalDistance, radius,
                    resourceDeltaWood, expectedCount);
            case "go_to_tree":
                return CheckNearTarget(r, "tree", actualTargetType, finalDistance, radius,
                    expectResourceChange: false, 0, 0);
            case "go_to_sheep":
                return CheckNearTarget(r, "sheep", actualTargetType, finalDistance, radius,
                    expectResourceChange: false, 0, 0);
            case "build_campfire":
                if (!string.IsNullOrEmpty(wantType) && actualTargetType != wantType
                    && actualTargetType != "campfire_slot" && actualTargetType != "home_interaction_point")
                {
                    r.Ok = false;
                    r.Reason = $"bad_target_type want=campfire_slot got={actualTargetType}";
                }
                return r;
            case "idle":
                // Stay near spawn/home — soft check only.
                if (finalDistance > 12f)
                {
                    r.Ok = false;
                    r.Reason = $"idle_too_far d={finalDistance:F1}";
                }
                return r;
            case "patrol":
            case "walk_circle":
            case "circle":
            case "walk_forward":
            case "walk_back":
            case "spin_in_place":
                return r; // pattern checked by trajectory checker
            default:
                r.Notes.Add("no_specific_checker");
                return r;
        }
    }

    static Result CheckNearTarget(
        Result r,
        string wantType,
        string actualType,
        float dist,
        float radius,
        bool expectResourceChange,
        int delta,
        int expectedDelta)
    {
        if (!string.IsNullOrEmpty(wantType) && !string.IsNullOrEmpty(actualType) && actualType != wantType)
        {
            r.Ok = false;
            r.Reason = $"bad_target_type want={wantType} got={actualType}";
            return r;
        }
        float lim = radius > 0f ? radius + 0.75f : 3f;
        if (dist > lim && dist >= 0f)
        {
            r.Ok = false;
            r.Reason = $"too_far_from_target d={dist:F2} lim={lim:F2}";
            return r;
        }
        if (!expectResourceChange && delta != 0)
        {
            r.Ok = false;
            r.Reason = $"unexpected_resource_delta={delta}";
            return r;
        }
        if (expectResourceChange && delta < expectedDelta)
        {
            r.Ok = false;
            r.Reason = $"resource_delta={delta} want>={expectedDelta}";
        }
        return r;
    }

    static Result CheckCollect(
        Result r,
        string wantType,
        string actualType,
        float dist,
        float radius,
        int delta,
        int expectedCount)
    {
        return CheckNearTarget(r, wantType, actualType, dist, radius,
            expectResourceChange: true, delta, expectedCount);
    }
}
