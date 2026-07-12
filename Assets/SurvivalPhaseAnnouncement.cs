using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Крупная надпись при смене этапа выживания (только presentation Env).
/// </summary>
public sealed class SurvivalPhaseAnnouncement : MonoBehaviour
{
    [SerializeField] private float displaySeconds = 4f;
    [SerializeField] private Color textColor = new Color(1f, 0.45f, 0.35f, 1f);
    [SerializeField] private int fontSize = 58;

    static SurvivalPhaseAnnouncement _instance;
    Canvas _canvas;
    TextMeshProUGUI _text;
    Coroutine _routine;

    static SurvivalPhaseAnnouncement Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject(nameof(SurvivalPhaseAnnouncement));
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<SurvivalPhaseAnnouncement>();
            }
            return _instance;
        }
    }

    public static void ShowPhase2(string message = "Этап 2. Зомби апокалипсис")
    {
        Instance.Show(message);
    }

    public static void ShowPhase3(string message = "Этап 3. Кошмар")
    {
        Instance.Show(message);
    }

    void Show(string message)
    {
        EnsureUi();
        if (_routine != null)
            StopCoroutine(_routine);
        _routine = StartCoroutine(ShowRoutine(message));
    }

    IEnumerator ShowRoutine(string message)
    {
        _text.text = message;
        _canvas.enabled = true;

        yield return new WaitForSecondsRealtime(Mathf.Max(0.5f, displaySeconds));

        if (_canvas != null)
            _canvas.enabled = false;
        _routine = null;
    }

    void EnsureUi()
    {
        if (_canvas != null)
            return;

        var canvasGo = new GameObject("PhaseAnnouncementCanvas");
        canvasGo.transform.SetParent(transform, false);

        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 1800;

        canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGo.AddComponent<GraphicRaycaster>();

        var panelRt = new GameObject("Panel").AddComponent<RectTransform>();
        panelRt.SetParent(canvasGo.transform, false);
        panelRt.anchorMin = Vector2.zero;
        panelRt.anchorMax = Vector2.one;
        panelRt.offsetMin = Vector2.zero;
        panelRt.offsetMax = Vector2.zero;

        var panel = panelRt.gameObject.AddComponent<Image>();
        panel.color = new Color(0f, 0f, 0f, 0.35f);

        var textRt = new GameObject("Message").AddComponent<RectTransform>();
        textRt.SetParent(panelRt, false);
        textRt.anchorMin = new Vector2(0.5f, 0.5f);
        textRt.anchorMax = new Vector2(0.5f, 0.5f);
        textRt.pivot = new Vector2(0.5f, 0.5f);
        textRt.sizeDelta = new Vector2(1200f, 140f);

        _text = textRt.gameObject.AddComponent<TextMeshProUGUI>();
        _text.alignment = TextAlignmentOptions.Center;
        _text.fontSize = fontSize;
        _text.fontStyle = FontStyles.Bold;
        _text.color = textColor;
        if (TMP_Settings.defaultFontAsset != null)
            _text.font = TMP_Settings.defaultFontAsset;

        _canvas.enabled = false;
    }
}
