using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Спавнит по 1 зомби каждые N секунд в фиксированной точке.
/// У префаба зомби должен быть слой Zombie и скрипты ZombieChase, ZombieHealth.
/// </summary>
public class ZombieSpawner : MonoBehaviour
{
    [Header("Prefab")]
    [SerializeField] private GameObject zombiePrefab;

    [Header("Spawn Settings")]
    [Tooltip("Если true: при старте спавнит 10 зомби сразу, затем раз в 2 секунды добавляет +1 (игнорируя лимит).")]
    [SerializeField] private bool zombie_from_hills = false;
    [Tooltip("Если true: при включении спавнит 8 idle-зомби вокруг спавнера и больше не спавнит.")]
    [SerializeField] private bool spawn_idle = false;
    [Tooltip("Один зомби появляется каждые столько секунд")]
    [SerializeField] private float spawnInterval = 5f;
    [Tooltip("Максимум зомби на сцене")]
    [SerializeField] private int maxZombies = 15;
    [Tooltip("Точка появления зомби")]
    [SerializeField] private Vector3 spawnPosition = new Vector3(3.25f, -0.03f, -5.93f);

    [Tooltip("Если включено — использовать позицию самого спавнера как точку спавна (удобно для ZombieSpawner_Hills).")]
    [SerializeField] private bool useTransformPositionAsSpawn = true;

    [Tooltip("Шаг сетки (расстояние между idle-зомби), если spawn_idle=true.")]
    [SerializeField] private float idleSpawnGridSpacing = 0.8f;

    [Tooltip("Сколько зомби заспавнить плотным квадратом (если spawn_idle=true).")]
    [SerializeField] private int idleSpawnCount = 10;

    private List<GameObject> zombies = new List<GameObject>();
    private float nextRespawnTime;
    Transform _spawnProxy;

    static bool IsAlive(GameObject go) => go != null;

    private void OnEnable()
    {
        // При включении объекта во время Play Start() может уже не вызываться — поэтому инициализация здесь.
        BootstrapSpawn();
    }

    private void Start()
    {
        // На случай, если объект активен с самого начала сцены.
        BootstrapSpawn();
    }

    private void BootstrapSpawn()
    {
        if (useTransformPositionAsSpawn)
            spawnPosition = transform.position;
        else
            spawnPosition = TrainingEnvSpace.LocalToWorld(transform, spawnPosition);

        if (spawn_idle)
        {
            SpawnIdleGrid(Mathf.Max(1, idleSpawnCount));
            nextRespawnTime = float.PositiveInfinity;
            return;
        }

        if (zombie_from_hills)
        {
            // Сразу спавним 10 зомби
            for (int i = 0; i < 10; i++)
                SpawnOne();

            // Дальше — раз в 2 секунды +1, независимо от лимита
            nextRespawnTime = Time.time + 2f;
        }
        else
        {
            nextRespawnTime = Time.time + spawnInterval;
        }
    }

    private void Update()
    {
        if (zombiePrefab == null) return;
        if (spawn_idle) return;
        if (Time.time < nextRespawnTime) return;
        nextRespawnTime = Time.time + (zombie_from_hills ? 2f : spawnInterval);

        RemoveDestroyed();
        if (zombie_from_hills || zombies.Count < maxZombies)
            SpawnOne();
    }

    private void RemoveDestroyed()
    {
        zombies.RemoveAll(z => !IsAlive(z));
    }

    Transform EnsureActiveSpawnProxy()
    {
        if (_spawnProxy != null)
            return _spawnProxy;

        var proxyGo = new GameObject($"{gameObject.name}_SpawnProxy");
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        proxyGo.transform.SetParent(envRoot != null ? envRoot : transform, false);
        proxyGo.transform.SetPositionAndRotation(transform.position, transform.rotation);
        proxyGo.SetActive(true);
        _spawnProxy = proxyGo.transform;
        return _spawnProxy;
    }

    Transform ResolveSpawnParent()
    {
        if (gameObject.activeInHierarchy)
            return transform;

        return EnsureActiveSpawnProxy();
    }

    void ClearSpawnProxyChildren()
    {
        if (_spawnProxy == null)
            return;

        for (int i = _spawnProxy.childCount - 1; i >= 0; i--)
        {
            var child = _spawnProxy.GetChild(i);
            if (child != null)
                Destroy(child.gameObject);
        }
    }

    void OnDestroy()
    {
        if (_spawnProxy != null)
            Destroy(_spawnProxy.gameObject);
    }

    private void SpawnOne()
    {
        SpawnOneAt(spawnPosition);
    }

    private void SpawnIdleGrid(int count)
    {
        if (zombiePrefab == null) return;
        if (count <= 0) return;

        Vector3 center = useTransformPositionAsSpawn ? transform.position : spawnPosition;
        // Плотная "квадратная" раскладка по XZ вокруг центра.
        // Для 10 получится 4+3+3 (всего 10), начиная от центра.
        int side = Mathf.CeilToInt(Mathf.Sqrt(count));
        float spacing = Mathf.Max(0.05f, idleSpawnGridSpacing);

        int spawned = 0;
        for (int z = 0; z < side && spawned < count; z++)
        {
            for (int x = 0; x < side && spawned < count; x++)
            {
                float ox = (x - (side - 1) * 0.5f) * spacing;
                float oz = (z - (side - 1) * 0.5f) * spacing;
                SpawnOneAt(center + new Vector3(ox, 0f, oz));
                spawned++;
            }
        }
    }

    private void SpawnOneAt(Vector3 worldPos)
    {
        if (zombiePrefab == null)
            return;

        Transform parent = ResolveSpawnParent();
        GameObject zombie = Instantiate(zombiePrefab, worldPos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), parent);
        SetZombieLayer(zombie);
        EnsureZombieComponents(zombie);
        zombies.Add(zombie);
    }

    private static void EnsureZombieComponents(GameObject zombie)
    {
        if (zombie == null) return;
        // Важно: на некоторых префабах компоненты могут быть на корне или на детях.
        // Для логики игры нам нужен ZombieChase и ZombieAttack на корне (или хотя бы в иерархии),
        // а ZombieHealth — чтобы зомби умирал от урона.
        if (zombie.GetComponentInChildren<ZombieChase>() == null)
            zombie.AddComponent<ZombieChase>();
        if (zombie.GetComponentInChildren<ZombieAttack>() == null)
            zombie.AddComponent<ZombieAttack>();
        if (zombie.GetComponentInChildren<ZombieHealth>() == null)
            zombie.AddComponent<ZombieHealth>();
    }

    private void SetZombieLayer(GameObject zombie)
    {
        int layer = LayerMask.NameToLayer("Zombie");
        if (layer >= 0)
            SetLayerRecursively(zombie, layer);
    }

    private void SetLayerRecursively(GameObject go, int layer)
    {
        if (!IsAlive(go))
            return;

        go.layer = layer;
        foreach (Transform child in go.transform)
        {
            if (child == null)
                continue;
            SetLayerRecursively(child.gameObject, layer);
        }
    }

    /// <summary>Очистить всех зомби (например при новом эпизоде).</summary>
    public void ClearZombies()
    {
        RemoveDestroyed();
        foreach (var z in zombies)
        {
            if (IsAlive(z))
                Destroy(z);
        }
        zombies.Clear();
        ClearSpawnProxyChildren();
    }

    /// <summary>Спавн count зомби вокруг worldPos (Twitch #zombie=N). Возвращает сколько создано.</summary>
    public int SpawnZombiesNear(Vector3 worldPos, int count, float radius = 7f)
    {
        if (zombiePrefab == null)
            return 0;

        count = Mathf.Clamp(count, 1, 10);
        RemoveDestroyed();

        int spawned = 0;
        float groundY = worldPos.y;

        for (int i = 0; i < count; i++)
        {
            Vector2 ring = Random.insideUnitCircle * radius;
            Vector3 pos = new Vector3(worldPos.x + ring.x, groundY, worldPos.z + ring.y);
            SpawnOneAt(pos);
            spawned++;
        }

        return spawned;
    }

    /// <summary>Убить всех зомби и заново запустить спавн (новый эпизод).</summary>
    public void ResetForNewEpisode()
    {
        ClearZombies();
        if (!gameObject.activeInHierarchy)
        {
            nextRespawnTime = Time.time + spawnInterval;
            return;
        }

        BootstrapSpawn();
    }
}
