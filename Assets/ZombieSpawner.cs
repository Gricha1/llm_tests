using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Спавнит по 1 зомби каждые N секунд в фиксированной точке.
/// У префаба зомби должен быть слой Zombie и скрипты ZombieChase, ZombieHealth.
/// </summary>
public class ZombieSpawner : MonoBehaviour
{
    const float DefaultSpawnScale = 1f;
    const float EnvGroundLocalY = -5.228786f;
    const float PrefabCharacterControllerHeight = 2f;
    const float PrefabCharacterControllerRadius = 0.5f;
    static readonly Vector3 PrefabCharacterControllerCenter = new Vector3(0f, 1f, 0f);

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
    [Tooltip("Множитель размера заспawnенного зомби (1 = как в префабе).")]
    [SerializeField] private float spawnScaleMultiplier = 1f;
    [Tooltip("Доп. смещение по Y поверх уровня земли Env (тонкая подстройка).")]
    [SerializeField] private float spawnHeightOffset = 0f;
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
    GameObject _resolvedZombiePrefab;
    Vector3 _prefabRootScale = Vector3.one;
    float _spawnMoveSpeedMultiplier = 1f;

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
        if (ResolveZombiePrefab() == null) return;
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

    Transform ResolveSpawnParent()
    {
        if (gameObject.activeInHierarchy)
            return transform;

        return null;
    }

    public static ZombieSpawner FindPresentationZombieSpawner()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root != null)
        {
            var spawners = root.GetComponentsInChildren<ZombieSpawner>(true);
            for (int i = 0; i < spawners.Length; i++)
            {
                if (spawners[i] != null && spawners[i].gameObject.name == "ZombieSpawner")
                    return spawners[i];
            }

            if (spawners.Length > 0)
                return spawners[0];
        }

        var all = Object.FindObjectsOfType<ZombieSpawner>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].gameObject.name == "ZombieSpawner")
                return all[i];
        }

        return all.Length > 0 ? all[0] : null;
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
        if (ResolveZombiePrefab() == null) return;
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
        GameObject prefab = ResolveZombiePrefab();
        if (prefab == null)
            return;

        worldPos = SnapSpawnToEnvGround(worldPos);
        GameObject zombie = Instantiate(prefab, worldPos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
        FinalizeSpawnedZombie(zombie);
        zombies.Add(zombie);
    }

    Vector3 SnapSpawnToEnvGround(Vector3 worldPos)
    {
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot != null)
        {
            Vector3 local = envRoot.InverseTransformPoint(worldPos);
            local.y = EnvGroundLocalY + spawnHeightOffset;
            return envRoot.TransformPoint(local);
        }

        return worldPos + Vector3.up * spawnHeightOffset;
    }

    float ResolveSpawnScale()
    {
        // В старых сценах поле могло не сериализоваться → 0 и зомби становились крошечными (0.1x).
        return spawnScaleMultiplier > 0.01f ? spawnScaleMultiplier : DefaultSpawnScale;
    }

    void ApplySpawnScale(GameObject zombie, float extraMultiplier = 1f)
    {
        if (zombie == null)
            return;

        float scaleMult = ResolveSpawnScale() * Mathf.Max(0.1f, extraMultiplier);
        zombie.transform.localScale = _prefabRootScale * scaleMult;

        // Модель крупная, но коллайдер — как у человека (не умножаем на scale).
        var cc = zombie.GetComponentInChildren<CharacterController>();
        if (cc != null)
        {
            cc.height = PrefabCharacterControllerHeight;
            cc.radius = PrefabCharacterControllerRadius;
            cc.center = PrefabCharacterControllerCenter;
        }
    }

    void FinalizeSpawnedZombie(GameObject zombie, float scaleExtra = 1f)
    {
        if (zombie == null)
            return;

        zombie.SetActive(true);
        ApplySpawnScale(zombie, scaleExtra);

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        zombie.transform.SetParent(envRoot != null ? envRoot : transform, true);
        SetZombieLayer(zombie);
        EnsureZombieComponents(zombie);

        var chase = zombie.GetComponentInChildren<ZombieChase>();
        if (chase != null)
        {
            chase.EnableAgentChaseMode();
            if (_spawnMoveSpeedMultiplier > 1f)
                chase.SetMoveSpeedMultiplier(_spawnMoveSpeedMultiplier);
            AssignEnvLocalTargets(zombie, transform, chase);
        }
    }

    public void SetSpawnMoveSpeedMultiplier(float multiplier)
    {
        _spawnMoveSpeedMultiplier = Mathf.Max(0.05f, multiplier);
    }

    public GameObject SpawnBossZombie(
        float scaleMultiplier = 3f,
        float hpMultiplier = 5f,
        float moveSpeedMultiplier = 1f,
        float attackDamageMultiplier = 1f,
        float attackCooldownMultiplier = 1f)
    {
        if (ResolveZombiePrefab() == null)
            return null;

        RemoveDestroyed();
        Vector3 pos = SnapSpawnToEnvGround(GetSpawnCenterWorld());
        GameObject zombie = Instantiate(ResolveZombiePrefab(), pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
        FinalizeSpawnedZombie(zombie, scaleMultiplier);
        zombies.Add(zombie);

        var health = zombie.GetComponentInChildren<ZombieHealth>();
        if (health != null)
        {
            int bossHp = Mathf.Max(1, Mathf.RoundToInt(health.MaxHp * Mathf.Max(1f, hpMultiplier)));
            health.ConfigureMaxHp(bossHp);
        }

        var chase = zombie.GetComponentInChildren<ZombieChase>();
        if (chase != null && moveSpeedMultiplier > 0f)
            chase.SetMoveSpeedMultiplier(moveSpeedMultiplier);

        var attack = zombie.GetComponentInChildren<ZombieAttack>();
        if (attack != null)
            attack.ConfigureCombat(attackDamageMultiplier, attackCooldownMultiplier);

        return zombie;
    }

    static void AssignEnvLocalTargets(GameObject zombie, Transform spawner, ZombieChase chase)
    {
        if (chase == null)
            return;

        var envRoot = TrainingEnvSpace.FindRoot(spawner);
        Transform jack = null;
        Transform lily = null;

        if (envRoot != null)
        {
            var agents = envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(false);
            for (int i = 0; i < agents.Length; i++)
            {
                var candidate = agents[i];
                if (candidate == null || TwitchEphemeralEffects.IsTwitchClone(candidate))
                    continue;
                if (TrainingEnvSpace.IsGeorgeAgent(candidate))
                    continue;
                jack = candidate.transform;
                break;
            }

            var lilyScript = envRoot.GetComponentInChildren<LilyScript>(false);
            if (lilyScript != null)
                lily = lilyScript.transform;
        }
        else
        {
            var presentationJack = TrainingEnvSpace.FindPresentationJack();
            if (presentationJack != null)
                jack = presentationJack.transform;

            var lilyRoot = TrainingEnvSpace.PresentationRoot;
            LilyScript presentationLily = lilyRoot != null
                ? lilyRoot.GetComponentInChildren<LilyScript>(false)
                : Object.FindObjectOfType<LilyScript>();
            if (presentationLily != null)
                lily = presentationLily.transform;
        }

        chase.SetPresentationTargets(jack, lily);
    }

    GameObject ResolveZombiePrefab()
    {
        if (_resolvedZombiePrefab != null)
            return _resolvedZombiePrefab;

        if (zombiePrefab != null && !zombiePrefab.scene.IsValid())
        {
            _resolvedZombiePrefab = zombiePrefab;
            _prefabRootScale = _resolvedZombiePrefab.transform.localScale;
            return _resolvedZombiePrefab;
        }

#if UNITY_EDITOR
        if (zombiePrefab != null)
        {
            var source = UnityEditor.PrefabUtility.GetCorrespondingObjectFromOriginalSource(zombiePrefab);
            if (source != null)
            {
                _resolvedZombiePrefab = source;
                _prefabRootScale = _resolvedZombiePrefab.transform.localScale;
                return _resolvedZombiePrefab;
            }
        }

        _resolvedZombiePrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Prefabs/FatZombie.prefab");
        if (_resolvedZombiePrefab != null)
        {
            _prefabRootScale = _resolvedZombiePrefab.transform.localScale;
            return _resolvedZombiePrefab;
        }

        _resolvedZombiePrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Prefabs/ZombieGoblin.prefab");
        if (_resolvedZombiePrefab != null)
        {
            _prefabRootScale = _resolvedZombiePrefab.transform.localScale;
            return _resolvedZombiePrefab;
        }

        _resolvedZombiePrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/3D Characters Zombie City Streets Lowpoly Pack - Lite/Prefabs/(P) Characters_Zombie_SuitMan_1.prefab");
        if (_resolvedZombiePrefab != null)
        {
            _prefabRootScale = _resolvedZombiePrefab.transform.localScale;
            return _resolvedZombiePrefab;
        }
#endif

        _resolvedZombiePrefab = zombiePrefab;
        if (_resolvedZombiePrefab != null)
            _prefabRootScale = _resolvedZombiePrefab.transform.localScale;
        return _resolvedZombiePrefab;
    }

    public Vector3 GetSpawnCenterWorld()
    {
        if (useTransformPositionAsSpawn)
            return transform.position;

        return TrainingEnvSpace.LocalToWorld(transform, spawnPosition);
    }

    /// <summary>Спавн ровно в точке ZombieSpawner (домик).</summary>
    public int SpawnZombiesAtSpawner(int count)
    {
        if (ResolveZombiePrefab() == null)
            return 0;

        count = Mathf.Clamp(count, 1, 10);
        RemoveDestroyed();

        Vector3 pos = GetSpawnCenterWorld();
        for (int i = 0; i < count; i++)
            SpawnOneAt(pos);

        return count;
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
        _spawnMoveSpeedMultiplier = 1f;
    }

    /// <summary>Спавн count зомби вокруг worldPos (Twitch #zombie=N). Возвращает сколько создано.</summary>
    public int SpawnZombiesNear(Vector3 worldPos, int count, float radius = 7f)
    {
        if (ResolveZombiePrefab() == null)
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

    /// <summary>Старт эпизода обучения: зомби сразу, дальше по spawnInterval.</summary>
    public void StartTrainingEpisode(int immediateCount = 2)
    {
        if (useTransformPositionAsSpawn)
            spawnPosition = transform.position;
        else
            spawnPosition = TrainingEnvSpace.LocalToWorld(transform, spawnPosition);

        RemoveDestroyed();
        immediateCount = Mathf.Clamp(immediateCount, 1, maxZombies);
        for (int i = 0; i < immediateCount; i++)
            SpawnOne();

        nextRespawnTime = Time.time + Mathf.Max(0.5f, spawnInterval);
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
