#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Статически назначает URP-материалы Chuby на Skinned Mesh «Chub» у Jack и Lily:
/// слот с материалом «Shirt» (имя из префаба/FBX) — отдельный более синий/розовый тинт,
/// остальные слоты — общий палитровый тинт (ChubyPalette_*).
/// </summary>
public static class ChubyJackLilyStaticMaterials
{
    const string JackBodyPath =
        "Assets/ResilientLogicGames/ChubyCharacterFree/Materials/ChubyPalette_Jack.mat";
    const string LilyBodyPath =
        "Assets/ResilientLogicGames/ChubyCharacterFree/Materials/ChubyPalette_Lily.mat";
    const string JackShirtPath =
        "Assets/ResilientLogicGames/ChubyCharacterFree/Materials/ChubyShirt_Jack.mat";
    const string LilyShirtPath =
        "Assets/ResilientLogicGames/ChubyCharacterFree/Materials/ChubyShirt_Lily.mat";

    [MenuItem("Forest Survival/Chuby: назначить материалы Jack/Lily на Chub (открытые сцены)")]
    public static void ApplyWithoutSave()
    {
        ApplyInternal(false);
    }

    [MenuItem("Forest Survival/Chuby: назначить материалы Jack/Lily и сохранить открытые сцены")]
    public static void ApplyAndSave()
    {
        ApplyInternal(true);
    }

    static void ApplyInternal(bool saveScenes)
    {
        var jackBody = AssetDatabase.LoadAssetAtPath<Material>(JackBodyPath);
        var lilyBody = AssetDatabase.LoadAssetAtPath<Material>(LilyBodyPath);
        var jackShirt = AssetDatabase.LoadAssetAtPath<Material>(JackShirtPath);
        var lilyShirt = AssetDatabase.LoadAssetAtPath<Material>(LilyShirtPath);
        if (jackBody == null || lilyBody == null || jackShirt == null || lilyShirt == null)
        {
            Debug.LogError(
                "ChubyJackLilyStaticMaterials: не найдены материалы. Нужны ChubyPalette_* и ChubyShirt_* в папке Materials.");
            return;
        }

        int updated = 0;
        int shirtSlotsTotal = 0;
        foreach (var smr in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (smr.gameObject.name != "Chub")
                continue;

            var root = smr.transform.root;
            if (root.name != "Jack" && root.name != "Lily")
                continue;

            var body = root.name == "Jack" ? jackBody : lilyBody;
            var shirt = root.name == "Jack" ? jackShirt : lilyShirt;
            int n = smr.sharedMaterials.Length;
            if (n == 0)
                continue;

            var origSmr = PrefabUtility.GetCorrespondingObjectFromOriginalSource(smr) as SkinnedMeshRenderer;
            Material[] origMats = origSmr != null && origSmr.sharedMaterials != null
                ? origSmr.sharedMaterials
                : null;

            var arr = new Material[n];
            for (int i = 0; i < n; i++)
            {
                bool isShirt = IsShirtSlot(origMats, i, smr.sharedMaterials);
                arr[i] = isShirt ? shirt : body;
                if (isShirt)
                    shirtSlotsTotal++;
            }

            Undo.RecordObject(smr, "Chuby Jack/Lily palette + shirt materials");
            smr.sharedMaterials = arr;
            EditorUtility.SetDirty(smr);
            PrefabUtility.RecordPrefabInstancePropertyModifications(smr);
            updated++;
        }

        if (updated == 0)
        {
            Debug.LogWarning(
                "ChubyJackLilyStaticMaterials: не найдено ни одного SkinnedMeshRenderer на объекте «Chub» " +
                "под корнем Jack или Lily. Открой сцену с персонажами и повтори.");
            return;
        }

        if (shirtSlotsTotal == 0)
            Debug.LogWarning(
                "ChubyJackLilyStaticMaterials: слот «Shirt» не распознан по имени материала. " +
                "Открой префаб Chuby в изоляции и проверь, что на Chub есть материал с именем Shirt; " +
                "или вручную перетащи ChubyShirt_Jack / ChubyShirt_Lily на нужный слот.");

        for (int i = 0; i < SceneManager.sceneCount; i++)
            EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneAt(i));

        Debug.Log($"ChubyJackLilyStaticMaterials: обновлено рендереров: {updated}, слотов Shirt: {shirtSlotsTotal}.");

        if (saveScenes)
            EditorSceneManager.SaveOpenScenes();
    }

    static bool IsShirtSlot(Material[] originalFromPrefab, int index, Material[] current)
    {
        if (originalFromPrefab != null && index < originalFromPrefab.Length)
        {
            var m = originalFromPrefab[index];
            if (m != null && NameIsShirt(m.name))
                return true;
        }

        if (current != null && index < current.Length && current[index] != null &&
            NameIsShirt(current[index].name))
            return true;

        return false;
    }

    static bool NameIsShirt(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return name.IndexOf("shirt", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
#endif
