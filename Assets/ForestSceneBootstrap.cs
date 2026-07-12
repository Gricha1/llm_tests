using UnityEngine;

/// <summary>Фильтр для RuntimeInitializeOnLoad — только объекты загруженной сцены, не prefab/assets.</summary>
public static class ForestSceneBootstrap
{
    public static bool IsLoadedSceneComponent(Component component)
    {
        if (component == null)
            return false;

        var scene = component.gameObject.scene;
        return scene.IsValid() && scene.isLoaded;
    }
}
