using UnityEngine;

/// <summary>
/// Подставляет Animator Jack/Lily, если на Hero не назначен controller (напр. GeorgeHero / PT_Boy).
/// </summary>
public static class DefaultHeroAnimatorController
{
    const string JackPath =
        "Assets/ResilientLogicGames/ChubyCharacterFree/Animations/Animator Jack.controller";
    const string LilyPath =
        "Assets/ResilientLogicGames/ChubyCharacterFree/Animations/Animator Lily.controller";

    static RuntimeAnimatorController _jack;
    static RuntimeAnimatorController _lily;

    public static RuntimeAnimatorController ForAgent(Component agent)
    {
        var fromCatalog = HeroAnimatorCatalog.ForAgent(agent);
        if (fromCatalog != null)
            return fromCatalog;

        if (agent is LilyScript)
            return _lily ??= LoadEditor(LilyPath);
        return _jack ??= LoadEditor(JackPath);
    }

    static RuntimeAnimatorController LoadEditor(string assetPath)
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(assetPath);
#else
        return null;
#endif
    }
}
