#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Polytope Peasants: в URP розовый = старый Built-in шейдер. Подменяем шейдеры из URP unitypackage.
/// </summary>
public static class PolytopeUrpShaderFix
{
    const string UrpPackagePath =
        "Assets/Polytope Studio/Lowpoly_Characters/URP/PT_Peasants_Free_URP_17.unitypackage";

    const string NpcMaterialPath =
        "Assets/Polytope Studio/Lowpoly_Characters/Sources/Modular_NPC/Materials/PT_NPC_Mat.mat";

    const string MaleModularPrefabPath =
        "Assets/Polytope Studio/Lowpoly_Characters/Prefabs/Modular_NPC/Peasants_Citizens/PT_Male_Modular_Free_Pack.prefab";

    [MenuItem("Forest Survival/Polytope: URP шейдеры (Peasants) — импорт")]
    public static void ImportUrpShadersFromPackage()
    {
        if (!File.Exists(UrpPackagePath))
        {
            Debug.LogError($"PolytopeUrpShaderFix: не найден {UrpPackagePath}");
            return;
        }

        AssetDatabase.ImportPackage(UrpPackagePath, false);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Polytope URP шейдеры импортированы. Если персонаж всё ещё розовый — открой сцену заново.");
    }

    [MenuItem("Forest Survival/Polytope: проверить PT_Male_Modular_Free_Pack")]
    public static void ValidateMaleModularPrefab()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(NpcMaterialPath);
        if (mat == null)
        {
            Debug.LogError($"PolytopeUrpShaderFix: материал не найден: {NpcMaterialPath}");
            return;
        }

        if (mat.shader == null || !mat.shader.isSupported)
            Debug.LogWarning($"PT_NPC_Mat: шейдер не поддерживается в URP ({mat.shader?.name}). Запусти импорт URP шейдеров.");
        else
            Debug.Log($"PT_NPC_Mat OK: {mat.shader.name}");

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MaleModularPrefabPath);
        if (prefab == null)
        {
            Debug.LogError($"Prefab не найден: {MaleModularPrefabPath}");
            return;
        }

        int meshCount = 0;
        foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            meshCount++;
            for (int i = 0; i < smr.sharedMaterials.Length; i++)
            {
                var m = smr.sharedMaterials[i];
                if (m == null)
                    Debug.LogWarning($"{smr.gameObject.name}: пустой материал слот {i}");
            }
        }

        Debug.Log($"PT_Male_Modular_Free_Pack: {meshCount} частей тела, материал = PT_NPC_Mat. Перезайди в Play если был розовый.");
    }
}
#endif
