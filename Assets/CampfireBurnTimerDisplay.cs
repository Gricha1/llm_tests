using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Счётчик оставшегося времени горения костра (экранный текст над огнём).
/// Компонент вешается на Jack — fire VFX может быть выключен, а таймер всё равно рисуется.
/// </summary>
[DisallowMultipleComponent]
public sealed class CampfireBurnTimerDisplay : MonoBehaviour
{
    [SerializeField] private Vector3 worldOffset = new Vector3(0f, 1.35f, 0f);
    [SerializeField] private Color textColor = new Color(1f, 0.55f, 0.55f, 1f);
    [SerializeField] private int fontSize = 28;

    Transform _worldAnchor;
    AgentGoToHouseDiscrete _jack;
    Canvas _canvas;
    RectTransform _textRt;
    TextMeshProUGUI _text;

    public void Bind(Transform worldAnchor, AgentGoToHouseDiscrete owner)
    {
        _worldAnchor = worldAnchor;
        _jack = owner;
    }

    void EnsureUi()
    {
        if (_text != null)
            return;

        var canvasGo = new GameObject("CampfireBurnTimerCanvas");
        canvasGo.transform.SetParent(transform, false);

        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 1400;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        var textGo = new GameObject("CampfireBurnTimerText");
        textGo.transform.SetParent(canvasGo.transform, false);

        _textRt = textGo.AddComponent<RectTransform>();
        _textRt.sizeDelta = new Vector2(220f, 48f);

        _text = textGo.AddComponent<TextMeshProUGUI>();
        _text.alignment = TextAlignmentOptions.Center;
        _text.fontSize = fontSize;
        _text.color = textColor;
        _text.enableWordWrapping = false;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.fontStyle = FontStyles.Bold;
        if (TMP_Settings.defaultFontAsset != null)
            _text.font = TMP_Settings.defaultFontAsset;
    }

    void LateUpdate()
    {
        if (_jack == null || !_jack.gameObject.activeInHierarchy)
            _jack = GetComponent<AgentGoToHouseDiscrete>();

        if (_worldAnchor == null && _jack != null && _jack.houseTargetPublic != null)
            _worldAnchor = _jack.houseTargetPublic;

        float remaining = _jack != null ? _jack.CampfireBurnSecondsRemaining : 0f;
        bool show = _jack != null
            && _jack.ShouldShowCampfireTimer
            && _worldAnchor != null
            && TrainingEnvSpace.ShouldPlayFeedback(_jack.transform);

        if (!show)
        {
            Hide();
            return;
        }

        EnsureUi();
        if (_canvas != null)
            _canvas.gameObject.SetActive(true);
        _text.gameObject.SetActive(true);
        _text.color = textColor;
        _text.text = FormatRemaining(remaining);

        var cam = Camera.main;
        if (cam == null)
        {
            foreach (var c in Camera.allCameras)
            {
                if (c != null && c.enabled && c.gameObject.activeInHierarchy)
                {
                    cam = c;
                    break;
                }
            }
        }

        if (cam == null)
            return;

        Vector3 worldPos = _worldAnchor.position + worldOffset;
        Vector3 screenPos = cam.WorldToScreenPoint(worldPos);
        if (screenPos.z <= 0f)
        {
            _text.gameObject.SetActive(false);
            return;
        }

        _textRt.position = screenPos;
    }

    public void Hide()
    {
        if (_text != null)
            _text.gameObject.SetActive(false);
        if (_canvas != null)
            _canvas.gameObject.SetActive(false);
    }

    static string FormatRemaining(float seconds)
    {
        if (seconds >= 60f)
        {
            int total = Mathf.CeilToInt(seconds);
            int minutes = total / 60;
            int secs = total % 60;
            return $"{minutes}:{secs:00}";
        }

        if (seconds >= 10f)
            return $"{Mathf.CeilToInt(seconds)}с";

        return $"{seconds:0.0}с";
    }
}
