using UnityEngine;

/// <summary>
/// Resource collection may only complete next to the WorldRegistry target.
/// Training AI / ML-Agents paths are not used here.
/// </summary>
public static class StreamingSurvivalResourceGuard
{
    public const float WaterInteractionRadius = 2.0f;
    public const float WoodInteractionRadius = 2.0f;
    public const float FoodInteractionRadius = 2.0f;
    public const float SheepInteractionRadius = 2.0f;

    public enum CollectState
    {
        None,
        MovingToResource,
        ReachedResource,
        WorkingAtResource,
        ResourceAdded,
        Denied,
    }

    public sealed class GuardResult
    {
        public bool Ok;
        public string Reason = "";
        public float Distance = -1f;
        public float Radius = 2f;
        public string TargetType = "";
        public string TargetId = "";
    }

    public static float RadiusForAction(string action)
    {
        switch ((action ?? "").ToLowerInvariant())
        {
            case "collect_water":
            case "go_to_water": return WaterInteractionRadius;
            case "collect_wood":
            case "go_to_tree": return WoodInteractionRadius;
            case "collect_food":
            case "kill_sheep":
            case "go_to_sheep": return Mathf.Max(FoodInteractionRadius, SheepInteractionRadius);
            case "go_home":
            case "go_to_base":
            case "go_to_campfire":
            case "build_campfire": return 2.8f;
            case "collect_stone": return 0f; // disabled
            default: return 2f;
        }
    }

    public static bool IsResourceAction(string action)
    {
        switch ((action ?? "").ToLowerInvariant())
        {
            case "collect_water":
            case "collect_wood":
            case "collect_food":
            case "kill_sheep":
            case "collect_stone":
                return true;
            default:
                return false;
        }
    }

    public static string ExpectedTargetType(string action)
    {
        switch ((action ?? "").ToLowerInvariant())
        {
            case "collect_water":
            case "go_to_water": return "water_source";
            case "collect_wood":
            case "go_to_tree": return "tree";
            case "collect_stone": return "stone";
            case "collect_food":
            case "kill_sheep":
            case "go_to_sheep": return "sheep";
            case "go_home":
            case "go_to_base": return "home_interaction_point";
            case "go_to_campfire":
            case "build_campfire": return "campfire_slot";
            default: return "";
        }
    }

    public static bool IsMovementOnlyAction(string action)
    {
        switch ((action ?? "").ToLowerInvariant())
        {
            case "go_home":
            case "go_to_base":
            case "go_to_water":
            case "go_to_campfire":
            case "go_to_tree":
            case "go_to_sheep":
                return true;
            default:
                return false;
        }
    }

    public static GuardResult CanComplete(
        string action,
        string targetType,
        string targetId,
        Vector3 characterPos,
        Vector3 targetPos,
        bool reachedResource,
        bool workStartedNearTarget,
        Vector3? objectAnchor = null)
    {
        var r = new GuardResult
        {
            TargetType = targetType ?? "",
            TargetId = targetId ?? "",
            Radius = RadiusForAction(action),
        };
        action = (action ?? "").ToLowerInvariant();

        if (action == "collect_stone")
        {
            r.Ok = false;
            r.Reason = "stone_disabled";
            return r;
        }

        if (!IsResourceAction(action))
        {
            r.Ok = false;
            r.Reason = "not_resource_action";
            return r;
        }

        string expect = ExpectedTargetType(action);
        if (string.IsNullOrEmpty(targetType) || targetType != expect)
        {
            r.Ok = false;
            r.Reason = $"bad_target_type want={expect} got={targetType}";
            return r;
        }

        if (string.IsNullOrEmpty(targetId))
        {
            r.Ok = false;
            r.Reason = "missing_target_id";
            return r;
        }

        // Prefer the locked object anchor from when the character selected the target.
        // Re-querying GetNearestTree can return a different trunk with the same live id.
        Vector3 objectPos = objectAnchor ?? targetPos;
        bool found = objectAnchor.HasValue;
        var reg = StreamingSurvivalWorldRegistry.Instance;
        if (!found && reg != null)
        {
            foreach (var o in reg.All)
            {
                if (o == null || !o.Active) continue;
                if (o.Id == targetId)
                {
                    found = true;
                    objectPos = o.Position;
                    break;
                }
            }
            if (!found)
            {
                r.Ok = false;
                r.Reason = $"target_id_not_in_registry id={targetId}";
                return r;
            }
        }

        float standToObjX = targetPos.x - objectPos.x;
        float standToObjZ = targetPos.z - objectPos.z;
        float standToObj = Mathf.Sqrt(standToObjX * standToObjX + standToObjZ * standToObjZ);
        Vector3 interaction = targetPos;
        // Water stand is on the south rim of the pond (pond side of fence), not west pocket.
        bool waterOffsetStand = action == "collect_water"
            && standToObj > 0.05f
            && standToObj <= 8.0f;
        if (!waterOffsetStand && (standToObj < 0.05f || standToObj > r.Radius + 0.75f))
            interaction = objectPos;

        float idx = characterPos.x - interaction.x;
        float idz = characterPos.z - interaction.z;
        r.Distance = Mathf.Sqrt(idx * idx + idz * idz);
        if (r.Distance > r.Radius)
        {
            r.Ok = false;
            r.Reason = $"too_far_from_target distance={r.Distance:F1}";
            return r;
        }

        // Reject forest/gate / west-of-fence credits — must be at pond south rim.
        if (action == "collect_water" && (characterPos.z < 27.5f || characterPos.x < 15.5f))
        {
            r.Ok = false;
            r.Reason =
                $"water_credit_not_at_pond x={characterPos.x:F1} z={characterPos.z:F1}";
            return r;
        }

        float ox = characterPos.x - objectPos.x;
        float oz = characterPos.z - objectPos.z;
        float toObj = Mathf.Sqrt(ox * ox + oz * oz);
        float maxObj = r.Radius + 1.5f;
        if (waterOffsetStand)
            maxObj = 8.0f;
        // Solid colliders keep characters off trunk centers; stand+arrive can be ~2–3m.
        if (toObj > maxObj)
        {
            r.Ok = false;
            r.Reason = $"too_far_from_object distance={toObj:F1}";
            return r;
        }

        if (!reachedResource)
        {
            r.Ok = false;
            r.Reason = "not_reached_resource";
            return r;
        }

        if (!workStartedNearTarget)
        {
            r.Ok = false;
            r.Reason = "work_not_started_at_target";
            return r;
        }

        r.Ok = true;
        r.Reason = "ok";
        return r;
    }
}
