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
                SetFlowerLayer(flower);
                flowers.Add(flower);
                return;
            }
        }
    }

    /// <summary>Ставит слой Flower — Jack игнорирует его (Physics.IgnoreLayerCollision), Lily застревает и собирает.</summary>
    private void SetFlowerLayer(GameObject flower)
    {
        int layer = LayerMask.NameToLayer("Flower");
        if (layer >= 0)
            SetLayerRecursively(flower, layer);
    }

    private void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    public void ResetFlowers()
    {
        ClearFlowers();
        SpawnAll();
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
                    SetFlowerLayer(flower);
                    flowers.Add(flower);
                    break;
                }
            }
        }
    }

    private void ClearFlowers()
    {
        foreach (var f in flowers)
        {
            if (IsAlive(f))
                Destroy(f);
        }
        flowers.Clear();
    }
}
