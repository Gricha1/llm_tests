using System.Collections.Generic;
using UnityEngine;

public class TreeSpawner : MonoBehaviour
{
    [Header("Tree Prefabs")]
    [SerializeField] private GameObject[] treePrefabs;

    [Header("Spawn Settings")]
    [SerializeField] private int treeCount = 30;
    [SerializeField] private float y = -5.718786f;
    [SerializeField] private float minDistance = 1.5f;
    [SerializeField] private float respawnInterval = 1f;
    [Tooltip("Не давать лесу падать ниже этого числа — досыпаем пачкой.")]
    [SerializeField] private int minAliveTrees = 12;
    [Tooltip("Сколько деревьев ставить за один тик, если лес ниже цели.")]
    [SerializeField] private int refillPerTick = 4;
    [Tooltip("Раз в N секунд: если деревьев мало — полный сброс.")]
    [SerializeField] private float watchdogInterval = 5f;
    [Tooltip("Сколько тиков подряд не удалось досыпать → ResetTrees.")]
    [SerializeField] private int failedRefillTicksBeforeReset = 2;
    [Tooltip("После ResetTrees: сколько раз перегенерить, пока не наберём minAlive.")]
    [SerializeField] private int postSpawnVerifyRetries = 4;

    private readonly float minX = -6.17f;
    private readonly float maxX = 14.44f;
    private readonly float minZ = 27.0f;
    private readonly float maxZ = 32.64f;

    private readonly float extraMinX = 1.05f;
    private readonly float extraMaxX = 14.63f;
    private readonly float extraMinZ = 18.0f;
    private readonly float extraMaxZ = 26.0f;

    private readonly List<GameObject> trees = new List<GameObject>();
    private float nextRespawnTime;
    private float nextWatchdogTime;
    private int failedRefillTicks;
    private bool _resetInProgress;
    private Transform _envRoot;

    static bool IsUnityNull(GameObject go) => go == null;

    /// <summary>
    /// Живое дерево: есть визуал. Маркер ChoppableTree дописываем, если пропал —
    /// раньше его отсутствие чистило весь лес Destroy().
    /// Не требовать activeInHierarchy: при #reset / Mute родитель Env может быть
    /// выключен на кадр — иначе чекер FAIL 0/30 и спам ошибок.
    /// Не требовать renderer.enabled — MuteEnvPresentation гасит рендереры.
    /// </summary>
    static bool IsAliveTree(GameObject go)
    {
        if (IsUnityNull(go) || !go.activeSelf)
            return false;

        var renderers = go.GetComponentsInChildren<Renderer>(true);
        bool hasVisual = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null)
                continue;
            if (r.bounds.size.sqrMagnitude > 0.02f)
            {
                hasVisual = true;
                break;
            }
        }

        if (!hasVisual && go.GetComponentInChildren<MeshFilter>(true) != null)
            hasVisual = true;
        if (!hasVisual && go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
            hasVisual = true;

        if (!hasVisual)
            return false;

        if (go.GetComponent<ChoppableTree>() == null)
            go.AddComponent<ChoppableTree>();

        return true;
    }

    private void Awake()
    {
        _envRoot = TrainingEnvSpace.FindRoot(transform);
    }

    private void OnEnable()
    {
        // Если спавнер выключали (смерть/пауза) — сразу проверить лес.
        nextWatchdogTime = Time.unscaledTime + 0.5f;
        nextRespawnTime = Time.unscaledTime + 0.1f;
    }

    private Vector3 ToWorld(Vector3 localPos)
    {
        if (_envRoot == null)
            _envRoot = TrainingEnvSpace.FindRoot(transform);
        return _envRoot != null ? _envRoot.TransformPoint(localPos) : localPos;
    }

    private Vector3 RandomLocalSpawn()
    {
        bool useExtraArea = Random.value < 0.3f;
        if (useExtraArea)
        {
            return new Vector3(
                Random.Range(extraMinX, extraMaxX),
                y,
                Random.Range(extraMinZ, extraMaxZ)
            );
        }

        return new Vector3(
            Random.Range(minX, maxX),
            y,
            Random.Range(minZ, maxZ)
        );
    }

    private void Start()
    {
        nextWatchdogTime = Time.unscaledTime + watchdogInterval;
        // Уже заполнили при #env_N / Instantate — не дёргать ResetTrees второй раз в том же кадре.
        if (CountAliveChildren() == 0)
            ResetTrees();
    }

    private void Update()
    {
        // Скрытая копия Env (меню K) — не крутить ResetTrees впустую.
        if (!isActiveAndEnabled || !gameObject.activeInHierarchy)
            return;

        if (_resetInProgress)
            return;

        float now = Time.unscaledTime;

        // Критично: пустой лес → сразу полный сброс, не ждём watchdog.
        int quickCount = CountAliveChildren();
        if (quickCount == 0)
        {
            Debug.LogWarning("[TreeSpawner] лес пуст — немедленный ResetTrees", this);
            ResetTrees();
            return;
        }

        if (now >= nextWatchdogTime)
        {
            nextWatchdogTime = now + Mathf.Max(2f, watchdogInterval);
            RunWatchdog();
        }

        if (now < nextRespawnTime)
            return;

        TryRefillTick();
    }

    int CountAliveChildren()
    {
        int n = 0;
        for (int i = 0; i < transform.childCount; i++)
        {
            if (IsAliveTree(transform.GetChild(i).gameObject))
                n++;
        }
        return n;
    }

    void RunWatchdog()
    {
        ReconcileTreeList();
        int alive = trees.Count;
        int need = Mathf.Max(1, minAliveTrees);
        if (alive >= need)
            return;

        Debug.LogWarning(
            $"[TreeSpawner] watchdog: деревьев {alive}/{treeCount} (min={need}), полный сброс",
            this);
        PresentationWorldSnapshotLogger.Note(
            "tree_watchdog_reset",
            $"alive={alive}/{treeCount} min={need} env={(_envRoot != null ? _envRoot.name : "?")}");
        ResetTrees();
        failedRefillTicks = 0;
    }

    void TryRefillTick()
    {
        ReconcileTreeList();
        int alive = trees.Count;

        if (alive >= treeCount)
        {
            failedRefillTicks = 0;
            nextRespawnTime = Time.unscaledTime + respawnInterval;
            return;
        }

        bool critical = alive < Mathf.Max(1, minAliveTrees);
        int toSpawn = critical
            ? Mathf.Max(refillPerTick, Mathf.Min(treeCount - alive, refillPerTick * 2))
            : Mathf.Min(refillPerTick, treeCount - alive);
        toSpawn = Mathf.Clamp(toSpawn, 1, treeCount - alive);

        float spacing = critical ? Mathf.Max(0.45f, minDistance * 0.5f) : minDistance;
        int placed = RefillBatch(toSpawn, spacing, critical);

        nextRespawnTime = Time.unscaledTime + (critical ? respawnInterval * 0.35f : respawnInterval);

        if (placed > 0)
        {
            failedRefillTicks = 0;
            // После дозаполнения — чекер: всё ещё мало? полный сброс.
            if (critical && trees.Count < minAliveTrees)
                EnsureMinTreesOrReset("после refill");
            return;
        }

        if (!critical)
            return;

        failedRefillTicks++;
        if (failedRefillTicks >= failedRefillTicksBeforeReset)
        {
            Debug.LogWarning(
                $"[TreeSpawner] {failedRefillTicks} неудачных дозаполнений ({alive}/{treeCount}), сброс",
                this);
            ResetTrees();
            failedRefillTicks = 0;
        }
    }

    int RefillBatch(int toSpawn, float spacing, bool critical)
    {
        int placed = 0;
        for (int i = 0; i < toSpawn; i++)
        {
            if (TryPlaceTree(spacing)
                || TryPlaceTree(Mathf.Max(0.35f, spacing * 0.65f))
                || (critical && TryPlaceTreeOnGrid(spacing * 0.5f))
                || (critical && TryPlaceTreeForceAnywhere()))
            {
                placed++;
                continue;
            }

            break;
        }

        return placed;
    }

    void ReconcileTreeList()
    {
        trees.Clear();

        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i).gameObject;
            if (IsAliveTree(child))
            {
                trees.Add(child);
                continue;
            }

            // Пустая оболочка без визуала — убрать.
            if (!IsUnityNull(child))
                Destroy(child);
        }

        TrimOverCap();
    }

    void TrimOverCap()
    {
        while (trees.Count > treeCount)
        {
            var excess = trees[trees.Count - 1];
            trees.RemoveAt(trees.Count - 1);
            if (!IsUnityNull(excess))
                Destroy(excess);
        }
    }

    GameObject ResolveSpawnerChildRoot(GameObject tree)
    {
        if (IsUnityNull(tree))
            return null;

        Transform t = tree.transform;
        for (; t != null; t = t.parent)
        {
            if (t.parent == transform)
                return t.gameObject;
        }

        return tree;
    }

    public void NotifyTreeChopped(GameObject tree)
    {
        var root = ResolveSpawnerChildRoot(tree);
        if (root != null)
        {
            trees.Remove(root);
            // Same-frame retarget must not see this trunk as alive (Destroy is deferred).
            root.SetActive(false);
        }

        // Delay refill so agent walks to another living tree instead of farming one stand.
        nextRespawnTime = Time.unscaledTime + Mathf.Max(0.75f, respawnInterval);
        ReconcileTreeList();
    }

    public int TargetCount => treeCount;

    public int AliveCount
    {
        get
        {
            ReconcileTreeList();
            return trees.Count;
        }
    }

    /// <summary>Ближайшее живое дерево из зоны TreeSpawner (не декорации за забором).</summary>
    public bool TryGetNearestAliveTree(Vector3 worldFrom, out GameObject tree, out Vector3 worldPos)
    {
        ReconcileTreeList();
        tree = null;
        worldPos = default;
        float best = float.MaxValue;
        for (int i = 0; i < trees.Count; i++)
        {
            var t = trees[i];
            if (!IsAliveTree(t)) continue;
            Vector3 p = t.transform.position;
            // z>29 — край у верхнего забора/пруда, агент упирается
            if (p.z < 8f || p.z > 28.5f)
                continue;
            float dx = p.x - worldFrom.x;
            float dz = p.z - worldFrom.z;
            float d = dx * dx + dz * dz;
            if (d < best)
            {
                best = d;
                tree = t;
                worldPos = p;
            }
        }
        return tree != null;
    }

    /// <summary>Полный сброс + чекер: пока мало живых — генерируем снова.</summary>
    public void ResetTrees()
    {
        if (_resetInProgress)
            return;

        _resetInProgress = true;
        try
        {
            failedRefillTicks = 0;
            nextRespawnTime = Time.unscaledTime + 0.05f;
            nextWatchdogTime = Time.unscaledTime + Mathf.Max(2f, watchdogInterval);

            int need = Mathf.Max(1, minAliveTrees);
            int attempts = Mathf.Max(1, postSpawnVerifyRetries);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                ClearTreesImmediate();
                SpawnTrees();
                ReconcileTreeList();

                int alive = trees.Count;
                if (alive >= need)
                {
                    if (attempt > 0)
                    {
                        Debug.Log(
                            $"[TreeSpawner] чекер OK после попытки {attempt + 1}: {alive}/{treeCount}",
                            this);
                    }
                    return;
                }

                Debug.LogWarning(
                    $"[TreeSpawner] чекер: после генерации {alive}/{treeCount} " +
                    $"(нужно ≥{need}), попытка {attempt + 1}/{attempts} — снова",
                    this);
            }

            // Последний шанс: форс-спавн без spacing.
            ReconcileTreeList();
            int guard = treeCount * 3;
            while (trees.Count < need && guard-- > 0)
            {
                if (!TryPlaceTreeForceAnywhere())
                    break;
            }

            ReconcileTreeList();
            if (trees.Count < need)
            {
                Debug.LogError(
                    $"[TreeSpawner] чекер FAIL: {trees.Count}/{treeCount} " +
                    $"(prefabs={(treePrefabs == null ? 0 : treePrefabs.Length)})",
                    this);
            }
        }
        finally
        {
            _resetInProgress = false;
        }
    }

    void EnsureMinTreesOrReset(string reason)
    {
        ReconcileTreeList();
        if (trees.Count >= minAliveTrees)
            return;

        Debug.LogWarning(
            $"[TreeSpawner] {reason}: {trees.Count}<{minAliveTrees} — ResetTrees",
            this);
        PresentationWorldSnapshotLogger.Note(
            "tree_min_reset",
            $"{reason} alive={trees.Count} min={minAliveTrees}");
        ResetTrees();
    }

    void ClearTreesImmediate()
    {
        trees.Clear();
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
    }

    private void SpawnTrees()
    {
        if (treePrefabs == null || treePrefabs.Length == 0)
        {
            Debug.LogWarning($"TreeSpawner на {name}: treePrefabs пустой", this);
            return;
        }

        float spacing = minDistance;
        for (int i = 0; i < treeCount; i++)
        {
            if (TryPlaceTree(spacing))
                continue;

            if (TryPlaceTree(Mathf.Max(0.75f, spacing * 0.75f)))
                continue;

            if (TryPlaceTreeOnGrid(spacing * 0.6f))
                continue;

            if (TryPlaceTreeForceAnywhere())
                continue;
        }

        int safety = 0;
        while (trees.Count < treeCount && safety++ < treeCount * 4)
        {
            if (TryPlaceTree(Mathf.Max(0.4f, minDistance * 0.5f))
                || TryPlaceTreeOnGrid(0.4f)
                || TryPlaceTreeForceAnywhere())
                continue;

            break;
        }
    }

    bool TryPlaceTree(float spacing)
    {
        if (treePrefabs == null || treePrefabs.Length == 0)
            return false;

        for (int attempt = 0; attempt < 120; attempt++)
        {
            GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Length)];
            if (prefab == null)
                continue;

            Vector3 pos = ToWorld(RandomLocalSpawn());
            if (IsTooClose(pos, spacing))
                continue;

            SpawnTreeAt(pos, prefab);
            return true;
        }

        return false;
    }

    bool TryPlaceTreeOnGrid(float spacing)
    {
        if (treePrefabs == null || treePrefabs.Length == 0)
            return false;

        float step = Mathf.Max(0.45f, spacing);
        var zones = new[]
        {
            (minX, maxX, minZ, maxZ),
            (extraMinX, extraMaxX, extraMinZ, extraMaxZ)
        };

        foreach (var (zxMin, zxMax, zzMin, zzMax) in zones)
        {
            for (float x = zxMin; x <= zxMax; x += step)
            {
                for (float z = zzMin; z <= zzMax; z += step)
                {
                    Vector3 pos = ToWorld(new Vector3(x, y, z));
                    if (IsTooClose(pos, spacing))
                        continue;

                    GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Length)];
                    if (prefab == null)
                        continue;

                    SpawnTreeAt(pos, prefab);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Без проверки расстояния — только чтобы лес не оставался пустым.</summary>
    bool TryPlaceTreeForceAnywhere()
    {
        if (treePrefabs == null || treePrefabs.Length == 0)
            return false;

        GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Length)];
        if (prefab == null)
            return false;

        Vector3 pos = ToWorld(RandomLocalSpawn());
        SpawnTreeAt(pos, prefab);
        return true;
    }

    bool IsTooClose(Vector3 pos, float spacing)
    {
        for (int i = 0; i < trees.Count; i++)
        {
            var tree = trees[i];
            if (!IsAliveTree(tree))
                continue;

            if (HorizontalDistance(pos, tree.transform.position) < spacing)
                return true;
        }

        return false;
    }

    void SpawnTreeAt(Vector3 pos, GameObject prefab)
    {
        GameObject spawned = Instantiate(prefab, pos, Quaternion.identity, transform);
        PrepareChoppableTree(spawned);
        trees.Add(spawned);
    }

    static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    static void PrepareChoppableTree(GameObject tree)
    {
        if (tree == null)
            return;

        if (tree.GetComponent<ChoppableTree>() == null)
            tree.AddComponent<ChoppableTree>();

        int treeLayer = LayerMask.NameToLayer("Tree");
        if (treeLayer >= 0)
            SetLayerRecursively(tree.transform, treeLayer);

        if (HasOverlapFriendlyCollider(tree))
            return;

        Bounds b = ComputeVisualBounds(tree);
        var capsule = tree.GetComponent<CapsuleCollider>();
        if (capsule == null)
            capsule = tree.AddComponent<CapsuleCollider>();

        float radius = Mathf.Max(0.25f, Mathf.Max(b.extents.x, b.extents.z) * 0.35f);
        float height = Mathf.Max(radius * 2f + 0.5f, b.size.y);
        capsule.radius = radius;
        capsule.height = height;
        capsule.direction = 1;
        Vector3 localCenter = tree.transform.InverseTransformPoint(b.center);
        capsule.center = localCenter;
    }

    static bool HasOverlapFriendlyCollider(GameObject tree)
    {
        var cols = tree.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null || !c.enabled)
                continue;
            if (c is MeshCollider mesh && !mesh.convex)
                continue;
            return true;
        }
        return false;
    }

    static Bounds ComputeVisualBounds(GameObject tree)
    {
        var renderers = tree.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return new Bounds(tree.transform.position + Vector3.up, Vector3.one * 2f);

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);
        return b;
    }

    static void SetLayerRecursively(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++)
            SetLayerRecursively(t.GetChild(i), layer);
    }

    public GameObject FindNearestTreeInReach(Vector3 origin, float reach)
    {
        ReconcileTreeList();
        GameObject best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < trees.Count; i++)
        {
            var tree = trees[i];
            if (!IsAliveTree(tree))
                continue;

            float d = DistanceToTreeBounds(origin, tree);
            if (d > reach || d >= bestDist)
                continue;

            bestDist = d;
            best = tree;
        }
        return best;
    }

    public static float DistanceToTreeBounds(Vector3 origin, GameObject tree)
    {
        var cols = tree.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null || !c.enabled)
                continue;
            return Vector3.Distance(origin, c.bounds.ClosestPoint(origin));
        }

        var rend = tree.GetComponentInChildren<Renderer>(true);
        if (rend != null)
            return Vector3.Distance(origin, rend.bounds.ClosestPoint(origin));

        Vector3 p = tree.transform.position;
        p.y = origin.y;
        return Vector3.Distance(origin, p);
    }

    public int SpawnTreesNear(Vector3 worldPos, int count, float radius = 7f)
    {
        if (treePrefabs == null || treePrefabs.Length == 0)
            return 0;

        count = Mathf.Clamp(count, 1, 10);
        ReconcileTreeList();

        int spawned = 0;
        float groundY = ToWorld(new Vector3(0f, y, 0f)).y;

        for (int i = 0; i < count; i++)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                Vector2 ring = Random.insideUnitCircle * radius;
                Vector3 pos = new Vector3(worldPos.x + ring.x, groundY, worldPos.z + ring.y);

                if (IsTooClose(pos, minDistance))
                    continue;

                GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Length)];
                if (prefab == null)
                    continue;

                SpawnTreeAt(pos, prefab);
                spawned++;
                break;
            }

            // Не нашли свободное место — всё равно ставим рядом.
            if (spawned <= i)
            {
                Vector2 ring = Random.insideUnitCircle * radius;
                Vector3 pos = new Vector3(worldPos.x + ring.x, groundY, worldPos.z + ring.y);
                GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Length)];
                if (prefab != null)
                {
                    SpawnTreeAt(pos, prefab);
                    spawned++;
                }
            }
        }

        TrimOverCap();
        return spawned;
    }
}
