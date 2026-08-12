using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Персонаж фолловера: species + Character Behavior DSL (circle/wander/follow/…).
/// Не на слое Sheep — Джек/Лили не едят.
/// </summary>
public sealed class ViewerSimpleAgent : MonoBehaviour
{
    const string PrefabResource = "ViewerSheep";
    const float LabelHeight = 1.35f;
    const float SpawnScale = 1.35f;

    static readonly Dictionary<string, ViewerSimpleAgent> ByUser =
        new Dictionary<string, ViewerSimpleAgent>();

    [SerializeField] string username = "";
    [SerializeField] string species = "sheep";
    [SerializeField] int level = 1;
    [SerializeField] string behaviorName = "";
    [SerializeField] float moveSpeed = 1.4f;
    [SerializeField] float wanderRadius = 5f;

    CharacterController _cc;
    TextMesh _label;
    Transform _labelTf;
    Vector3 _home;
    Vector3 _target;
    float _retargetAt;
    float _circleAngle;
    string _rawJson = "";
    string _activeAction = "wander";
    string _activeTarget = "spawn_point";
    float _actionRadius = 3f;
    float _actionSpeed = 1f;
    float _keepDistance = 4f;
    readonly List<BehaviorRule> _rules = new List<BehaviorRule>();

    struct BehaviorRule
    {
        public int Priority;
        public string CondType;
        public string CondTarget;
        public float CondDistance;
        public float CondValue;
        public string ActionType;
        public string ActionTarget;
        public float Radius;
        public float Speed;
        public float Distance;
    }

    public string Username => username;

    public static ViewerSimpleAgent FindOrSpawn(string user)
    {
        string key = (user ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
            key = "viewer";

        if (ByUser.TryGetValue(key, out var existing) && existing != null)
            return existing;

        GameObject go = SpawnVisual(key);
        var root = TrainingEnvSpace.PresentationRoot;
        if (root != null)
            go.transform.SetParent(root, true);

        Vector3 pos = PickSpawnNearSheep(root);
        go.transform.position = pos;
        go.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        go.transform.localScale = Vector3.one * SpawnScale;

        var agent = go.GetComponent<ViewerSimpleAgent>();
        if (agent == null)
            agent = go.AddComponent<ViewerSimpleAgent>();
        agent.Setup(user, pos);

        var behavior = go.GetComponent<ViewerAgentBehavior>();
        if (behavior == null)
            behavior = go.AddComponent<ViewerAgentBehavior>();
        behavior.SetBehaviorProgram(user, "", "ожидает #do", "", createLabel: false);

        ByUser[key] = agent;
        Debug.Log($"[ViewerSimpleAgent] spawn {user} at {pos}");
        return agent;
    }

    public static bool Despawn(string user)
    {
        string key = (user ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
            key = "viewer";

        if (!ByUser.TryGetValue(key, out var agent) || agent == null)
        {
            ByUser.Remove(key);
            return false;
        }

        ByUser.Remove(key);
        Object.Destroy(agent.gameObject);
        Debug.Log($"[ViewerSimpleAgent] despawn {user}");
        return true;
    }

    public void ApplyCharacterBehavior(string user, string speciesName, string behName, int lvl, string json)
    {
        if (!string.IsNullOrEmpty(user))
            username = user;
        species = string.IsNullOrEmpty(speciesName) ? "sheep" : speciesName.ToLowerInvariant();
        behaviorName = behName ?? "";
        level = Mathf.Max(1, lvl);
        _rawJson = json ?? "";
        ParseRules(json);
        ApplySpeciesTint();
        RefreshLabel();
        Debug.Log($"[ViewerSimpleAgent] behavior {username} species={species} name={behaviorName} rules={_rules.Count}");
    }

    static GameObject SpawnVisual(string key)
    {
        var prefab = Resources.Load<GameObject>(PrefabResource);
        if (prefab == null)
        {
            var spawner = TrainingEnvSpace.FindInPresentation<SheepSpawner>();
            if (spawner != null)
                prefab = spawner.PeekSheepPrefab();
        }
        if (prefab == null)
            prefab = Resources.Load<GameObject>("Sheep_1");

        GameObject go;
        if (prefab != null)
        {
            go = Object.Instantiate(prefab);
            SanitizeAsViewerPet(go);
        }
        else
        {
            Debug.LogWarning("[ViewerSimpleAgent] ViewerSheep missing — capsule fallback");
            go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            var col = go.GetComponent<Collider>();
            if (col != null)
                Object.Destroy(col);
        }

        go.name = "Viewer_" + key;
        return go;
    }

    static void SanitizeAsViewerPet(GameObject go)
    {
        foreach (var w in go.GetComponentsInChildren<SheepWander>(true))
            Object.Destroy(w);

        SetLayerRecursively(go, 0);
        go.tag = "Untagged";

        for (int i = go.transform.childCount - 1; i >= 0; i--)
        {
            var ch = go.transform.GetChild(i);
            if (ch != null && (ch.name == "NameLabel" || ch.name == "BehaviorLabel"))
                Object.Destroy(ch.gameObject);
        }
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        var tr = go.transform;
        for (int i = 0; i < tr.childCount; i++)
        {
            var c = tr.GetChild(i);
            if (c != null)
                SetLayerRecursively(c.gameObject, layer);
        }
    }

    static Vector3 PickSpawnNearSheep(Transform root)
    {
        var sheeps = root != null
            ? root.GetComponentsInChildren<SheepWander>(true)
            : Object.FindObjectsByType<SheepWander>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        Vector3 basePos = Vector3.zero;
        int n = 0;
        if (sheeps != null)
        {
            for (int i = 0; i < sheeps.Length; i++)
            {
                var s = sheeps[i];
                if (s == null || !s.gameObject.activeInHierarchy)
                    continue;
                if (s.GetComponent<ViewerSimpleAgent>() != null)
                    continue;
                basePos += s.transform.position;
                n++;
            }
        }

        if (n > 0)
            basePos /= n;
        else
        {
            var spawner = TrainingEnvSpace.FindInPresentation<SheepSpawner>();
            if (spawner != null)
                basePos = spawner.transform.position;
            else if (root != null)
                basePos = root.position + new Vector3(2f, 0f, 2f);
        }

        float ang = Random.Range(0f, Mathf.PI * 2f);
        float dist = Random.Range(1.5f, 3.5f);
        Vector3 p = basePos + new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
        p.y = basePos.y;
        return p;
    }

    void Setup(string user, Vector3 home)
    {
        username = user ?? "viewer";
        _home = home;
        _target = home;
        _retargetAt = 0f;
        _circleAngle = Random.Range(0f, Mathf.PI * 2f);

        _cc = GetComponent<CharacterController>();
        if (_cc == null)
        {
            var capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
                Object.Destroy(capsule);
            _cc = gameObject.AddComponent<CharacterController>();
            _cc.height = 0.9f;
            _cc.radius = 0.35f;
            _cc.center = new Vector3(0f, 0.45f, 0f);
        }

        EnsureLabel();
        RefreshLabel();
    }

    void EnsureLabel()
    {
        if (_label != null)
            return;

        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            if (ch != null && (ch.name == "NameLabel" || ch.name == "BehaviorLabel" || ch.name == "NameLabelShadow"))
                Object.Destroy(ch.gameObject);
        }

        var go = new GameObject("NameLabel");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, LabelHeight, 0f);
        _labelTf = go.transform;

        _label = go.AddComponent<TextMesh>();
        _label.characterSize = 0.045f;
        _label.fontSize = 48;
        _label.anchor = TextAnchor.LowerCenter;
        _label.alignment = TextAlignment.Center;
        _label.color = new Color(1f, 0.95f, 0.55f, 1f);
        _label.fontStyle = FontStyle.Bold;
        _label.text = username;
    }

    void RefreshLabel()
    {
        EnsureLabel();
        if (_label == null)
            return;
        // Stream overlay: nickname only (no species/level/behavior dump).
        _label.text = string.IsNullOrEmpty(username) ? "viewer" : username;
    }

    void ApplySpeciesTint()
    {
        var rends = GetComponentsInChildren<Renderer>(true);
        Color c = SpeciesColor(species);
        for (int i = 0; i < rends.Length; i++)
        {
            var r = rends[i];
            if (r == null) continue;
            // не трогать TextMesh
            if (r.GetComponent<TextMesh>() != null) continue;
            var mats = r.materials;
            for (int m = 0; m < mats.Length; m++)
            {
                if (mats[m] != null && mats[m].HasProperty("_Color"))
                    mats[m].color = Color.Lerp(mats[m].color, c, 0.55f);
            }
        }
        transform.localScale = Vector3.one * SpeciesScale(species);
    }

    static Color SpeciesColor(string sp)
    {
        switch ((sp ?? "").ToLowerInvariant())
        {
            case "rabbit": return new Color(0.92f, 0.88f, 0.82f);
            case "fox": return new Color(0.95f, 0.45f, 0.15f);
            case "dog": return new Color(0.55f, 0.4f, 0.25f);
            case "wolf": return new Color(0.45f, 0.5f, 0.55f);
            case "bear": return new Color(0.35f, 0.22f, 0.12f);
            default: return new Color(0.95f, 0.95f, 0.9f);
        }
    }

    static float SpeciesScale(string sp)
    {
        switch ((sp ?? "").ToLowerInvariant())
        {
            case "rabbit": return 0.95f;
            case "fox": return 1.15f;
            case "dog": return 1.2f;
            case "wolf": return 1.35f;
            case "bear": return 1.7f;
            default: return SpawnScale;
        }
    }

    void ParseRules(string json)
    {
        _rules.Clear();
        if (string.IsNullOrEmpty(json))
            return;

        // простой разбор массива rules по вхождениям "priority"
        int searchFrom = 0;
        while (true)
        {
            int pIdx = json.IndexOf("\"priority\"", searchFrom, System.StringComparison.Ordinal);
            if (pIdx < 0) break;

            int ruleStart = json.LastIndexOf('{', pIdx);
            int ruleEnd = FindMatchingBrace(json, ruleStart);
            if (ruleStart < 0 || ruleEnd < 0)
            {
                searchFrom = pIdx + 10;
                continue;
            }

            string chunk = json.Substring(ruleStart, ruleEnd - ruleStart + 1);
            var rule = new BehaviorRule
            {
                Priority = Mathf.Clamp(ExtractInt(chunk, "priority", 50), 1, 100),
                CondType = ExtractNestedString(chunk, "condition", "type", "always"),
                CondTarget = ExtractNestedString(chunk, "condition", "target", ""),
                CondDistance = ExtractNestedFloat(chunk, "condition", "distance", 4f),
                CondValue = ExtractNestedFloat(chunk, "condition", "value", 0f),
                ActionType = ExtractNestedString(chunk, "action", "type", "wander"),
                ActionTarget = ExtractNestedString(chunk, "action", "target", "spawn_point"),
                Radius = Mathf.Clamp(ExtractNestedFloat(chunk, "action", "radius", 3f), 1f, 10f),
                Speed = Mathf.Clamp(ExtractNestedFloat(chunk, "action", "speed", 1f), 0.2f, 3f),
                Distance = Mathf.Clamp(ExtractNestedFloat(chunk, "action", "distance", 4f), 1f, 20f),
            };
            _rules.Add(rule);
            searchFrom = ruleEnd + 1;
        }

        _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        if (_rules.Count == 0)
        {
            _rules.Add(new BehaviorRule
            {
                Priority = 50,
                CondType = "always",
                ActionType = "wander",
                ActionTarget = "spawn_point",
                Radius = 3f,
                Speed = 1f,
                Distance = 4f,
            });
        }
    }

    static int FindMatchingBrace(string s, int openIdx)
    {
        if (openIdx < 0 || openIdx >= s.Length || s[openIdx] != '{') return -1;
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    static int ExtractInt(string json, string key, int def)
    {
        float f = ExtractFloat(json, key, def);
        return Mathf.RoundToInt(f);
    }

    static float ExtractFloat(string json, string key, float def)
    {
        string pattern = "\"" + key + "\"";
        int i = json.IndexOf(pattern, System.StringComparison.Ordinal);
        if (i < 0) return def;
        int colon = json.IndexOf(':', i + pattern.Length);
        if (colon < 0) return def;
        int j = colon + 1;
        while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
        int k = j;
        while (k < json.Length && "0123456789.-+eE".IndexOf(json[k]) >= 0) k++;
        string token = json.Substring(j, Mathf.Max(0, k - j));
        return float.TryParse(token, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : def;
    }

    static string ExtractString(string json, string key, string def)
    {
        string pattern = "\"" + key + "\"";
        int i = json.IndexOf(pattern, System.StringComparison.Ordinal);
        if (i < 0) return def;
        int colon = json.IndexOf(':', i + pattern.Length);
        if (colon < 0) return def;
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return def;
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return def;
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    static string ExtractNestedString(string ruleJson, string objKey, string field, string def)
    {
        string pattern = "\"" + objKey + "\"";
        int i = ruleJson.IndexOf(pattern, System.StringComparison.Ordinal);
        if (i < 0) return def;
        int brace = ruleJson.IndexOf('{', i);
        if (brace < 0) return def;
        int end = FindMatchingBrace(ruleJson, brace);
        if (end < 0) return def;
        return ExtractString(ruleJson.Substring(brace, end - brace + 1), field, def);
    }

    static float ExtractNestedFloat(string ruleJson, string objKey, string field, float def)
    {
        string pattern = "\"" + objKey + "\"";
        int i = ruleJson.IndexOf(pattern, System.StringComparison.Ordinal);
        if (i < 0) return def;
        int brace = ruleJson.IndexOf('{', i);
        if (brace < 0) return def;
        int end = FindMatchingBrace(ruleJson, brace);
        if (end < 0) return def;
        return ExtractFloat(ruleJson.Substring(brace, end - brace + 1), field, def);
    }

    void Update()
    {
        SelectActiveRule();
        RunActiveAction();
    }

    void SelectActiveRule()
    {
        if (_rules.Count == 0)
        {
            _activeAction = "wander";
            _activeTarget = "spawn_point";
            return;
        }

        for (int i = 0; i < _rules.Count; i++)
        {
            var r = _rules[i];
            if (!EvalCondition(r))
                continue;
            _activeAction = r.ActionType;
            _activeTarget = r.ActionTarget;
            _actionRadius = r.Radius;
            _actionSpeed = r.Speed;
            _keepDistance = r.Distance;
            moveSpeed = Mathf.Clamp(r.Speed, 0.2f, 3f) * 1.2f;
            return;
        }

        var last = _rules[_rules.Count - 1];
        _activeAction = last.ActionType;
        _activeTarget = last.ActionTarget;
        _actionRadius = last.Radius;
        _actionSpeed = last.Speed;
        _keepDistance = last.Distance;
    }

    bool EvalCondition(BehaviorRule r)
    {
        string t = (r.CondType ?? "always").ToLowerInvariant();
        if (t == "always") return true;
        if (t == "near")
        {
            var p = ResolveTarget(r.CondTarget);
            if (!p.HasValue) return false;
            return HorizontalDist(transform.position, p.Value) <= r.CondDistance;
        }
        if (t == "far_from")
        {
            var p = ResolveTarget(r.CondTarget);
            if (!p.HasValue) return true;
            return HorizontalDist(transform.position, p.Value) >= r.CondDistance;
        }
        // episode/health/hunger — в MVP всегда true (нет сенсоров у pet)
        return true;
    }

    void RunActiveAction()
    {
        string act = (_activeAction ?? "wander").ToLowerInvariant();
        switch (act)
        {
            case "circle":
                DoCircle();
                break;
            case "follow":
                DoFollow(false);
                break;
            case "keep_distance":
                DoKeepDistance();
                break;
            case "flee_from":
                DoFlee();
                break;
            case "sit_near":
            case "sleep":
                DoSitNear();
                break;
            case "graze":
            case "patrol":
            case "move_to":
            case "wander":
            default:
                DoWanderLike(act == "graze" || act == "patrol");
                break;
        }
    }

    void DoCircle()
    {
        Vector3 center = ResolveTarget(_activeTarget) ?? _home;
        _circleAngle += (_actionSpeed / Mathf.Max(0.5f, _actionRadius)) * Time.deltaTime;
        Vector3 want = center + new Vector3(Mathf.Cos(_circleAngle), 0f, Mathf.Sin(_circleAngle)) * _actionRadius;
        want.y = transform.position.y;
        MoveToward(want, moveSpeed);
    }

    void DoFollow(bool stopClose)
    {
        Vector3? t = ResolveTarget(_activeTarget);
        if (!t.HasValue)
        {
            DoWanderLike(false);
            return;
        }
        float d = HorizontalDist(transform.position, t.Value);
        if (stopClose && d < 2.2f)
            return;
        MoveToward(t.Value, moveSpeed);
    }

    void DoKeepDistance()
    {
        Vector3? t = ResolveTarget(_activeTarget);
        if (!t.HasValue) return;
        float d = HorizontalDist(transform.position, t.Value);
        Vector3 flat = transform.position - t.Value;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.001f)
            flat = transform.forward;
        flat.Normalize();
        if (d < _keepDistance - 0.4f)
        {
            Vector3 want = t.Value + flat * _keepDistance;
            MoveToward(want, moveSpeed);
        }
        else if (d > _keepDistance + 1.2f)
        {
            MoveToward(t.Value, moveSpeed * 0.9f);
        }
    }

    void DoFlee()
    {
        Vector3? t = ResolveTarget(_activeTarget);
        if (!t.HasValue)
        {
            DoWanderLike(false);
            return;
        }
        Vector3 away = transform.position - t.Value;
        away.y = 0f;
        if (away.sqrMagnitude < 0.001f)
            away = -transform.forward;
        Vector3 want = transform.position + away.normalized * 4f;
        MoveToward(want, moveSpeed * 1.3f);
    }

    void DoSitNear()
    {
        Vector3? t = ResolveTarget(_activeTarget);
        Vector3 spot = t ?? _home;
        float d = HorizontalDist(transform.position, spot);
        if (d > 1.6f)
            MoveToward(spot, moveSpeed * 0.7f);
    }

    void DoWanderLike(bool tighter)
    {
        float radius = tighter ? Mathf.Min(wanderRadius, 3.5f) : wanderRadius;
        Vector3 center = ResolveTarget(_activeTarget) ?? _home;
        if (Time.time >= _retargetAt)
        {
            _retargetAt = Time.time + Random.Range(2.5f, 5.5f);
            Vector2 r = Random.insideUnitCircle * radius;
            _target = center + new Vector3(r.x, 0f, r.y);
        }
        MoveToward(_target, moveSpeed);
    }

    void MoveToward(Vector3 worldTarget, float speed)
    {
        Vector3 flat = worldTarget - transform.position;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.04f)
            return;

        Vector3 dir = flat.normalized;
        Vector3 move = dir * (speed * Time.deltaTime);
        if (_cc != null && _cc.enabled)
            _cc.Move(move + Vector3.down * 9.81f * Time.deltaTime);
        else
            transform.position += move;

        if (dir.sqrMagnitude > 0.001f)
        {
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(dir),
                8f * Time.deltaTime);
        }
    }

    Vector3? ResolveTarget(string target)
    {
        string t = (target ?? "spawn_point").ToLowerInvariant();
        switch (t)
        {
            case "spawn_point":
                return _home;
            case "random_point":
                return _home + new Vector3(Random.Range(-4f, 4f), 0f, Random.Range(-4f, 4f));
            case "main_ai":
            case "main_agent":
                return FindMainAi();
            case "safe_zone":
                return FindSafeZone() ?? _home;
            case "nearest_tree":
                return FindNearestWithName("Tree", "tree") ?? _home;
            case "nearest_food":
            case "nearest_water":
                return FindNearestComponent<SheepWander>() ?? FindNearestWithName("Food", "food") ?? _home;
            case "nearest_character":
            case "nearest_zombie":
                return FindNearestOtherViewer() ?? FindNearestWithName("Zombie", "zombie");
            default:
                return _home;
        }
    }

    Vector3? FindMainAi()
    {
        var jack = TrainingEnvSpace.FindPresentationJack();
        if (jack != null) return jack.transform.position;
        var lily = TrainingEnvSpace.FindPresentationLily();
        if (lily != null) return lily.transform.position;
        return null;
    }

    Vector3? FindSafeZone()
    {
        // база ≈ главный AI / костёр по имени
        var named = FindNearestWithName("Fire", "Campfire", "Bonfire", "Base");
        if (named.HasValue) return named;
        return FindMainAi() ?? _home;
    }

    Vector3? FindNearestOtherViewer()
    {
        ViewerSimpleAgent best = null;
        float bestD = float.MaxValue;
        foreach (var kv in ByUser)
        {
            var a = kv.Value;
            if (a == null || a == this) continue;
            float d = HorizontalDist(transform.position, a.transform.position);
            if (d < bestD)
            {
                bestD = d;
                best = a;
            }
        }
        return best != null ? best.transform.position : (Vector3?)null;
    }

    Vector3? FindNearestComponent<T>() where T : Component
    {
        var root = TrainingEnvSpace.PresentationRoot;
        T[] arr = root != null
            ? root.GetComponentsInChildren<T>(true)
            : Object.FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        T best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < arr.Length; i++)
        {
            var c = arr[i];
            if (c == null || c.gameObject == gameObject) continue;
            if (c.GetComponent<ViewerSimpleAgent>() != null) continue;
            float d = HorizontalDist(transform.position, c.transform.position);
            if (d < bestD)
            {
                bestD = d;
                best = c;
            }
        }
        return best != null ? best.transform.position : (Vector3?)null;
    }

    Vector3? FindNearestWithName(params string[] needles)
    {
        var root = TrainingEnvSpace.PresentationRoot;
        Transform[] all = root != null
            ? root.GetComponentsInChildren<Transform>(true)
            : Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        Transform best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < all.Length; i++)
        {
            var tr = all[i];
            if (tr == null || tr == transform) continue;
            string n = tr.name;
            bool hit = false;
            for (int k = 0; k < needles.Length; k++)
            {
                if (n.IndexOf(needles[k], System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) continue;
            float d = HorizontalDist(transform.position, tr.position);
            if (d < bestD)
            {
                bestD = d;
                best = tr;
            }
        }
        return best != null ? best.position : (Vector3?)null;
    }

    static float HorizontalDist(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    void LateUpdate()
    {
        if (_labelTf == null)
            return;
        var cam = BillboardIconCamera.Resolve(_labelTf.position, null);
        if (cam == null)
            return;
        _labelTf.rotation = cam.transform.rotation;
    }

    void OnDestroy()
    {
        string key = (username ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(key) && ByUser.TryGetValue(key, out var a) && a == this)
            ByUser.Remove(key);
    }
}
