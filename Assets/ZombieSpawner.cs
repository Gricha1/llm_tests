using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Спавнит зомби. Высота в лесу — только local Y=0 у спавнера (без SerializeField на Y —
/// старые значения в сцене ставили world Y≈0 и зомби «висели»).
/// </summary>
public class ZombieSpawner : MonoBehaviour
{
    const float DefaultSpawnScale = 1f;
    const float PrefabCharacterControllerHeight = 2f;
    const float PrefabCharacterControllerRadius = 0.5f;
    static readonly Vector3 PrefabCharacterControllerCenter = new Vector3(0f, 1f, 0f);
    const float CityFixedSpawnWorldY = 0.44f;
    const float ForestSpawnLocalY = 0f;

    [Header("Prefab")]
    [SerializeField] private GameObject zombiePrefab;

    [Header("Spawn Settings")]
    [SerializeField] private bool zombie_from_hills = false;
    [SerializeField] private bool spawn_idle = false;
    [SerializeField] private float spawnInterval = 5f;
    [SerializeField] private int maxZombies = 15;
    [SerializeField] private float spawnScaleMultiplier = 1f;
    [SerializeField] private float idleSpawnGridSpacing = 0.8f;
    [SerializeField] private int idleSpawnCount = 10;

    readonly List<GameObject> zombies = new List<GameObject>();
    float nextRespawnTime;
    GameObject _resolvedZombiePrefab;
    Vector3 _prefabRootScale = Vector3.one;
    float _spawnMoveSpeedMultiplier = 1f;
    bool _bootstrapDone;
    RuntimeAnimatorController _cachedZombieAnimator;

    static bool IsCityScene()
    {
        string name = SceneManager.GetActiveScene().name;
        return !string.IsNullOrEmpty(name)
            && name.IndexOf("City", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool IsAlive(GameObject go) => go != null;

    void OnEnable()
    {
        if (_bootstrapDone)
            return;
        _bootstrapDone = true;
        BootstrapSpawn();
    }

    void Start()
    {
        if (_bootstrapDone)
            return;
        _bootstrapDone = true;
        BootstrapSpawn();
    }

    void OnDisable()
    {
        _bootstrapDone = false;
    }

    void BootstrapSpawn()
    {
        if (spawn_idle)
        {
            SpawnIdleGrid(Mathf.Max(1, idleSpawnCount));
            nextRespawnTime = float.PositiveInfinity;
            return;
        }

        if (zombie_from_hills)
        {
            for (int i = 0; i < 10; i++)
                SpawnOne();
            nextRespawnTime = Time.time + 2f;
        }
        else
        {
            nextRespawnTime = Time.time + spawnInterval;
        }
    }

    void Update()
    {
        if (ResolveZombiePrefab() == null)
            return;
        if (spawn_idle)
            return;
        if (Time.time < nextRespawnTime)
            return;

        nextRespawnTime = Time.time + (zombie_from_hills ? 2f : spawnInterval);
        RemoveDestroyed();
        if (zombie_from_hills || zombies.Count < maxZombies)
            SpawnOne();
    }

    void RemoveDestroyed() => zombies.RemoveAll(z => !IsAlive(z));

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

    void SpawnOne() => SpawnOneAt(Vector3.zero);

    void SpawnIdleGrid(int count)
    {
        if (ResolveZombiePrefab() == null || count <= 0)
            return;

        int side = Mathf.CeilToInt(Mathf.Sqrt(count));
        float spacing = Mathf.Max(0.05f, idleSpawnGridSpacing);
        int spawned = 0;
        for (int z = 0; z < side && spawned < count; z++)
        {
            for (int x = 0; x < side && spawned < count; x++)
            {
                float ox = (x - (side - 1) * 0.5f) * spacing;
                float oz = (z - (side - 1) * 0.5f) * spacing;
                SpawnOneForestLocal(ox, oz);
                spawned++;
            }
        }
    }

    void SpawnOneAt(Vector3 _)
    {
        RemoveDestroyed();
        if (zombies.Count >= maxZombies)
            return;
        if (ResolveZombiePrefab() == null)
            return;

        if (IsCityScene())
            SpawnOneCity();
        else
            SpawnOneForestLocal(Random.Range(-1.2f, 1.2f), Random.Range(-1.2f, 1.2f));
    }

    void SpawnOneForestLocal(float localX, float localZ)
    {
        GameObject zombie = Instantiate(ResolveZombiePrefab());
        zombie.name = "FatZombie";
        FinalizeForestZombie(zombie, localX, localZ, 1f);
        zombies.Add(zombie);
    }

    void SpawnOneCity()
    {
        GameObject zombie = Instantiate(ResolveZombiePrefab());
        zombie.name = "FatZombie";
        Vector3 worldPos = transform.position;
        worldPos.x += Random.Range(-1.2f, 1.2f);
        worldPos.z += Random.Range(-1.2f, 1.2f);
        worldPos.y = CityFixedSpawnWorldY;
        FinalizeCityZombie(zombie, worldPos, 1f);
        zombies.Add(zombie);
    }

    void ApplySpawnScale(GameObject zombie, float extraMultiplier)
    {
        if (zombie == null)
            return;

        float scaleMult = (spawnScaleMultiplier > 0.01f ? spawnScaleMultiplier : DefaultSpawnScale)
            * Mathf.Max(0.1f, extraMultiplier);
        zombie.transform.localScale = _prefabRootScale * scaleMult;

        var cc = zombie.GetComponentInChildren<CharacterController>();
        if (cc != null)
        {
            cc.height = PrefabCharacterControllerHeight;
            cc.radius = PrefabCharacterControllerRadius;
            cc.center = PrefabCharacterControllerCenter;
            cc.skinWidth = 0.08f;
            cc.enabled = false;
        }
    }

    void FinalizeForestZombie(GameObject zombie, float localX, float localZ, float scaleExtra)
    {
        zombie.SetActive(true);
        DisableAnimRootMotion(zombie);
        ApplySpawnScale(zombie, scaleExtra);
        DisableCharacterControllers(zombie);

        // Только local у спавнера — высота = высота спавнера/Джека, не world Y.
        zombie.transform.SetParent(transform, false);
        zombie.transform.localPosition = new Vector3(localX, ForestSpawnLocalY, localZ);
        zombie.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

        FinishSpawnedZombie(zombie, city: false);
    }

    void FinalizeCityZombie(GameObject zombie, Vector3 worldPos, float scaleExtra)
    {
        zombie.SetActive(true);
        DisableAnimRootMotion(zombie);
        ApplySpawnScale(zombie, scaleExtra);
        DisableCharacterControllers(zombie);

        zombie.transform.SetParent(null, false);
        zombie.transform.SetPositionAndRotation(
            worldPos,
            Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));

        FinishSpawnedZombie(zombie, city: true);
    }

    /// <summary>Совместимость с SpawnBossZombie(zombie, pos, scale).</summary>
    void FinalizeSpawnedZombie(GameObject zombie, Vector3? forcedWorldPos = null, float scaleExtra = 1f)
    {
        if (zombie == null)
            return;

        if (IsCityScene())
        {
            Vector3 p = forcedWorldPos ?? transform.position;
            p.y = CityFixedSpawnWorldY;
            FinalizeCityZombie(zombie, p, scaleExtra);
        }
        else
        {
            float lx = Random.Range(-1.2f, 1.2f);
            float lz = Random.Range(-1.2f, 1.2f);
            if (forcedWorldPos.HasValue)
            {
                Vector3 local = transform.InverseTransformPoint(forcedWorldPos.Value);
                lx = local.x;
                lz = local.z;
            }

            FinalizeForestZombie(zombie, lx, lz, scaleExtra);
        }
    }

    void FinishSpawnedZombie(GameObject zombie, bool city)
    {
        SetZombieLayer(zombie);
        EnsureZombieComponents(zombie);
        EnsureZombieAnimatorController(zombie);

        var cc = zombie.GetComponentInChildren<CharacterController>(true);
        if (cc != null)
            cc.enabled = true;

        var chase = zombie.GetComponentInChildren<ZombieChase>();
        if (chase == null)
            return;

        chase.EnableAgentChaseMode();
        if (city)
            chase.LockWorldY(CityFixedSpawnWorldY);
        else
            chase.LockParentLocalY(ForestSpawnLocalY);

        if (_spawnMoveSpeedMultiplier > 1f)
            chase.SetMoveSpeedMultiplier(_spawnMoveSpeedMultiplier);
        AssignEnvLocalTargets(zombie, transform, chase);
    }

    static void DisableCharacterControllers(GameObject zombie)
    {
        var ccs = zombie.GetComponentsInChildren<CharacterController>(true);
        for (int i = 0; i < ccs.Length; i++)
        {
            if (ccs[i] != null)
                ccs[i].enabled = false;
        }
    }

    static void DisableAnimRootMotion(GameObject zombie)
    {
        var animators = zombie.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < animators.Length; i++)
        {
            if (animators[i] != null)
                animators[i].applyRootMotion = false;
        }
    }

    void EnsureZombieAnimatorController(GameObject zombie)
    {
        if (zombie == null)
            return;

        var animators = zombie.GetComponentsInChildren<Animator>(true);
        var controller = ResolveZombieAnimatorController();
        for (int i = 0; i < animators.Length; i++)
        {
            var anim = animators[i];
            if (anim == null)
                continue;
            anim.applyRootMotion = false;
            if (anim.runtimeAnimatorController == null && controller != null)
                anim.runtimeAnimatorController = controller;
        }
    }

    RuntimeAnimatorController ResolveZombieAnimatorController()
    {
        if (_cachedZombieAnimator != null)
            return _cachedZombieAnimator;

#if UNITY_EDITOR
        _cachedZombieAnimator = UnityEditor.AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
            "Assets/ResilientLogicGames/ChubyCharacterFree/Animations/Animator Zombie.controller");
        if (_cachedZombieAnimator != null)
            return _cachedZombieAnimator;
#endif

        var prefab = ResolveZombiePrefab();
        if (prefab != null)
        {
            var anim = prefab.GetComponentInChildren<Animator>(true);
            if (anim != null)
                _cachedZombieAnimator = anim.runtimeAnimatorController;
        }

        return _cachedZombieAnimator;
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
        GameObject zombie = Instantiate(ResolveZombiePrefab());
        zombie.name = "FatZombie";
        FinalizeSpawnedZombie(zombie, transform.position, scaleMultiplier);
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

        if (jack == null)
        {
            var presentationJack = TrainingEnvSpace.FindPresentationJack();
            if (presentationJack != null)
                jack = presentationJack.transform;
        }

        if (lily == null)
        {
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

#if UNITY_EDITOR
        var gameFat = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/FatZombie.prefab");
        if (gameFat != null)
        {
            _resolvedZombiePrefab = gameFat;
            _prefabRootScale = gameFat.transform.localScale;
            return _resolvedZombiePrefab;
        }
#endif

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
            if (source != null
                && source.GetComponentInChildren<ZombieChase>(true) != null
                && HasAnimatorController(source))
            {
                _resolvedZombiePrefab = source;
                _prefabRootScale = source.transform.localScale;
                return _resolvedZombiePrefab;
            }
        }
#endif

        _resolvedZombiePrefab = zombiePrefab;
        if (_resolvedZombiePrefab != null)
            _prefabRootScale = _resolvedZombiePrefab.transform.localScale;
        return _resolvedZombiePrefab;
    }

    static bool HasAnimatorController(GameObject go)
    {
        if (go == null)
            return false;
        var anim = go.GetComponentInChildren<Animator>(true);
        return anim != null && anim.runtimeAnimatorController != null;
    }

    public Vector3 GetSpawnCenterWorld() => transform.position;

    public int SpawnZombiesAtSpawner(int count)
    {
        if (ResolveZombiePrefab() == null)
            return 0;

        count = Mathf.Clamp(count, 1, 10);
        RemoveDestroyed();
        for (int i = 0; i < count; i++)
            SpawnOne();
        return count;
    }

    static void EnsureZombieComponents(GameObject zombie)
    {
        if (zombie == null)
            return;
        if (zombie.GetComponentInChildren<ZombieChase>() == null)
            zombie.AddComponent<ZombieChase>();
        if (zombie.GetComponentInChildren<ZombieAttack>() == null)
            zombie.AddComponent<ZombieAttack>();
        if (zombie.GetComponentInChildren<ZombieHealth>() == null)
            zombie.AddComponent<ZombieHealth>();
        if (zombie.GetComponentInChildren<CharacterController>() == null)
        {
            var cc = zombie.AddComponent<CharacterController>();
            cc.height = PrefabCharacterControllerHeight;
            cc.radius = PrefabCharacterControllerRadius;
            cc.center = PrefabCharacterControllerCenter;
            cc.skinWidth = 0.08f;
        }
    }

    void SetZombieLayer(GameObject zombie)
    {
        int layer = LayerMask.NameToLayer("Zombie");
        if (layer >= 0)
            SetLayerRecursively(zombie, layer);
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        if (!IsAlive(go))
            return;

        go.layer = layer;
        foreach (Transform child in go.transform)
        {
            if (child != null)
                SetLayerRecursively(child.gameObject, layer);
        }
    }

    public void ClearZombies()
    {
        RemoveDestroyed();
        foreach (var z in zombies)
        {
            if (!IsAlive(z))
                continue;
            z.SetActive(false);
            Destroy(z);
        }
        zombies.Clear();
        _spawnMoveSpeedMultiplier = 1f;

        // Сироты без родителя.
        var all = FindObjectsOfType<Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null || t.parent != null)
                continue;
            string n = t.name;
            if (n.IndexOf("FatZombie", System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            Destroy(t.gameObject);
        }
    }

    /// <summary>Очистить всех зомби во всех Env (переключение меню K / #env_N).</summary>
    public static void ClearZombiesInAllEnvs()
    {
        var spawners = Object.FindObjectsOfType<ZombieSpawner>(true);
        for (int i = 0; i < spawners.Length; i++)
        {
            if (spawners[i] == null)
                continue;
            spawners[i].ClearZombies();
            if (spawners[i].gameObject.name.IndexOf("Hills", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (spawners[i].gameObject.activeSelf)
                spawners[i].gameObject.SetActive(false);
        }
    }

    public int SpawnZombiesNear(Vector3 worldPos, int count, float radius = 7f)
    {
        if (ResolveZombiePrefab() == null)
            return 0;

        count = Mathf.Clamp(count, 1, 10);
        RemoveDestroyed();
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            Vector2 ring = Random.insideUnitCircle * radius;
            if (IsCityScene())
            {
                Vector3 pos = new Vector3(worldPos.x + ring.x, CityFixedSpawnWorldY, worldPos.z + ring.y);
                GameObject zombie = Instantiate(ResolveZombiePrefab());
                zombie.name = "FatZombie";
                FinalizeCityZombie(zombie, pos, 1f);
                zombies.Add(zombie);
            }
            else
            {
                Vector3 local = transform.InverseTransformPoint(
                    new Vector3(worldPos.x + ring.x, transform.position.y, worldPos.z + ring.y));
                SpawnOneForestLocal(local.x, local.z);
            }

            spawned++;
        }

        return spawned;
    }

    public int StartTrainingEpisode(int immediateCount = 2)
    {
        ClearZombies();
        if (ResolveZombiePrefab() == null)
        {
            Debug.LogWarning($"[{name}] StartTrainingEpisode: zombiePrefab=null", this);
            return 0;
        }

        immediateCount = Mathf.Clamp(immediateCount, 1, maxZombies);
        for (int i = 0; i < immediateCount; i++)
            SpawnOne();

        nextRespawnTime = Time.time + Mathf.Max(0.5f, spawnInterval);
        return zombies.Count;
    }

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
