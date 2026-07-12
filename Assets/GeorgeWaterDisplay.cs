using UnityEngine;
using TMPro;

public sealed class GeorgeWaterDisplay : MonoBehaviour
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
            var jackHud = Object.FindObjectOfType<WaterDisplay>();
            if (jackHud != null)
                spriteAsset = jackHud.WaterSpriteAsset;
        }

        GeorgeHudBootstrap.EnsureHudVisible(this);
        ApplyLayoutPosition();
    }

    internal void BindWaterSpriteFromJack()
    {
        var jackHud = Object.FindObjectOfType<WaterDisplay>();
        if (jackHud != null)
            spriteAsset = jackHud.WaterSpriteAsset;
        if (_text != null && spriteAsset != null)
            _text.spriteAsset = spriteAsset;
    }

    internal void ApplyLayoutPosition()
    {
        var rt = GetComponent<RectTransform>();
        if (rt != null)
            rt.anchoredPosition = new Vector2(130f, -180f);
    }

    void LateUpdate()
    {
        if (_text == null)
            return;

        if (george == null || !george.gameObject.activeInHierarchy)
            george = TrainingEnvSpace.FindPresentationGeorge();

        if (george == null)
            return;

        if (spriteAsset != null && _text.spriteAsset != spriteAsset)
            _text.spriteAsset = spriteAsset;
        else if (spriteAsset == null)
        {
            var jackHud = Object.FindObjectOfType<WaterDisplay>();
            if (jackHud != null && jackHud.WaterSpriteAsset != null)
            {
                spriteAsset = jackHud.WaterSpriteAsset;
                _text.spriteAsset = spriteAsset;
            }
        }

        if (george.water <= 0)
        {
            float wave = (Mathf.Sin(Time.unscaledTime * pulseSpeed) + 1f) * 0.5f;
            int sizePct = Mathf.RoundToInt(Mathf.Lerp(pulseMinSizePercent, pulseMaxSizePercent, wave));
            _text.text = $"<size={sizePct}%><sprite=0></size> {george.water}";
        }
        else
        {
            _text.text = $"<sprite=0> {george.water}";
        }
    }
}
