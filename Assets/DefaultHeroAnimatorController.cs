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
        if (agent is LilyScript)
            return _lily ??= Load(LilyPath);
        return _jack ??= Load(JackPath);
    }

    static RuntimeAnimatorController Load(string assetPath)
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(assetPath);
#else
        return null;
#endif
    }
}
