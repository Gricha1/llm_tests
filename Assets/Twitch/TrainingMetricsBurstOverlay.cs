using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>По #show metrics — переключатель (вкл/выкл) графиков обучения на экране.</summary>
public sealed class TrainingMetricsBurstOverlay : MonoBehaviour
{
    const int GraphWidth = 300;
    const int GraphHeight = 100;
    const int YAxisWidth = 52;
    const int SlotPaddingX = 8;
    const int XAxisHeight = 20;
    const int LineThickness = 2;
    const int YTickCount = 5;
    const int XTickCount = 3;
    const float RedrawIntervalSeconds = 0.5f;

    static readonly Color GridColor = new Color(0.22f, 0.26f, 0.3f, 1f);
    static readonly Color AxisLabelColor = new Color(0.78f, 0.82f, 0.88f, 1f);
    static readonly Color GraphBg = new Color(0.08f, 0.1f, 0.12f, 1f);

    static TrainingMetricsBurstOverlay _instance;
    static Color[] _sharedPixels;

    sealed class GraphSlot
    {
        public RawImage Image;
        public Texture2D Tex;
        public TMP_Text TitleLabel;
        public TMP_Text[] YLabels = new TMP_Text[YTickCount];
        public TMP_Text[] XLabels = new TMP_Text[XTickCount];
        public float LastYMin;
        public float LastYMax;
        public int LastPointCount;
        public bool IsPercent;
    }

    GameObject _panel;
    GraphSlot _rewardGraph;
    GraphSlot _entropyGraph;
    GraphSlot _gradGraph;
    GraphSlot _woodSuccessGraph;
    GraphSlot _sheepSuccessGraph;
    GraphSlot _fireSuccessGraph;
    TMP_Text _valuesText;
    bool _visible;
    float _nextRedrawTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_instance != null)
            return;

        var go = new GameObject(nameof(TrainingMetricsBurstOverlay));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<TrainingMetricsBurstOverlay>();
    }

    public static void Toggle()
    {
        if (_instance == null)
            Bootstrap();
        _instance.ToggleInternal();
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        BuildUi();
        _panel.SetActive(false);
    }

    void Update()
    {
        if (!_visible)
            return;
        // Не каждый кадр: 6× SetPixels+диск убивали FPS стрима → «игра замерла».
        if (Time.unscaledTime < _nextRedrawTime)
            return;
        _nextRedrawTime = Time.unscaledTime + RedrawIntervalSeconds;
        RedrawAll();
    }

    void ToggleInternal()
    {
        if (_panel == null)
            BuildUi();

        _visible = !_visible;
        _panel.SetActive(_visible);
        if (_visible)
        {
            _nextRedrawTime = 0f;
            RedrawAll();
        }
    }

    void BuildUi()
    {
        var canvasGo = new GameObject("MetricsBurstCanvas");
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1100;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasGo.AddComponent<GraphicRaycaster>();

        _panel = new GameObject("Panel");
        _panel.transform.SetParent(canvasGo.transform, false);

        var panelRt = _panel.AddComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.5f, 0.5f);
        panelRt.anchorMax = new Vector2(0.5f, 0.5f);
        panelRt.pivot = new Vector2(0.5f, 0.5f);
        panelRt.sizeDelta = new Vector2(1180f, 640f);

        var bg = _panel.AddComponent<Image>();
        bg.color = new Color(0.03f, 0.05f, 0.08f, 0.92f);
        bg.raycastTarget = false;
        _panel.AddComponent<RectMask2D>();

        CreateTitle(_panel.transform, "Метрики обучения (#show metrics — вкл/выкл)", new Vector2(0f, -8f));

        float colStep = (GraphWidth + YAxisWidth + SlotPaddingX * 2f + 12f);
        _rewardGraph = CreateGraphSlot(_panel.transform, "Суммарный reward (EMA)", new Vector2(-colStep, 165f), false);
        _entropyGraph = CreateGraphSlot(_panel.transform, "Энтропия политики", new Vector2(0f, 165f), false);
        _gradGraph = CreateGraphSlot(_panel.transform, "Grad norm (approx)", new Vector2(colStep, 165f), false);

        _woodSuccessGraph = CreateGraphSlot(_panel.transform, "Success: Wood (≥10)", new Vector2(-colStep, -55f), true);
        _sheepSuccessGraph = CreateGraphSlot(_panel.transform, "Success: Sheep (≥10)", new Vector2(0f, -55f), true);
        _fireSuccessGraph = CreateGraphSlot(_panel.transform, "Success: Fire (дом)", new Vector2(colStep, -55f), true);

        var valuesGo = new GameObject("Values");
        valuesGo.transform.SetParent(_panel.transform, false);
        var valuesRt = valuesGo.AddComponent<RectTransform>();
        valuesRt.anchorMin = new Vector2(0f, 0f);
        valuesRt.anchorMax = new Vector2(1f, 0f);
        valuesRt.pivot = new Vector2(0.5f, 0f);
        valuesRt.anchoredPosition = new Vector2(0f, 10f);
        valuesRt.sizeDelta = new Vector2(-24f, 44f);

        _valuesText = valuesGo.AddComponent<TextMeshProUGUI>();
        _valuesText.fontSize = 17;
        _valuesText.alignment = TextAlignmentOptions.Center;
        _valuesText.raycastTarget = false;
        if (TMP_Settings.defaultFontAsset != null)
            _valuesText.font = TMP_Settings.defaultFontAsset;
    }

    static void CreateTitle(Transform parent, string text, Vector2 anchoredPos)
    {
        var go = new GameObject("Title");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(980f, 32f);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 24;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        if (TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;
    }

    static GraphSlot CreateGraphSlot(Transform parent, string label, Vector2 anchoredPos, bool isPercent)
    {
        var slot = new GraphSlot { IsPercent = isPercent };

        var slotGo = new GameObject(label);
        slotGo.transform.SetParent(parent, false);
        var rt = slotGo.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(GraphWidth + YAxisWidth + SlotPaddingX * 2f, GraphHeight + XAxisHeight + 48f);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(slotGo.transform, false);
        var labelRt = labelGo.AddComponent<RectTransform>();
        labelRt.anchorMin = new Vector2(0f, 1f);
        labelRt.anchorMax = new Vector2(1f, 1f);
        labelRt.pivot = new Vector2(0.5f, 1f);
        labelRt.anchoredPosition = Vector2.zero;
        labelRt.sizeDelta = new Vector2(0f, 26f);

        slot.TitleLabel = labelGo.AddComponent<TextMeshProUGUI>();
        slot.TitleLabel.text = label;
        slot.TitleLabel.fontSize = 18;
        slot.TitleLabel.fontStyle = FontStyles.Bold;
        slot.TitleLabel.alignment = TextAlignmentOptions.Center;
        slot.TitleLabel.raycastTarget = false;
        if (TMP_Settings.defaultFontAsset != null)
            slot.TitleLabel.font = TMP_Settings.defaultFontAsset;

        var plotGo = new GameObject("Plot");
        plotGo.transform.SetParent(slotGo.transform, false);
        var plotRt = plotGo.AddComponent<RectTransform>();
        plotRt.anchorMin = new Vector2(0f, 0f);
        plotRt.anchorMax = new Vector2(1f, 1f);
        plotRt.offsetMin = new Vector2(SlotPaddingX, 0f);
        plotRt.offsetMax = new Vector2(-SlotPaddingX, -28f);

        var yAxisGo = new GameObject("YAxis");
        yAxisGo.transform.SetParent(plotGo.transform, false);
        var yAxisRt = yAxisGo.AddComponent<RectTransform>();
        yAxisRt.anchorMin = new Vector2(0f, 0f);
        yAxisRt.anchorMax = new Vector2(0f, 1f);
        yAxisRt.pivot = new Vector2(0f, 0.5f);
        yAxisRt.anchoredPosition = Vector2.zero;
        yAxisRt.sizeDelta = new Vector2(YAxisWidth, -XAxisHeight);

        for (int i = 0; i < YTickCount; i++)
        {
            float t = i / (float)(YTickCount - 1);
            slot.YLabels[i] = CreateAxisLabel(yAxisGo.transform, $"Y{i}", "0",
                new Vector2(0f, 1f - t), new Vector2(1f, 1f - t), new Vector2(1f, 0.5f),
                Vector2.zero, new Vector2(YAxisWidth - 4f, 18f), 12, TextAlignmentOptions.MidlineRight);
        }

        var graphGo = new GameObject("Graph");
        graphGo.transform.SetParent(plotGo.transform, false);
        var graphRt = graphGo.AddComponent<RectTransform>();
        graphRt.anchorMin = new Vector2(0f, 0f);
        graphRt.anchorMax = new Vector2(1f, 1f);
        graphRt.offsetMin = new Vector2(YAxisWidth, XAxisHeight);
        graphRt.offsetMax = Vector2.zero;

        slot.Tex = new Texture2D(GraphWidth, GraphHeight, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        slot.Image = graphGo.AddComponent<RawImage>();
        slot.Image.texture = slot.Tex;
        slot.Image.raycastTarget = false;

        var xAxisGo = new GameObject("XAxis");
        xAxisGo.transform.SetParent(plotGo.transform, false);
        var xAxisRt = xAxisGo.AddComponent<RectTransform>();
        xAxisRt.anchorMin = new Vector2(0f, 0f);
        xAxisRt.anchorMax = new Vector2(1f, 0f);
        xAxisRt.pivot = new Vector2(0.5f, 0f);
        xAxisRt.offsetMin = new Vector2(YAxisWidth, 0f);
        xAxisRt.offsetMax = new Vector2(0f, XAxisHeight);

        for (int i = 0; i < XTickCount; i++)
        {
            float anchorX = i / (float)(XTickCount - 1);
            slot.XLabels[i] = CreateAxisLabel(xAxisGo.transform, $"X{i}", "0",
                new Vector2(anchorX, 0f), new Vector2(anchorX, 0f), new Vector2(0.5f, 0f),
                Vector2.zero, new Vector2(64f, 18f), 12, TextAlignmentOptions.Bottom);
            slot.XLabels[i].alignment = i == 0
                ? TextAlignmentOptions.BottomLeft
                : (i == XTickCount - 1 ? TextAlignmentOptions.BottomRight : TextAlignmentOptions.Bottom);
        }

        return slot;
    }

    static TMP_Text CreateAxisLabel(Transform parent, string name, string text,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPos, Vector2 size, int fontSize,
        TextAlignmentOptions alignment)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        var label = go.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.color = AxisLabelColor;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.alignment = alignment;
        label.raycastTarget = false;
        if (TMP_Settings.defaultFontAsset != null)
            label.font = TMP_Settings.defaultFontAsset;
        return label;
    }

    void RedrawAll()
    {
        TrainingTaskSuccessTracker.TickOverlayRefresh();

        // Без копии списка каждый кадр — только чтение.
        var rewardSeries = TrainingGraphOverlay.GetJackRewardSeriesReadonly();
        var entropySeries = TrainingPolicyStats.EntropySeries;
        var gradSeries = TrainingPolicyStats.GradNormSeries;
        var woodSeries = TrainingTaskSuccessTracker.GetRateSeries(TrainingTaskSuccessTracker.Metric.Wood);
        var sheepSeries = TrainingTaskSuccessTracker.GetRateSeries(TrainingTaskSuccessTracker.Metric.Sheep);
        var fireSeries = TrainingTaskSuccessTracker.GetRateSeries(TrainingTaskSuccessTracker.Metric.Fire);

        DrawGraph(_rewardGraph, rewardSeries, new Color(0.22f, 0.48f, 0.95f, 1f), "Суммарный reward (EMA)", null);
        DrawGraph(_entropyGraph, entropySeries, new Color(0.35f, 0.95f, 0.55f, 1f), "Энтропия политики", null);
        DrawGraph(_gradGraph, gradSeries, new Color(0.98f, 0.72f, 0.25f, 1f), "Grad norm (approx)", null);

        float woodRate = TrainingTaskSuccessTracker.GetLastRate(TrainingTaskSuccessTracker.Metric.Wood);
        float sheepRate = TrainingTaskSuccessTracker.GetLastRate(TrainingTaskSuccessTracker.Metric.Sheep);
        float fireRate = TrainingTaskSuccessTracker.GetLastRate(TrainingTaskSuccessTracker.Metric.Fire);
        int woodN = TrainingTaskSuccessTracker.GetSampleCount(TrainingTaskSuccessTracker.Metric.Wood);
        int sheepN = TrainingTaskSuccessTracker.GetSampleCount(TrainingTaskSuccessTracker.Metric.Sheep);
        int fireN = TrainingTaskSuccessTracker.GetSampleCount(TrainingTaskSuccessTracker.Metric.Fire);

        DrawGraph(_woodSuccessGraph, woodSeries, new Color(0.55f, 0.82f, 0.35f, 1f),
            $"Success: Wood (≥10) — {woodRate:P0} · n={woodN}", 0f, 1f);
        DrawGraph(_sheepSuccessGraph, sheepSeries, new Color(0.98f, 0.55f, 0.35f, 1f),
            $"Success: Sheep (≥10) — {sheepRate:P0} · n={sheepN}", 0f, 1f);
        DrawGraph(_fireSuccessGraph, fireSeries, new Color(0.95f, 0.45f, 0.55f, 1f),
            $"Success: Fire (дом) — {fireRate:P0} · n={fireN}", 0f, 1f);

        float jackLive = TrainingGraphOverlay.GetPresentationJackCumulativeReward();
        if (_valuesText != null)
        {
            int graphPoints = TrainingGraphOverlay.GraphPointCount;
            _valuesText.text =
                $"Reward EMA: {TrainingGraphOverlay.JackEma:F2} · эпизод: {jackLive:F2} · точек: {graphPoints}   |   " +
                $"Entropy: {TrainingPolicyStats.LastEntropy:F2}   |   Grad≈: {TrainingPolicyStats.LastGradNormApprox:F3}   |   " +
                $"SR wood {woodRate:P0} (n={woodN}) · sheep {sheepRate:P0} (n={sheepN}) · " +
                $"fire {fireRate:P0} (n={fireN}) · water {TrainingTaskSuccessTracker.GetLastRate(TrainingTaskSuccessTracker.Metric.Water):P0}";
        }
    }

    static void DrawGraph(GraphSlot slot, IReadOnlyList<float> values, Color lineColor, string title,
        float? fixedMin = null, float? fixedMax = null)
    {
        if (slot == null)
            return;

        if (slot.TitleLabel != null)
        {
            slot.TitleLabel.text = title;
            slot.TitleLabel.raycastTarget = false;
        }

        DrawSeriesTexture(slot, values, lineColor, fixedMin, fixedMax);
        UpdateAxisLabels(slot);
    }

    static Color[] GetPixelBuffer(int w, int h)
    {
        int need = w * h;
        if (_sharedPixels == null || _sharedPixels.Length != need)
            _sharedPixels = new Color[need];
        return _sharedPixels;
    }

    static void DrawSeriesTexture(GraphSlot slot, IReadOnlyList<float> values, Color lineColor,
        float? fixedMin = null, float? fixedMax = null)
    {
        var tex = slot.Tex;
        if (tex == null)
            return;

        int w = tex.width;
        int h = tex.height;
        var pixels = GetPixelBuffer(w, h);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = GraphBg;

        slot.LastPointCount = values?.Count ?? 0;

        float yMin = fixedMin ?? float.PositiveInfinity;
        float yMax = fixedMax ?? float.NegativeInfinity;
        if (values != null && values.Count > 0 && (!fixedMin.HasValue || !fixedMax.HasValue))
        {
            for (int i = 0; i < values.Count; i++)
            {
                yMin = Mathf.Min(yMin, values[i]);
                yMax = Mathf.Max(yMax, values[i]);
            }
        }

        if (float.IsPositiveInfinity(yMin))
        {
            yMin = 0f;
            yMax = 1f;
        }

        if (Mathf.Approximately(yMin, yMax))
        {
            yMin -= 1f;
            yMax += 1f;
        }

        slot.LastYMin = yMin;
        slot.LastYMax = yMax;

        for (int i = 0; i < YTickCount; i++)
        {
            float t = i / (float)(YTickCount - 1);
            int gy = Mathf.RoundToInt(t * (h - 1));
            DrawHLine(pixels, w, h, gy, GridColor);
        }

        if (values == null || values.Count == 0)
        {
            tex.SetPixels(pixels);
            tex.Apply(false);
            return;
        }

        // Не рисуем все 512 точек толстой кистью — прореживаем до ширины графика.
        int count = values.Count;
        int step = Mathf.Max(1, count / w);
        if (count == 1)
        {
            int py = ValueToPixelY(values[0], yMin, yMax, h);
            DrawHLine(pixels, w, h, py, lineColor, LineThickness);
        }
        else
        {
            int prevI = 0;
            for (int i = step; i < count; i += step)
            {
                float x0 = prevI / (float)(count - 1) * (w - 1);
                float x1 = i / (float)(count - 1) * (w - 1);
                int y0 = ValueToPixelY(values[prevI], yMin, yMax, h);
                int y1 = ValueToPixelY(values[i], yMin, yMax, h);
                DrawLineThick(pixels, w, h, Mathf.RoundToInt(x0), y0, Mathf.RoundToInt(x1), y1, lineColor, LineThickness);
                prevI = i;
            }

            if (prevI != count - 1)
            {
                float x0 = prevI / (float)(count - 1) * (w - 1);
                float x1 = w - 1;
                int y0 = ValueToPixelY(values[prevI], yMin, yMax, h);
                int y1 = ValueToPixelY(values[count - 1], yMin, yMax, h);
                DrawLineThick(pixels, w, h, Mathf.RoundToInt(x0), y0, Mathf.RoundToInt(x1), y1, lineColor, LineThickness);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(false);
    }

    static int ValueToPixelY(float value, float yMin, float yMax, int h)
    {
        float t = Mathf.InverseLerp(yMin, yMax, value);
        return Mathf.RoundToInt(t * (h - 1));
    }

    static void UpdateAxisLabels(GraphSlot slot)
    {
        for (int i = 0; i < YTickCount; i++)
        {
            if (slot.YLabels[i] == null)
                continue;

            float t = i / (float)(YTickCount - 1);
            float value = Mathf.Lerp(slot.LastYMax, slot.LastYMin, t);
            slot.YLabels[i].text = slot.IsPercent
                ? $"{Mathf.Clamp(Mathf.RoundToInt(value * 100f), 0, 100)}%"
                : FormatAxisValue(value);
        }

        int count = slot.LastPointCount;
        if (count <= 0)
        {
            if (slot.XLabels[0] != null) slot.XLabels[0].text = "0";
            if (slot.XLabels[1] != null) slot.XLabels[1].text = "";
            if (slot.XLabels[2] != null) slot.XLabels[2].text = "0";
            return;
        }

        int xMin = 1;
        int xMax = count;
        if (slot.XLabels[0] != null) slot.XLabels[0].text = xMin.ToString();
        if (slot.XLabels[1] != null) slot.XLabels[1].text = ((xMin + xMax) / 2).ToString();
        if (slot.XLabels[2] != null) slot.XLabels[2].text = xMax.ToString();
    }

    static string FormatAxisValue(float value)
    {
        float abs = Mathf.Abs(value);
        if (abs >= 100f)
            return value.ToString("F0");
        if (abs >= 10f)
            return value.ToString("F1");
        return value.ToString("F2");
    }

    static void DrawHLine(Color[] pixels, int w, int h, int y, Color color, int thickness = 1)
    {
        for (int dy = -thickness / 2; dy <= thickness / 2; dy++)
        {
            int yy = Mathf.Clamp(y + dy, 0, h - 1);
            for (int x = 0; x < w; x++)
                pixels[yy * w + x] = color;
        }
    }

    static void DrawLineThick(Color[] pixels, int w, int h, int x0, int y0, int x1, int y1, Color color, int thickness)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int half = Mathf.Max(1, thickness / 2);

        while (true)
        {
            for (int oy = -half; oy <= half; oy++)
            {
                for (int ox = -half; ox <= half; ox++)
                {
                    int px = x0 + ox;
                    int py = y0 + oy;
                    if (px >= 0 && px < w && py >= 0 && py < h)
                        pixels[py * w + px] = color;
                }
            }

            if (x0 == x1 && y0 == y1)
                break;

            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }
}
