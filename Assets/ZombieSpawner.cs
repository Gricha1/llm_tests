using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

/// <summary>
/// Спавнит зомби. Высота в лесу — только local Y=0 у спавнера (без SerializeField на Y —
/// старые значения в сцене ставили world Y≈0 и зомби «висели»).
/// </summary>
public class ZombieSpawner : MonoBehaviour
{
    const float DefaultSpawnScale = 1f;
    const float PrefabCharacterControllerHeight = 2f;
    // Не раздувать CC: иначе Jack.radius+zombie.radius ≈ «стена». Разнос зомби — Separation.
    const float PrefabCharacterControllerRadius = 0.45f;
    static readonly Vector3 PrefabCharacterControllerCenter = new Vector3(0f, 1f, 0f);
    const float CityFixedSpawnWorldY = 0.44f;
    // Как у Jack; Y спавнера в сцене ≈ -5.92 — на нём зомби тонут и стопорятся.
    const float ForestGroundEnvLocalY = -5.228786f;

    [Header("Prefab")]
    [SerializeField] private GameObject zombiePrefab;

    public GameObject PeekZombiePrefab() => zombiePrefab;

    [Header("Spawn Settings")]
    [SerializeField] private bool zombie_from_hills = false;
    [SerializeField] private bool spawn_idle = false;
    [SerializeField] private float spawnInterval = 5f;
    [SerializeField] private int maxZombies = 15;
    [SerializeField] private float spawnScaleMultiplier = 1f;
    [SerializeField] private float idleSpawnGridSpacing = 0.8f;
    [SerializeField] private int idleSpawnCount = 10;

    const string ShirtlessResourceName = "ShirtlessZombie";
    const string ShirtlessBundleName = "zombie_skins";
    const string ShirtlessBundleAssetName = "ShirtlessZombie";
    const string FatPrefabEditorPath = "Assets/Prefabs/FatZombie.prefab";
    const string ShirtlessPrefabEditorPath = "Assets/Prefabs/ShirtlessZombie.prefab";
    /// <summary>DLL marker — apocalypse mixes Fat + Shirtless skins.</summary>
    public const string ShirtlessSpawnMarker = "SHIRTLESS_ZOMBIE_SPAWN";

    readonly List<GameObject> zombies = new List<GameObject>();
    float nextRespawnTime;
    GameObject _resolvedZombiePrefab;
    Vector3 _prefabRootScale = Vector3.one;
    GameObject _fatPrefab;
    GameObject _shirtlessPrefab;
    Vector3 _fatPrefabScale = Vector3.one;
    Vector3 _shirtlessPrefabScale = Vector3.one;
    bool _skinCatalogResolved;
    static AssetBundle _zombieSkinsBundle;
    float _spawnMoveSpeedMultiplier = 1f;
    bool _bootstrapDone;
    /// <summary>Presentation apocalypse: runtime cap/interval override (−1 = use SerializeField).</summary>
    int _pressureMax = -1;
    float _pressureInterval = -1f;
    RuntimeAnimatorController _cachedZombieAnimator;
    /// <summary>После Twitch #add zombie — не досыпать в Update до maxZombies.</summary>
    bool _periodicSpawnSuppressed;

    public int AliveCount
    {
        get
        {
            RemoveDestroyed();
            return zombies.Count;
        }
    }

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

        // Меню K / клон Env (N): не BootstrapSpawn (hills×10) — иначе hitch на каждом #env_N.
        if (ShouldSkipAutoBootstrap())
        {
            nextRespawnTime = Time.time + Mathf.Max(0.5f, spawnInterval);
            return;
        }

        BootstrapSpawn();
    }

    void Start()
    {
        if (_bootstrapDone)
            return;
        _bootstrapDone = true;

        if (ShouldSkipAutoBootstrap())
        {
            nextRespawnTime = Time.time + Mathf.Max(0.5f, spawnInterval);
            return;
        }

        BootstrapSpawn();
    }

    // Не сбрасываем _bootstrapDone в OnDisable: SetActive при #env_N иначе снова hills×10.

    bool ShouldSkipAutoBootstrap()
    {
        if (TrainingEnvSpace.IsDebugEnvFocusActive)
            return true;
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        return envRoot != null && TrainingEnvSpace.GetEnvCopyIndex(envRoot) > 0;
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
        if (_periodicSpawnSuppressed)
            return;

        // Меню K: досыпать только на JackZombie, раз в 2 сек (не hills-армия).
        if (TrainingEnvSpace.IsDebugEnvFocusActive)
        {
            if (!TrainingEnvSpace.IsJackZombieDebugFocus)
                return;
            if (zombie_from_hills)
                return;
        }

        float interval = TrainingEnvSpace.IsDebugEnvFocusActive
            ? 2f
            : (zombie_from_hills ? 2f : EffectiveSpawnInterval);

        if (Time.time < nextRespawnTime)
            return;

        nextRespawnTime = Time.time + interval;
        RemoveDestroyed();
        if (zombie_from_hills || zombies.Count < EffectiveMaxZombies)
            SpawnOne();
    }

    int EffectiveMaxZombies => _pressureMax > 0 ? _pressureMax : maxZombies;
    float EffectiveSpawnInterval => _pressureInterval > 0f ? _pressureInterval : spawnInterval;

    /// <summary>
    /// Presentation этап 2: со временем поднимать лимит живых зомби и ускорять спавн.
    /// </summary>
    public void SetApocalypsePressure(int maxAlive, float intervalSeconds)
    {
        _pressureMax = Mathf.Clamp(maxAlive, 1, 60);
        _pressureInterval = Mathf.Clamp(intervalSeconds, 0.8f, 12f);
        // Если уже ниже нового капа — не ждать полный старый interval.
        if (zombies.Count < EffectiveMaxZombies && nextRespawnTime > Time.time + _pressureInterval)
            nextRespawnTime = Time.time + Mathf.Min(1.5f, _pressureInterval);
    }

    public void ClearApocalypsePressure()
    {
        _pressureMax = -1;
        _pressureInterval = -1f;
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

        var all = Object.FindObjectsByType<ZombieSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None);
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
        if (zombies.Count >= EffectiveMaxZombies)
            return;
        if (ResolveZombiePrefab() == null)
            return;

        if (IsCityScene())
            SpawnOneCity();
        else
            SpawnOneForestLocal(Random.Range(-2.2f, 2.2f), Random.Range(-2.2f, 2.2f));
    }

    void SpawnOneForestLocal(float localX, float localZ)
    {
        if (!TryInstantiateZombie(out GameObject zombie))
            return;
        FinalizeForestZombie(zombie, localX, localZ, 1f);
        zombies.Add(zombie);
    }

    void SpawnOneCity()
    {
        if (!TryInstantiateZombie(out GameObject zombie))
            return;
        Vector3 worldPos = transform.position;
        worldPos.x += Random.Range(-1.2f, 1.2f);
        worldPos.z += Random.Range(-1.2f, 1.2f);
        worldPos.y = CityFixedSpawnWorldY;
        FinalizeCityZombie(zombie, worldPos, 1f);
        zombies.Add(zombie);
    }

    bool TryInstantiateZombie(out GameObject zombie)
    {
        zombie = null;
        GameObject prefab = PickSpawnPrefab(out Vector3 rootScale, out string spawnName);
        if (prefab == null)
            return false;

        _prefabRootScale = rootScale;
        zombie = Instantiate(prefab);
        zombie.name = spawnName;
        return true;
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
            // Радиус растёт со scale — иначе толстый меш пересекается, а CC тонкий.
            float r = PrefabCharacterControllerRadius * Mathf.Max(1f, scaleMult);
            cc.height = PrefabCharacterControllerHeight * Mathf.Max(1f, scaleMult);
            cc.radius = r;
            cc.center = new Vector3(
                PrefabCharacterControllerCenter.x,
                PrefabCharacterControllerCenter.y * Mathf.Max(1f, scaleMult),
                PrefabCharacterControllerCenter.z);
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

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        // XZ у спавнера (дом), Y — уровень пола Env как у Jack (не Y объекта Spawner).
        Vector3 worldAtSpawner = transform.TransformPoint(new Vector3(localX, 0f, localZ));
        Vector3 envLocal;
        if (envRoot != null)
        {
            envLocal = envRoot.InverseTransformPoint(worldAtSpawner);
            envLocal.y = ForestGroundEnvLocalY;
            zombie.transform.SetParent(envRoot, false);
        }
        else
        {
            envLocal = new Vector3(localX, ForestGroundEnvLocalY, localZ);
            zombie.transform.SetParent(transform, false);
        }

        zombie.transform.localPosition = envLocal;
        zombie.transform.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

        if (!IsFinitePos(zombie.transform.position) || !IsFinitePos(zombie.transform.localScale))
        {
            Vector3 world = worldAtSpawner;
            world.y = envRoot != null
                ? envRoot.TransformPoint(new Vector3(0f, ForestGroundEnvLocalY, 0f)).y
                : world.y;
            if (!IsFinitePos(world))
                world = Vector3.up;
            zombie.transform.SetParent(null, false);
            zombie.transform.SetPositionAndRotation(world, Quaternion.identity);
            zombie.transform.localScale = Vector3.one;
            ApplySpawnScale(zombie, scaleExtra);
            if (envRoot != null)
            {
                zombie.transform.SetParent(envRoot, true);
                var lp = zombie.transform.localPosition;
                lp.y = ForestGroundEnvLocalY;
                zombie.transform.localPosition = lp;
            }
        }

        FinishSpawnedZombie(zombie, city: false);
    }

    static bool IsFinitePos(Vector3 v) =>
        !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
          || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)
          || Mathf.Abs(v.x) > 100000f || Mathf.Abs(v.y) > 100000f || Mathf.Abs(v.z) > 100000f);

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
        chase.ConfigureApproach(0.4f);
        if (city)
            chase.LockWorldY(CityFixedSpawnWorldY);
        else
        {
            var envRoot = TrainingEnvSpace.FindRoot(transform);
            if (envRoot != null)
                chase.LockEnvLocalY(envRoot, ForestGroundEnvLocalY);
        }

        var attack = zombie.GetComponentInChildren<ZombieAttack>();
        if (attack != null)
            attack.ConfigureDamageRadius(1.9f);

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

        _cachedZombieAnimator = EditorLoadAsset<RuntimeAnimatorController>(
            "Assets/ResilientLogicGames/ChubyCharacterFree/Animations/Animator Zombie.controller");
        if (_cachedZombieAnimator != null)
            return _cachedZombieAnimator;

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
        if (!TryInstantiateZombie(out GameObject zombie))
            return null;
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
                : Object.FindFirstObjectByType<LilyScript>();
            if (presentationLily != null)
                lily = presentationLily.transform;
        }

        chase.SetPresentationTargets(jack, lily);
    }

    GameObject ResolveZombiePrefab()
    {
        EnsureSkinCatalog();
        if (_resolvedZombiePrefab != null)
            return _resolvedZombiePrefab;

        if (_fatPrefab != null)
        {
            _resolvedZombiePrefab = _fatPrefab;
            _prefabRootScale = _fatPrefabScale;
            return _resolvedZombiePrefab;
        }

        if (_shirtlessPrefab != null)
        {
            _resolvedZombiePrefab = _shirtlessPrefab;
            _prefabRootScale = _shirtlessPrefabScale;
            return _resolvedZombiePrefab;
        }

        return null;
    }

    void EnsureSkinCatalog()
    {
        if (_skinCatalogResolved)
            return;
        _skinCatalogResolved = true;

        // Keep marker string in the player DLL for deploy verification.
        _ = ShirtlessSpawnMarker;

        _fatPrefab = ResolveFatPrefab();
        if (_fatPrefab != null)
            _fatPrefabScale = _fatPrefab.transform.localScale;

        _shirtlessPrefab = ResolveShirtlessPrefab();
        if (_shirtlessPrefab != null)
            _shirtlessPrefabScale = _shirtlessPrefab.transform.localScale;

        if (_fatPrefab != null)
        {
            _resolvedZombiePrefab = _fatPrefab;
            _prefabRootScale = _fatPrefabScale;
        }
        else if (_shirtlessPrefab != null)
        {
            _resolvedZombiePrefab = _shirtlessPrefab;
            _prefabRootScale = _shirtlessPrefabScale;
        }
    }

    GameObject PickSpawnPrefab(out Vector3 rootScale, out string spawnName)
    {
        EnsureSkinCatalog();

        bool hasFat = _fatPrefab != null;
        bool hasShirtless = _shirtlessPrefab != null;
        if (hasFat && hasShirtless)
        {
            // Apocalypse mix: ~half Fat, ~half Shirtless.
            if (Random.value < 0.5f)
            {
                rootScale = _fatPrefabScale;
                spawnName = "FatZombie";
                return _fatPrefab;
            }

            rootScale = _shirtlessPrefabScale;
            spawnName = "ShirtlessZombie";
            return _shirtlessPrefab;
        }

        if (hasShirtless)
        {
            rootScale = _shirtlessPrefabScale;
            spawnName = "ShirtlessZombie";
            return _shirtlessPrefab;
        }

        if (hasFat)
        {
            rootScale = _fatPrefabScale;
            spawnName = "FatZombie";
            return _fatPrefab;
        }

        rootScale = Vector3.one;
        spawnName = "FatZombie";
        return null;
    }

    GameObject ResolveFatPrefab()
    {
        var gameFat = EditorLoadAsset<GameObject>(FatPrefabEditorPath);
        if (gameFat != null)
            return gameFat;

        if (zombiePrefab != null && !zombiePrefab.scene.IsValid())
            return zombiePrefab;

        if (zombiePrefab != null)
        {
            var source = EditorPrefabOriginal(zombiePrefab);
            if (source != null
                && source.GetComponentInChildren<ZombieChase>(true) != null
                && HasAnimatorController(source))
                return source;
        }

        return zombiePrefab;
    }

    GameObject ResolveShirtlessPrefab()
    {
        var fromResources = Resources.Load<GameObject>(ShirtlessResourceName);
        if (fromResources != null)
            return fromResources;

        var fromBundle = LoadShirtlessFromAssetBundle();
        if (fromBundle != null)
            return fromBundle;

        return EditorLoadAsset<GameObject>(ShirtlessPrefabEditorPath);
    }

    static GameObject LoadShirtlessFromAssetBundle()
    {
        try
        {
            if (_zombieSkinsBundle == null)
            {
                string path = Path.Combine(Application.streamingAssetsPath, ShirtlessBundleName);
                if (!File.Exists(path))
                    return null;
                _zombieSkinsBundle = AssetBundle.LoadFromFile(path);
                if (_zombieSkinsBundle == null)
                    return null;
            }

            return _zombieSkinsBundle.LoadAsset<GameObject>(ShirtlessBundleAssetName);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ZombieSpawner] shirtless bundle load failed: {e.Message}");
            return null;
        }
    }

    static bool HasAnimatorController(GameObject go)
    {
        if (go == null)
            return false;
        var anim = go.GetComponentInChildren<Animator>(true);
        return anim != null && anim.runtimeAnimatorController != null;
    }

    // No hard UnityEditor reference: ScriptAssemblies (editor) DLL is copied into
    // the Linux player, and AssetDatabase would MissingMethod/FileNotFound every frame.
    static T EditorLoadAsset<T>(string path) where T : UnityEngine.Object
    {
        if (!Application.isEditor || string.IsNullOrEmpty(path))
            return null;
        try
        {
            var db = System.Type.GetType("UnityEditor.AssetDatabase, UnityEditor");
            if (db == null)
                return null;
            var method = db.GetMethod("LoadAssetAtPath", new[] { typeof(string), typeof(System.Type) });
            if (method == null)
                return null;
            return method.Invoke(null, new object[] { path, typeof(T) }) as T;
        }
        catch
        {
            return null;
        }
    }

    static GameObject EditorPrefabOriginal(GameObject instance)
    {
        if (!Application.isEditor || instance == null)
            return null;
        try
        {
            var util = System.Type.GetType("UnityEditor.PrefabUtility, UnityEditor");
            if (util == null)
                return null;
            var method = util.GetMethod("GetCorrespondingObjectFromOriginalSource",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(GameObject) },
                null);
            if (method == null)
                return null;
            return method.Invoke(null, new object[] { instance }) as GameObject;
        }
        catch
        {
            return null;
        }
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
        ClearZombies(scanOrphanRoots: true);
    }

    public void ClearZombies(bool scanOrphanRoots)
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
        ClearApocalypsePressure();

        // Полный FindObjectsOfType по сцене — дорого; при ClearZombiesInAllEnvs делаем один раз.
        if (scanOrphanRoots)
            DestroyOrphanZombieRoots();
    }

    static void DestroyOrphanZombieRoots()
    {
        // Только корни сцены — не FindObjectsOfType<Transform> по всей иерархии (хитч при #env_N).
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!scene.IsValid())
            return;

        var roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            var go = roots[i];
            if (go == null)
                continue;
            string n = go.name;
            if (n.IndexOf("FatZombie", System.StringComparison.OrdinalIgnoreCase) < 0
                && n.IndexOf("ShirtlessZombie", System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            Object.Destroy(go);
        }
    }

    /// <summary>Очистить всех зомби во всех Env (переключение меню K / #env_N).</summary>
    public static void ClearZombiesInAllEnvs()
    {
        var spawners = Object.FindObjectsByType<ZombieSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < spawners.Length; i++)
        {
            if (spawners[i] == null)
                continue;
            // orphan-scan один раз в конце — иначе N×FindObjectsOfType → hitch при #env_N
            spawners[i].ClearZombies(scanOrphanRoots: false);
            if (spawners[i].gameObject.name.IndexOf("Hills", System.StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (spawners[i].gameObject.activeSelf)
                spawners[i].gameObject.SetActive(false);
        }

        DestroyOrphanZombieRoots();
    }

    public int SpawnZombiesNear(Vector3 worldPos, int count, float radius = 7f)
    {
        if (ResolveZombiePrefab() == null)
            return 0;

        // Twitch #add zombie: спавнер мог быть выключен ClearZombiesInAllEnvs.
        bool wasInactive = !gameObject.activeSelf;
        if (wasInactive)
            gameObject.SetActive(true);

        count = Mathf.Clamp(count, 1, 10);
        RemoveDestroyed();
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            Vector2 ring = Random.insideUnitCircle * radius;
            if (IsCityScene())
            {
                Vector3 pos = new Vector3(worldPos.x + ring.x, CityFixedSpawnWorldY, worldPos.z + ring.y);
                if (!TryInstantiateZombie(out GameObject zombie))
                    break;
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

        // Только N зомби из чата — не включать бесконечный Update-респавн до maxZombies.
        _periodicSpawnSuppressed = true;
        nextRespawnTime = float.PositiveInfinity;
        if (wasInactive && gameObject.activeSelf)
            gameObject.SetActive(false);

        return spawned;
    }

    float _lastTrainingEpisodeStartTime = -999f;

    public int StartTrainingEpisode(int immediateCount = 2)
    {
        // Меню K: ForceApply / EndEpisode / delayed ForceStart зовут подряд — без debounce лаги.
        if (zombies.Count > 0
            && Time.unscaledTime - _lastTrainingEpisodeStartTime < 1f)
            return zombies.Count;

        _periodicSpawnSuppressed = false;
        ClearZombies(scanOrphanRoots: false);
        if (ResolveZombiePrefab() == null)
        {
            Debug.LogWarning($"[{name}] StartTrainingEpisode: zombiePrefab=null", this);
            return 0;
        }

        // Меню K: всегда ровно 1 — иначе 2 в ±1.2м выглядят как «стопка» + hitch Instantiate.
        if (TrainingEnvSpace.IsDebugEnvFocusActive)
            immediateCount = 1;
        else
            immediateCount = Mathf.Clamp(immediateCount, 1, EffectiveMaxZombies);

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);

        for (int i = 0; i < immediateCount; i++)
            SpawnOne();

        nextRespawnTime = Time.time + (TrainingEnvSpace.IsDebugEnvFocusActive
            ? 2f
            : Mathf.Max(0.5f, EffectiveSpawnInterval));
        _lastTrainingEpisodeStartTime = Time.unscaledTime;
        return zombies.Count;
    }

    public void ResetForNewEpisode()
    {
        ClearZombies();
        if (!gameObject.activeInHierarchy || ShouldSkipAutoBootstrap())
        {
            nextRespawnTime = Time.time + spawnInterval;
            return;
        }

        BootstrapSpawn();
    }
}
