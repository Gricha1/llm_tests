using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.MLAgents;

/// <summary>
/// График награды по эпизодам (Джек / Лили) поверх Game view.
/// При обучении с несколькими Env — только training-копии (не presentation Full).
/// G — показать/скрыть.
/// </summary>
public sealed class TrainingGraphOverlay : MonoBehaviour
{
    const int MaxPoints = 160;
    const int GraphWidth = 360;
    const int GraphHeight = 150;
    // Сглаживание реварда по нескольким эпизодам: чтобы динамика читалась, а не «пила».
    // Чем меньше alpha — тем более плавный график.
    const float EmaAlpha = 0.03f;
    // Сколько завершённых эпизодов (по всем параллельным env) усредняем в одну точку графика.
    const int RewardAggregateEpisodes = 8;

    static readonly Color JackColor = new Color(0.22f, 0.48f, 0.95f, 1f);
    static readonly Color LilyColor = new Color(0.98f, 0.38f, 0.62f, 1f);
    static readonly Color GridColor = new Color(1f, 1f, 1f, 0.08f);
    static readonly Color BgColor = new Color(0.04f, 0.06f, 0.08f, 0.82f);

    static TrainingGraphOverlay _instance;

    sealed class AgentEpisodeTracker
    {
        public Agent Agent;
        public int LastCompletedEpisodes;
        public float LastCumulative;
    }

    [SerializeField] private bool showOnlyWhenTraining;
    [SerializeField] private bool visible = true;
    [SerializeField] private KeyCode toggleKey = KeyCode.G;

    readonly List<AgentEpisodeTracker> _jackTrackers = new List<AgentEpisodeTracker>();
    readonly List<AgentEpisodeTracker> _lilyTrackers = new List<AgentEpisodeTracker>();
    readonly List<float> _jackRewards = new List<float>();
    readonly List<float> _lilyRewards = new List<float>();

    float _jackEma;
    float _lilyEma;
    float _jackLastRaw;
    float _lilyLastRaw;
    float _jackAggSum;
    int _jackAggCount;
    float _lilyAggSum;
    int _lilyAggCount;
    bool _usesTrainingEnvs;
    bool _needsRedraw = true;

    GameObject _panel;
    RawImage _graphImage;
    Texture2D _tex;
    TMP_Text _legend;
    TMP_Text _title;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_instance != null)
            return;
        var go = new GameObject(nameof(TrainingGraphOverlay));
        _instance = go.AddComponent<TrainingGraphOverlay>();
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
    }

    void Update()
    {
        if (toggleKey != KeyCode.None && Input.GetKeyDown(toggleKey))
            visible = !visible;

        // Важно: трекинг награды идёт ВСЕГДА, даже когда UI скрыт,
        // иначе #show metrics не видит историю «за всё время обучения».
        RefreshTrackers();
        TrackAll(_jackTrackers, _jackRewards, ref _jackEma, ref _jackLastRaw,
            ref _jackAggSum, ref _jackAggCount, notifyPolicyStats: true);
        TrackAll(_lilyTrackers, _lilyRewards, ref _lilyEma, ref _lilyLastRaw,
            ref _lilyAggSum, ref _lilyAggCount, notifyPolicyStats: false);

        bool show = visible && ShouldShow();
        if (_panel != null)
            _panel.SetActive(show);

        if (!show)
            return;

        if (_needsRedraw)
        {
            RedrawGraph();
            _needsRedraw = false;
        }
    }

    bool ShouldShow()
    {
        if (!showOnlyWhenTraining)
            return true;
        return Academy.IsInitialized && Academy.Instance.IsCommunicatorOn;
    }

    void RefreshTrackers()
    {
        bool training = Academy.IsInitialized && Academy.Instance.IsCommunicatorOn;
        _usesTrainingEnvs = training && TrainingEnvSpace.HasMultipleTrainingEnvs();

        SyncTrackers(CollectJackAgents(training), _jackTrackers);
        SyncTrackers(CollectLilyAgents(training), _lilyTrackers);

        if (_title != null)
        {
            _title.text = _usesTrainingEnvs
                ? "График обучения — training env (G — скрыть)"
                : "График обучения (G — скрыть)";
        }
    }

    static List<Agent> CollectJackAgents(bool training)
    {
        var agents = new List<Agent>();

        if (training && TrainingEnvSpace.HasMultipleTrainingEnvs())
        {
            foreach (var root in TrainingEnvSpace.GetAllEnvRoots())
            {
                if (TrainingEnvSpace.IsPresentationEnv(root))
                    continue;

                var jack = root.GetComponentInChildren<AgentGoToHouseDiscrete>(false);
                if (jack != null)
                    agents.Add(jack);
            }
        }
        else
        {
            var jack = FindPresentationJack();
            if (jack != null)
                agents.Add(jack);
        }

        return agents;
    }

    static List<Agent> CollectLilyAgents(bool training)
    {
        var agents = new List<Agent>();

        if (training && TrainingEnvSpace.HasMultipleTrainingEnvs())
        {
            foreach (var root in TrainingEnvSpace.GetAllEnvRoots())
            {
                if (TrainingEnvSpace.IsPresentationEnv(root))
                    continue;

                var lily = root.GetComponentInChildren<LilyScript>(false);
                if (lily != null)
                    agents.Add(lily);
            }
        }
        else
        {
            var lily = FindPresentationLily();
            if (lily != null)
                agents.Add(lily);
        }

        return agents;
    }

    static void SyncTrackers(List<Agent> agents, List<AgentEpisodeTracker> trackers)
    {
        for (int i = trackers.Count - 1; i >= 0; i--)
        {
            if (trackers[i].Agent == null)
                trackers.RemoveAt(i);
        }

        for (int i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            if (agent == null)
                continue;

            bool known = false;
            for (int j = 0; j < trackers.Count; j++)
            {
                if (trackers[j].Agent != agent)
                    continue;

                known = true;
                break;
            }

            if (known)
                continue;

            trackers.Add(new AgentEpisodeTracker
            {
                Agent = agent,
                LastCompletedEpisodes = agent.CompletedEpisodes,
                LastCumulative = agent.GetCumulativeReward()
            });
        }
    }

    void TrackAll(
        List<AgentEpisodeTracker> trackers,
        List<float> history,
        ref float ema,
        ref float lastRaw,
        ref float aggSum,
        ref int aggCount,
        bool notifyPolicyStats)
    {
        for (int i = 0; i < trackers.Count; i++)
        {
            if (!TrackOne(trackers[i], out float episodeReward))
                continue;

            lastRaw = episodeReward;
            aggSum += episodeReward;
            aggCount++;

            // Обновляем график реже: одна точка на несколько эпизодов.
            if (aggCount < RewardAggregateEpisodes)
                continue;

            float aggregated = aggSum / Mathf.Max(1, aggCount);
            aggSum = 0f;
            aggCount = 0;

            ema = history.Count == 0
                ? aggregated
                : EmaAlpha * aggregated + (1f - EmaAlpha) * ema;

            history.Add(ema);
            if (history.Count > MaxPoints)
                history.RemoveAt(0);

            _needsRedraw = true;
            if (notifyPolicyStats)
                TrainingPolicyStats.NotifyEpisodeRewardEma(ema);
        }
    }

    static bool TrackOne(AgentEpisodeTracker tracker, out float episodeReward)
    {
        episodeReward = 0f;
        var agent = tracker.Agent;
        if (agent == null)
            return false;

        float current = agent.GetCumulativeReward();
        int ep = agent.CompletedEpisodes;
        bool changed = false;

        if (ep > tracker.LastCompletedEpisodes)
        {
            tracker.LastCompletedEpisodes = ep;
            episodeReward = tracker.LastCumulative;
            changed = true;
        }

        tracker.LastCumulative = current;
        return changed;
    }

    public static float JackEma => _instance != null ? _instance._jackEma : 0f;

    public static List<float> GetJackRewardSeriesCopy()
    {
        if (_instance == null)
            return new List<float>();
        return new List<float>(_instance._jackRewards);
    }

    public static float GetPresentationJackCumulativeReward()
    {
        var jack = FindPresentationJack();
        return jack != null ? jack.GetCumulativeReward() : 0f;
    }

    static AgentGoToHouseDiscrete FindPresentationJack()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root != null)
            return root.GetComponentInChildren<AgentGoToHouseDiscrete>(false);
        return Object.FindObjectOfType<AgentGoToHouseDiscrete>();
    }

    static LilyScript FindPresentationLily()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root != null)
            return root.GetComponentInChildren<LilyScript>(false);
        return Object.FindObjectOfType<LilyScript>();
    }

    void BuildUi()
    {
        var canvasGo = new GameObject("TrainingGraphCanvas");
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 900;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        canvasGo.AddComponent<GraphicRaycaster>();

        _panel = new GameObject("Panel");
        _panel.transform.SetParent(canvasGo.transform, false);

        var panelRt = _panel.AddComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0f, 0f);
        panelRt.anchorMax = new Vector2(0f, 0f);
        panelRt.pivot = new Vector2(0f, 0f);
        panelRt.anchoredPosition = new Vector2(16f, 16f);
        panelRt.sizeDelta = new Vector2(GraphWidth + 24f, GraphHeight + 56f);

        var panelBg = _panel.AddComponent<Image>();
        panelBg.color = BgColor;

        var titleGo = new GameObject("Title");
        titleGo.transform.SetParent(_panel.transform, false);
        var titleRt = titleGo.AddComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0f, 1f);
        titleRt.anchorMax = new Vector2(1f, 1f);
        titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.anchoredPosition = new Vector2(0f, -6f);
        titleRt.sizeDelta = new Vector2(-16f, 22f);

        _title = titleGo.AddComponent<TextMeshProUGUI>();
        _title.text = "График обучения (G — скрыть)";
        _title.fontSize = 16;
        _title.alignment = TextAlignmentOptions.Left;
        if (TMP_Settings.defaultFontAsset != null)
            _title.font = TMP_Settings.defaultFontAsset;

        var graphGo = new GameObject("Graph");
        graphGo.transform.SetParent(_panel.transform, false);
        var graphRt = graphGo.AddComponent<RectTransform>();
        graphRt.anchorMin = new Vector2(0f, 0f);
        graphRt.anchorMax = new Vector2(1f, 1f);
        graphRt.offsetMin = new Vector2(10f, 28f);
        graphRt.offsetMax = new Vector2(-10f, -28f);

        _graphImage = graphGo.AddComponent<RawImage>();
        _tex = new Texture2D(GraphWidth, GraphHeight, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _graphImage.texture = _tex;

        var legendGo = new GameObject("Legend");
        legendGo.transform.SetParent(_panel.transform, false);
        var legendRt = legendGo.AddComponent<RectTransform>();
        legendRt.anchorMin = new Vector2(0f, 0f);
        legendRt.anchorMax = new Vector2(1f, 0f);
        legendRt.pivot = new Vector2(0.5f, 0f);
        legendRt.anchoredPosition = new Vector2(0f, 6f);
        legendRt.sizeDelta = new Vector2(-16f, 18f);

        _legend = legendGo.AddComponent<TextMeshProUGUI>();
        _legend.fontSize = 13;
        _legend.alignment = TextAlignmentOptions.Left;
        if (TMP_Settings.defaultFontAsset != null)
            _legend.font = TMP_Settings.defaultFontAsset;
    }

    void RedrawGraph()
    {
        if (_tex == null)
            return;

        var pixels = new Color[GraphWidth * GraphHeight];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color(0.08f, 0.1f, 0.12f, 1f);

        float yMin = 0f;
        float yMax = 1f;
        bool showLily = _lilyTrackers.Count > 0;
        ComputeRange(_jackRewards, showLily ? _lilyRewards : null, ref yMin, ref yMax);

        for (int i = 0; i <= 4; i++)
        {
            int gy = Mathf.RoundToInt(i / 4f * (GraphHeight - 1));
            DrawHLine(pixels, GraphWidth, GraphHeight, gy, GridColor);
        }

        DrawSeries(pixels, GraphWidth, GraphHeight, _jackRewards, yMin, yMax, JackColor);
        if (showLily)
            DrawSeries(pixels, GraphWidth, GraphHeight, _lilyRewards, yMin, yMax, LilyColor);

        _tex.SetPixels(pixels);
        _tex.Apply(false);

        float jackDisplay = _jackRewards.Count > 0 ? _jackRewards[_jackRewards.Count - 1] : 0f;
        if (_legend != null)
        {
            string src = _usesTrainingEnvs ? "training env" : "presentation";
            if (showLily)
            {
                float lilyDisplay = _lilyRewards.Count > 0 ? _lilyRewards[_lilyRewards.Count - 1] : 0f;
                _legend.text =
                    $"<color=#{ColorToHex(JackColor)}>●</color> Джек EMA: {jackDisplay:F1} (посл. {_jackLastRaw:F1})   " +
                    $"<color=#{ColorToHex(LilyColor)}>●</color> Лили EMA: {lilyDisplay:F1} (посл. {_lilyLastRaw:F1})   " +
                    $"{src} · эп. {_jackRewards.Count}";
            }
            else
            {
                _legend.text =
                    $"<color=#{ColorToHex(JackColor)}>●</color> Джек EMA: {jackDisplay:F1} (посл. {_jackLastRaw:F1})   " +
                    $"{src} · эп. {_jackRewards.Count}";
            }
        }
    }

    static void ComputeRange(List<float> a, List<float> b, ref float yMin, ref float yMax)
    {
        yMin = float.PositiveInfinity;
        yMax = float.NegativeInfinity;
        AccumulateRange(a, ref yMin, ref yMax);
        if (b != null)
            AccumulateRange(b, ref yMin, ref yMax);

        if (float.IsInfinity(yMin))
        {
            yMin = -1f;
            yMax = 1f;
            return;
        }

        if (Mathf.Approximately(yMin, yMax))
        {
            yMin -= 1f;
            yMax += 1f;
        }
        else
        {
            float pad = (yMax - yMin) * 0.1f;
            yMin -= pad;
            yMax += pad;
        }
    }

    static void AccumulateRange(List<float> values, ref float yMin, ref float yMax)
    {
        for (int i = 0; i < values.Count; i++)
        {
            yMin = Mathf.Min(yMin, values[i]);
            yMax = Mathf.Max(yMax, values[i]);
        }
    }

    static void DrawSeries(Color[] pixels, int w, int h, List<float> values, float yMin, float yMax, Color color)
    {
        if (values.Count < 2)
            return;

        int count = values.Count;
        for (int i = 1; i < count; i++)
        {
            float x0 = (i - 1) / (float)(count - 1) * (w - 1);
            float x1 = i / (float)(count - 1) * (w - 1);
            float y0 = ValueToY(values[i - 1], yMin, yMax, h);
            float y1 = ValueToY(values[i], yMin, yMax, h);
            DrawLine(pixels, w, h, Mathf.RoundToInt(x0), Mathf.RoundToInt(y0), Mathf.RoundToInt(x1), Mathf.RoundToInt(y1), color);
        }
    }

    static float ValueToY(float value, float yMin, float yMax, int h)
    {
        float t = (value - yMin) / (yMax - yMin);
        t = Mathf.Clamp01(t);
        return (1f - t) * (h - 1);
    }

    static void DrawHLine(Color[] pixels, int w, int h, int y, Color color)
    {
        y = Mathf.Clamp(y, 0, h - 1);
        for (int x = 0; x < w; x++)
            pixels[y * w + x] = color;
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

    static string ColorToHex(Color c)
    {
        Color32 c32 = c;
        return $"{c32.r:x2}{c32.g:x2}{c32.b:x2}";
    }
}
