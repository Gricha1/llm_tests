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
    [SerializeField] private float panelWidth = 920f;
    [SerializeField] private float labelHeight = 28f;
    [SerializeField] private float barHeight = 22f;
    [SerializeField] private float barSpacing = 10f;
    [SerializeField] private float markerHeight = 22f;
    [SerializeField] private float markerRowSpacing = 6f;

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

    const int LayoutVersion = 2;
    int _layoutVersionApplied;

    void Awake()
    {
        if (GlobalEnabled && showHud)
            EnsureUi();
    }

    void Update()
    {
        ApplyVisibility();
        if (!GlobalEnabled || !showHud)
            return;

        EnsureUi();

        if (_jack == null || !_jack.isActiveAndEnabled)
            _jack = TrainingEnvSpace.FindPresentationJack();

        if (_canvas != null)
        {
            bool showTaskHud = _jack != null
                && TrainingEnvSpace.IsPresentationStreamEnv(TrainingEnvSpace.ActiveViewEnvRoot);
            _canvas.enabled = GlobalEnabled && showHud && showTaskHud;
            if (!showTaskHud)
                return;
        }

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
    }

    void EnsureUi()
    {
        if (_canvas != null && _layoutVersionApplied == LayoutVersion)
            return;

        if (_canvas != null)
            Destroy(_canvas.gameObject);

        _canvas = null;
        _greenFillRt = null;
        _redFillRt = null;
        _purpleFillRt = null;
        _phase1MarkerRt = null;
        _phase2MarkerRt = null;
        _phase3MarkerRt = null;
        _label = null;
        _layoutVersionApplied = LayoutVersion;
        CreateUi();
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
        root.sizeDelta = new Vector2(panelWidth, labelHeight + barSpacing + barHeight + markerRowSpacing + markerHeight + 16f);

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

        var markersRow = new GameObject("MarkersRow").AddComponent<RectTransform>();
        markersRow.SetParent(root, false);
        markersRow.anchorMin = new Vector2(0f, 0f);
        markersRow.anchorMax = new Vector2(1f, 0f);
        markersRow.pivot = new Vector2(0.5f, 0f);
        markersRow.anchoredPosition = new Vector2(0f, 8f);
        markersRow.sizeDelta = new Vector2(-24f, markerHeight);

        _phase1MarkerRt = CreatePhaseLabel(markersRow, phase1MarkerText, 0f, phase1MarkerColor);
        _phase2MarkerRt = CreatePhaseLabel(markersRow, phase2MarkerText, 0.5f, phase2MarkerColor);
        _phase3MarkerRt = CreatePhaseLabel(markersRow, phase3MarkerText, 1f, phase3MarkerColor);

        var barBg = new GameObject("BarBg").AddComponent<RectTransform>();
        barBg.SetParent(root, false);
        barBg.anchorMin = new Vector2(0f, 0f);
        barBg.anchorMax = new Vector2(1f, 0f);
        barBg.pivot = new Vector2(0.5f, 0f);
        barBg.anchoredPosition = new Vector2(0f, 8f + markerHeight + markerRowSpacing);
        barBg.sizeDelta = new Vector2(-24f, barHeight);

        _barInnerWidth = panelWidth - 24f;

        var barBgImg = barBg.gameObject.AddComponent<Image>();
        barBgImg.color = barBackgroundColor;

        _greenFillRt = CreateBarFill(barBg, "GreenFill", phase1FillColor);
        _redFillRt = CreateBarFill(barBg, "RedFill", phase2FillColor);
        _purpleFillRt = CreateBarFill(barBg, "PurpleFill", phase3FillColor);
        _redFillRt.gameObject.SetActive(false);
        _purpleFillRt.gameObject.SetActive(false);
    }

    static RectTransform CreatePhaseLabel(RectTransform parent, string text, float anchorX, Color color)
    {
        var rt = new GameObject(text + "Label").AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(anchorX, 0f);
        rt.anchorMax = new Vector2(anchorX, 1f);
        rt.pivot = new Vector2(anchorX, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(280f, 0f);

        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 14f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = color;
        tmp.alignment = anchorX <= 0.01f
            ? TextAlignmentOptions.BottomLeft
            : anchorX >= 0.99f
                ? TextAlignmentOptions.BottomRight
                : TextAlignmentOptions.Bottom;
        tmp.enableWordWrapping = false;
        if (TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;

        return rt;
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
}
