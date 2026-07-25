#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Ночной свет для BossArena: луна, постобработка, фонари в городе. Лес остаётся дневным (отдельный слой).
/// Меню: Forest Survival → BossArena → Ночной свет (зомби апокалипсис).
/// </summary>
public static class BossArenaNightLighting
{
    const string BossArenaName = "BossArena";
    const string NightProfilePath = "Assets/Settings/BossArenaNightVolumeProfile.asset";
    const string BossArenaLayerName = "BossArena";

    static readonly string[] SkipLayerAssignNames =
    {
        "Main Camera", "Directional Light", "Global Volume", "CameraPath", "NightAmbience"
    };

    [MenuItem("Forest Survival/BossArena/Ночной свет (зомби апокалипсис)")]
    public static void ApplyNightLighting()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var bossArena = GameObject.Find(BossArenaName);
        if (bossArena == null)
        {
            Debug.LogError($"BossArenaNightLighting: объект «{BossArenaName}» не найден в сцене.");
            return;
        }

        int bossLayer = LayerMask.NameToLayer(BossArenaLayerName);
        if (bossLayer < 0)
        {
            Debug.LogError($"BossArenaNightLighting: слой «{BossArenaLayerName}» не найден в TagManager.");
            return;
        }

        int bossMask = 1 << bossLayer;
        Undo.RegisterCompleteObjectUndo(bossArena, "BossArena night lighting");

        AssignBossArenaLayer(bossArena.transform, bossLayer);
        ConfigureMoonLight(bossArena.transform, bossMask);
        ExcludeBossLayerFromSceneSun(bossArena.transform, bossMask);
        ConfigureNightVolume(bossArena.transform);
        ConfigureBossCamera(bossArena.transform);
        BoostCityStreetLights(bossArena.transform);

        EditorSceneManager.MarkSceneDirty(bossArena.scene);
        Debug.Log(
            "BossArenaNightLighting: ночь включена (луна + постобработка + фонари). Сохрани сцену (Ctrl+S). " +
            "Лес не затронут — дневной свет не светит слой BossArena.");
    }

    static void AssignBossArenaLayer(Transform bossRoot, int layer)
    {
        AssignLayerRecursive(bossRoot, layer, skipRoot: true);
    }

    static void AssignLayerRecursive(Transform t, int layer, bool skipRoot)
    {
        if (!skipRoot && !ShouldSkipLayerAssign(t))
        {
            Undo.RecordObject(t.gameObject, "BossArena layer");
            t.gameObject.layer = layer;
        }

        for (int i = 0; i < t.childCount; i++)
            AssignLayerRecursive(t.GetChild(i), layer, skipRoot: false);
    }

    static bool ShouldSkipLayerAssign(Transform t)
    {
        string n = t.name;
        for (int i = 0; i < SkipLayerAssignNames.Length; i++)
        {
            if (n == SkipLayerAssignNames[i])
                return true;
        }

        return t.GetComponent<Camera>() != null || t.GetComponent<Volume>() != null;
    }

    static Light FindDirectionalLight(Transform bossRoot)
    {
        var lights = bossRoot.GetComponentsInChildren<Light>(true);
        for (int i = 0; i < lights.Length; i++)
        {
            var light = lights[i];
            if (light != null && light.type == LightType.Directional)
                return light;
        }

        return null;
    }

    static void ConfigureMoonLight(Transform bossRoot, int bossMask)
    {
        var light = FindDirectionalLight(bossRoot);
        GameObject lightGo;
        if (light == null)
        {
            lightGo = new GameObject("Directional Light");
            Undo.RegisterCreatedObjectUndo(lightGo, "Create moon light");
            lightGo.transform.SetParent(bossRoot, false);
            light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            Undo.AddComponent<UniversalAdditionalLightData>(lightGo);
        }
        else
        {
            lightGo = light.gameObject;
        }

        Undo.RecordObject(light, "Moon light");
        Undo.RecordObject(lightGo.transform, "Moon light rotation");

        lightGo.name = "Directional Light";
        lightGo.transform.localRotation = Quaternion.Euler(58f, 165f, 0f);
        light.color = new Color(0.62f, 0.72f, 1f);
        light.useColorTemperature = false;
        light.intensity = 0.28f;
        light.shadows = LightShadows.Soft;
        light.shadowStrength = 0.85f;
        light.cullingMask = bossMask;
    }

    static void ExcludeBossLayerFromSceneSun(Transform bossRoot, int bossMask)
    {
        var allLights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
        for (int i = 0; i < allLights.Length; i++)
        {
            var light = allLights[i];
            if (light.type != LightType.Directional)
                continue;
            if (light.transform.IsChildOf(bossRoot))
                continue;

            Undo.RecordObject(light, "Exclude BossArena from sun");
            light.cullingMask &= ~bossMask;
        }
    }

    static void ConfigureNightVolume(Transform bossRoot)
    {
        Volume volume = null;
        var volumes = bossRoot.GetComponentsInChildren<Volume>(true);
        for (int i = 0; i < volumes.Length; i++)
        {
            if (volumes[i] != null)
            {
                volume = volumes[i];
                break;
            }
        }

        GameObject volumeGo;
        if (volume == null)
        {
            volumeGo = new GameObject("Global Volume");
            Undo.RegisterCreatedObjectUndo(volumeGo, "Create night volume");
            volumeGo.transform.SetParent(bossRoot, false);
            volume = Undo.AddComponent<Volume>(volumeGo);
        }
        else
        {
            volumeGo = volume.gameObject;
        }

        volumeGo.name = "Global Volume";
        volumeGo.transform.localPosition = Vector3.zero;

        var box = volumeGo.GetComponent<BoxCollider>();
        if (box == null)
            box = Undo.AddComponent<BoxCollider>(volumeGo);
        Undo.RecordObject(box, "Night volume collider");
        box.isTrigger = true;
        box.size = new Vector3(140f, 80f, 140f);
        box.center = Vector3.zero;

        Undo.RecordObject(volume, "Night volume");
        volume.isGlobal = false;
        volume.priority = 10;
        volume.blendDistance = 18f;
        volume.weight = 1f;
        volume.sharedProfile = GetOrCreateNightProfile();
    }

    static VolumeProfile GetOrCreateNightProfile()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(NightProfilePath);
        if (profile != null)
            return profile;

        profile = ScriptableObject.CreateInstance<VolumeProfile>();

        var tonemap = profile.Add<Tonemapping>(true);
        tonemap.mode.Override(TonemappingMode.ACES);

        var color = profile.Add<ColorAdjustments>(true);
        color.postExposure.Override(-1.15f);
        color.contrast.Override(12f);
        color.saturation.Override(-18f);
        color.colorFilter.Override(new Color(0.72f, 0.8f, 1f));

        var wb = profile.Add<WhiteBalance>(true);
        wb.temperature.Override(-12f);
        wb.tint.Override(8f);

        var vignette = profile.Add<Vignette>(true);
        vignette.intensity.Override(0.38f);
        vignette.smoothness.Override(0.45f);
        vignette.color.Override(new Color(0.02f, 0.03f, 0.08f));

        var bloom = profile.Add<Bloom>(true);
        bloom.intensity.Override(0.55f);
        bloom.threshold.Override(0.85f);
        bloom.scatter.Override(0.65f);

        AssetDatabase.CreateAsset(profile, NightProfilePath);
        AssetDatabase.SaveAssets();
        return profile;
    }

    static void ConfigureBossCamera(Transform bossRoot)
    {
        var cam = bossRoot.GetComponentInChildren<Camera>(true);
        if (cam == null)
            return;

        Undo.RecordObject(cam, "Boss camera night sky");
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.04f, 0.06f, 0.12f, 1f);
    }

    static void BoostCityStreetLights(Transform bossRoot)
    {
        var lights = bossRoot.GetComponentsInChildren<Light>(true);
        for (int i = 0; i < lights.Length; i++)
        {
            var light = lights[i];
            if (light.type == LightType.Directional)
                continue;

            Undo.RecordObject(light, "Street light");
            light.enabled = true;
            light.intensity = Mathf.Max(light.intensity, 2.8f);
            light.color = new Color(1f, 0.82f, 0.55f);
            light.range = Mathf.Max(light.range, 14f);
        }
    }
}
#endif
