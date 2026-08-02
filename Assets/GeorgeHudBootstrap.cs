using UnityEngine;
using TMPro;

public static class GeorgeHudBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void BootstrapAll()
    {
        var runner = new GameObject(nameof(GeorgeHudBootstrap));
        runner.hideFlags = HideFlags.HideAndDontSave;
        runner.AddComponent<GeorgeHudBootstrapRunner>();
    }

    internal static void EnsureAll()
    {
        foreach (var d in Resources.FindObjectsOfTypeAll<GeorgeWaterDisplay>())
            if (d != null && ForestSceneBootstrap.IsLoadedSceneComponent(d)) { EnsureHudVisible(d); d.ApplyLayoutPosition(); }
        foreach (var d in Resources.FindObjectsOfTypeAll<GeorgeSatietyDisplay>())
            if (d != null && ForestSceneBootstrap.IsLoadedSceneComponent(d)) { EnsureHudVisible(d); d.ApplyLayoutPosition(); }
        foreach (var d in Resources.FindObjectsOfTypeAll<GeorgeHeatDisplay>())
            if (d != null && ForestSceneBootstrap.IsLoadedSceneComponent(d)) { EnsureHudVisible(d); d.ApplyLayoutPosition(); }

        foreach (var d in Resources.FindObjectsOfTypeAll<GeorgeWoodDisplay>())
        {
            if (d != null && d.gameObject != null && ForestSceneBootstrap.IsLoadedSceneComponent(d))
                Object.Destroy(d.gameObject);
        }

        if (HasLoadedDisplay<GeorgeWaterDisplay>() && TrainingEnvSpace.FindPresentationGeorge() == null)
            return;

        Transform parent = FindHudParent();
        if (parent == null)
            return;

        if (!HasLoadedDisplay<GeorgeWaterDisplay>())
        {
            CreateSticker<GeorgeSatietyDisplay>("GeorgeSatietyText", new Vector2(-70f, -180f));
            CreateSticker<GeorgeWaterDisplay>("GeorgeWaterText", new Vector2(130f, -180f));
            CreateSticker<GeorgeHeatDisplay>("GeorgeHeatText", new Vector2(330f, -180f));
        }

        if (TrainingEnvSpace.FindPresentationGeorge() != null)
            CreateGeorgeReward(parent);
    }

    sealed class GeorgeHudBootstrapRunner : MonoBehaviour
    {
        float _retryUntil;

        void Awake()
        {
            _retryUntil = Time.unscaledTime + 2f;
            EnsureAll();
        }

        void Update()
        {
            if (Time.unscaledTime > _retryUntil)
            {
                Destroy(gameObject);
                return;
            }

            if (TrainingEnvSpace.FindPresentationGeorge() != null)
            {
                EnsureAll();
                Destroy(gameObject);
            }
        }
    }

    static Transform FindHudParent()
    {
        var water = FindLoadedDisplay<WaterDisplay>();
        if (water != null)
            return water.transform.parent;

        var lilyWater = FindLoadedDisplay<LilyWaterDisplay>();
        if (lilyWater != null)
            return lilyWater.transform.parent;

        var canvas = GameObject.Find("Canvas");
        return canvas != null ? canvas.transform : null;
    }

    static void CreateGeorgeReward(Transform parent)
    {
        if (RewardDisplay.CountLoaded(RewardDisplay.AgentKind.George) > 0)
            return;

        var go = new GameObject("GeorgeRewardText");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(1f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-24f, -70f);
        rt.sizeDelta = new Vector2(280f, 50f);

        go.AddComponent<CanvasRenderer>();
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.fontSize = 36f;
        if (TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;

        var reward = go.AddComponent<RewardDisplay>();
        reward.ConfigureForGeorge();
        EnsureHudVisible(reward);
        RewardDisplay.DeduplicateAll();
    }

    static bool HasLoadedDisplay<T>() where T : Component
    {
        foreach (var d in Resources.FindObjectsOfTypeAll<T>())
        {
            if (d != null && d.gameObject.scene.IsValid() && d.gameObject.scene.isLoaded)
                return true;
        }

        return false;
    }

    static T FindLoadedDisplay<T>() where T : Component
    {
        foreach (var d in Resources.FindObjectsOfTypeAll<T>())
        {
            if (d != null && d.gameObject.scene.IsValid() && d.gameObject.scene.isLoaded)
                return d;
        }

        return null;
    }

    static void CreateSticker<T>(string name, Vector2 pos) where T : MonoBehaviour
    {
        Transform parent = FindHudParent();
        if (parent == null)
            return;

        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(200f, 50f);

        go.AddComponent<CanvasRenderer>();
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.richText = true;
        tmp.fontSize = 36f;
        if (TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;

        if (typeof(T) == typeof(GeorgeWaterDisplay))
        {
            var jackWater = Object.FindFirstObjectByType<WaterDisplay>();
            if (jackWater != null && jackWater.WaterSpriteAsset != null)
                tmp.spriteAsset = jackWater.WaterSpriteAsset;
        }

        var display = go.AddComponent<T>();
        if (display is GeorgeWaterDisplay waterDisplay)
            waterDisplay.BindWaterSpriteFromJack();
        EnsureHudVisible(display);
    }

    internal static void EnsureHudVisible(Component display)
    {
        if (display == null)
            return;

        var canvas = display.GetComponentInParent<Canvas>();
        if (canvas != null)
        {
            var canvasRt = canvas.GetComponent<RectTransform>();
            if (canvasRt != null && canvasRt.localScale.sqrMagnitude < 1e-4f)
                canvasRt.localScale = Vector3.one;
        }

        if (!display.gameObject.activeSelf)
            display.gameObject.SetActive(true);
    }
}
