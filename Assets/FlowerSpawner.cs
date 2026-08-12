using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Спавн и респавн цветов и грибов. Добавь объект в сцену, укажи префабы в списке, задай зону и количество.
/// Объекты цветов должны быть на слое, который Lily проверяет (flowerLayer).
/// </summary>
public class FlowerSpawner : MonoBehaviour
{
    [Header("Prefabs")]
    [Tooltip("Список префабов цветов и грибов")]
    [SerializeField] private GameObject[] flowerPrefabs;

    [Header("Spawn Settings")]
    [SerializeField] private int flowerCount = 20;
    [SerializeField] private float minDistance = 1.5f;
    [SerializeField] private float respawnInterval = 1f;

    // Зона спавна (local Env, поляна рядом с Jack)
    private readonly float areaMinX = 3.73f;
    private readonly float areaMaxX = 16.73f;
    private readonly float areaMinZ = 10.65f;
    private readonly float areaMaxZ = 15.94f;
    private readonly float spawnY = -5.588786f;

    private List<GameObject> flowers = new List<GameObject>();
    private float nextRespawnTime;
    private Transform _envRoot;

    static bool IsAlive(GameObject go) => go != null;

    private void Awake()
    {
        _envRoot = TrainingEnvSpace.FindRoot(transform);
    }

    private Vector3 ToWorld(Vector3 localPos)
    {
        if (_envRoot == null)
            _envRoot = TrainingEnvSpace.FindRoot(transform);
        return _envRoot != null ? _envRoot.TransformPoint(localPos) : localPos;
    }

    private void Start()
    {
        nextRespawnTime = Time.time + respawnInterval;
        ResetFlowers();
    }

    private void Update()
    {
        if (Time.time < nextRespawnTime) return;
        nextRespawnTime = Time.time + respawnInterval;

        RemoveDestroyed();
        if (flowers.Count < flowerCount && flowerPrefabs != null && flowerPrefabs.Length > 0)
            SpawnOne();
    }

    private void RemoveDestroyed()
    {
        flowers.RemoveAll(f => !IsAlive(f));
    }

    private void SpawnOne()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            GameObject prefab = flowerPrefabs[Random.Range(0, flowerPrefabs.Length)];
            Vector3 pos = ToWorld(new Vector3(
                Random.Range(areaMinX, areaMaxX),
                spawnY,
                Random.Range(areaMinZ, areaMaxZ)
            ));

            bool tooClose = false;
            foreach (var f in flowers)
            {
                if (!IsAlive(f))
                    continue;

                if (Vector3.Distance(pos, f.transform.position) < minDistance)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
            {
                GameObject flower = Instantiate(prefab, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), transform);
                PrepareCollectibleFlower(flower);
                flowers.Add(flower);
                return;
            }
        }
    }

    public Vector3 GetAreaCenterWorld()
    {
        return ToWorld(new Vector3(
            (areaMinX + areaMaxX) * 0.5f,
            spawnY,
            (areaMinZ + areaMaxZ) * 0.5f));
    }

    public Vector3 GetAreaCenterLocal()
    {
        return new Vector3(
            (areaMinX + areaMaxX) * 0.5f,
            spawnY,
            (areaMinZ + areaMaxZ) * 0.5f);
    }

    public static FlowerSpawner FindInPresentation()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root != null)
        {
            var fs = root.GetComponentInChildren<FlowerSpawner>(true);
            if (fs != null) return fs;
        }
        return Object.FindFirstObjectByType<FlowerSpawner>(FindObjectsInactive.Include);
    }

    public void NotifyFlowerCollected(GameObject flower)
    {
        if (flower != null)
            flowers.Remove(flower);
    }

    public GameObject FindNearestFlowerInReach(Vector3 origin, float reach)
    {
        RemoveDestroyed();
        GameObject best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < flowers.Count; i++)
        {
            var flower = flowers[i];
            if (!IsAlive(flower))
                continue;

            float d = DistanceToFlowerBounds(origin, flower);
            if (d > reach || d >= bestDist)
                continue;

            bestDist = d;
            best = flower;
        }
        return best;
    }

    public static float DistanceToFlowerBounds(Vector3 origin, GameObject flower)
    {
        if (flower == null)
            return float.MaxValue;

        var cols = flower.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null || !c.enabled)
                continue;
            return Vector3.Distance(origin, c.ClosestPoint(origin));
        }

        var rend = flower.GetComponentInChildren<Renderer>(true);
        if (rend != null)
            return Vector3.Distance(origin, rend.bounds.ClosestPoint(origin));

        Vector3 p = flower.transform.position;
        p.y = origin.y;
        return Vector3.Distance(origin, p);
    }

    static void PrepareCollectibleFlower(GameObject flower)
    {
        if (flower == null)
            return;

        SetFlowerLayer(flower);

        // Выключаем огромные/non-convex меш-коллайдеры префаба — иначе OverlapSphere
        // цепляет цветок с полкарты, а ClosestPoint даёт дистанцию ≈0.
        var cols = flower.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null)
                continue;
            if (c is MeshCollider || c is BoxCollider || c is CapsuleCollider || c is SphereCollider)
                c.enabled = false;
        }

        Bounds b = ComputeVisualBounds(flower);
        var sphere = flower.GetComponent<SphereCollider>();
        if (sphere == null)
            sphere = flower.AddComponent<SphereCollider>();

        float radius = Mathf.Clamp(Mathf.Max(b.extents.x, b.extents.z) * 0.45f, 0.2f, 0.85f);
        sphere.enabled = true;
        sphere.isTrigger = false;
        sphere.radius = radius;
        sphere.center = flower.transform.InverseTransformPoint(b.center);
    }

    static Bounds ComputeVisualBounds(GameObject flower)
    {
        var renderers = flower.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return new Bounds(flower.transform.position + Vector3.up * 0.25f, Vector3.one * 0.5f);

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);
        return b;
    }

    /// <summary>Ставит слой Flower — Jack игнорирует его (Physics.IgnoreLayerCollision), Lily застревает и собирает.</summary>
    private static void SetFlowerLayer(GameObject flower)
    {
        int layer = LayerMask.NameToLayer("Flower");
        if (layer >= 0)
            SetLayerRecursively(flower, layer);
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    public void ResetFlowers()
    {
        ClearFlowersImmediate();
        nextRespawnTime = Time.time + respawnInterval;
        SpawnAll();
    }

    void ClearFlowersImmediate()
    {
        flowers.Clear();
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
    }

    private void ClearFlowers()
    {
        ClearFlowersImmediate();
    }

    private void SpawnAll()
    {
        if (flowerPrefabs == null || flowerPrefabs.Length == 0) return;

        for (int i = 0; i < flowerCount; i++)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                GameObject prefab = flowerPrefabs[Random.Range(0, flowerPrefabs.Length)];
                Vector3 pos = ToWorld(new Vector3(
                    Random.Range(areaMinX, areaMaxX),
                    spawnY,
                    Random.Range(areaMinZ, areaMaxZ)
                ));

                bool tooClose = false;
                foreach (var f in flowers)
                {
                    if (!IsAlive(f))
                        continue;

                    if (Vector3.Distance(pos, f.transform.position) < minDistance)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    GameObject flower = Instantiate(prefab, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), transform);
                    PrepareCollectibleFlower(flower);
                    flowers.Add(flower);
                    break;
                }
            }
        }
    }
}
