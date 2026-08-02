using System.Collections.Generic;
using UnityEngine;

public class SheepSpawner : MonoBehaviour
{
    [Header("Sheep Prefab")]
    [SerializeField] private GameObject sheepPrefab;

    [Header("Spawn Settings")]
    [SerializeField] private int sheepCount = 5;
    [SerializeField] private float y = -5.515023f;
    [SerializeField] private float minDistance = 1.5f;
    [SerializeField] private float respawnInterval = 2f; // секунд между попытками доп. спавна
    [SerializeField] private float maxDistanceFromSpawn = 12f; // овца возвращается в зону, если ушла дальше

    // Область спавна (local Env, поляна у домика)
    private readonly float minX = 1.43f;
    private readonly float maxX = 14.05f;
    private readonly float minZ = 10.65f;
    private readonly float maxZ = 15.94f;

    private Vector3 SpawnCenterLocal => new Vector3((minX + maxX) * 0.5f, y, (minZ + maxZ) * 0.5f);

    private List<GameObject> sheeps = new List<GameObject>();
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
        RemoveDestroyedSheep();
        if (sheeps.Count == 0)
            SpawnSheep();
    }

    private void Update()
    {
        if (Time.time < nextRespawnTime) return;
        nextRespawnTime = Time.time + respawnInterval;

        RemoveDestroyedSheep();
        TryFillSpawnSlots();
    }

    /// <summary>Вызывается после съеденной овцы — сразу пытаемся восполнить пул, не ждём Update.</summary>
    public void NotifySheepEaten()
    {
        RemoveDestroyedSheep();
        TryFillSpawnSlots();
    }

    private void TryFillSpawnSlots()
    {
        int guard = sheepCount * 2;
        while (sheeps.Count < sheepCount && guard-- > 0)
        {
            if (!SpawnOneSheep())
                break;
        }
    }

    private void RemoveDestroyedSheep()
    {
        sheeps.RemoveAll(s => !IsAlive(s));
    }

    GameObject ResolveSheepPrefab()
    {
        if (sheepPrefab != null)
            return sheepPrefab;

        // Prefab мог стать null после ClearSheep, если в инспекторе был scene-object.
        var all = Object.FindObjectsByType<SheepSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            var other = all[i];
            if (other == null || other == this || other.sheepPrefab == null)
                continue;
            sheepPrefab = other.sheepPrefab;
            Debug.LogWarning($"[SheepSpawner] восстановил sheepPrefab с {other.name}", this);
            return sheepPrefab;
        }

        var fromResources = Resources.Load<GameObject>("Sheep_1");
        if (fromResources != null)
        {
            sheepPrefab = fromResources;
            return sheepPrefab;
        }

        Debug.LogError("[SheepSpawner] sheepPrefab=null — овцы не спавнятся", this);
        return null;
    }

    private bool SpawnOneSheep()
    {
        var prefab = ResolveSheepPrefab();
        if (prefab == null)
            return false;

        for (int attempt = 0; attempt < 100; attempt++)
        {
            Vector3 pos = ToWorld(new Vector3(
                Random.Range(minX, maxX),
                y,
                Random.Range(minZ, maxZ)
            ));

            bool tooClose = false;
            foreach (var sheep in sheeps)
            {
                if (!IsAlive(sheep))
                    continue;

                if (Vector3.Distance(pos, sheep.transform.position) < minDistance)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
            {
                GameObject sheep = Instantiate(
                    prefab,
                    pos,
                    Quaternion.Euler(0f, Random.Range(0f, 360f), 0f),
                    transform
                );
                SetSheepSpawnArea(sheep);
                sheeps.Add(sheep);
                return true;
            }
        }

        return false;
    }

    private void SetSheepSpawnArea(GameObject sheepObj)
    {
        var wander = sheepObj.GetComponent<SheepWander>();
        if (wander == null)
            return;

        if (_envRoot == null)
            _envRoot = TrainingEnvSpace.FindRoot(transform);

        if (_envRoot != null)
            wander.SetSpawnAreaLocal(_envRoot, SpawnCenterLocal, maxDistanceFromSpawn);
        else
            wander.SetSpawnArea(ToWorld(SpawnCenterLocal), maxDistanceFromSpawn);
    }

    /// <summary>После сдвига Env (N) на место presentation — зоны возврата овец.</summary>
    public void RefreshSpawnAreas()
    {
        _envRoot = TrainingEnvSpace.FindRoot(transform);
        RemoveDestroyedSheep();
        for (int i = 0; i < sheeps.Count; i++)
        {
            if (IsAlive(sheeps[i]))
                SetSheepSpawnArea(sheeps[i]);
        }
    }

    public int TargetCount => sheepCount;

    public int AliveCount
    {
        get
        {
            RemoveDestroyedSheep();
            return sheeps.Count;
        }
    }

    public void ResetSheep()
    {
        PresentationWorldSnapshotLogger.Note(
            "sheep_reset",
            $"before={sheeps.Count} target={sheepCount} env={(_envRoot != null ? _envRoot.name : "?")}");
        ClearSheep();
        SpawnSheep();
    }

    private void SpawnSheep()
    {
        for (int i = 0; i < sheepCount; i++)
        {
            if (!SpawnOneSheep())
            {
                Debug.LogWarning($"SheepSpawner: не удалось разместить овцу {i + 1}/{sheepCount}");
            }
        }
    }

    private void ClearSheep()
    {
        foreach (var sheep in sheeps)
        {
            if (IsAlive(sheep))
                Destroy(sheep);
        }
        sheeps.Clear();

        // Сироты после Instantiate(Env): только овцы (SheepWander), не весь child-иерархию
        // (иначе можно снести scene-template и обнулить sheepPrefab).
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i).gameObject;
            if (IsUnityNull(child))
                continue;
            if (child.GetComponent<SheepWander>() == null)
                continue;
            Destroy(child);
        }
    }

    static bool IsUnityNull(GameObject go) => go == null;

    /// <summary>Спавн count овец вокруг worldPos (Twitch). Не удаляет существующих.</summary>
    public int SpawnSheepNear(Vector3 worldPos, int count, float radius = 7f)
    {
        if (ResolveSheepPrefab() == null)
            return 0;

        count = Mathf.Clamp(count, 1, 20);
        RemoveDestroyedSheep();

        int spawned = 0;
        float groundY = ToWorld(new Vector3(0f, y, 0f)).y;

        for (int i = 0; i < count; i++)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                Vector2 ring = Random.insideUnitCircle * radius;
                Vector3 pos = new Vector3(worldPos.x + ring.x, groundY, worldPos.z + ring.y);

                bool tooClose = false;
                foreach (var existing in sheeps)
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

                GameObject sheep = Instantiate(
                    sheepPrefab,
                    pos,
                    Quaternion.Euler(0f, Random.Range(0f, 360f), 0f),
                    transform
                );
                SetSheepSpawnArea(sheep);
                sheeps.Add(sheep);
                spawned++;
                break;
            }
        }

        return spawned;
    }
}
