using System.Collections.Generic;
using UnityEngine;

public class TreeSpawner : MonoBehaviour
{
    [Header("Tree Prefabs")]
    [SerializeField] private GameObject[] treePrefabs; // массив разных типов деревьев

    [Header("Spawn Settings")]
    [SerializeField] private int treeCount = 30;
    [SerializeField] private float y = 0.42f;
    [SerializeField] private float minDistance = 1.5f; // минимальное расстояние между деревьями
    [SerializeField] private float respawnInterval = 2f; // секунд между попытками доп. спавна

    // Основная область спавна
    private readonly float minX = -28.40f;
    private readonly float maxX = -7.79f;
    private readonly float minZ = 8.93f;
    private readonly float maxZ = 14.59f;

    // Дополнительная область
    private readonly float extraMinX = -21.18f;
    private readonly float extraMaxX = -7.6f;
    private readonly float extraMinZ = 4f;
    private readonly float extraMaxZ = 6.93f;

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

    private void Start()
    {
        nextRespawnTime = Time.time + respawnInterval;
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

    private void SpawnOneTree()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Length)];
            bool useExtraArea = Random.value < 0.3f;

            Vector3 pos;
            if (useExtraArea)
                pos = ToWorld(new Vector3(Random.Range(extraMinX, extraMaxX), y, Random.Range(extraMinZ, extraMaxZ)));
            else
                pos = ToWorld(new Vector3(Random.Range(minX, maxX), y, Random.Range(minZ, maxZ)));

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
                trees.Add(tree);
                return;
            }
        }
    }

    public void ResetTrees()
    {
        ClearTrees();
        SpawnTrees();
    }

    private void SpawnTrees()
    {
        int attempts = 0;

        for (int i = 0; i < treeCount; i++)
        {
            bool treePlaced = false;

            while (!treePlaced && attempts < 100)
            {
                attempts++;

                // Выбираем случайный префаб
                GameObject prefab = treePrefabs[Random.Range(0, treePrefabs.Length)];

                // Выбираем случайную область: основную или дополнительную
                bool useExtraArea = Random.value < 0.3f;

                Vector3 pos;
                if (useExtraArea)
                {
                    pos = ToWorld(new Vector3(
                        Random.Range(extraMinX, extraMaxX),
                        y,
                        Random.Range(extraMinZ, extraMaxZ)
                    ));
                }
                else
                {
                    pos = ToWorld(new Vector3(
                        Random.Range(minX, maxX),
                        y,
                        Random.Range(minZ, maxZ)
                    ));
                }

                // Проверяем минимальное расстояние до всех уже размещённых деревьев
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
                    trees.Add(tree);
                    treePlaced = true;
                }
            }

            if (attempts >= 100)
            {
                Debug.LogWarning("Не удалось разместить все деревья без пересечений");
                break;
            }
        }
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
                trees.Add(spawnedTree);
                spawned++;
                break;
            }
        }

        return spawned;
    }
}
