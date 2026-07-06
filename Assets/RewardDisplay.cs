using UnityEngine;
using TMPro;
using Unity.MLAgents;

public class RewardDisplay : MonoBehaviour
{
    public enum AgentKind
    {
        Auto,
        Jack,
        Lily
    }

    [SerializeField] private Agent agent;
    [SerializeField] private AgentKind agentKind = AgentKind.Auto;
    [SerializeField] private string label = "";
    TMP_Text _text;

    void Awake()
    {
        _text = GetComponent<TMP_Text>();
        ResolveLabel();
    }

    void ResolveLabel()
    {
        if (!string.IsNullOrEmpty(label))
            return;

        if (IsLilyDisplay())
            label = "Награда Лили";
        else
            label = "Награда Джека";
    }

    bool IsLilyDisplay()
    {
        if (agentKind == AgentKind.Lily)
            return true;
        if (agentKind == AgentKind.Jack)
            return false;

        if (agent != null)
        {
            if (agent is LilyScript)
                return true;
            if (agent.GetComponent<LilyScript>() != null)
                return true;
        }

        return gameObject.name.IndexOf("Lily", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void Update()
    {
        agent = IsLilyDisplay() ? FindActiveLily() : FindActiveJack();
        if (_text == null)
            return;

        if (agent == null)
        {
            _text.gameObject.SetActive(false);
            return;
        }

        if (!_text.gameObject.activeSelf)
            _text.gameObject.SetActive(true);

        _text.text = $"{label}: {agent.GetCumulativeReward():F2}";
    }

    static AgentGoToHouseDiscrete FindActiveJack()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root != null)
            return root.GetComponentInChildren<AgentGoToHouseDiscrete>(false);
        return FindObjectOfType<AgentGoToHouseDiscrete>();
    }

    static LilyScript FindActiveLily()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root != null)
            return root.GetComponentInChildren<LilyScript>(false);
        return FindObjectOfType<LilyScript>();
    }
}
