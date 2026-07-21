using UnityEngine;
using TMPro;

public class WaterDisplay : MonoBehaviour
{
    [SerializeField] private AgentGoToHouseDiscrete agent;
    [SerializeField] private TMP_SpriteAsset spriteAsset;
    public TMP_SpriteAsset WaterSpriteAsset => spriteAsset;
    [SerializeField] private float pulseSpeed = 5f;
    [SerializeField] private float pulseMinSizePercent = 85f;
    [SerializeField] private float pulseMaxSizePercent = 135f;

    TMP_Text _text;

    void Awake()
    {
        _text = GetComponent<TMP_Text>();
        if (_text != null)
            _text.richText = true;
    }

    void LateUpdate()
    {
        if (_text == null)
            return;

        agent = TrainingEnvSpace.FindPresentationJack();
        if (agent == null || !TrainingEnvSpace.ShouldShowHudForRole(EnvTrainingAgentRole.Jack))
        {
            _text.enabled = false;
            return;
        }

        _text.enabled = true;

        if (spriteAsset != null && _text.spriteAsset != spriteAsset)
            _text.spriteAsset = spriteAsset;

        if (agent.water <= 0)
        {
            float wave = (Mathf.Sin(Time.unscaledTime * pulseSpeed) + 1f) * 0.5f;
            int sizePct = Mathf.RoundToInt(Mathf.Lerp(pulseMinSizePercent, pulseMaxSizePercent, wave));
            _text.text = $"<size={sizePct}%><sprite=0></size> {agent.water}";
        }
        else
        {
            _text.text = $"<sprite=0> {agent.water}";
        }
    }
}
