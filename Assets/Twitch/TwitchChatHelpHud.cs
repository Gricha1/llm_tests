using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>Подсказка справа снизу: команды Twitch для чата.</summary>
public sealed class TwitchChatHelpHud : MonoBehaviour
{
    static TwitchChatHelpHud _instance;

    GameObject _panel;
    TMP_Text _title;
    TMP_Text _body;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.ShouldRunPresentationOnlyServices())
            return;
        // в Streaming Survival свой HUD — старый help не нужен
        if (TrainingEnvSpace.IsStreamingSurvivalMode)
            return;

        if (_instance != null)
            return;

        var go = new GameObject(nameof(TwitchChatHelpHud));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<TwitchChatHelpHud>();
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
        if (_panel == null)
            return;

        bool show = TrainingEnvSpace.PresentationRoot != null;
        _panel.SetActive(show);
    }

    void BuildUi()
    {
        var canvasGo = new GameObject("TwitchHelpCanvas");
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 850;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        _panel = new GameObject("Panel");
        _panel.transform.SetParent(canvasGo.transform, false);

        var rt = _panel.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-16f, 16f);
        rt.sizeDelta = new Vector2(460f, 248f);

        var bg = _panel.AddComponent<Image>();
        bg.color = new Color(0.03f, 0.05f, 0.07f, 0.88f);

        var titleGo = new GameObject("Title");
        titleGo.transform.SetParent(_panel.transform, false);
        var titleRt = titleGo.AddComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0f, 1f);
        titleRt.anchorMax = new Vector2(1f, 1f);
        titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.anchoredPosition = new Vector2(0f, -10f);
        titleRt.sizeDelta = new Vector2(-24f, 40f);

        _title = titleGo.AddComponent<TextMeshProUGUI>();
        _title.text = "FOLLOWER CHARACTERS";
        _title.fontSize = 22;
        _title.fontStyle = FontStyles.Bold;
        _title.alignment = TextAlignmentOptions.Center;
        _title.color = new Color(1f, 0.92f, 0.55f, 1f);
        if (TMP_Settings.defaultFontAsset != null)
            _title.font = TMP_Settings.defaultFontAsset;

        var textGo = new GameObject("Body");
        textGo.transform.SetParent(_panel.transform, false);
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(16f, 12f);
        textRt.offsetMax = new Vector2(-16f, -56f);

        _body = textGo.AddComponent<TextMeshProUGUI>();
        _body.text = TwitchChatCommandCatalog.HelpText;
        _body.fontSize = 22;
        _body.alignment = TextAlignmentOptions.TopLeft;
        _body.richText = true;
        _body.textWrappingMode = TextWrappingModes.Normal;
        _body.lineSpacing = 8f;
        _body.color = new Color(0.95f, 0.97f, 1f, 1f);
        if (TMP_Settings.defaultFontAsset != null)
            _body.font = TMP_Settings.defaultFontAsset;
    }
}

static class TwitchChatCommandCatalog
{
    /// <summary>
    /// Только чат-команды (без Play-тест / клавиш 1–0).
    /// Примеры с числом, чтобы зритель сразу копировал формат.
    /// </summary>
    public const string HelpText =
        "<b>#join</b> — войти в игру\n" +
        "<b>#do</b> добывай воду   ·   <b>#do</b> руби дерево\n" +
        "<b>#do</b> убивай овечек   ·   <b>#do</b> бей зомби\n" +
        "<b>#skins</b> — показывает доступные скины\n" +
        "<b>#stats</b> — статистика   ·   <b>#exit</b> — выйти";
}
