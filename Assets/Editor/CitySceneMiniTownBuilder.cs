#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Собирает небольшой городок в <see cref="CityScenePath"/> из префабов
/// "Low Poly Simple Urban City 3D Asset Pack". Запуск: меню Unity.
/// </summary>
public static class CitySceneMiniTownBuilder
{
    const string CityScenePath = "Assets/CityScene.unity";
    const string PackPrefabs = "Assets/Low Poly Simple Urban City 3D Asset Pack/Prefabs";
    const string RootObjectName = "CityPack_MiniTown";

    [MenuItem("Forest Survival/CityScene/Собрать мини-город (Urban City Pack)")]
    public static void BuildMiniTown()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var scene = EditorSceneManager.OpenScene(CityScenePath, OpenSceneMode.Single);
        if (!scene.IsValid())
        {
            Debug.LogError($"CitySceneMiniTownBuilder: не удалось открыть сцену {CityScenePath}");
            return;
        }

        var roadStraight = LoadPrefab($"{PackPrefabs}/Roads/Road_1.prefab");
        var roadCross = LoadPrefab($"{PackPrefabs}/Roads/Road_Crossroads_1.prefab");
        if (roadStraight == null || roadCross == null)
        {
            Debug.LogError("CitySceneMiniTownBuilder: не найдены Road_1 или Road_Crossroads_1. Проверь путь к ассет-паку.");
            return;
        }

        var buildingPrefabs = new List<GameObject>();
        for (int i = 1; i <= 9; i++)
        {
            var b = LoadPrefab($"{PackPrefabs}/Buildings/Building_{i}.prefab");
            if (b != null)
                buildingPrefabs.Add(b);
        }

        if (buildingPrefabs.Count == 0)
        {
            Debug.LogError("CitySceneMiniTownBuilder: не найдены префабы Building_1..9.");
            return;
        }

        Transform root = GetOrCreateRoot();
        ClearChildren(root);

        Undo.RegisterCompleteObjectUndo(root.gameObject, "City mini town");

        float roadZ = AxisSize(roadStraight, 2);
        float roadX = AxisSize(roadStraight, 0);
        Bounds crossB = WorldBounds(roadCross);

        // Центр — перекрёсток
        var crossGo = InstantiateUnder(root, roadCross, Vector3.zero, Quaternion.identity, "Crossroads_Center");

        // Отступ от центра перекрёстка до центра первого сегмента дороги
        float halfCrossZ = crossB.extents.z;
        float halfCrossX = crossB.extents.x;
        float halfRoadZ = roadZ * 0.5f;
        float halfRoadX = roadX * 0.5f;
        float gapZ = halfCrossZ + halfRoadZ;
        float gapX = halfCrossX + halfRoadX;

        int segmentsPerRay = 2;
        Quaternion rotAlongZ = Quaternion.identity;
        Quaternion rotAlongX = Quaternion.Euler(0f, 90f, 0f);

        for (int i = 1; i <= segmentsPerRay; i++)
        {
            float dz = gapZ + (i - 1) * roadZ;
            InstantiateUnder(root, roadStraight, new Vector3(0f, 0f, dz), rotAlongZ, $"Road_NS_N_{i}");
            InstantiateUnder(root, roadStraight, new Vector3(0f, 0f, -dz), rotAlongZ, $"Road_NS_S_{i}");
            float dx = gapX + (i - 1) * roadX;
            InstantiateUnder(root, roadStraight, new Vector3(dx, 0f, 0f), rotAlongX, $"Road_EW_E_{i}");
            InstantiateUnder(root, roadStraight, new Vector3(-dx, 0f, 0f), rotAlongX, $"Road_EW_W_{i}");
        }

        // Здания вдоль северной и южной «лучей» (по бокам главной оси Z)
        float streetHalfWidth = Mathf.Max(halfCrossX, halfRoadX) + 1f;
        float buildingOffset = streetHalfWidth + 4f;

        int bi = 0;

        // Ряды зданий вдоль улицы (север–юг)
        float[] rowZ =
        {
            0f,
            gapZ,
            gapZ + roadZ,
            -gapZ,
            -(gapZ + roadZ)
        };

        foreach (float z in rowZ)
        {
            var prefabL = buildingPrefabs[bi % buildingPrefabs.Count];
            bi++;
            var prefabR = buildingPrefabs[bi % buildingPrefabs.Count];
            bi++;

            float bxL = AxisSize(prefabL, 0) * 0.5f;
            float bxR = AxisSize(prefabR, 0) * 0.5f;

            InstantiateUnder(root, prefabL, new Vector3(-(buildingOffset + bxL), 0f, z),
                Quaternion.Euler(0f, 90f, 0f), $"Building_W_{z:F0}");
            InstantiateUnder(root, prefabR, new Vector3(buildingOffset + bxR, 0f, z),
                Quaternion.Euler(0f, -90f, 0f), $"Building_E_{z:F0}");
        }

        // Немного пропсов у перекрёстка
        var hydrant = LoadPrefab($"{PackPrefabs}/Props/Other/Hydrant.prefab");
        var tree = LoadPrefab($"{PackPrefabs}/Props/Other/Tree_1.prefab");
        var traffic = LoadPrefab($"{PackPrefabs}/Props/Road_Signs/Traffic_Light_1.prefab");
        if (hydrant != null)
            InstantiateUnder(root, hydrant, new Vector3(-2f, 0f, 2f), Quaternion.identity, "Hydrant");
        if (tree != null)
            InstantiateUnder(root, tree, new Vector3(6f, 0f, -5f), Quaternion.identity, "Tree");
        if (traffic != null)
            InstantiateUnder(root, traffic, new Vector3(3f, 0f, -3f), Quaternion.Euler(0f, 45f, 0f), "TrafficLight");

        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = crossGo;

        Debug.Log(
            "CitySceneMiniTownBuilder: мини-город собран в объекте «" + RootObjectName +
            "». Сохрани сцену (Ctrl+S), если всё устраивает.");
    }

    [MenuItem("Forest Survival/CityScene/Удалить мини-город (CityPack_MiniTown)")]
    public static void ClearMiniTown()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var scene = EditorSceneManager.OpenScene(CityScenePath, OpenSceneMode.Single);
        var root = GameObject.Find(RootObjectName);
        if (root == null)
        {
            Debug.LogWarning("CitySceneMiniTownBuilder: объект «" + RootObjectName + "» не найден.");
            return;
        }

        Undo.DestroyObjectImmediate(root);
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("CitySceneMiniTownBuilder: «" + RootObjectName + "» удалён.");
    }

    static GameObject LoadPrefab(string assetPath)
    {
        return AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
    }

    static Transform GetOrCreateRoot()
    {
        var existing = GameObject.Find(RootObjectName);
        if (existing != null)
            return existing.transform;

        var go = new GameObject(RootObjectName);
        Undo.RegisterCreatedObjectUndo(go, "Create " + RootObjectName);
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        return go.transform;
    }

    static void ClearChildren(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--)
            Undo.DestroyObjectImmediate(root.GetChild(i).gameObject);
    }

    static GameObject InstantiateUnder(Transform parent, GameObject prefab, Vector3 worldPos, Quaternion worldRot,
        string name)
    {
        var instance = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
        if (instance == null)
            return null;
        instance.name = name;
        instance.transform.SetPositionAndRotation(worldPos, worldRot);
        Undo.RegisterCreatedObjectUndo(instance, "Place " + name);
        return instance;
    }

    static Bounds WorldBounds(GameObject prefab)
    {
        var temp = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (temp == null)
            return new Bounds(Vector3.zero, Vector3.one * 8f);

        temp.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        temp.transform.localScale = Vector3.one;

        var renderers = temp.GetComponentsInChildren<Renderer>();
        Bounds b = default;
        var any = false;
        foreach (var r in renderers)
        {
            if (!any)
            {
                b = r.bounds;
                any = true;
            }
            else
                b.Encapsulate(r.bounds);
        }

        Object.DestroyImmediate(temp);
        return any ? b : new Bounds(Vector3.zero, Vector3.one * 8f);
    }

    static float AxisSize(GameObject prefab, int axis)
    {
        var s = WorldBounds(prefab).size;
        return axis == 0 ? s.x : axis == 1 ? s.y : s.z;
    }
}
#endif
