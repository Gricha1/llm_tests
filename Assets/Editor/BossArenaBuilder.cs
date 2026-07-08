#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Каменная арена внутри BossArena: пол + кольцо камней + Jack в центре.
/// Меню: Forest Survival → BossArena → Собрать каменную арену.
/// </summary>
public static class BossArenaBuilder
{
    const string RootName = "BossArena";
    const string JackPrefabPath = "Assets/ResilientLogicGames/ChubyCharacterFree/Prefabs/Chuby.prefab";
    const string RockMatPath = "Assets/SimpleLowPolyNature/Materials/RockMat.mat";
    const string RocksFolder = "Assets/SimpleLowPolyNature/Prefabs";

    const float FloorSize = 22f;
    const float FloorThickness = 0.45f;
    const float RingRadius = 9f;
    const int RingRockCount = 14;
    const float JackYOffset = 0.05f;

    [MenuItem("Forest Survival/BossArena/Собрать каменную арену")]
    public static void BuildBossArena()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var rockMat = AssetDatabase.LoadAssetAtPath<Material>(RockMatPath);
        var jackPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(JackPrefabPath);
        if (rockMat == null || jackPrefab == null)
        {
            Debug.LogError("BossArenaBuilder: не найден RockMat или Chuby.prefab.");
            return;
        }

        var rockPrefabs = LoadRockPrefabs();
        if (rockPrefabs.Length == 0)
        {
            Debug.LogError("BossArenaBuilder: не найдены префабы Rock1..Rock12.");
            return;
        }

        Transform root = GetOrCreateBossArenaRoot();
        Undo.RegisterCompleteObjectUndo(root.gameObject, "Build BossArena");
        ClearChildren(root);

        CreateStoneFloor(root, rockMat);
        CreateRockRing(root, rockPrefabs);
        CreateJack(root, jackPrefab);

        Selection.activeGameObject = root.gameObject;
        EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        Debug.Log(
            $"BossArenaBuilder: арена собрана в «{RootName}» (пол {FloorSize}×{FloorSize}, {RingRockCount} камней, Jack в центре). Сохрани сцену (Ctrl+S).");
    }

    [MenuItem("Forest Survival/BossArena/Очистить BossArena")]
    public static void ClearBossArena()
    {
        var root = GameObject.Find(RootName);
        if (root == null)
        {
            Debug.LogWarning($"BossArenaBuilder: «{RootName}» не найден в сцене.");
            return;
        }

        Undo.RegisterCompleteObjectUndo(root, "Clear BossArena");
        ClearChildren(root.transform);
        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log($"BossArenaBuilder: дочерние объекты «{RootName}» удалены.");
    }

    static GameObject[] LoadRockPrefabs()
    {
        var list = new System.Collections.Generic.List<GameObject>(12);
        for (int i = 1; i <= 12; i++)
        {
            var rock = AssetDatabase.LoadAssetAtPath<GameObject>($"{RocksFolder}/Rock{i}.prefab");
            if (rock != null)
                list.Add(rock);
        }

        return list.ToArray();
    }

    static Transform GetOrCreateBossArenaRoot()
    {
        var existing = GameObject.Find(RootName);
        if (existing != null)
            return existing.transform;

        var go = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(go, "Create BossArena");
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        return go.transform;
    }

    static void CreateStoneFloor(Transform root, Material rockMat)
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "StoneFloor";
        Undo.RegisterCreatedObjectUndo(floor, "Create StoneFloor");
        floor.transform.SetParent(root, false);
        floor.transform.localPosition = new Vector3(0f, -FloorThickness * 0.5f, 0f);
        floor.transform.localRotation = Quaternion.identity;
        floor.transform.localScale = new Vector3(FloorSize, FloorThickness, FloorSize);

        var renderer = floor.GetComponent<MeshRenderer>();
        if (renderer != null)
            renderer.sharedMaterial = rockMat;
    }

    static void CreateRockRing(Transform root, GameObject[] rockPrefabs)
    {
        var ringRoot = new GameObject("RockRing");
        Undo.RegisterCreatedObjectUndo(ringRoot, "Create RockRing");
        ringRoot.transform.SetParent(root, false);
        ringRoot.transform.localPosition = Vector3.zero;
        ringRoot.transform.localRotation = Quaternion.identity;

        for (int i = 0; i < RingRockCount; i++)
        {
            float angle = (Mathf.PI * 2f * i) / RingRockCount;
            float x = Mathf.Cos(angle) * RingRadius;
            float z = Mathf.Sin(angle) * RingRadius;
            var prefab = rockPrefabs[i % rockPrefabs.Length];

            var rock = PrefabUtility.InstantiatePrefab(prefab, ringRoot.transform) as GameObject;
            if (rock == null)
                continue;

            rock.name = $"Rock_{i + 1:00}";
            Undo.RegisterCreatedObjectUndo(rock, "Place ring rock");

            float yaw = Mathf.Atan2(-x, -z) * Mathf.Rad2Deg;
            float scale = Random.Range(1.1f, 1.6f);
            rock.transform.localPosition = new Vector3(x, 0f, z);
            rock.transform.localRotation = Quaternion.Euler(0f, yaw + Random.Range(-18f, 18f), 0f);
            rock.transform.localScale = Vector3.one * scale;
        }
    }

    static void CreateJack(Transform root, GameObject jackPrefab)
    {
        var jack = PrefabUtility.InstantiatePrefab(jackPrefab, root) as GameObject;
        if (jack == null)
            return;

        jack.name = "Jack";
        Undo.RegisterCreatedObjectUndo(jack, "Place Jack");
        jack.transform.localPosition = new Vector3(0f, JackYOffset, 0f);
        jack.transform.localRotation = Quaternion.identity;
    }

    static void ClearChildren(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--)
            Undo.DestroyObjectImmediate(root.GetChild(i).gameObject);
    }
}
#endif
