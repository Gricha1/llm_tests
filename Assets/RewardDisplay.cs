using UnityEngine;
using TMPro;
using Unity.MLAgents;

public class RewardDisplay : MonoBehaviour
{
    public enum AgentKind
    {
        Auto,
        Jack,
        Lily,
        George
    }

    [SerializeField] private Agent agent;
    [SerializeField] private AgentKind agentKind = AgentKind.Auto;
    [SerializeField] private string label = "";
    TMP_Text _text;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void BootstrapAll()
    {
        var displays = Resources.FindObjectsOfTypeAll<RewardDisplay>();
        for (int i = 0; i < displays.Length; i++)
        {
            if (displays[i] != null)
            {
                displays[i].EnsureHudVisible();
                displays[i].ApplyLayout();
            }
        }
    }

    void Awake()
    {
        _text = GetComponent<TMP_Text>();
        ResolveLabel();
        EnsureHudVisible();
        ApplyReadableStyle();
        ApplyLayout();
    }

    void ApplyLayout()
    {
        var rt = GetComponent<RectTransform>();
        if (rt == null || _text == null)
            return;

        bool lily = IsLilyDisplay();
        bool george = IsGeorgeDisplay();
        rt.anchorMin = new Vector2(1f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.sizeDelta = new Vector2(280f, 50f);
        if (george)
            rt.anchoredPosition = new Vector2(-24f, -70f);
        else if (lily)
            rt.anchoredPosition = new Vector2(-24f, 0f);
        else
            rt.anchoredPosition = new Vector2(-24f, 70f);

        _text.enableWordWrapping = false;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.horizontalAlignment = HorizontalAlignmentOptions.Left;
    }

    void ApplyReadableStyle()
    {
        if (_text == null)
            return;

        _text.color = Color.white;
        _text.outlineWidth = 0.25f;
        _text.outlineColor = Color.black;
    }

    void EnsureHudVisible()
    {
        var canvas = GetComponentInParent<Canvas>();
        if (canvas != null)
        {
            var canvasRt = canvas.GetComponent<RectTransform>();
            if (canvasRt != null && canvasRt.localScale.sqrMagnitude < 1e-4f)
                canvasRt.localScale = Vector3.one;
        }

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);
    }

    public void ConfigureForGeorge()
    {
        agentKind = AgentKind.George;
        label = "R гера";
        ResolveLabel();
        ApplyLayout();
    }

    void ResolveLabel()
    {
        if (!string.IsNullOrEmpty(label))
            return;

        if (IsGeorgeDisplay())
            label = "R гера";
        else if (IsLilyDisplay())
            label = "R лили";
        else
            label = "R джек";
    }

    bool IsGeorgeDisplay()
    {
        if (agentKind == AgentKind.George)
            return true;
        if (agentKind == AgentKind.Jack || agentKind == AgentKind.Lily)
            return false;

        if (agent != null)
        {
            if (agent is GeorgeScript)
                return true;
            if (agent is AgentGoToHouseDiscrete jackAgent && TrainingEnvSpace.IsGeorgeAgent(jackAgent))
                return true;
            if (agent.GetComponent<GeorgeScript>() != null)
                return true;
        }

        return gameObject.name.IndexOf("George", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    bool IsLilyDisplay()
    {
        if (IsGeorgeDisplay())
            return false;
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
        if (IsGeorgeDisplay())
            agent = FindActiveGeorge();
        else
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

        _text.text = $"{label} {agent.GetCumulativeReward():F2}";
    }

    static AgentGoToHouseDiscrete FindActiveGeorge() =>
        TrainingEnvSpace.FindPresentationGeorge();

    static AgentGoToHouseDiscrete FindActiveJack() =>
        TrainingEnvSpace.FindPresentationJack();

    static LilyScript FindActiveLily() => TrainingEnvSpace.FindPresentationLily();
}
