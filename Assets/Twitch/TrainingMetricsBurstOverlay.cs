using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>По #show metrics — переключатель (вкл/выкл) графиков обучения на экране.</summary>
public sealed class TrainingMetricsBurstOverlay : MonoBehaviour
{
    const int GraphWidth = 280;
    const int GraphHeight = 110;

    static TrainingMetricsBurstOverlay _instance;

    GameObject _panel;
    RawImage _rewardGraph;
    RawImage _entropyGraph;
    RawImage _gradGraph;
    TMP_Text _valuesText;
    Texture2D _rewardTex;
    Texture2D _entropyTex;
    Texture2D _gradTex;
    bool _visible;

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
        RedrawAll();
    }

    void ToggleInternal()
    {
        if (_panel == null)
            BuildUi();

        _visible = !_visible;
        _panel.SetActive(_visible);
        if (_visible)
            RedrawAll();
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
        panelRt.sizeDelta = new Vector2(920f, 420f);

        var bg = _panel.AddComponent<Image>();
        bg.color = new Color(0.03f, 0.05f, 0.08f, 0.92f);

        CreateTitle(_panel.transform, "Метрики обучения (#show metrics — вкл/выкл)", new Vector2(0f, -8f));

        _rewardGraph = CreateGraphSlot(_panel.transform, "Суммарный reward (EMA)", new Vector2(-300f, 40f), out _rewardTex);
        _entropyGraph = CreateGraphSlot(_panel.transform, "Энтропия политики", new Vector2(0f, 40f), out _entropyTex);
        _gradGraph = CreateGraphSlot(_panel.transform, "Grad norm (approx)", new Vector2(300f, 40f), out _gradTex);

        var valuesGo = new GameObject("Values");
        valuesGo.transform.SetParent(_panel.transform, false);
        var valuesRt = valuesGo.AddComponent<RectTransform>();
        valuesRt.anchorMin = new Vector2(0f, 0f);
        valuesRt.anchorMax = new Vector2(1f, 0f);
        valuesRt.pivot = new Vector2(0.5f, 0f);
        valuesRt.anchoredPosition = new Vector2(0f, 10f);
        valuesRt.sizeDelta = new Vector2(-24f, 36f);

        _valuesText = valuesGo.AddComponent<TextMeshProUGUI>();
        _valuesText.fontSize = 15;
        _valuesText.alignment = TextAlignmentOptions.Center;
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
        rt.sizeDelta = new Vector2(880f, 28f);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 20;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        if (TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;
    }

    static RawImage CreateGraphSlot(Transform parent, string label, Vector2 anchoredPos, out Texture2D tex)
    {
        var slot = new GameObject(label);
        slot.transform.SetParent(parent, false);
        var rt = slot.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(GraphWidth + 12f, GraphHeight + 36f);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(slot.transform, false);
        var labelRt = labelGo.AddComponent<RectTransform>();
        labelRt.anchorMin = new Vector2(0f, 1f);
        labelRt.anchorMax = new Vector2(1f, 1f);
        labelRt.pivot = new Vector2(0.5f, 1f);
        labelRt.anchoredPosition = Vector2.zero;
        labelRt.sizeDelta = new Vector2(0f, 22f);

        var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
        labelTmp.text = label;
        labelTmp.fontSize = 14;
        labelTmp.alignment = TextAlignmentOptions.Center;
        if (TMP_Settings.defaultFontAsset != null)
            labelTmp.font = TMP_Settings.defaultFontAsset;

        var graphGo = new GameObject("Graph");
        graphGo.transform.SetParent(slot.transform, false);
        var graphRt = graphGo.AddComponent<RectTransform>();
        graphRt.anchorMin = new Vector2(0f, 0f);
        graphRt.anchorMax = new Vector2(1f, 1f);
        graphRt.offsetMin = new Vector2(0f, 0f);
        graphRt.offsetMax = new Vector2(0f, -24f);

        tex = new Texture2D(GraphWidth, GraphHeight, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        var raw = graphGo.AddComponent<RawImage>();
        raw.texture = tex;
        return raw;
    }

    void RedrawAll()
    {
        var rewardSeries = TrainingGraphOverlay.GetJackRewardSeriesCopy();
        var entropySeries = TrainingPolicyStats.EntropySeries;
        var gradSeries = TrainingPolicyStats.GradNormSeries;

        DrawSeriesTexture(_rewardTex, rewardSeries, new Color(0.22f, 0.48f, 0.95f, 1f));
        DrawSeriesTexture(_entropyTex, entropySeries, new Color(0.35f, 0.95f, 0.55f, 1f));
        DrawSeriesTexture(_gradTex, gradSeries, new Color(0.98f, 0.72f, 0.25f, 1f));

        float jackLive = TrainingGraphOverlay.GetPresentationJackCumulativeReward();
        if (_valuesText != null)
        {
            _valuesText.text =
                $"Reward EMA: {TrainingGraphOverlay.JackEma:F2} · эпизод сейчас: {jackLive:F2}   |   " +
                $"Entropy: {TrainingPolicyStats.LastEntropy:F2}   |   " +
                $"Grad≈: {TrainingPolicyStats.LastGradNormApprox:F3}";
        }
    }

    static void DrawSeriesTexture(Texture2D tex, IReadOnlyList<float> values, Color lineColor)
    {
        if (tex == null)
            return;

        int w = tex.width;
        int h = tex.height;
        var pixels = new Color[w * h];
        var bg = new Color(0.08f, 0.1f, 0.12f, 1f);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = bg;

        if (values == null || values.Count < 2)
        {
            tex.SetPixels(pixels);
            tex.Apply(false);
            return;
        }

        float yMin = float.PositiveInfinity;
        float yMax = float.NegativeInfinity;
        for (int i = 0; i < values.Count; i++)
        {
            yMin = Mathf.Min(yMin, values[i]);
            yMax = Mathf.Max(yMax, values[i]);
        }

        if (Mathf.Approximately(yMin, yMax))
        {
            yMin -= 1f;
            yMax += 1f;
        }

        int count = values.Count;
        for (int i = 1; i < count; i++)
        {
            float x0 = (i - 1) / (float)(count - 1) * (w - 1);
            float x1 = i / (float)(count - 1) * (w - 1);
            float y0 = (1f - Mathf.InverseLerp(yMin, yMax, values[i - 1])) * (h - 1);
            float y1 = (1f - Mathf.InverseLerp(yMin, yMax, values[i])) * (h - 1);
            DrawLine(pixels, w, h, Mathf.RoundToInt(x0), Mathf.RoundToInt(y0), Mathf.RoundToInt(x1), Mathf.RoundToInt(y1), lineColor);
        }

        tex.SetPixels(pixels);
        tex.Apply(false);
    }

    static void DrawLine(Color[] pixels, int w, int h, int x0, int y0, int x1, int y1, Color color)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h)
                pixels[y0 * w + x0] = color;

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
