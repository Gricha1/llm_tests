using System.Collections.Generic;
using UnityEngine;

public class TreeSpawner : MonoBehaviour
{
    [Header("Tree Prefabs")]
    [SerializeField] private GameObject[] treePrefabs; // массив разных типов деревьев

    [Header("Spawn Settings")]
    [SerializeField] private int treeCount = 30;
    [SerializeField] private float y = -5.718786f;
    [SerializeField] private float minDistance = 1.5f; // минимальное расстояние между деревьями
    [SerializeField] private float respawnInterval = 2f; // секунд между попытками доп. спавна

    // Основная область спавна (local Env, за домиком — как на стриме)
    private readonly float minX = -6.17f;
    private readonly float maxX = 14.44f;
    private readonly float minZ = 26.98f;
    private readonly float maxZ = 32.64f;

    // Дополнительная область
    private readonly float extraMinX = 1.05f;
    private readonly float extraMaxX = 14.63f;
    private readonly float extraMinZ = 22.05f;
    private readonly float extraMaxZ = 24.98f;

    private List<GameObject> trees = new List<GameObject>();
    private float nextRespawnTime;
    private Transform _envRoot;

    static bool IsAlive(GameObject go) => go != null;

    private void Awake()
    {
        _envRoot = TrainingEnvSpace.FindRoot(transform);
    }

    private Vector3 ToWorld(Vector3 localPos)
    {
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
        nextRespawnTime = Time.time + respawnInterval;
        RemoveDestroyedTrees();
        if (trees.Count == 0)
            SpawnTrees();
    }

    private void Update()
    {
        if (Time.time < nextRespawnTime) return;
        nextRespawnTime = Time.time + respawnInterval;

        RemoveDestroyedTrees();
        if (trees.Count < treeCount)
            SpawnOneTree();
    }

    private void RemoveDestroyedTrees()
    {
        trees.RemoveAll(t => !IsAlive(t));
    }

    public void NotifyTreeChopped(GameObject tree)
    {
        if (tree != null)
            trees.Remove(tree);
    }

    private void SpawnOneTree()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Length)];
            Vector3 pos = ToWorld(RandomLocalSpawn());

            bool tooClose = false;
            foreach (var tree in trees)
            {
                if (!IsAlive(tree))
                    continue;

                if (Vector3.Distance(pos, tree.transform.position) < minDistance)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
            {
                GameObject tree = Instantiate(prefab, pos, Quaternion.identity, transform);
                PrepareChoppableTree(tree);
                trees.Add(tree);
                return;
            }
        }
    }

    public int TargetCount => treeCount;

    public int AliveCount
    {
        get
        {
            RemoveDestroyedTrees();
            return trees.Count;
        }
    }

    public void ResetTrees()
    {
        ClearTrees();
        SpawnTrees();
    }

    private void SpawnTrees()
    {
        for (int i = 0; i < treeCount; i++)
        {
            bool treePlaced = false;

            for (int attempt = 0; attempt < 100 && !treePlaced; attempt++)
            {
                GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Length)];
                Vector3 pos = ToWorld(RandomLocalSpawn());

                bool tooClose = false;
                foreach (var tree in trees)
                {
                    if (!IsAlive(tree))
                        continue;

                    if (Vector3.Distance(pos, tree.transform.position) < minDistance)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    GameObject tree = Instantiate(prefab, pos, Quaternion.identity, transform);
                    PrepareChoppableTree(tree);
                    trees.Add(tree);
                    treePlaced = true;
                }
            }

            if (!treePlaced)
                Debug.LogWarning($"TreeSpawner: не удалось разместить дерево {i + 1}/{treeCount}");
        }
    }

    /// <summary>
    /// Префабы часто без слоя Tree / только non-convex MeshCollider —
    /// OverlapSphere и ClosestPoint тогда пропускают дерево рядом с агентом.
    /// </summary>
    static void PrepareChoppableTree(GameObject tree)
    {
        if (tree == null)
            return;

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
        capsule.direction = 1; // Y
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

    /// <summary>Ближайшее живое дерево по bounds (фолбэк, если Physics.OverlapSphere пустой).</summary>
    public GameObject FindNearestTreeInReach(Vector3 origin, float reach)
    {
        RemoveDestroyedTrees();
        GameObject best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < trees.Count; i++)
        {
            var tree = trees[i];
            if (!IsAlive(tree))
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

    private void ClearTrees()
    {
        foreach (var tree in trees)
        {
            if (IsAlive(tree))
                Destroy(tree);
        }
        trees.Clear();
    }

    /// <summary>Спавн count деревьев вокруг worldPos (для Twitch и т.п.). Возвращает сколько поставили.</summary>
    public int SpawnTreesNear(Vector3 worldPos, int count, float radius = 7f)
    {
        if (treePrefabs == null || treePrefabs.Length == 0)
            return 0;

        count = Mathf.Clamp(count, 1, 10);
        RemoveDestroyedTrees();

        int spawned = 0;
        float groundY = ToWorld(new Vector3(0f, y, 0f)).y;

        for (int i = 0; i < count; i++)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                Vector2 ring = Random.insideUnitCircle * radius;
                Vector3 pos = new Vector3(worldPos.x + ring.x, groundY, worldPos.z + ring.y);

                bool tooClose = false;
                foreach (var existing in trees)
                {
                    if (!IsAlive(existing))
                        continue;

                    if (Vector3.Distance(pos, existing.transform.position) < minDistance)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (tooClose)
                    continue;

                GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Length)];
                GameObject spawnedTree = Instantiate(prefab, pos, Quaternion.identity, transform);
                PrepareChoppableTree(spawnedTree);
                trees.Add(spawnedTree);
                spawned++;
                break;
            }
        }

        return spawned;
    }
}
