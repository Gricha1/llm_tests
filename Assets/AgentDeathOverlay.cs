using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.MLAgents;

/// <summary>
/// Показывает «Джек погиб» / «Лили погибла» по центру экрана, затем вызывает EndEpisode().
/// </summary>
public sealed class AgentDeathOverlay : MonoBehaviour
{
    public static bool Enabled { get; set; } = true;

    [SerializeField] private float displaySeconds = 2f;
    [SerializeField] private float textDelaySeconds = 1f;
    [SerializeField] private Color panelColor = new Color(0f, 0f, 0f, 0.65f);
    [SerializeField] private Color textColor = new Color(1f, 0.35f, 0.35f, 1f);
    [SerializeField] private int fontSize = 42;

    static AgentDeathOverlay _instance;
    static bool _deathInProgress;

    Canvas _canvas;
    TextMeshProUGUI _text;
    Image _panel;
    Coroutine _routine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_instance != null)
            return;

        var all = Resources.FindObjectsOfTypeAll<AgentDeathOverlay>();
        for (int i = 0; i < all.Length; i++)
        {
            var h = all[i];
            if (h == null) continue;
            var sc = h.gameObject.scene;
            if (sc.IsValid() && sc.isLoaded)
            {
                _instance = h;
                return;
            }
        }

        var go = new GameObject(nameof(AgentDeathOverlay));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<AgentDeathOverlay>();
    }

    static AgentDeathOverlay Instance
    {
        get
        {
            if (_instance == null)
                Bootstrap();
            return _instance;
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
        Hide();
    }

    public static void ShowAndEndEpisode(Agent agent, string message, float? delaySeconds = null)
    {
        if (agent == null)
            return;

        if (!Enabled)
        {
            agent.EndEpisode();
            return;
        }

        if (!TrainingEnvSpace.IsPresentationTransform(agent.transform))
        {
            EndEpisodeInEnvOnly(agent);
            return;
        }

        if (_deathInProgress)
            return;

        _deathInProgress = true;

        GameSfx.PlayHeroDied(source: agent.transform);
        BackgroundMusic.PauseMusic();
        GameSfx.StopFireLoop(agent.transform);
        DeathFreeze.FreezeForAgent(agent.transform);

        float delay = delaySeconds ?? Instance.displaySeconds;
        Instance.StartDeathSequence(agent, message, delay);
    }

    static void EndEpisodeInEnvOnly(Agent agent)
    {
        JointEpisodeReset.EndBothAgentEpisodes(TrainingEnvSpace.FindRoot(agent.transform));
    }

    public static void Hide()
    {
        if (_instance == null)
            return;

        if (_instance._routine != null)
        {
            _instance.StopCoroutine(_instance._routine);
            _instance._routine = null;
        }

        if (_instance._canvas != null)
            _instance._canvas.enabled = false;

        if (_instance._text != null)
            _instance._text.gameObject.SetActive(false);

        _deathInProgress = false;
    }

    void StartDeathSequence(Agent agent, string message, float delay)
    {
        if (_routine != null)
            StopCoroutine(_routine);

        _routine = StartCoroutine(DeathRoutine(agent, message, delay));
    }

    IEnumerator DeathRoutine(Agent agent, string message, float delay)
    {
        if (_canvas == null)
            BuildUi();

        _text.text = message;
        _text.gameObject.SetActive(false);
        _canvas.enabled = true;

        float beforeText = Mathf.Max(0f, textDelaySeconds);
        if (beforeText > 0f)
            yield return new WaitForSecondsRealtime(beforeText);

        _text.gameObject.SetActive(true);

        yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, delay));

        JointEpisodeReset.EndBothAgentEpisodes(TrainingEnvSpace.FindRoot(agent.transform));

        if (_canvas != null)
            _canvas.enabled = false;

        if (_text != null)
            _text.gameObject.SetActive(false);

        _deathInProgress = false;
        _routine = null;
    }

    void BuildUi()
    {
        if (_canvas != null)
            return;

        var canvasGo = new GameObject("DeathOverlayCanvas");
        canvasGo.transform.SetParent(transform, false);

        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 2000;

        canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGo.AddComponent<GraphicRaycaster>();

        var panelRt = new GameObject("Panel").AddComponent<RectTransform>();
        panelRt.SetParent(canvasGo.transform, false);
        panelRt.anchorMin = Vector2.zero;
        panelRt.anchorMax = Vector2.one;
        panelRt.offsetMin = Vector2.zero;
        panelRt.offsetMax = Vector2.zero;

        _panel = panelRt.gameObject.AddComponent<Image>();
        _panel.color = panelColor;

        var textRt = new GameObject("Message").AddComponent<RectTransform>();
        textRt.SetParent(panelRt, false);
        textRt.anchorMin = new Vector2(0.5f, 0.5f);
        textRt.anchorMax = new Vector2(0.5f, 0.5f);
        textRt.pivot = new Vector2(0.5f, 0.5f);
        textRt.sizeDelta = new Vector2(900f, 120f);

        _text = textRt.gameObject.AddComponent<TextMeshProUGUI>();
        _text.alignment = TextAlignmentOptions.Center;
        _text.fontSize = fontSize;
        _text.fontStyle = FontStyles.Bold;
        _text.color = textColor;
        _text.enableWordWrapping = false;
        if (TMP_Settings.defaultFontAsset != null)
            _text.font = TMP_Settings.defaultFontAsset;
    }
}
