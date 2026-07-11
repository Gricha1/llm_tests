#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// JackHero / LilyHero (Polytope Peasants): назначает те же Animator Controller, что у Jack/Lily
/// (Kevin Iglesias / Chuby), и Humanoid Avatar из rig Peasants.
/// </summary>
public static class PolytopeHeroAnimatorSetup
{
    const string JackControllerPath =
        "Assets/ResilientLogicGames/ChubyCharacterFree/Animations/Animator Jack.controller";
    const string LilyControllerPath =
        "Assets/ResilientLogicGames/ChubyCharacterFree/Animations/Animator Lily.controller";

    const string MaleAvatarModelPath =
        "Assets/Polytope Studio/Lowpoly_Characters/Sources/Modular_NPC/Meshes/Peasants_Citizens/PT_Male_Modular_Free_Pack.fbx";
    const string FemaleAvatarModelPath =
        "Assets/Polytope Studio/Lowpoly_Characters/Sources/Modular_NPC/Meshes/Peasants_Citizens/PT_Female_Modular_Free_Pack.fbx";

    const string MaleHumanoidSourcePath =
        "Assets/Polytope Studio/Lowpoly_Characters/Sources/Modular_NPC/Meshes/Peasants_Citizens/Separate_Parts/PT_Male_Peasant_01_upper.fbx";
    const string FemaleHumanoidSourcePath =
        "Assets/Polytope Studio/Lowpoly_Characters/Sources/Modular_NPC/Meshes/Peasants_Citizens/Separate_Parts/PT_Female_Peasant_01_upper_short.fbx";

    [MenuItem("Forest Survival/Polytope: починить Humanoid avatar (Peasant Sets)")]
    public static void FixPeasantHumanoidAvatars()
    {
        CopyHumanoidFrom(MaleHumanoidSourcePath, MaleAvatarModelPath);
        CopyHumanoidFrom(FemaleHumanoidSourcePath, FemaleAvatarModelPath);
        CopyHumanoidFrom(MaleHumanoidSourcePath,
            "Assets/Polytope Studio/Lowpoly_Characters/Sources/Modular_NPC/Meshes/Peasants_Citizens/Sets/PT_Male_Peasant_01.fbx");
        CopyHumanoidFrom(FemaleHumanoidSourcePath,
            "Assets/Polytope Studio/Lowpoly_Characters/Sources/Modular_NPC/Meshes/Peasants_Citizens/Sets/PT_Female_Peasant_01_a.fbx");
        CopyHumanoidFrom(FemaleHumanoidSourcePath,
            "Assets/Polytope Studio/Lowpoly_Characters/Sources/Modular_NPC/Meshes/Peasants_Citizens/Sets/PT_Female_Peasant_01_b.fbx");
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("PolytopeHeroAnimatorSetup: Humanoid mapping скопирован на Set FBX. Перезапусти назначение анимаций.");
    }

    [MenuItem("Forest Survival/Hero: анимации JackHero/LilyHero (открытые сцены)")]
    public static void ApplyWithoutSave()
    {
        ApplyInternal(false);
    }

    [MenuItem("Forest Survival/Hero: анимации JackHero/LilyHero и сохранить сцены")]
    public static void ApplyAndSave()
    {
        ApplyInternal(true);
    }

    static void ApplyInternal(bool saveScenes)
    {
        var jackController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(JackControllerPath);
        var lilyController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(LilyControllerPath);
        if (jackController == null || lilyController == null)
        {
            Debug.LogError("PolytopeHeroAnimatorSetup: не найдены Animator Jack / Lily.controller");
            return;
        }

        int updated = 0;
        updated += ApplyHero("JackHero", jackController, MaleAvatarModelPath);
        updated += ApplyHero("LilyHero", lilyController, FemaleAvatarModelPath);

        if (updated == 0)
        {
            Debug.LogWarning(
                "PolytopeHeroAnimatorSetup: не найдено JackHero или LilyHero на открытых сценах. " +
                "Добавь модели, сохрани сцену и повтори.");
            return;
        }

        for (int i = 0; i < SceneManager.sceneCount; i++)
            EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneAt(i));

        Debug.Log($"PolytopeHeroAnimatorSetup: настроено объектов: {updated}.");

        if (saveScenes)
            EditorSceneManager.SaveOpenScenes();
    }

    static int ApplyHero(string heroName, RuntimeAnimatorController controller, string avatarModelPath)
    {
        int count = 0;
        foreach (var root in FindSceneRootsNamed(heroName))
        {
            if (!ApplyHeroOnRoot(root, controller, avatarModelPath))
                continue;
            count++;
        }

        return count;
    }

    static bool ApplyHeroOnRoot(GameObject heroRoot, RuntimeAnimatorController controller, string avatarModelPath)
    {
        var avatar = LoadAvatarFromModel(avatarModelPath);
        if (avatar == null)
        {
            Debug.LogError($"PolytopeHeroAnimatorSetup: Avatar не найден в {avatarModelPath}. " +
                           "Сначала запусти «Polytope: починить Humanoid avatar».");
            return false;
        }

        var animatorGo = ResolveAnimatorHost(heroRoot);
        var animator = animatorGo.GetComponent<Animator>();
        if (animator == null)
            animator = Undo.AddComponent<Animator>(animatorGo);

        Undo.RecordObject(animator, "Polytope hero animator");
        animator.runtimeAnimatorController = controller;
        animator.avatar = avatar;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
        EditorUtility.SetDirty(animator);
        PrefabUtility.RecordPrefabInstancePropertyModifications(animator);

        DisableExtraAnimators(heroRoot, animator);

        EnsureLocomotion(animatorGo);

        var hasAgentScript = heroRoot.GetComponent<AgentGoToHouseDiscrete>() != null;
        var hasLilyScript = heroRoot.GetComponent<LilyScript>() != null;
        if ((hasAgentScript || hasLilyScript) && animatorGo != heroRoot)
        {
            Debug.LogWarning(
                $"{heroRoot.name}: Agent/LilyScript на корне, Animator на «{animatorGo.name}». " +
                "GetComponent<Animator> не найдёт его — перенеси скрипт на объект с Animator или добавь Animator на корень.");
        }

        if (avatar.isValid)
            Debug.Log($"{heroRoot.name}: Animator OK — {controller.name}, avatar {avatar.name}");
        else
            Debug.LogWarning($"{heroRoot.name}: Avatar назначен, но isValid=false — запусти починку Humanoid.");

        return true;
    }

    static GameObject ResolveAnimatorHost(GameObject heroRoot)
    {
        var existing = heroRoot.GetComponent<Animator>();
        if (existing != null)
            return heroRoot;

        var childAnimator = heroRoot.GetComponentInChildren<Animator>(true);
        if (childAnimator != null)
            return childAnimator.gameObject;

        var hips = FindTransformNamed(heroRoot.transform, "PT_Hips");
        if (hips != null)
            return hips.parent != null && hips.parent != heroRoot.transform ? hips.parent.gameObject : hips.gameObject;

        foreach (var smr in heroRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.rootBone != null)
                return smr.rootBone.parent != null ? smr.rootBone.parent.gameObject : smr.rootBone.gameObject;
        }

        return heroRoot;
    }

    static void EnsureLocomotion(GameObject go)
    {
        if (go.GetComponent<PolytopeHeroLocomotion>() != null)
            return;
        if (go.GetComponent<AgentGoToHouseDiscrete>() != null || go.GetComponent<LilyScript>() != null)
            return;
        Undo.AddComponent<PolytopeHeroLocomotion>(go);
    }

    static void DisableExtraAnimators(GameObject heroRoot, Animator keep)
    {
        foreach (var other in heroRoot.GetComponentsInChildren<Animator>(true))
        {
            if (other == keep || !other.enabled)
                continue;
            Undo.RecordObject(other, "Disable duplicate hero Animator");
            other.enabled = false;
            EditorUtility.SetDirty(other);
        }
    }

    static Avatar LoadAvatarFromModel(string modelPath)
    {
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(modelPath))
        {
            if (asset is Avatar avatar)
                return avatar;
        }

        return null;
    }

    static void CopyHumanoidFrom(string sourceModelPath, string targetModelPath)
    {
        var source = AssetImporter.GetAtPath(sourceModelPath) as ModelImporter;
        var target = AssetImporter.GetAtPath(targetModelPath) as ModelImporter;
        if (source == null || target == null)
        {
            Debug.LogWarning($"PolytopeHeroAnimatorSetup: пропуск — {sourceModelPath} → {targetModelPath}");
            return;
        }

        target.animationType = ModelImporterAnimationType.Human;
        target.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        target.humanDescription = source.humanDescription;
        target.SaveAndReimport();
        Debug.Log($"Humanoid скопирован: {sourceModelPath} → {targetModelPath}");
    }

    static Transform FindTransformNamed(Transform root, string name)
    {
        if (root.name == name)
            return root;
        foreach (Transform child in root)
        {
            var found = FindTransformNamed(child, name);
            if (found != null)
                return found;
        }

        return null;
    }

    static GameObject[] FindSceneRootsNamed(string name)
    {
        var list = new System.Collections.Generic.List<GameObject>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;
            foreach (var root in scene.GetRootGameObjects())
                CollectNamed(root.transform, name, list);
        }

        return list.ToArray();
    }

    static void CollectNamed(Transform t, string name, System.Collections.Generic.List<GameObject> list)
    {
        if (t.name == name)
            list.Add(t.gameObject);
        foreach (Transform child in t)
            CollectNamed(child, name, list);
    }
}
#endif
