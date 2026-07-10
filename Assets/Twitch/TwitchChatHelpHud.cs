using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>Подсказка справа снизу: команды Twitch и статус F.</summary>
public sealed class TwitchChatHelpHud : MonoBehaviour
{
    static TwitchChatHelpHud _instance;

    GameObject _panel;
    TMP_Text _body;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
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
        if (!show || _body == null)
            return;

        string chatState = TwitchChatReader.CommandsEnabled
            ? "<color=#7CFC90>вкл</color>"
            : "<color=#FF7A7A>выкл</color>";

        _body.text =
            "Для взаимодействия пишите в чат\n" +
            $"(F — обработка команд: {chatState})\n\n" +
            TwitchChatCommandCatalog.HelpText;
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
        rt.sizeDelta = new Vector2(400f, 340f);

        var bg = _panel.AddComponent<Image>();
        bg.color = new Color(0.04f, 0.06f, 0.08f, 0.78f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(_panel.transform, false);
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(12f, 10f);
        textRt.offsetMax = new Vector2(-12f, -10f);

        _body = textGo.AddComponent<TextMeshProUGUI>();
        _body.fontSize = 17;
        _body.alignment = TextAlignmentOptions.TopLeft;
        _body.richText = true;
        _body.enableWordWrapping = true;
        if (TMP_Settings.defaultFontAsset != null)
            _body.font = TMP_Settings.defaultFontAsset;
    }
}

static class TwitchChatCommandCatalog
{
    public const string HelpText =
        "#add_tree=N — деревья у Джека (1–10)\n" +
        "#add_sheep=N — овцы у Джека (1–20)\n" +
        "#up=N — прыжок, высота N ростов (1–5)\n" +
        "#forward=N — толчок вперёд (1–5)\n" +
        "#zombie=N — зомби рядом (1–10)\n" +
        "#clone_jack — один клон Jack (та же сеть)\n" +
        "#size=N — размер (1=обычный, 2=×2, 5=×5)\n" +
        "#speed_up=N — скорость бега (1–5, 3=×3)\n" +
        "#reset — начать эпизод заново\n" +
        "#show metrics — графики обучения (вкл/выкл)\n" +
        "\nPlay-тест: 1–0 — те же команды, Z — #zombie=1, P — Env копии";
}
