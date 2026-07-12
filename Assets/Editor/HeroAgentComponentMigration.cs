#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Перенос ML/agent-компонентов с Jack/Lily на JackHero/LilyHero.
/// Polytope-модель и Animator на Hero остаются.
/// </summary>
public static class HeroAgentComponentMigration
{
    static readonly HashSet<Type> SkipCopy = new HashSet<Type>
    {
        typeof(Transform),
        typeof(Animator),
        typeof(SkinnedMeshRenderer),
        typeof(MeshRenderer),
        typeof(MeshFilter),
        typeof(PolytopeHeroLocomotion),
    };

    [MenuItem("Forest Survival/Migrate George to GeorgeHero — SAVE SCENE")]
    public static void MigrateGeorgeAndSave() =>
        MigratePair("George", "GeorgeHero", saveScenes: true, assignJackLilyAnimator: false);

    [MenuItem("Forest Survival/Migrate Jack+Lily to Hero — SAVE SCENE")]
    public static void MigrateAllAndSave()
    {
        MigratePair("Jack", "JackHero", false, true);
        MigratePair("Lily", "LilyHero", false, true);
        MigratePair("George", "GeorgeHero", true, false);
    }

    [MenuItem("Forest Survival/Migrate Jack to JackHero only")]
    public static void MigrateJackOnly() => MigratePair("Jack", "JackHero", false, true);

    [MenuItem("Forest Survival/Migrate Lily to LilyHero only")]
    public static void MigrateLilyOnly() => MigratePair("Lily", "LilyHero", false, true);

    static void MigratePair(string sourceName, string heroName, bool saveScenes, bool assignJackLilyAnimator)
    {
        var source = FindInLoadedScenes(sourceName);
        var hero = FindInLoadedScenes(heroName);
        if (source == null || hero == null)
        {
            Debug.LogError($"HeroAgentComponentMigration: не найдено «{sourceName}» или «{heroName}» на открытых сценах.");
            return;
        }

        Undo.RegisterFullObjectHierarchyUndo(hero, $"Migrate {sourceName} → {heroName}");

        CopyTransform(source.transform, hero.transform);
        hero.transform.SetParent(source.transform.parent, true);

        hero.tag = source.tag;
        ApplyLayerRecursively(hero.transform, source.layer);

        RemoveConflictingAgentScripts(source, hero);
        CopyRootComponents(source, hero);
        CopyAgentChildren(source.transform, hero.transform);

        RemoveComponent<PolytopeHeroLocomotion>(hero);

        if (assignJackLilyAnimator)
            EnsureAnimatorOnRoot(hero, sourceName.StartsWith("Jack", StringComparison.OrdinalIgnoreCase));

        hero.SetActive(true);

        EditorUtility.SetDirty(hero);
        for (int i = 0; i < SceneManager.sceneCount; i++)
            EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneAt(i));

        Debug.Log($"HeroAgentComponentMigration: «{sourceName}» → «{heroName}» OK. Старый «{sourceName}» не трогали — выключи его галочкой.");

        if (saveScenes)
            EditorSceneManager.SaveOpenScenes();
    }

    static void CopyTransform(Transform src, Transform dst)
    {
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

                Undo.RecordObject(target, "Copy agent component");
                EditorUtility.CopySerialized(srcList[i], target);
                EditorUtility.SetDirty(target);
            }
        }
    }

    static void RemoveConflictingAgentScripts(GameObject source, GameObject hero)
    {
        var sourceAgentTypes = new HashSet<Type>();
        foreach (var c in source.GetComponents<Component>())
        {
            if (c == null)
                continue;
            if (c is AgentGoToHouseDiscrete || c is LilyScript || c is GeorgeScript)
                sourceAgentTypes.Add(c.GetType());
        }

        foreach (var c in hero.GetComponents<Component>())
        {
            if (c == null)
                continue;
            if (c is AgentGoToHouseDiscrete || c is LilyScript || c is GeorgeScript)
            {
                if (!sourceAgentTypes.Contains(c.GetType()))
                    Undo.DestroyObjectImmediate(c);
            }
        }
    }

    static void CopyAgentChildren(Transform src, Transform dst)
    {
        for (int i = 0; i < src.childCount; i++)
        {
            var child = src.GetChild(i);
            if (IsVisualMeshChild(child))
                continue;
            if (dst.Find(child.name) != null)
                continue;

            var clone = UnityEngine.Object.Instantiate(child.gameObject);
            clone.name = child.name;
            Undo.RegisterCreatedObjectUndo(clone, "Copy agent child");
            clone.transform.SetParent(dst, false);
            clone.transform.localPosition = child.localPosition;
            clone.transform.localRotation = child.localRotation;
            clone.transform.localScale = child.localScale;
            ApplyLayerRecursively(clone.transform, child.gameObject.layer);
        }
    }

    static bool IsVisualMeshChild(Transform t)
    {
        if (t.name.Equals("Chub", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.GetComponent<SkinnedMeshRenderer>() != null)
            return true;
        if (t.name.StartsWith("PT_", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    static void EnsureAnimatorOnRoot(GameObject hero, bool isJack)
    {
        var animator = hero.GetComponent<Animator>();
        if (animator == null)
            animator = hero.GetComponentInChildren<Animator>(true);
        if (animator == null)
        {
            Debug.LogWarning($"{hero.name}: Animator не найден.");
            return;
        }

        var path = isJack
            ? "Assets/ResilientLogicGames/ChubyCharacterFree/Animations/Animator Jack.controller"
            : "Assets/ResilientLogicGames/ChubyCharacterFree/Animations/Animator Lily.controller";
        var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(path);
        if (ctrl != null)
        {
            Undo.RecordObject(animator, "Hero animator controller");
            animator.runtimeAnimatorController = ctrl;
            animator.applyRootMotion = false;
            EditorUtility.SetDirty(animator);
        }
    }

    static void ApplyLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        foreach (Transform child in root)
            ApplyLayerRecursively(child, layer);
    }

    static void RemoveComponent<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c == null)
            return;
        Undo.DestroyObjectImmediate(c);
    }

    static GameObject FindInLoadedScenes(string objectName)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = FindByName(root.transform, objectName);
                if (found != null)
                    return found;
            }
        }

        return null;
    }

    static GameObject FindByName(Transform t, string objectName)
    {
        if (t.name == objectName)
            return t.gameObject;
        foreach (Transform child in t)
        {
            var found = FindByName(child, objectName);
            if (found != null)
                return found;
        }

        return null;
    }
}
#endif
