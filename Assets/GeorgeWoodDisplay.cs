using UnityEngine;
using TMPro;

public sealed class GeorgeWoodDisplay : MonoBehaviour
{
    [SerializeField] private AgentGoToHouseDiscrete george;
    [SerializeField] private TMP_SpriteAsset spriteAsset;

    TMP_Text _text;

    void Awake()
    {
        _text = GetComponent<TMP_Text>();
        if (_text != null)
            _text.richText = true;
        if (spriteAsset == null)
        {
            var jackHud = Object.FindFirstObjectByType<TreeDisplay>();
            var jackText = jackHud != null ? jackHud.GetComponent<TMP_Text>() : null;
            if (jackText != null)
                spriteAsset = jackText.spriteAsset;
        }

        GeorgeHudBootstrap.EnsureHudVisible(this);
        ApplyLayoutPosition();
    }

    internal void ApplyLayoutPosition()
    {
        var rt = GetComponent<RectTransform>();
        if (rt != null)
            rt.anchoredPosition = new Vector2(530f, -180f);
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

        _text.text = $"<sprite=0> {george.wood}";
    }
}
