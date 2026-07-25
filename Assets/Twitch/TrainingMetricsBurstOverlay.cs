using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// #show metrics jack|lily|george — задачи героя и success rate.
/// #show metrics без имени — скрыть. Меньше лагов: реже redraw, меньше графиков, skip SetPixels.
/// </summary>
public sealed class TrainingMetricsBurstOverlay : MonoBehaviour
{
    public enum MetricsHero
    {
        None = 0,
        Jack = 1,
        Lily = 2,
        George = 3,
    }

    const int GraphWidth = 200;
    const int GraphHeight = 68;
    const int YAxisWidth = 44;
    const int SlotPaddingX = 6;
    const int XAxisHeight = 16;
    const int LineThickness = 2;
    const int YTickCount = 3;
    const int XTickCount = 3;
    const int MaxSlots = 6; // reward + entropy + до 4 задач
    const float RedrawIntervalSeconds = 2.5f;

    static readonly Color GridColor = new Color(0.22f, 0.26f, 0.3f, 1f);
    static readonly Color AxisLabelColor = new Color(0.78f, 0.82f, 0.88f, 1f);
    static readonly Color GraphBg = new Color(0.08f, 0.1f, 0.12f, 1f);

    static readonly EnvTrainingTask[] JackTasks =
    {
        EnvTrainingTask.JackWood,
        EnvTrainingTask.JackFood,
        EnvTrainingTask.JackWater,
        EnvTrainingTask.JackZombie,
    };

    static readonly EnvTrainingTask[] LilyTasks =
    {
        EnvTrainingTask.LilyFood,
        EnvTrainingTask.LilyWater,
        EnvTrainingTask.LilyHeat,
        EnvTrainingTask.LilyFlower,
    };

    static readonly EnvTrainingTask[] GeorgeTasks =
    {
        EnvTrainingTask.GeorgeFood,
        EnvTrainingTask.GeorgeWater,
        EnvTrainingTask.GeorgeHeat,
    };

    static TrainingMetricsBurstOverlay _instance;
    static Color[] _sharedPixels;

    sealed class GraphSlot
    {
        public GameObject Root;
        public RawImage Image;
        public Texture2D Tex;
        public TMP_Text TitleLabel;
        public TMP_Text[] YLabels = new TMP_Text[YTickCount];
        public TMP_Text[] XLabels = new TMP_Text[XTickCount];
        public float LastYMin;
        public float LastYMax;
        public int LastPointCount;
        public int LastDrawSig;
        public bool IsPercent = true;
    }

    GameObject _panel;
    TMP_Text _titleText;
    TMP_Text _valuesText;
    readonly GraphSlot[] _slots = new GraphSlot[MaxSlots];
    bool _visible;
    MetricsHero _hero = MetricsHero.None;
    float _nextRedrawTime;
    readonly StringBuilder _sb = new StringBuilder(256);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_instance != null)
            return;

        var go = new GameObject(nameof(TrainingMetricsBurstOverlay));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<TrainingMetricsBurstOverlay>();
    }

    public static void Toggle() => Show(MetricsHero.Jack);

    /// <summary>9 в Play: Jack → Lily → George → скрыть → Jack…</summary>
    public static void CycleHeroes()
    {
        if (_instance == null)
            Bootstrap();
        _instance.CycleHeroesInternal();
    }

    public static void Show(MetricsHero hero)
    {
        if (_instance == null)
            Bootstrap();
        _instance.ShowInternal(hero);
    }

    void CycleHeroesInternal()
    {
        if (!_visible || _hero == MetricsHero.None)
        {
            ShowInternal(MetricsHero.Jack);
            return;
        }

        switch (_hero)
        {
            case MetricsHero.Jack:
                ShowInternal(MetricsHero.Lily);
                break;
            case MetricsHero.Lily:
                ShowInternal(MetricsHero.George);
                break;
            default:
                ShowInternal(MetricsHero.None);
                break;
        }
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
        if (Time.unscaledTime < _nextRedrawTime)
            return;
        _nextRedrawTime = Time.unscaledTime + RedrawIntervalSeconds;
        RedrawAll();
    }

    void ShowInternal(MetricsHero hero)
    {
        if (_panel == null)
            BuildUi();

        // Без имени героя — только скрыть.
        if (hero == MetricsHero.None)
        {
            _visible = false;
            _hero = MetricsHero.None;
            _panel.SetActive(false);
            return;
        }

        // Тот же герой повторно — скрыть.
        if (_visible && _hero == hero)
        {
            _visible = false;
            _hero = MetricsHero.None;
            _panel.SetActive(false);
            return;
        }

        _hero = hero;
        _visible = true;
        _panel.SetActive(true);
        _nextRedrawTime = 0f;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] != null)
                _slots[i].LastDrawSig = int.MinValue;
        }

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
        panelRt.sizeDelta = new Vector2(1180f, 580f);

        var bg = _panel.AddComponent<Image>();
        bg.color = new Color(0.03f, 0.05f, 0.08f, 0.92f);
        bg.raycastTarget = false;
        _panel.AddComponent<RectMask2D>();

        _titleText = CreateTitle(_panel.transform, "Метрики", new Vector2(0f, -8f));

        float colStep = GraphWidth + YAxisWidth + SlotPaddingX * 2f + 20f;
        float rowTop = 145f;
        float rowBot = -55f;
        Vector2[] positions =
        {
            new Vector2(-colStep, rowTop),
            new Vector2(0f, rowTop),
            new Vector2(colStep, rowTop),
            new Vector2(-colStep, rowBot),
            new Vector2(0f, rowBot),
            new Vector2(colStep, rowBot),
        };

        for (int i = 0; i < MaxSlots; i++)
            _slots[i] = CreateGraphSlot(_panel.transform, $"Slot{i}", positions[i]);

        var valuesGo = new GameObject("Values");
        valuesGo.transform.SetParent(_panel.transform, false);
        var valuesRt = valuesGo.AddComponent<RectTransform>();
        valuesRt.anchorMin = new Vector2(0f, 0f);
        valuesRt.anchorMax = new Vector2(1f, 0f);
        valuesRt.pivot = new Vector2(0.5f, 0f);
        valuesRt.anchoredPosition = new Vector2(0f, 10f);
        valuesRt.sizeDelta = new Vector2(-24f, 52f);

        _valuesText = valuesGo.AddComponent<TextMeshProUGUI>();
        _valuesText.fontSize = 16;
        _valuesText.alignment = TextAlignmentOptions.Center;
        _valuesText.raycastTarget = false;
        if (TMP_Settings.defaultFontAsset != null)
            _valuesText.font = TMP_Settings.defaultFontAsset;
    }

    static TMP_Text CreateTitle(Transform parent, string text, Vector2 anchoredPos)
    {
        var go = new GameObject("Title");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(920f, 32f);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = 22;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        if (TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;
        return tmp;
    }

    static GraphSlot CreateGraphSlot(Transform parent, string label, Vector2 anchoredPos)
    {
        var slot = new GraphSlot();

        var slotGo = new GameObject(label);
        slotGo.transform.SetParent(parent, false);
        slot.Root = slotGo;
        var rt = slotGo.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(GraphWidth + YAxisWidth + SlotPaddingX * 2f, GraphHeight + XAxisHeight + 44f);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(slotGo.transform, false);
        var labelRt = labelGo.AddComponent<RectTransform>();
        labelRt.anchorMin = new Vector2(0f, 1f);
        labelRt.anchorMax = new Vector2(1f, 1f);
        labelRt.pivot = new Vector2(0.5f, 1f);
        labelRt.anchoredPosition = Vector2.zero;
        labelRt.sizeDelta = new Vector2(0f, 24f);

        slot.TitleLabel = labelGo.AddComponent<TextMeshProUGUI>();
        slot.TitleLabel.fontSize = 16;
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
        plotRt.offsetMax = new Vector2(-SlotPaddingX, -26f);

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
                Vector2.zero, new Vector2(YAxisWidth - 4f, 16f), 11, TextAlignmentOptions.MidlineRight);
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
                Vector2.zero, new Vector2(56f, 16f), 11, TextAlignmentOptions.Bottom);
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
        label.textWrappingMode = TextWrappingModes.NoWrap;
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

        EnvTrainingTask[] tasks = TasksForHero(_hero);
        string heroName = HeroTitle(_hero);
        if (_titleText != null)
            _titleText.text = $"Метрики: {heroName}  (#show metrics — скрыть)";

        var rewardSeries = RewardSeriesForHero(_hero);
        float rewardEma = RewardEmaForHero(_hero);
        var entropySeries = TrainingPolicyStats.EntropySeries;

        // Слот 0 — reward, 1 — entropy, дальше задачи героя.
        DrawGraph(_slots[0], rewardSeries, new Color(0.22f, 0.48f, 0.95f, 1f),
            $"Reward EMA — {rewardEma:F2}", isPercent: false);
        DrawGraph(_slots[1], entropySeries, new Color(0.35f, 0.95f, 0.55f, 1f),
            $"Entropy — {TrainingPolicyStats.LastEntropy:F2}", isPercent: false);
        if (_slots[0]?.Root != null) _slots[0].Root.SetActive(true);
        if (_slots[1]?.Root != null) _slots[1].Root.SetActive(true);

        _sb.Clear();
        _sb.Append(heroName)
            .Append("  Reward EMA: ").Append(rewardEma.ToString("F2"))
            .Append(" · Entropy: ").Append(TrainingPolicyStats.LastEntropy.ToString("F2"))
            .Append(" · SR: ");

        for (int i = 0; i < MaxSlots - 2; i++)
        {
            int slotIndex = i + 2;
            var slot = _slots[slotIndex];
            if (slot == null)
                continue;

            if (i >= tasks.Length)
            {
                if (slot.Root != null)
                    slot.Root.SetActive(false);
                continue;
            }

            if (slot.Root != null)
                slot.Root.SetActive(true);

            var task = tasks[i];
            var series = TrainingTaskSuccessTracker.GetTaskRateSeriesCached(task);
            float rate = TrainingTaskSuccessTracker.GetTaskLastRateCached(task);
            int n = TrainingTaskSuccessTracker.GetTaskSampleCountCached(task);
            string label = TaskShortName(task);
            string title = $"{label} — {rate:P0} · n={n}";
            DrawGraph(slot, series, TaskColor(task), title, 0f, 1f, isPercent: true);

            if (i > 0)
                _sb.Append(" · ");
            _sb.Append(label).Append(' ').Append(Mathf.RoundToInt(rate * 100f)).Append("% (n=").Append(n).Append(')');
        }

        if (_valuesText != null)
            _valuesText.text = _sb.ToString();
    }

    static IReadOnlyList<float> RewardSeriesForHero(MetricsHero hero)
    {
        switch (hero)
        {
            case MetricsHero.Lily: return TrainingGraphOverlay.GetLilyRewardSeriesReadonly();
            case MetricsHero.George: return TrainingGraphOverlay.GetGeorgeRewardSeriesReadonly();
            default: return TrainingGraphOverlay.GetJackRewardSeriesReadonly();
        }
    }

    static float RewardEmaForHero(MetricsHero hero)
    {
        switch (hero)
        {
            case MetricsHero.Lily: return TrainingGraphOverlay.LilyEma;
            case MetricsHero.George: return TrainingGraphOverlay.GeorgeEma;
            default: return TrainingGraphOverlay.JackEma;
        }
    }

    static EnvTrainingTask[] TasksForHero(MetricsHero hero)
    {
        switch (hero)
        {
            case MetricsHero.Jack: return JackTasks;
            case MetricsHero.Lily: return LilyTasks;
            case MetricsHero.George: return GeorgeTasks;
            default: return System.Array.Empty<EnvTrainingTask>();
        }
    }

    static string HeroTitle(MetricsHero hero)
    {
        switch (hero)
        {
            case MetricsHero.Jack: return "Jack";
            case MetricsHero.Lily: return "Lily";
            case MetricsHero.George: return "George";
            default: return "";
        }
    }

    static string TaskShortName(EnvTrainingTask task)
    {
        switch (task)
        {
            case EnvTrainingTask.JackWood: return "Wood";
            case EnvTrainingTask.JackFood:
            case EnvTrainingTask.LilyFood:
            case EnvTrainingTask.GeorgeFood: return "Sheep";
            case EnvTrainingTask.JackWater:
            case EnvTrainingTask.LilyWater:
            case EnvTrainingTask.GeorgeWater: return "Water";
            case EnvTrainingTask.JackZombie: return "Zombie";
            case EnvTrainingTask.LilyHeat:
            case EnvTrainingTask.GeorgeHeat: return "Fire";
            case EnvTrainingTask.LilyFlower: return "Flower";
            default: return task.ToString();
        }
    }

    static Color TaskColor(EnvTrainingTask task)
    {
        switch (task)
        {
            case EnvTrainingTask.JackWood: return new Color(0.55f, 0.82f, 0.35f, 1f);
            case EnvTrainingTask.JackFood:
            case EnvTrainingTask.LilyFood:
            case EnvTrainingTask.GeorgeFood: return new Color(0.98f, 0.55f, 0.35f, 1f);
            case EnvTrainingTask.JackWater:
            case EnvTrainingTask.LilyWater:
            case EnvTrainingTask.GeorgeWater: return new Color(0.35f, 0.7f, 0.98f, 1f);
            case EnvTrainingTask.JackZombie: return new Color(0.72f, 0.45f, 0.95f, 1f);
            case EnvTrainingTask.LilyHeat:
            case EnvTrainingTask.GeorgeHeat: return new Color(0.95f, 0.45f, 0.55f, 1f);
            case EnvTrainingTask.LilyFlower: return new Color(0.95f, 0.85f, 0.35f, 1f);
            default: return Color.white;
        }
    }

    static void DrawGraph(GraphSlot slot, IReadOnlyList<float> values, Color lineColor, string title,
        float? fixedMin = null, float? fixedMax = null, bool isPercent = true)
    {
        if (slot == null)
            return;

        slot.IsPercent = isPercent;

        if (slot.TitleLabel != null)
        {
            slot.TitleLabel.text = title;
            slot.TitleLabel.raycastTarget = false;
        }

        // Подпись обновляем всегда; SetPixels — только если серия реально изменилась.
        int count = values?.Count ?? 0;
        float last = count > 0 ? values[count - 1] : 0f;
        int sig = count * 397 ^ (int)(last * 10000f) ^ (isPercent ? 1 : 0);
        if (sig == slot.LastDrawSig)
            return;
        slot.LastDrawSig = sig;

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

        if (slot.XLabels[0] != null) slot.XLabels[0].text = "1";
        if (slot.XLabels[1] != null) slot.XLabels[1].text = ((1 + count) / 2).ToString();
        if (slot.XLabels[2] != null) slot.XLabels[2].text = count.ToString();
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
