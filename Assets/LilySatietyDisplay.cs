using UnityEngine;
using TMPro;

public class LilySatietyDisplay : MonoBehaviour
{
    [SerializeField] private LilyScript lily;
    [SerializeField] private TMP_SpriteAsset spriteAsset;
    [SerializeField] private float pulseSpeed = 5f;
    [SerializeField] private float pulseMinSizePercent = 85f;
    [SerializeField] private float pulseMaxSizePercent = 135f;

    TMP_Text _text;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void BootstrapAll()
    {
        var displays = Resources.FindObjectsOfTypeAll<LilySatietyDisplay>();
        for (int i = 0; i < displays.Length; i++)
        {
            if (displays[i] != null && ForestSceneBootstrap.IsLoadedSceneComponent(displays[i]))
            {
                displays[i].EnsureHudVisible();
                displays[i].ApplyLayoutPosition();
            }
        }

        if (HasLoadedDisplay())
            return;

        var waterDisplay = FindLoadedDisplay<LilyWaterDisplay>();
        if (waterDisplay == null)
            return;

        CreateHud(waterDisplay.transform.parent);
    }

    static bool HasLoadedDisplay()
    {
        var displays = Resources.FindObjectsOfTypeAll<LilySatietyDisplay>();
        for (int i = 0; i < displays.Length; i++)
        {
            var d = displays[i];
            if (d != null && d.gameObject.scene.IsValid() && d.gameObject.scene.isLoaded)
                return true;
        }

        return false;
    }

    static T FindLoadedDisplay<T>() where T : Component
    {
        var all = Resources.FindObjectsOfTypeAll<T>();
        for (int i = 0; i < all.Length; i++)
        {
            var d = all[i];
            if (d != null && d.gameObject.scene.IsValid() && d.gameObject.scene.isLoaded)
                return d;
        }

        return null;
    }

    static void CreateHud(Transform parent)
    {
        var go = new GameObject("LilySatietyText");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(-70f, -110f);
        rt.sizeDelta = new Vector2(200f, 50f);

        go.AddComponent<CanvasRenderer>();
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.richText = true;
        tmp.fontSize = 36f;
        if (TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;

        var display = go.AddComponent<LilySatietyDisplay>();
        var jackHud = FindObjectOfType<SatietyDisplay>();
        if (jackHud != null)
            display.spriteAsset = jackHud.FoodSpriteAsset;
        display.EnsureHudVisible();
        display.ApplyLayoutPosition();
    }

    void Awake()
    {
        _text = GetComponent<TMP_Text>();
        if (_text != null)
            _text.richText = true;

        if (spriteAsset == null)
        {
            var jackHud = FindObjectOfType<SatietyDisplay>();
            if (jackHud != null)
                spriteAsset = jackHud.FoodSpriteAsset;
        }

        EnsureHudVisible();
        ApplyLayoutPosition();
    }

    internal void ApplyLayoutPosition()
    {
        var rt = GetComponent<RectTransform>();
        if (rt != null)
            rt.anchoredPosition = new Vector2(-70f, -110f);
    }

    void EnsureHudVisible()
    {
        var canvas = GetComponentInParent<Canvas>();
        if (canvas != null)
        {
            var canvasRt = canvas.GetComponent<RectTransform>();
            if (canvasRt != null && canvasRt.localScale.sqrMagnitude < 1e-4f)
                canvasRt.localScale = Vector3.one;
        }

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);
    }

    void LateUpdate()
    {
        if (_text == null)
            return;

        lily = TrainingEnvSpace.FindPresentationLily();
        if (lily == null || !TrainingEnvSpace.ShouldShowHudForRole(EnvTrainingAgentRole.Lily))
        {
            _text.enabled = false;
            return;
        }

        if (spriteAsset != null && _text.spriteAsset != spriteAsset)
            _text.spriteAsset = spriteAsset;

        if (lily.Satiety <= 0)
        {
            float wave = (Mathf.Sin(Time.unscaledTime * pulseSpeed) + 1f) * 0.5f;
            int sizePct = Mathf.RoundToInt(Mathf.Lerp(pulseMinSizePercent, pulseMaxSizePercent, wave));
            _text.text = $"<size={sizePct}%><sprite=0></size> {lily.Satiety}";
        }
        else
        {
            _text.text = $"<sprite=0> {lily.Satiety}";
        }

        _text.enabled = true;
    }
}
