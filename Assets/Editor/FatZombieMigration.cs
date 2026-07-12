#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Копирует игровые компоненты с ZombieGoblin на FatZombie, сохраняет префаб и обновляет ZombieSpawner.
/// </summary>
public static class FatZombieMigration
{
    const string PrefabPath = "Assets/Prefabs/FatZombie.prefab";
    const string GoblinPrefabPath = "Assets/Prefabs/ZombieGoblin.prefab";
    const string ZombieControllerPath =
        "Assets/ResilientLogicGames/ChubyCharacterFree/Animations/Animator Zombie.controller";

    static readonly HashSet<Type> SkipCopy = new HashSet<Type>
    {
        typeof(Transform),
        typeof(Animator),
        typeof(Animation),
        typeof(SkinnedMeshRenderer),
        typeof(MeshRenderer),
        typeof(MeshFilter),
        typeof(LODGroup),
    };

    [MenuItem("Forest Survival/Migrate ZombieGoblin → FatZombie — SAVE SCENE")]
    public static void MigrateAndSave()
    {
        var source = FindGoblinSource();
        if (source == null)
        {
            Debug.LogError("FatZombieMigration: не найден ZombieGoblin (или зомби с ZombieChase) на открытой сцене.");
            return;
        }

        var fat = FindFatZombieInScene();
        if (fat == null)
        {
            Debug.LogError("FatZombieMigration: добавь в сцену объект «FatZombie» и повтори.");
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "Migrate → FatZombie",
                "Скопирует ZombieChase / ZombieAttack / ZombieHealth и др. с ZombieGoblin на FatZombie,\n" +
                "сохранит Assets/Prefabs/FatZombie.prefab и обновит ZombieSpawner.\n" +
                "Старый ZombieGoblin будет выключен.",
                "Продолжить", "Отмена"))
            return;

        try
        {
            EditorUtility.DisplayProgressBar("FatZombie migration", "Копируем компоненты…", 0.2f);

            CopyTransform(source.transform, fat.transform);
            fat.transform.SetParent(source.transform.parent, true);
            fat.tag = source.tag;
            ApplyLayerRecursively(fat.transform, source.layer);

            CopyRootComponents(source, fat);
            CopyLogicChildren(source.transform, fat.transform);
            SetupFatZombieAnimator(fat, source);
            EnsureAttackCollider(fat);

            fat.SetActive(true);
            if (source != fat)
                source.SetActive(false);

            EditorUtility.DisplayProgressBar("FatZombie migration", "Сохраняем префаб…", 0.55f);
            var prefab = SaveFatZombiePrefab(fat);

            EditorUtility.DisplayProgressBar("FatZombie migration", "Обновляем ZombieSpawner…", 0.8f);
            int spawners = UpdateZombieSpawnerPrefabs(prefab != null ? prefab : fat);

            for (int i = 0; i < SceneManager.sceneCount; i++)
                EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneAt(i));

            Debug.Log(
                $"FatZombieMigration: OK. Prefab={(prefab != null ? PrefabPath : "не сохранён")}, " +
                $"spawner'ов={spawners}. Сохрани сцену (Ctrl+S).");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static GameObject FindGoblinSource()
    {
        var inScene = FindInLoadedScenes(n =>
            n.Equals("ZombieGoblin", StringComparison.OrdinalIgnoreCase));
        if (inScene != null && inScene.GetComponentInChildren<ZombieChase>(true) != null)
            return inScene;

        foreach (var chase in UnityEngine.Object.FindObjectsByType<ZombieChase>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (chase == null || !chase.gameObject.scene.IsValid())
                continue;
            var go = chase.gameObject;
            if (go.name.IndexOf("FatZombie", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            return go;
        }

        return AssetDatabase.LoadAssetAtPath<GameObject>(GoblinPrefabPath);
    }

    static GameObject FindFatZombieInScene() =>
        FindInLoadedScenes(n => n.Equals("FatZombie", StringComparison.OrdinalIgnoreCase));

    static void SetupFatZombieAnimator(GameObject target, GameObject goblinSource)
    {
        var anim = target.GetComponent<Animator>() ?? target.GetComponentInChildren<Animator>();
        if (anim == null)
            return;

        Undo.RecordObject(anim, "FatZombie animator");

        var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ZombieControllerPath);
        if (controller != null)
            anim.runtimeAnimatorController = controller;
        else
        {
            var srcAnim = goblinSource.GetComponent<Animator>();
            if (srcAnim != null && srcAnim.runtimeAnimatorController != null)
                anim.runtimeAnimatorController = srcAnim.runtimeAnimatorController;
        }

        anim.applyRootMotion = false;
        EditorUtility.SetDirty(anim);
    }

    static void EnsureAttackCollider(GameObject root)
    {
        if (root.GetComponent<Collider>() != null)
            return;

        var cc = root.GetComponent<CharacterController>();
        if (cc == null)
            return;

        var cap = Undo.AddComponent<CapsuleCollider>(root);
        cap.height = cc.height;
        cap.radius = cc.radius;
        cap.center = cc.center;
        cap.isTrigger = false;
    }

    static void CopyTransform(Transform src, Transform dst)
    {
        Undo.RecordObject(dst, "FatZombie migrate transform");
        dst.localPosition = src.localPosition;
        dst.localRotation = src.localRotation;
        // Масштаб FatZombie оставляем как в сцене (модель уже крупная).
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
        if (n.Contains("metarig") || n.Contains("armature") || n.Contains("skel") || n.Contains("mesh")
            || n.StartsWith("mixamorig") || n.Contains("fatzombie_"))
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
            var prefabProp = so.FindProperty("zombiePrefab");
            var scaleProp = so.FindProperty("spawnScaleMultiplier");
            var heightProp = so.FindProperty("spawnHeightOffset");
            if (prefabProp == null)
                continue;

            Undo.RecordObject(spawner, "FatZombie spawner");
            prefabProp.objectReferenceValue = prefabAsset;
            if (scaleProp != null)
                scaleProp.floatValue = 1f;
            if (heightProp != null)
                heightProp.floatValue = 0f;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(spawner);
            updated++;
        }

        return updated;
    }

    static GameObject SaveFatZombiePrefab(GameObject root)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
            AssetDatabase.CreateFolder("Assets", "Prefabs");

        var temp = UnityEngine.Object.Instantiate(root);
        temp.name = "FatZombie";
        temp.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        temp.transform.localScale = root.transform.localScale;
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
