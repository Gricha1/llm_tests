#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Перенос игровых компонентов с Zombie на ZombieGoblin (модель и Animator Goblin остаются).
/// </summary>
public static class ZombieGoblinMigration
{
    const string PrefabPath = "Assets/Prefabs/ZombieGoblin.prefab";

    static readonly HashSet<Type> SkipCopy = new HashSet<Type>
    {
        typeof(Transform),
        typeof(Animator),
        typeof(Animation),
        typeof(SkinnedMeshRenderer),
        typeof(MeshRenderer),
        typeof(MeshFilter),
    };

    static GameObject FindZombieSource()
    {
        GameObject best = null;
        foreach (var chase in UnityEngine.Object.FindObjectsByType<ZombieChase>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (chase == null || chase.gameObject.scene.IsValid() == false)
                continue;
            var go = chase.gameObject;
            if (NameLooksLikeGoblin(go.name) || IsGoblinPrefabInstance(go))
                continue;
            if (best == null || go.name.Equals("Zombie", StringComparison.OrdinalIgnoreCase))
                best = go;
        }

        if (best != null)
            return best;

        return FindInLoadedScenes(n => n.Equals("Zombie", StringComparison.OrdinalIgnoreCase));
    }

    static GameObject FindOrCreateGoblin(GameObject source)
    {
        var inScene = FindGoblinInScene();
        if (inScene != null)
            return inScene;

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/ZombieGoblin.prefab");
        }

        if (prefab == null)
            return null;

        var parent = source != null ? source.transform.parent : null;
        var goblin = PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
        if (goblin == null)
            return null;

        Undo.RegisterCreatedObjectUndo(goblin, "Create ZombieGoblin");
        goblin.name = "ZombieGoblin";
        if (source != null)
        {
            goblin.transform.localPosition = source.transform.localPosition;
            goblin.transform.localRotation = source.transform.localRotation;
            goblin.transform.localScale = source.transform.localScale;
        }

        return goblin;
    }

    static bool IsGoblinPrefabInstance(GameObject go)
    {
        if (!PrefabUtility.IsPartOfPrefabInstance(go))
            return false;
        var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
        if (string.IsNullOrEmpty(path))
            return false;
        return path.IndexOf("ZombieGoblin", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("Zombie_T-Pose", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("GAMWILL", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool NameLooksLikeGoblin(string name) =>
        name.IndexOf("Goblin", StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.Equals("Zombie_T-Pose", StringComparison.OrdinalIgnoreCase);

    static GameObject FindGoblinInScene()
    {
        var byName = FindInLoadedScenes(n =>
            n.Equals("ZombieGoblin", StringComparison.OrdinalIgnoreCase) || NameLooksLikeGoblin(n));
        if (byName != null)
            return byName;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = FindGoblinByPrefab(root.transform);
                if (found != null)
                    return found;
            }
        }

        return null;
    }

    static GameObject FindGoblinByPrefab(Transform t)
    {
        var go = t.gameObject;
        if (PrefabUtility.IsPartOfPrefabInstance(go))
        {
            var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            if (!string.IsNullOrEmpty(path) &&
                (path.IndexOf("ZombieGoblin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 path.IndexOf("Zombie_T-Pose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 path.IndexOf("GAMWILL", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                if (!go.GetComponentInChildren<ZombieChase>(true))
                    return go;
            }
        }

        foreach (Transform child in t)
        {
            var found = FindGoblinByPrefab(child);
            if (found != null)
                return found;
        }

        return null;
    }

    [MenuItem("Forest Survival/Migrate Zombie to ZombieGoblin — SAVE SCENE")]
    public static void MigrateAndSave()
    {
        var source = FindZombieSource();
        if (source == null)
        {
            Debug.LogError("ZombieGoblinMigration: не найден «Zombie» / «zombie» со скриптом ZombieChase на открытой сцене.");
            return;
        }

        var goblin = FindOrCreateGoblin(source);
        if (goblin == null)
        {
            Debug.LogError(
                "ZombieGoblinMigration: нет Goblin на сцене (ZombieGoblin / Zombie_T-Pose) и не найден префаб Assets/Prefabs/ZombieGoblin.prefab.");
            return;
        }

        if (!goblin.name.Equals("ZombieGoblin", StringComparison.OrdinalIgnoreCase))
        {
            Undo.RecordObject(goblin, "Rename goblin");
            goblin.name = "ZombieGoblin";
        }

        if (!EditorUtility.DisplayDialog(
                "Migrate Zombie → ZombieGoblin",
                "Скопирует скрипты зомби на Goblin, сохранит префаб и обновит ZombieSpawner.\nСтарый Zombie будет выключен.",
                "Продолжить", "Отмена"))
            return;

        try
        {
            EditorUtility.DisplayProgressBar("Zombie migration", "Копируем компоненты…", 0.2f);

            CopyTransform(source.transform, goblin.transform);
            goblin.transform.SetParent(source.transform.parent, true);
            goblin.tag = source.tag;
            ApplyLayerRecursively(goblin.transform, source.layer);

            CopyRootComponents(source, goblin);
            CopyLogicChildren(source.transform, goblin.transform);

            goblin.SetActive(true);
            source.SetActive(false);

            EditorUtility.DisplayProgressBar("Zombie migration", "Сохраняем префаб…", 0.55f);
            var prefab = SaveGoblinPrefab(goblin);

            EditorUtility.DisplayProgressBar("Zombie migration", "Обновляем ZombieSpawner…", 0.8f);
            int spawners = UpdateZombieSpawnerPrefabs(prefab != null ? prefab : goblin);

            for (int i = 0; i < SceneManager.sceneCount; i++)
                EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneAt(i));

            Debug.Log(
                $"ZombieGoblinMigration: OK. Prefab={(prefab != null ? PrefabPath : "не сохранён")}, " +
                $"spawner'ов={spawners}. Сохрани сцену (Ctrl+S). Старый «Zombie» выключен.");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static void CopyTransform(Transform src, Transform dst)
    {
        Undo.RecordObject(dst, "Zombie migrate transform");
        dst.localPosition = src.localPosition;
        dst.localRotation = src.localRotation;
        dst.localScale = src.localScale;
    }

    static void CopyRootComponents(GameObject src, GameObject dst)
    {
        var byType = new Dictionary<Type, List<Component>>();
        foreach (var c in src.GetComponents<Component>())
        {
            if (c == null || SkipCopy.Contains(c.GetType()))
                continue;

            if (!byType.TryGetValue(c.GetType(), out var list))
            {
                list = new List<Component>();
                byType[c.GetType()] = list;
            }

            list.Add(c);
        }

        foreach (var kvp in byType)
        {
            var type = kvp.Key;
            var srcList = kvp.Value;
            var dstList = new List<Component>(dst.GetComponents(type));

            for (int i = dstList.Count - 1; i >= srcList.Count; i--)
                Undo.DestroyObjectImmediate(dstList[i]);

            dstList = new List<Component>(dst.GetComponents(type));

            for (int i = 0; i < srcList.Count; i++)
            {
                Component target;
                if (i < dstList.Count)
                    target = dstList[i];
                else
                {
                    target = Undo.AddComponent(dst, type);
                    if (target == null)
                        continue;
                }

                Undo.RecordObject(target, "Copy zombie component");
                EditorUtility.CopySerialized(srcList[i], target);
                EditorUtility.SetDirty(target);
            }
        }
    }

    static void CopyLogicChildren(Transform src, Transform dst)
    {
        for (int i = 0; i < src.childCount; i++)
        {
            var child = src.GetChild(i);
            if (IsVisualChild(child))
                continue;
            if (dst.Find(child.name) != null)
                continue;

            var clone = UnityEngine.Object.Instantiate(child.gameObject);
            clone.name = child.name;
            Undo.RegisterCreatedObjectUndo(clone, "Copy zombie child");
            clone.transform.SetParent(dst, false);
            clone.transform.localPosition = child.localPosition;
            clone.transform.localRotation = child.localRotation;
            clone.transform.localScale = child.localScale;
            ApplyLayerRecursively(clone.transform, child.gameObject.layer);
        }
    }

    static bool IsVisualChild(Transform t)
    {
        if (t.GetComponent<SkinnedMeshRenderer>() != null || t.GetComponent<MeshRenderer>() != null)
            return true;
        if (t.GetComponent<Animator>() != null && t.GetComponents<Component>().Length <= 2)
            return true;
        var n = t.name.ToLowerInvariant();
        if (n.Contains("metarig") || n.Contains("armature") || n.StartsWith("mixamorig"))
            return true;
        return false;
    }

    static int UpdateZombieSpawnerPrefabs(GameObject prefabOrRoot)
    {
        var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefabAsset == null)
            prefabAsset = prefabOrRoot;

        int updated = 0;
        var spawners = UnityEngine.Object.FindObjectsByType<ZombieSpawner>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (var spawner in spawners)
        {
            if (spawner == null || !spawner.gameObject.scene.IsValid())
                continue;

            var so = new SerializedObject(spawner);
            var prop = so.FindProperty("zombiePrefab");
            if (prop == null)
                continue;

            Undo.RecordObject(spawner, "Zombie spawner prefab");
            prop.objectReferenceValue = prefabAsset;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(spawner);
            updated++;
        }

        return updated;
    }

    static GameObject SaveGoblinPrefab(GameObject goblinRoot)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
            AssetDatabase.CreateFolder("Assets", "Prefabs");

        var temp = UnityEngine.Object.Instantiate(goblinRoot);
        temp.name = goblinRoot.name;
        try
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(temp, PrefabPath);
            AssetDatabase.SaveAssets();
            return prefab;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(temp);
        }
    }

    static void ApplyLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        foreach (Transform child in root)
            ApplyLayerRecursively(child, layer);
    }

    static GameObject FindInLoadedScenes(Func<string, bool> nameMatch)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = FindByName(root.transform, nameMatch);
                if (found != null)
                    return found;
            }
        }

        return null;
    }

    static GameObject FindByName(Transform t, Func<string, bool> nameMatch)
    {
        if (nameMatch(t.name))
            return t.gameObject;
        foreach (Transform child in t)
        {
            var found = FindByName(child, nameMatch);
            if (found != null)
                return found;
        }

        return null;
    }
}
#endif
