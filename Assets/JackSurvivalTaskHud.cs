using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Нижняя плашка: этап 1 → этап 2 → этап 3, один эпизод с 0.
/// </summary>
public sealed class JackSurvivalTaskHud : MonoBehaviour
{
    public static bool GlobalEnabled { get; private set; } = true;

    [SerializeField] private bool showHud = true;

    [SerializeField] private Vector2 bottomPadding = new Vector2(0f, 28f);
    [SerializeField] private float panelWidth = 620f;
    [SerializeField] private float labelHeight = 28f;
    [SerializeField] private float barHeight = 16f;
    [SerializeField] private float barSpacing = 8f;
    [SerializeField] private float markerHeight = 28f;

    [SerializeField] private Color panelColor = new Color(0f, 0f, 0f, 0.55f);
    [SerializeField] private Color barBackgroundColor = new Color(1f, 1f, 1f, 0.12f);
    [SerializeField] private Color phase1FillColor = new Color(0.35f, 0.85f, 0.45f, 0.95f);
    [SerializeField] private Color phase2FillColor = new Color(0.92f, 0.28f, 0.24f, 0.95f);
    [SerializeField] private Color phase3FillColor = new Color(0.55f, 0.18f, 0.72f, 0.95f);
    [SerializeField] private Color labelColor = new Color(0.95f, 0.95f, 0.95f, 1f);
    [SerializeField] private Color phase1MarkerColor = new Color(0.75f, 1f, 0.8f, 1f);
    [SerializeField] private Color phase2MarkerColor = new Color(1f, 0.7f, 0.65f, 1f);
    [SerializeField] private Color phase3MarkerColor = new Color(0.85f, 0.65f, 1f, 1f);

    [SerializeField] private string phase1MarkerText = "Этап 1: Учимся выживать";
    [SerializeField] private string phase2MarkerText = "Этап 2: Зомби апокалипсис";
    [SerializeField] private string phase3MarkerText = "Этап 3: Кошмар";

    Canvas _canvas;
    RectTransform _greenFillRt;
    RectTransform _redFillRt;
    RectTransform _purpleFillRt;
    RectTransform _phase1MarkerRt;
    RectTransform _phase2MarkerRt;
    RectTransform _phase3MarkerRt;
    TextMeshProUGUI _label;
    AgentGoToHouseDiscrete _jack;
    float _barInnerWidth;

    public static void SetGlobalEnabled(bool enabled)
    {
        GlobalEnabled = enabled;
        var inst = FindObjectOfType<JackSurvivalTaskHud>();
        if (inst != null)
            inst.ApplyVisibility();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!GlobalEnabled)
            return;

        var all = Resources.FindObjectsOfTypeAll<JackSurvivalTaskHud>();
        for (int i = 0; i < all.Length; i++)
        {
            var h = all[i];
            if (h == null)
                continue;
            var sc = h.gameObject.scene;
            if (sc.IsValid() && sc.isLoaded)
                return;
        }

        var runtimeGo = new GameObject(nameof(JackSurvivalTaskHud));
        DontDestroyOnLoad(runtimeGo);
        runtimeGo.AddComponent<JackSurvivalTaskHud>();
    }

    void Awake()
    {
        if (GlobalEnabled && showHud)
            CreateUi();
    }

    void Update()
    {
        ApplyVisibility();
        if (!GlobalEnabled || !showHud)
            return;

        if (_canvas == null)
            CreateUi();

        if (_jack == null)
            _jack = TrainingEnvSpace.FindInPresentation<AgentGoToHouseDiscrete>();

        if (_jack == null || _greenFillRt == null || _label == null)
            return;

        float progress = _jack.SurvivalProgress01;
        float phase1End = _jack.Phase1EndProgress;
        float phase2End = _jack.Phase2EndProgress;
        float phase1Width = _barInnerWidth * phase1End;
        float phase2Width = _barInnerWidth * (phase2End - phase1End);
        float barH = _greenFillRt.sizeDelta.y;

        _label.text = _jack.SurvivalTaskLabel;

        float greenWidth = Mathf.Min(progress, phase1End) * _barInnerWidth;
        _greenFillRt.sizeDelta = new Vector2(greenWidth, barH);

        if (progress > phase1End)
        {
            float redProgress = Mathf.Min(progress, phase2End) - phase1End;
            float redWidth = redProgress * _barInnerWidth;
            _redFillRt.gameObject.SetActive(true);
            _redFillRt.anchoredPosition = new Vector2(phase1Width, 0f);
            _redFillRt.sizeDelta = new Vector2(redWidth, barH);
        }
        else if (_redFillRt != null)
        {
            _redFillRt.gameObject.SetActive(false);
        }

        if (progress > phase2End)
        {
            float purpleWidth = (progress - phase2End) * _barInnerWidth;
            _purpleFillRt.gameObject.SetActive(true);
            _purpleFillRt.anchoredPosition = new Vector2(phase1Width + phase2Width, 0f);
            _purpleFillRt.sizeDelta = new Vector2(purpleWidth, barH);
        }
        else if (_purpleFillRt != null)
        {
            _purpleFillRt.gameObject.SetActive(false);
        }

        SetMarkerPosition(_phase1MarkerRt, 0f);
        SetMarkerPosition(_phase2MarkerRt, phase1Width);
        SetMarkerPosition(_phase3MarkerRt, phase1Width + phase2Width);
    }

    static void SetMarkerPosition(RectTransform rt, float x)
    {
        if (rt == null)
            return;
        var pos = rt.anchoredPosition;
        pos.x = x;
        rt.anchoredPosition = pos;
    }

    void ApplyVisibility()
    {
        if (_canvas != null)
            _canvas.enabled = GlobalEnabled && showHud;
    }

    void CreateUi()
    {
        if (_canvas != null)
            return;

        var canvasGo = new GameObject("JackSurvivalTaskCanvas");
        canvasGo.transform.SetParent(transform, false);

        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 1100;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        canvasGo.AddComponent<GraphicRaycaster>();

        var root = new GameObject("BottomPanel").AddComponent<RectTransform>();
        root.SetParent(canvasGo.transform, false);
        root.anchorMin = new Vector2(0.5f, 0f);
        root.anchorMax = new Vector2(0.5f, 0f);
        root.pivot = new Vector2(0.5f, 0f);
        root.anchoredPosition = bottomPadding;
        root.sizeDelta = new Vector2(panelWidth, labelHeight + barSpacing + barHeight + markerHeight + 14f);

        var panelBg = root.gameObject.AddComponent<Image>();
        panelBg.color = panelColor;

        var labelRt = new GameObject("Label").AddComponent<RectTransform>();
        labelRt.SetParent(root, false);
        labelRt.anchorMin = new Vector2(0f, 1f);
        labelRt.anchorMax = new Vector2(1f, 1f);
        labelRt.pivot = new Vector2(0.5f, 1f);
        labelRt.anchoredPosition = new Vector2(0f, -6f);
        labelRt.sizeDelta = new Vector2(-24f, labelHeight);

        _label = labelRt.gameObject.AddComponent<TextMeshProUGUI>();
        _label.alignment = TextAlignmentOptions.Center;
        _label.fontSize = 20f;
        _label.fontStyle = FontStyles.Bold;
        _label.color = labelColor;
        if (TMP_Settings.defaultFontAsset != null)
            _label.font = TMP_Settings.defaultFontAsset;

        var barBg = new GameObject("BarBg").AddComponent<RectTransform>();
        barBg.SetParent(root, false);
        barBg.anchorMin = new Vector2(0f, 0f);
        barBg.anchorMax = new Vector2(1f, 0f);
        barBg.pivot = new Vector2(0.5f, 0f);
        barBg.anchoredPosition = new Vector2(0f, 8f);
        barBg.sizeDelta = new Vector2(-24f, barHeight);

        _barInnerWidth = panelWidth - 24f;

        var barBgImg = barBg.gameObject.AddComponent<Image>();
        barBgImg.color = barBackgroundColor;

        _greenFillRt = CreateBarFill(barBg, "GreenFill", phase1FillColor);
        _redFillRt = CreateBarFill(barBg, "RedFill", phase2FillColor);
        _purpleFillRt = CreateBarFill(barBg, "PurpleFill", phase3FillColor);
        _redFillRt.gameObject.SetActive(false);
        _purpleFillRt.gameObject.SetActive(false);

        _phase1MarkerRt = CreateBarMarker(barBg, phase1MarkerText, 16f, phase1MarkerColor, barHeight + 4f);
        _phase2MarkerRt = CreateBarMarker(barBg, phase2MarkerText, 16f, phase2MarkerColor, barHeight + 4f);
        _phase3MarkerRt = CreateBarMarker(barBg, phase3MarkerText, 16f, phase3MarkerColor, barHeight + 4f);
        SetMarkerPosition(_phase2MarkerRt, _barInnerWidth * (1f / 3f));
        SetMarkerPosition(_phase3MarkerRt, _barInnerWidth * (2f / 3f));
    }

    static RectTransform CreateBarFill(RectTransform barBg, string name, Color color)
    {
        var fill = new GameObject(name).AddComponent<RectTransform>();
        fill.SetParent(barBg, false);
        fill.anchorMin = new Vector2(0f, 0f);
        fill.anchorMax = new Vector2(0f, 1f);
        fill.pivot = new Vector2(0f, 0.5f);
        fill.anchoredPosition = Vector2.zero;
        fill.sizeDelta = Vector2.zero;

        var img = fill.gameObject.AddComponent<Image>();
        img.color = color;
        return fill;
    }

    static RectTransform CreateBarMarker(RectTransform barBg, string text, float fontSize, Color color, float y)
    {
        var rt = new GameObject(text + "Marker").AddComponent<RectTransform>();
        rt.SetParent(barBg, false);
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(0f, y);
        rt.sizeDelta = new Vector2(280f, 26f);

        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.BottomLeft;
        tmp.enableWordWrapping = false;
        if (TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;

        return rt;
    }
}
