using System.Collections;
using UnityEngine;
using Unity.MLAgents;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Presentation: при смерти героя — анимация + надпись.
/// Эпизод сбрасывается только когда погибли все: Джек, Лили и Гера.
/// Train: по-прежнему joint EndEpisode в Env.
/// </summary>
public sealed class AgentDeathOverlay : MonoBehaviour
{
    public static bool Enabled { get; set; } = true;

    [SerializeField] private float displaySeconds = 2f;
    [SerializeField] private float textDelaySeconds = 0.35f;
    [SerializeField] private Color panelColor = new Color(0f, 0f, 0f, 0.45f);
    [SerializeField] private Color textColor = new Color(1f, 0.35f, 0.35f, 1f);
    [SerializeField] private int fontSize = 36;

    const float FailsafeResetSeconds = 8f;

    static AgentDeathOverlay _instance;
    static bool _deathInProgress;
    static bool _awaitingFullWipeReset;

    Canvas _canvas;
    TextMeshProUGUI _text;
    Image _panel;
    Coroutine _routine;
    Coroutine _failsafeRoutine;
    Coroutine _toastRoutine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_instance != null)
            return;

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

    public static string GetDeathMessageFor(Agent agent)
    {
        if (agent is LilyScript)
            return "Лили погибла";
        if (agent is AgentGoToHouseDiscrete hero && TrainingEnvSpace.IsGeorgeAgent(hero))
            return "Гера погиб";
        return "Джек погиб";
    }

    public static void ShowAndEndEpisode(Agent agent, string message, float? delaySeconds = null)
    {
        if (agent == null)
            return;

        if (string.IsNullOrEmpty(message))
            message = GetDeathMessageFor(agent);

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

        var envRoot = TrainingEnvSpace.FindRoot(agent.transform);
        HeroDeathVisual.Play(agent);
        GameSfx.PlayHeroDied(source: agent.transform);

        if (!AreAllPresentationHeroesDead(envRoot))
        {
            // Один герой умер — мир продолжает идти, ждём остальных.
            Instance.ShowToast(message, delaySeconds ?? Instance.displaySeconds);
            return;
        }

        // Все трое мертвы — полный сброс эпизода.
        if (_deathInProgress && _awaitingFullWipeReset)
        {
            Instance.CancelActiveRoutines();
            ForceResetPresentationEnv(envRoot);
            JointEpisodeReset.EnsureAgentsRespawned(envRoot);
        }

        _deathInProgress = true;
        _awaitingFullWipeReset = true;

        BackgroundMusic.PauseMusic();
        GameSfx.StopFireLoop(agent.transform);
        DeathFreeze.FreezeForAgent(agent.transform);

        float delay = delaySeconds ?? Instance.displaySeconds;
        Instance.StartDeathSequence(agent, "Все погибли", delay, envRoot);
    }

    static bool AreAllPresentationHeroesDead(Transform envRoot)
    {
        int total = 0;
        int dead = 0;

        void Count(Agent a)
        {
            if (a == null || !a.isActiveAndEnabled || !a.gameObject.activeInHierarchy)
                return;
            if (envRoot != null && !TrainingEnvSpace.IsDescendantOf(a.transform, envRoot))
                return;
            if (TwitchEphemeralEffects.IsTwitchClone(a))
                return;

            total++;
            bool isDead = false;
            if (a is LilyScript lily)
                isDead = lily.IsInDeathState;
            else if (a is AgentGoToHouseDiscrete jg)
                isDead = jg.IsInDeathState;
            if (isDead)
                dead++;
        }

        Count(TrainingEnvSpace.FindPresentationPrimaryJack());
        Count(TrainingEnvSpace.FindPresentationLily());
        Count(TrainingEnvSpace.FindPresentationGeorge());

        // Fallback: если Find* вернул null, но герои есть в Env.
        if (total == 0 && envRoot != null)
        {
            foreach (var j in Object.FindObjectsByType<AgentGoToHouseDiscrete>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (j == null || TwitchEphemeralEffects.IsTwitchClone(j)) continue;
                if (!TrainingEnvSpace.IsDescendantOf(j.transform, envRoot)) continue;
                if (j.gameObject.name == "Jack" || j.gameObject.name == "George") continue;
                Count(j);
            }
            foreach (var lily in Object.FindObjectsByType<LilyScript>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (lily == null || TwitchEphemeralEffects.IsTwitchClone(lily)) continue;
                if (!TrainingEnvSpace.IsDescendantOf(lily.transform, envRoot)) continue;
                if (lily.gameObject.name == "Lily") continue;
                Count(lily);
            }
        }

        return total > 0 && dead >= total;
    }

    static void EndEpisodeInEnvOnly(Agent agent)
    {
        ForceResetPresentationEnv(TrainingEnvSpace.FindRoot(agent.transform));
    }

    static void ForceResetPresentationEnv(Transform envRoot)
    {
        DeathFreeze.UnfreezeWorld();
        JointEpisodeReset.EndAllAgentEpisodes(envRoot);
        JointEpisodeReset.EnsureAgentsRespawned(envRoot);
        BackgroundMusic.ResumeMusic();
        _awaitingFullWipeReset = false;
    }

    void CancelActiveRoutines()
    {
        if (_routine != null)
        {
            StopCoroutine(_routine);
            _routine = null;
        }

        if (_failsafeRoutine != null)
        {
            StopCoroutine(_failsafeRoutine);
            _failsafeRoutine = null;
        }

        if (_toastRoutine != null)
        {
            StopCoroutine(_toastRoutine);
            _toastRoutine = null;
        }
    }

    public static bool IsDeathSequenceRunning => _deathInProgress;

    public static void Hide()
    {
        if (_instance == null)
            return;

        _instance.CancelActiveRoutines();

        if (_instance._canvas != null)
            _instance._canvas.enabled = false;

        if (_instance._text != null)
            _instance._text.gameObject.SetActive(false);

        _deathInProgress = false;
        _awaitingFullWipeReset = false;
    }

    void ShowToast(string message, float delay)
    {
        if (_toastRoutine != null)
            StopCoroutine(_toastRoutine);
        _toastRoutine = StartCoroutine(ToastRoutine(message, delay));
    }

    IEnumerator ToastRoutine(string message, float delay)
    {
        if (_canvas == null)
            BuildUi();

        _text.text = message;
        _text.gameObject.SetActive(true);
        if (_panel != null)
            _panel.color = new Color(panelColor.r, panelColor.g, panelColor.b, 0.28f);
        _canvas.enabled = true;

        yield return new WaitForSecondsRealtime(Mathf.Max(0.4f, delay));

        if (!_awaitingFullWipeReset)
        {
            if (_canvas != null)
                _canvas.enabled = false;
            if (_text != null)
                _text.gameObject.SetActive(false);
        }

        _toastRoutine = null;
    }

    void StartDeathSequence(Agent agent, string message, float delay, Transform envRoot)
    {
        CancelActiveRoutines();
        _routine = StartCoroutine(DeathRoutine(agent, message, delay, envRoot));
        _failsafeRoutine = StartCoroutine(FailsafeRoutine(envRoot));
    }

    IEnumerator FailsafeRoutine(Transform envRoot)
    {
        yield return new WaitForSecondsRealtime(FailsafeResetSeconds);
        if (!_deathInProgress || !_awaitingFullWipeReset)
            yield break;

        Debug.LogWarning("[AgentDeathOverlay] failsafe: принудительный сброс после зависшей смерти");
        ForceResetPresentationEnv(envRoot);
        Hide();
        _failsafeRoutine = null;
    }

    IEnumerator DeathRoutine(Agent agent, string message, float delay, Transform envRoot)
    {
        bool resetDone = false;
        try
        {
            if (_canvas == null)
                BuildUi();

            _text.text = message;
            _text.gameObject.SetActive(false);
            if (_panel != null)
                _panel.color = panelColor;
            _canvas.enabled = true;

            float beforeText = Mathf.Max(0f, textDelaySeconds);
            if (beforeText > 0f)
                yield return new WaitForSecondsRealtime(beforeText);

            _text.gameObject.SetActive(true);

            ForceResetPresentationEnv(envRoot);
            resetDone = true;

            yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, delay));
        }
        finally
        {
            if (!resetDone)
                ForceResetPresentationEnv(envRoot);

            DeathFreeze.UnfreezeWorld();
            BackgroundMusic.ResumeMusic();

            if (_canvas != null)
                _canvas.enabled = false;

            if (_text != null)
                _text.gameObject.SetActive(false);

            _deathInProgress = false;
            _awaitingFullWipeReset = false;
            _routine = null;

            if (_failsafeRoutine != null)
            {
                StopCoroutine(_failsafeRoutine);
                _failsafeRoutine = null;
            }
        }
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
        _text.textWrappingMode = TextWrappingModes.NoWrap;
        if (TMP_Settings.defaultFontAsset != null)
            _text.font = TMP_Settings.defaultFontAsset;
    }
}
