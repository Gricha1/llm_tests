using UnityEngine;
using TMPro;

public sealed class GeorgeHeatDisplay : MonoBehaviour
{
    [SerializeField] private AgentGoToHouseDiscrete george;
    [SerializeField] private TMP_SpriteAsset spriteAsset;
    [SerializeField] private float pulseSpeed = 5f;
    [SerializeField] private float pulseMinSizePercent = 85f;
    [SerializeField] private float pulseMaxSizePercent = 135f;

    TMP_Text _text;

    void Awake()
    {
        _text = GetComponent<TMP_Text>();
        if (_text != null)
            _text.richText = true;

        if (spriteAsset == null)
        {
            var jackHud = Object.FindFirstObjectByType<HeatDisplay>();
            if (jackHud != null)
                spriteAsset = jackHud.HeatSpriteAsset;
        }

        GeorgeHudBootstrap.EnsureHudVisible(this);
        ApplyLayoutPosition();
    }

    internal void ApplyLayoutPosition()
    {
        var rt = GetComponent<RectTransform>();
        if (rt != null)
            rt.anchoredPosition = new Vector2(330f, -180f);
    }

    void LateUpdate()
    {
        if (_text == null)
            return;

        george = TrainingEnvSpace.FindPresentationGeorge();
        if (george == null || !TrainingEnvSpace.ShouldShowHudForRole(EnvTrainingAgentRole.George))
        {
            _text.enabled = false;
            return;
        }

        _text.enabled = true;

        if (spriteAsset != null && _text.spriteAsset != spriteAsset)
            _text.spriteAsset = spriteAsset;

        if (george.heat <= 0)
        {
            float wave = (Mathf.Sin(Time.unscaledTime * pulseSpeed) + 1f) * 0.5f;
            int sizePct = Mathf.RoundToInt(Mathf.Lerp(pulseMinSizePercent, pulseMaxSizePercent, wave));
            _text.text = $"<size={sizePct}%><sprite=0></size> {george.heat}";
        }
        else
        {
            _text.text = $"<sprite=0> {george.heat}";
        }
    }
}
