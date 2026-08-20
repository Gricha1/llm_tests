using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Typed world objects for Streaming Survival tests and follower targeting.
/// Does not modify the Training AI Forest Survival tab / training heroes.
/// </summary>
public sealed class StreamingSurvivalWorldRegistry : MonoBehaviour
{
    public static StreamingSurvivalWorldRegistry Instance { get; private set; }

    public enum ObjType
    {
        Home,
        WaterSource,
        Tree,
        Stone,
        Sheep,
        CampfireSlot,
        Spawn
    }

    public sealed class WorldObject
    {
        public string Id;
        public ObjType Type;
        public Vector3 Position;
        /// <summary>Approach / stand point (may differ from object center).</summary>
        public Vector3 InteractionPosition;
        public Transform Transform;
        public bool Active;
    }

    readonly List<WorldObject> _objects = new List<WorldObject>(64);
    readonly Dictionary<ObjType, List<WorldObject>> _byType =
        new Dictionary<ObjType, List<WorldObject>>();
    List<FenceMarker> _cachedFences = new List<FenceMarker>(128);

    public IReadOnlyList<WorldObject> All => _objects;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.IsStreamingSurvivalMode)
            return;
        if (Instance != null)
            return;
        var host = StreamingSurvivalController.Instance != null
            ? StreamingSurvivalController.Instance.gameObject
            : new GameObject(nameof(StreamingSurvivalWorldRegistry));
        if (host.GetComponent<StreamingSurvivalWorldRegistry>() == null)
            host.AddComponent<StreamingSurvivalWorldRegistry>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        Rebuild();
    }

    void Start() => Rebuild();

    public void Rebuild()
    {
        _objects.Clear();
        _byType.Clear();
        foreach (ObjType t in Enum.GetValues(typeof(ObjType)))
            _byType[t] = new List<WorldObject>();

        var ctrl = StreamingSurvivalController.Instance;
        if (ctrl != null)
        {
            Register("spawn_0", ObjType.Spawn, ctrl.FollowerSpawnWorld, null);
            Vector3 house = ctrl.HouseWorld;
            // Scene: door faces south; stone pad + campfire are SOUTH of cabin center.
            // Old +(0.4, +0.6) put the fire north of the house on charts.
            // South door pad, close to the wall so the agent stands at the door
            // (old z-1.7 left them south of the rocks, fire never placed).
            Vector3 campfire = house + new Vector3(-0.2f, 0f, -0.65f);
            Vector3 homeInteract = campfire + new Vector3(0.7f, 0f, 0.05f);
            homeInteract.y = house.y;
            campfire.y = house.y;
            Register("home_0", ObjType.Home, house, null, homeInteract);
            Register("campfire_slot_0", ObjType.CampfireSlot, campfire, null);
            Debug.Log(
                $"[SSWorldRegistry] home at ({house.x:F1},{house.z:F1}) " +
                $"interaction=({homeInteract.x:F1},{homeInteract.z:F1}) " +
                $"campfire=({campfire.x:F1},{campfire.z:F1})");
        }

        RegisterWaters();
        RegisterTrees();
        RegisterStones();
        RegisterSheep();
        // Snapshot fence colliders for QA charts (full yard + pond ring).
        _cachedFences = CollectFenceMarkers();
        // Do NOT disable fence/gate colliders for water — walk the real passage
        // (east of sheep, around rocks). Disabling was a hack that let agents
        // clip through the mid-yard door_* wall on charts and in gameplay.
        Debug.Log(
            $"[SSWorldRegistry] rebuilt count={_objects.Count} fences={_cachedFences.Count}");
    }

    /// <summary>
    /// Legacy no-op. Previously disabled fence colliders so followers could
    /// reach the pond; that made agents walk through walls. Kept only so old
    /// call sites / logs stay harmless if reintroduced by mistake.
    /// </summary>
    void OpenWaterApproachCorridor()
    {
        // Intentionally empty — real geometry stays solid.
    }

    void RegisterWaters()
    {
        int i = 0;
        // Only the real northern pond (GoalWater3). GoalWater2 sits south and
        // causes "collecting water" while standing in the forest corridor.
        string[] names = { "GoalWater3" };
        foreach (string n in names)
        {
            var go = GameObject.Find(n);
            if (go == null) continue;
            Vector3 p = go.transform.position;
            if (p.z < 28f) continue;
            Register($"water_{i}", ObjType.WaterSource, p, go.transform);
            i++;
        }

        var root = TrainingEnvSpace.PresentationRoot;
        WaterSource[] sources = root != null
            ? root.GetComponentsInChildren<WaterSource>(true)
            : FindObjectsByType<WaterSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var ws in sources)
        {
            if (ws == null) continue;
            Vector3 p = ws.transform.position;
            // Real pond body only (skip southern corridor markers).
            if (p.z < 28f || p.z > 40f) continue;
            if (p.x < -6f || p.x > 28f) continue;
            Register($"water_{i}", ObjType.WaterSource, p, ws.transform);
            i++;
        }

        // Ensure at least one northern water marker for tests
        if (i == 0)
        {
            var g3 = GameObject.Find("GoalWater3");
            Vector3 p = g3 != null
                ? g3.transform.position
                : new Vector3(13f, 0f, 26f);
            Register("water_0", ObjType.WaterSource, p, g3 != null ? g3.transform : null);
        }
    }

    void RegisterTrees()
    {
        var spawner = TrainingEnvSpace.FindInPresentation<TreeSpawner>()
            ?? FindFirstObjectByType<TreeSpawner>();
        if (spawner == null) return;
        // TreeSpawner exposes nearest lookup; scan children tagged as trees if possible
        int i = 0;
        foreach (Transform t in spawner.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || t == spawner.transform) continue;
            string n = t.name.ToLowerInvariant();
            if (!(n.Contains("tree") || n.Contains("pine") || n.Contains("fir")))
                continue;
            Vector3 p = t.position;
            if (p.z > 28.5f) continue;
            if (!StreamingSurvivalController.IsPlayableGround(p) && p.z < 0f) continue;
            Register($"tree_{i}", ObjType.Tree, p, t);
            i++;
            if (i >= 40) break;
        }
    }

    void RegisterStones()
    {
        int i = 0;
        var all = FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var t in all)
        {
            if (t == null || t.parent == null) continue;
            string n = t.name;
            string low = n.ToLowerInvariant();
            if (!(low.Contains("stone") || low.StartsWith("rock") || low.Contains("rock_")))
                continue;
            if (low.Contains("wall") || low.Contains("sword") || low.Contains("door"))
                continue;
            Vector3 p = t.position;
            if (p.z < 2f || p.z > 28.5f) continue;
            if (Mathf.Abs(p.x) > 40f) continue;
            // skip tiny/zero
            if (p.sqrMagnitude < 0.01f) continue;
            Register($"stone_{i}", ObjType.Stone, p, t);
            i++;
            if (i >= 24) break;
        }

        // Ensure at least one stone slot near home for tests if scene has none in bounds
        if (i == 0 && StreamingSurvivalController.Instance != null)
        {
            Vector3 h = StreamingSurvivalController.Instance.HouseWorld;
            Register("stone_0", ObjType.Stone, h + new Vector3(4.5f, 0f, -2f), null);
        }
    }

    void RegisterSheep()
    {
        int i = 0;
        var root = TrainingEnvSpace.PresentationRoot;
        var wanderers = root != null
            ? root.GetComponentsInChildren<SheepWander>(true)
            : FindObjectsByType<SheepWander>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (wanderers == null) return;
        foreach (var w in wanderers)
        {
            if (w == null || !w.gameObject.activeInHierarchy) continue;
            if (w.GetComponent<ViewerSimpleAgent>() != null) continue;
            if (w.GetComponent<SheepSpawner>() != null) continue;
            Register($"sheep_{i}", ObjType.Sheep, w.transform.position, w.transform);
            i++;
            if (i >= 20) break;
        }
    }

    void Register(string id, ObjType type, Vector3 pos, Transform tf, Vector3? interaction = null)
    {
        Vector3 interact = interaction ?? pos;
        // Water stand: south rim of GoalWater3 on the POND side of the mid fence.
        // Old stand (~12,27) was WEST of the fence (looks like "stop by a tree").
        // Pond body ≈ (21,33) → stand ≈ (18–20, 29–30).
        if (type == ObjType.WaterSource && !interaction.HasValue)
        {
            interact = new Vector3(
                Mathf.Clamp(pos.x - 2.0f, 17.0f, 20.5f),
                pos.y,
                Mathf.Clamp(pos.z - 3.5f, 28.5f, 31.0f));
        }
        var wo = new WorldObject
        {
            Id = id,
            Type = type,
            Position = pos,
            InteractionPosition = interact,
            Transform = tf,
            Active = tf == null || (tf.gameObject != null && tf.gameObject.activeInHierarchy),
        };
        _objects.Add(wo);
        _byType[type].Add(wo);
        if (type == ObjType.WaterSource)
        {
            var home = GetByType(ObjType.Home).Count > 0 ? GetByType(ObjType.Home)[0] : null;
            if (home != null)
            {
                float d = Horiz(home.InteractionPosition, pos);
                Debug.Log(
                    $"[SSWorldRegistry] water id={id} name={(tf != null ? tf.name : "?")} " +
                    $"pos=({pos.x:F1},{pos.z:F1}) interact=({interact.x:F1},{interact.z:F1}) " +
                    $"dist_to_home={d:F1}");
            }
        }
    }

    public List<WorldObject> GetByType(ObjType type)
    {
        return _byType.TryGetValue(type, out var list) ? list : new List<WorldObject>();
    }

    public WorldObject GetNearest(ObjType type, Vector3 from)
    {
        WorldObject best = null;
        float bestD = float.MaxValue;
        foreach (var o in GetByType(type))
        {
            if (!o.Active) continue;
            float d = Horiz(from, o.Position);
            if (d < bestD)
            {
                bestD = d;
                best = o;
            }
        }
        return best;
    }

    public WorldObject GetNearestWater(Vector3 from)
    {
        // Prefer northern water (credit zone). Never pick GoalWater1 / spawn-adjacent.
        WorldObject best = null;
        float bestD = float.MaxValue;
        foreach (var o in GetByType(ObjType.WaterSource))
        {
            if (!o.Active) continue;
            if (o.Position.z < 28f) continue;
            float d = Horiz(from, o.Position);
            // Bias toward GoalWater3 / water_0
            if (o.Id != null && o.Id.Contains("0")) d *= 0.85f;
            if (d < bestD)
            {
                bestD = d;
                best = o;
            }
        }
        if (best != null) return best;
        // Never fall back to southern/spawn water markers — that silently
        // breaks collect_water (credit zone is z>=24.5 at the pond).
        return null;
    }

    public WorldObject GetNearestTree(Vector3 from)
    {
        var spawner = TrainingEnvSpace.FindInPresentation<TreeSpawner>()
            ?? FindFirstObjectByType<TreeSpawner>();
        if (spawner != null
            && spawner.TryGetNearestAliveTree(from, out GameObject tree, out _))
        {
            if (tree != null && tree.transform.position.z <= 28.5f)
            {
                return new WorldObject
                {
                    Id = "tree_live",
                    Type = ObjType.Tree,
                    Position = tree.transform.position,
                    InteractionPosition = tree.transform.position,
                    Transform = tree.transform,
                    Active = true,
                };
            }
        }
        return GetNearest(ObjType.Tree, from);
    }
    public WorldObject GetNearestStone(Vector3 from) => GetNearest(ObjType.Stone, from);
    public WorldObject GetNearestSheep(Vector3 from)
    {
        var spawner = TrainingEnvSpace.FindInPresentation<SheepSpawner>()
            ?? FindFirstObjectByType<SheepSpawner>();
        if (spawner != null
            && spawner.TryGetNearestAliveSheep(from, out GameObject sheep, out Vector3 pos)
            && sheep != null
            && sheep.GetComponent<SheepSpawner>() == null
            && sheep.GetComponentInChildren<SheepWander>(true) != null)
        {
            return new WorldObject
            {
                Id = "sheep_live",
                Type = ObjType.Sheep,
                Position = pos,
                InteractionPosition = pos,
                Transform = sheep.transform,
                Active = true,
            };
        }
        return null;
    }
    public WorldObject GetHome() => GetByType(ObjType.Home).Count > 0 ? GetByType(ObjType.Home)[0] : null;

    public static string TypeName(ObjType t)
    {
        switch (t)
        {
            case ObjType.Home: return "home";
            case ObjType.WaterSource: return "water_source";
            case ObjType.Tree: return "tree";
            case ObjType.Stone: return "stone";
            case ObjType.Sheep: return "sheep";
            case ObjType.CampfireSlot: return "campfire_slot";
            case ObjType.Spawn: return "spawn";
            default: return t.ToString().ToLowerInvariant();
        }
    }

    /// <summary>
    /// JSON map for QA charts: objects + fence/gate colliders (center + xz size).
    /// </summary>
    public string ExportWorldMapJson()
    {
        Rebuild();
        var sb = new System.Text.StringBuilder(4096);
        sb.Append("{\"source\":\"StreamingSurvivalWorldRegistry\",\"objects\":[");
        for (int i = 0; i < _objects.Count; i++)
        {
            var o = _objects[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            sb.Append("\"id\":\"").Append(o.Id).Append("\",");
            sb.Append("\"type\":\"").Append(TypeName(o.Type)).Append("\",");
            sb.Append("\"x\":").Append(o.Position.x.ToString("F3")).Append(',');
            sb.Append("\"y\":").Append(o.Position.y.ToString("F3")).Append(',');
            sb.Append("\"z\":").Append(o.Position.z.ToString("F3")).Append(',');
            sb.Append("\"interaction_x\":").Append(o.InteractionPosition.x.ToString("F3")).Append(',');
            sb.Append("\"interaction_z\":").Append(o.InteractionPosition.z.ToString("F3"));
            sb.Append('}');
        }
        sb.Append("],\"checkpoints\":[");
        string[] goalNames = { "GoalWater1", "GoalWater2", "GoalWater3" };
        bool firstCp = true;
        for (int g = 0; g < goalNames.Length; g++)
        {
            var go = GameObject.Find(goalNames[g]);
            if (go == null) continue;
            Vector3 gp = go.transform.position;
            if (!firstCp) sb.Append(',');
            firstCp = false;
            sb.Append('{');
            sb.Append("\"id\":\"").Append(goalNames[g]).Append("\",");
            sb.Append("\"type\":\"goal_water\",");
            sb.Append("\"x\":").Append(gp.x.ToString("F3")).Append(',');
            sb.Append("\"z\":").Append(gp.z.ToString("F3"));
            sb.Append('}');
        }
        sb.Append("],\"fences\":[");
        var fences = _cachedFences != null && _cachedFences.Count > 0
            ? _cachedFences
            : CollectFenceMarkers();
        for (int i = 0; i < fences.Count; i++)
        {
            var f = fences[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            sb.Append("\"name\":\"").Append(f.Name.Replace("\"", "'")).Append("\",");
            sb.Append("\"x\":").Append(f.X.ToString("F3")).Append(',');
            sb.Append("\"z\":").Append(f.Z.ToString("F3")).Append(',');
            sb.Append("\"size_x\":").Append(f.SizeX.ToString("F3")).Append(',');
            sb.Append("\"size_z\":").Append(f.SizeZ.ToString("F3"));
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    sealed class FenceMarker
    {
        public string Name;
        public float X, Z, SizeX, SizeZ;
    }

    List<FenceMarker> CollectFenceMarkers()
    {
        var outList = new List<FenceMarker>(128);
        var root = TrainingEnvSpace.PresentationRoot;
        // Only ACTIVE objects — inactive door_* (gap planks) exist in ForestScene with
        // m_IsActive=0 so the sheep→pond opening stays open. Including them (includeInactive)
        // painted 4 fake vertical blocks and a tiny gap on QA charts vs the real 3D view.
        Collider[] cols = root != null
            ? root.GetComponentsInChildren<Collider>(false)
            : FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null || !c.enabled || c is TerrainCollider) continue;
            if (!c.gameObject.activeInHierarchy) continue;
            if (c.GetComponentInParent<CharacterController>() != null) continue;
            if (c.GetComponentInParent<StreamingSurvivalPlayer>() != null) continue;
            string n = c.gameObject.name.ToLowerInvariant();
            string pn = c.transform.parent != null
                ? c.transform.parent.name.ToLowerInvariant()
                : "";
            bool named = n.Contains("fence") || pn.Contains("fence")
                || n.Contains("gate") || pn.Contains("gate")
                || n.Contains("woodfence") || pn.Contains("woodfence")
                || n.StartsWith("door_") || pn.StartsWith("door_")
                || n.Contains("railing") || pn.Contains("railing");
            // Tall thin barriers without "fence" in the name (house enclosure meshes).
            Vector3 size = c.bounds.size;
            bool lookLikeFoliage = n.Contains("tree") || pn.Contains("tree")
                || n.Contains("bush") || pn.Contains("bush")
                || n.Contains("rock") || pn.Contains("rock")
                || n.Contains("stone") || pn.Contains("stone")
                || n.Contains("flower") || n.Contains("grass")
                || n.Contains("log") || n.Contains("stump")
                || n.Contains("sheep") || n.Contains("zombie");
            bool tallThin = !lookLikeFoliage
                && size.y > 1.0f && size.x < 5.5f && size.z < 5.5f
                && size.y > Mathf.Max(size.x, size.z) * 0.85f;
            if (!named && !tallThin) continue;
            // Skip huge ground-like planes.
            if (size.x > 12f && size.z > 12f && size.y < 3.5f) continue;
            // Must be visible in 3D — charts must match what CamA shows.
            var rend = c.GetComponent<Renderer>();
            if (rend == null) rend = c.GetComponentInChildren<Renderer>(false);
            if (rend == null || !rend.enabled || !rend.gameObject.activeInHierarchy)
                continue;
            Vector3 p = c.bounds.center;
            // Prefer visual mesh bounds on XZ so plank width matches the drawn mesh.
            Bounds rb = rend.bounds;
            if (rb.size.x > 0.01f && rb.size.z > 0.01f)
            {
                size.x = rb.size.x;
                size.z = rb.size.z;
                p = new Vector3(rb.center.x, p.y, rb.center.z);
            }
            // Full playable yard + pond rim (include near-spawn south fence).
            if (p.x < -18f || p.x > 35f || p.z < -2f || p.z > 45f) continue;
            outList.Add(new FenceMarker
            {
                Name = c.gameObject.name,
                X = p.x,
                Z = p.z,
                SizeX = Mathf.Max(size.x, 0.35f),
                SizeZ = Mathf.Max(size.z, 0.35f),
            });
        }

        // Second pass: visible named fence meshes (in case collider was removed).
        Renderer[] rends = root != null
            ? root.GetComponentsInChildren<Renderer>(false)
            : FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < rends.Length; i++)
        {
            var rend = rends[i];
            if (rend == null || !rend.enabled || !rend.gameObject.activeInHierarchy) continue;
            string n = rend.gameObject.name.ToLowerInvariant();
            string pn = rend.transform.parent != null
                ? rend.transform.parent.name.ToLowerInvariant()
                : "";
            bool named = n.Contains("fence") || pn.Contains("fence")
                || n.Contains("woodfence") || pn.Contains("woodfence")
                || n.StartsWith("door_") || pn.StartsWith("door_")
                || n.Contains("railing") || pn.Contains("railing");
            if (!named) continue;
            Bounds rb = rend.bounds;
            Vector3 p = rb.center;
            if (p.x < -18f || p.x > 35f || p.z < -2f || p.z > 45f) continue;
            float sx = Mathf.Max(rb.size.x, 0.35f);
            float sz = Mathf.Max(rb.size.z, 0.35f);
            bool dup = false;
            for (int j = 0; j < outList.Count; j++)
            {
                if (Mathf.Abs(outList[j].X - p.x) < 0.15f
                    && Mathf.Abs(outList[j].Z - p.z) < 0.15f)
                {
                    dup = true;
                    break;
                }
            }
            if (dup) continue;
            outList.Add(new FenceMarker
            {
                Name = rend.gameObject.name,
                X = p.x,
                Z = p.z,
                SizeX = sx,
                SizeZ = sz,
            });
        }
        return outList;
    }

    static float Horiz(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
