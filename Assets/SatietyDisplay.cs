using UnityEngine;
using TMPro;

public class SatietyDisplay : MonoBehaviour
{
    [SerializeField] private AgentGoToHouseDiscrete agent;
    [SerializeField] private TMP_SpriteAsset spriteAsset;
    public TMP_SpriteAsset FoodSpriteAsset => spriteAsset;
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

        if (agent == null || !agent.gameObject.activeInHierarchy)
            agent = TrainingEnvSpace.FindPresentationJack();

        if (agent == null)
            return;

        if (spriteAsset != null && _text.spriteAsset != spriteAsset)
            _text.spriteAsset = spriteAsset;

        if (agent.satiety <= 0)
        {
            float wave = (Mathf.Sin(Time.unscaledTime * pulseSpeed) + 1f) * 0.5f;
            int sizePct = Mathf.RoundToInt(Mathf.Lerp(pulseMinSizePercent, pulseMaxSizePercent, wave));
            _text.text = $"<size={sizePct}%><sprite=0></size> {agent.satiety}";
        }
        else
        {
            _text.text = $"<sprite=0> {agent.satiety}";
        }
    }
}
