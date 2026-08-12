using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>Self-test for StreamingSurvivalWorldRegistry. Logs [PASS]/[FAIL].</summary>
public sealed class StreamingSurvivalWorldRegistrySelfTest : MonoBehaviour
{
    public sealed class Result
    {
        public bool Ok;
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Fails = new List<string>();
    }

    public static Result Run()
    {
        var r = new Result { Ok = true };
        var reg = StreamingSurvivalWorldRegistry.Instance;
        if (reg == null)
        {
            var go = new GameObject(nameof(StreamingSurvivalWorldRegistry));
            reg = go.AddComponent<StreamingSurvivalWorldRegistry>();
        }
        reg.Rebuild();

        CheckFound(r, reg, StreamingSurvivalWorldRegistry.ObjType.Home, "home");
        CheckFound(r, reg, StreamingSurvivalWorldRegistry.ObjType.WaterSource, "water_source");
        CheckFound(r, reg, StreamingSurvivalWorldRegistry.ObjType.Tree, "tree");
        CheckFound(r, reg, StreamingSurvivalWorldRegistry.ObjType.Stone, "stone");
        CheckFound(r, reg, StreamingSurvivalWorldRegistry.ObjType.Sheep, "sheep");
        CheckFound(r, reg, StreamingSurvivalWorldRegistry.ObjType.CampfireSlot, "campfire_slot");

        foreach (var o in reg.All)
        {
            if (float.IsNaN(o.Position.x) || float.IsNaN(o.Position.y) || float.IsNaN(o.Position.z))
                Fail(r, $"{o.Id} has NaN position");
            else if (o.Position == Vector3.zero && o.Type != StreamingSurvivalWorldRegistry.ObjType.Spawn)
                Fail(r, $"{o.Id} zero-by-default position");
            else if (!o.Active)
                Fail(r, $"{o.Id} inactive");
        }

        Vector3 from = StreamingSurvivalController.Instance != null
            ? StreamingSurvivalController.Instance.FollowerSpawnWorld
            : Vector3.zero;

        CheckNearest(r, reg.GetNearestWater(from), StreamingSurvivalWorldRegistry.ObjType.WaterSource, "water", from);
        CheckNearest(r, reg.GetNearestTree(from), StreamingSurvivalWorldRegistry.ObjType.Tree, "tree", from);
        CheckNearest(r, reg.GetNearestStone(from), StreamingSurvivalWorldRegistry.ObjType.Stone, "stone", from);
        CheckNearest(r, reg.GetNearestSheep(from), StreamingSurvivalWorldRegistry.ObjType.Sheep, "sheep", from);

        // Water must not be accidentally registered next to home.
        const float MinWaterHomeDistance = 5.0f;
        var home = reg.GetHome();
        if (home != null)
        {
            foreach (var w in reg.GetByType(StreamingSurvivalWorldRegistry.ObjType.WaterSource))
            {
                float d = Vector3.Distance(
                    new Vector3(home.InteractionPosition.x, 0f, home.InteractionPosition.z),
                    new Vector3(w.Position.x, 0f, w.Position.z));
                Pass(r,
                    $"water {w.Id} dist_to_home={d:F1} pos=({w.Position.x:F1},{w.Position.z:F1}) " +
                    $"interact=({w.InteractionPosition.x:F1},{w.InteractionPosition.z:F1})");
                if (d < MinWaterHomeDistance)
                    Fail(r, $"water_source {w.Id} too close to home d={d:F1} < {MinWaterHomeDistance}");
            }
            Pass(r,
                $"home interaction=({home.InteractionPosition.x:F1},{home.InteractionPosition.z:F1}) " +
                $"center=({home.Position.x:F1},{home.Position.z:F1})");
        }

        foreach (var line in r.Lines)
            Debug.Log($"[SSWorldRegistrySelfTest] {line}");
        Debug.Log($"[SSTest] WORLD_REGISTRY overall={(r.Ok ? "PASS" : "FAIL")} fails={r.Fails.Count}");
        return r;
    }

    static void CheckFound(Result r, StreamingSurvivalWorldRegistry reg,
        StreamingSurvivalWorldRegistry.ObjType type, string label)
    {
        var list = reg.GetByType(type);
        if (list == null || list.Count == 0)
        {
            Fail(r, $"no {label} resources registered");
            return;
        }
        var o = list[0];
        Pass(r, $"{label} {o.Id} found at ({o.Position.x:F1},{o.Position.y:F1},{o.Position.z:F1})");
    }

    static void CheckNearest(Result r, StreamingSurvivalWorldRegistry.WorldObject o,
        StreamingSurvivalWorldRegistry.ObjType expect, string label, Vector3 from)
    {
        if (o == null)
        {
            Fail(r, $"GetNearest{label} returned null");
            return;
        }
        if (o.Type != expect)
        {
            Fail(r, $"GetNearest{label} type={o.Type} expected={expect}");
            return;
        }
        Pass(r, $"nearest {label} from debug_user = {o.Id} at ({o.Position.x:F1},{o.Position.y:F1},{o.Position.z:F1})");
    }

    static void Pass(Result r, string msg)
    {
        r.Lines.Add($"[PASS] {msg}");
    }

    static void Fail(Result r, string msg)
    {
        r.Ok = false;
        r.Fails.Add(msg);
        r.Lines.Add($"[FAIL] {msg}");
    }

    public static string ToJson(Result r)
    {
        var sb = new StringBuilder();
        sb.Append("{\"ok\":").Append(r.Ok ? "true" : "false").Append(",\"fails\":[");
        for (int i = 0; i < r.Fails.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Escape(r.Fails[i])).Append('"');
        }
        sb.Append("],\"lines\":[");
        for (int i = 0; i < r.Lines.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Escape(r.Lines[i])).Append('"');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static string Escape(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
