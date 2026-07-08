using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Runtime HUD: HP bars for Jack and Lily in top-left corner.
/// Created automatically at runtime (no scene wiring required).
/// </summary>
public sealed class HudHpBars : MonoBehaviour
{
    [Header("Visibility")]
    [Tooltip("Если false — HUD полоски HP (Jack/Lily) не рисуются.")]
    [SerializeField] private bool showHudHpBars = true;

    /// <summary>
    /// Глобальный переключатель HUD HP (для выключения из JackScript в инспекторе до Play).
    /// </summary>
    public static bool GlobalEnabled { get; private set; } = true;

    public static void SetGlobalEnabled(bool enabled)
    {
        GlobalEnabled = enabled;
        var inst = FindObjectOfType<HudHpBars>();
        if (inst != null)
            inst.ApplyVisibility();
    }

    [Header("Layout")]
    [SerializeField] private Vector2 padding = new Vector2(16f, 16f);
    [SerializeField] private float topOffset = 36f;
    [SerializeField] private float barWidth = 140f;
    [SerializeField] private float barHeight = 18f;
    [SerializeField] private float rowSpacing = 10f;

    [Header("Colors")]
    [SerializeField] private Color backgroundColor = new Color(0f, 0f, 0f, 0.55f);
    [SerializeField] private Color jackFill = new Color(0.2f, 0.75f, 1.0f, 0.95f);
    [SerializeField] private Color lilyFill = new Color(1.0f, 0.35f, 0.75f, 0.95f);

    private IHasHp _jack;
    private IHasHp _lily;

    private Image _jackFillImg;
    private RectTransform _jackFillRt;
    private TextMeshProUGUI _jackText;
    private Image _lilyFillImg;
    private RectTransform _lilyFillRt;
    private TextMeshProUGUI _lilyText;

    private Canvas _canvas;
    private RectTransform _topLeftRoot;
    private RectTransform _jackRow;
    private RectTransform _lilyRow;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (!GlobalEnabled)
            return;

        // FindObjectOfType не видит выключенные/неактивные объекты.
        // Поэтому ищем HudHpBars в загруженных сценах даже если он выключен —
        // чтобы можно было положить его в сцену ДО Play и управлять showHudHpBars в инспекторе.
        var all = Resources.FindObjectsOfTypeAll<HudHpBars>();
        for (int i = 0; i < all.Length; i++)
        {
            var h = all[i];
            if (h == null) continue;
            var go = h.gameObject;
            var sc = go.scene;
            if (sc.IsValid() && sc.isLoaded)
                return;
        }

        var runtimeGo = new GameObject(nameof(HudHpBars));
        DontDestroyOnLoad(runtimeGo);
        runtimeGo.AddComponent<HudHpBars>();
    }

    private void Awake()
    {
        // Если выключено ещё в инспекторе до Play — вообще не создаём Canvas.
        if (GlobalEnabled && showHudHpBars)
            CreateCanvasAndBars();
    }

    private void Update()
    {
        ApplyVisibility();
        if (!GlobalEnabled || !showHudHpBars)
            return;

        // Если включили во время Play, а Canvas ещё не создан — создаём лениво.
        if (_canvas == null)
            CreateCanvasAndBars();

        _jack = FindActiveJack();
        _lily = FindActiveLily();

        if (_lilyRow != null)
            _lilyRow.gameObject.SetActive(_lily != null);

        LayoutRoot();

        UpdateBar(_jack, _jackFillRt, _jackText, "Джек");
        if (_lily != null)
            UpdateBar(_lily, _lilyFillRt, _lilyText, "Lily");
    }

    static AgentGoToHouseDiscrete FindActiveJack() =>
        TrainingEnvSpace.FindPresentationJack();

    static LilyScript FindActiveLily()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root != null)
            return root.GetComponentInChildren<LilyScript>(false);
        return FindObjectOfType<LilyScript>();
    }

    void LayoutRoot()
    {
        if (_topLeftRoot == null)
            return;

        int rows = 1 + (_lilyRow != null && _lilyRow.gameObject.activeSelf ? 1 : 0);
        _topLeftRoot.sizeDelta = new Vector2(barWidth + 110f, (barHeight + rowSpacing) * rows + 10f);
    }

    private void ApplyVisibility()
    {
        if (_canvas != null)
            _canvas.enabled = GlobalEnabled && showHudHpBars;
    }

    private void UpdateBar(IHasHp hp, RectTransform fillRt, TextMeshProUGUI label, string name)
    {
        if (fillRt == null || label == null) return;

        if (hp == null || hp.MaxHp <= 0)
        {
            fillRt.sizeDelta = new Vector2(0f, fillRt.sizeDelta.y);
            label.text = $"{name}: --/--";
            return;
        }

        float t = Mathf.Clamp01((float)hp.Hp / hp.MaxHp);
        fillRt.sizeDelta = new Vector2(barWidth * t, fillRt.sizeDelta.y);
        label.text = $"{name}: {hp.Hp}/{hp.MaxHp}";
    }

    private void CreateCanvasAndBars()
    {
        var canvasGo = new GameObject("HudCanvas");
        canvasGo.transform.SetParent(transform, false);

        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 1000;

        canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGo.AddComponent<GraphicRaycaster>();

        var root = new GameObject("TopLeftRoot").AddComponent<RectTransform>();
        root.SetParent(canvasGo.transform, false);
        root.anchorMin = new Vector2(0f, 1f);
        root.anchorMax = new Vector2(0f, 1f);
        root.pivot = new Vector2(0f, 1f);
        root.anchoredPosition = new Vector2(padding.x, -(padding.y + topOffset));
        root.sizeDelta = new Vector2(barWidth + 110f, (barHeight + rowSpacing) * 2f + 10f);

        _topLeftRoot = root;

        _jackRow = CreateRow(root, 0, "Джек", jackFill, out _jackFillImg, out _jackFillRt, out _jackText);
        _lilyRow = CreateRow(root, 1, "Lily", lilyFill, out _lilyFillImg, out _lilyFillRt, out _lilyText);
        _lilyRow.gameObject.SetActive(FindActiveLily() != null);
        LayoutRoot();
    }

    private RectTransform CreateRow(RectTransform parent, int rowIndex, string title, Color fillColor,
        out Image fillImg, out RectTransform fillRt, out TextMeshProUGUI text)
    {
        float y = -rowIndex * (barHeight + rowSpacing);

        var row = new GameObject($"{title}Row").AddComponent<RectTransform>();
        row.SetParent(parent, false);
        row.anchorMin = new Vector2(0f, 1f);
        row.anchorMax = new Vector2(0f, 1f);
        row.pivot = new Vector2(0f, 1f);
        row.anchoredPosition = new Vector2(0f, y);
        row.sizeDelta = new Vector2(parent.sizeDelta.x, barHeight);

        // Background
        var bg = new GameObject($"{title}Bg").AddComponent<RectTransform>();
        bg.SetParent(row, false);
        bg.anchorMin = new Vector2(0f, 0f);
        bg.anchorMax = new Vector2(0f, 1f);
        bg.pivot = new Vector2(0f, 0.5f);
        bg.anchoredPosition = Vector2.zero;
        bg.sizeDelta = new Vector2(barWidth, 0f);

        var bgImg = bg.gameObject.AddComponent<Image>();
        bgImg.color = backgroundColor;

        // Fill
        var fill = new GameObject($"{title}Fill").AddComponent<RectTransform>();
        fill.SetParent(bg, false);
        fill.anchorMin = new Vector2(0f, 0f);
        fill.anchorMax = new Vector2(1f, 1f);
        fill.pivot = new Vector2(0f, 0.5f);
        fill.anchoredPosition = Vector2.zero;
        fill.sizeDelta = Vector2.zero;

        fillRt = fill;
        fillImg = fill.gameObject.AddComponent<Image>();
        fillImg.type = Image.Type.Simple;
        fillImg.color = fillColor;
        // We'll control width manually for a "shrinking bar" look.
        fill.anchorMax = new Vector2(0f, 1f);
        fill.sizeDelta = new Vector2(barWidth, 0f);

        // Text
        var labelRt = new GameObject($"{title}Text").AddComponent<RectTransform>();
        labelRt.SetParent(row, false);
        labelRt.anchorMin = new Vector2(0f, 0f);
        labelRt.anchorMax = new Vector2(1f, 1f);
        labelRt.pivot = new Vector2(0f, 0.5f);
        labelRt.anchoredPosition = new Vector2(barWidth + 8f, 0f);
        labelRt.sizeDelta = new Vector2(180f, 0f);

        text = labelRt.gameObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = 16f;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Left;
        text.text = $"{title}: --/--";
        text.enableWordWrapping = false;
        return row;
    }
}

