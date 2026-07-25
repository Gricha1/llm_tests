using UnityEngine;
using TMPro;
using Unity.MLAgents;

public class TreeDisplay : MonoBehaviour
{
    [SerializeField] private AgentGoToHouseDiscrete agent;
    [SerializeField] private TMP_SpriteAsset spriteAsset;
    private TMP_Text text;

    void Awake()
    {
        text = GetComponent<TMP_Text>();
        if (text != null)
        {
            text.richText = true;
        }
    }

    void Update()
    {
        if (text == null)
            return;

        agent = TrainingEnvSpace.FindPresentationJack();
        if (agent == null || !TrainingEnvSpace.ShouldShowHudForRole(EnvTrainingAgentRole.Jack))
        {
            text.enabled = false;
            return;
        }

        text.enabled = true;

        if (spriteAsset != null && text.spriteAsset != spriteAsset)
            text.spriteAsset = spriteAsset;

        text.text = $"<sprite=0> {agent.wood}";
    }
}