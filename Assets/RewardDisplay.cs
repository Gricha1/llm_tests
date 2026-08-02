using System.Collections.Generic;
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
    bool _dedupChecked;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void BootstrapAll()
    {
        DeduplicateAll();
        var displays = Resources.FindObjectsOfTypeAll<RewardDisplay>();
        for (int i = 0; i < displays.Length; i++)
        {
            if (displays[i] == null || !ForestSceneBootstrap.IsLoadedSceneComponent(displays[i]))
                continue;

            displays[i].EnsureHudVisible();
            displays[i].ApplyLayout();
            displays[i].ApplyReadableStyle();
        }
    }

    /// <summary>Оставляет один живой RewardDisplay на роль — иначе цифры «R …» накладываются.</summary>
    public static int DeduplicateAll()
    {
        var best = new Dictionary<AgentKind, RewardDisplay>(4);
        var all = Resources.FindObjectsOfTypeAll<RewardDisplay>();
        int destroyed = 0;

        for (int i = 0; i < all.Length; i++)
        {
            var d = all[i];
            if (d == null || !ForestSceneBootstrap.IsLoadedSceneComponent(d))
                continue;

            var kind = d.ResolveKind();
            if (kind == AgentKind.Auto)
                kind = AgentKind.Jack;

            if (!best.TryGetValue(kind, out var keep) || keep == null)
            {
                best[kind] = d;
                continue;
            }

            // Предпочитаем уже видимый / с явным agentKind в сцене.
            bool preferNew = d.agentKind != AgentKind.Auto && keep.agentKind == AgentKind.Auto;
            preferNew |= d.gameObject.activeInHierarchy && !keep.gameObject.activeInHierarchy;
            var drop = preferNew ? keep : d;
            var stay = preferNew ? d : keep;
            best[kind] = stay;

            PresentationWorldSnapshotLogger.Note(
                "hud_reward_dup_destroy",
                $"kind={kind} drop={drop.gameObject.name} keep={stay.gameObject.name}");
            Object.Destroy(drop.gameObject);
            destroyed++;
        }

        return destroyed;
    }

    public static int CountLoaded(AgentKind kind)
    {
        int n = 0;
        var all = Resources.FindObjectsOfTypeAll<RewardDisplay>();
        for (int i = 0; i < all.Length; i++)
        {
            var d = all[i];
            if (d == null || !ForestSceneBootstrap.IsLoadedSceneComponent(d))
                continue;
            if (d.ResolveKind() == kind)
                n++;
        }
        return n;
    }

    public static string DescribeHudHealth()
    {
        int jack = 0, lily = 0, george = 0, other = 0;
        var parts = new List<string>(8);
        var all = Resources.FindObjectsOfTypeAll<RewardDisplay>();
        for (int i = 0; i < all.Length; i++)
        {
            var d = all[i];
            if (d == null || !ForestSceneBootstrap.IsLoadedSceneComponent(d))
                continue;

            var kind = d.ResolveKind();
            if (kind == AgentKind.Jack) jack++;
            else if (kind == AgentKind.Lily) lily++;
            else if (kind == AgentKind.George) george++;
            else other++;

            var rt = d.GetComponent<RectTransform>();
            var canvas = d.GetComponentInParent<Canvas>();
            string pos = rt != null
                ? $"({rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0})"
                : "?";
            parts.Add(
                $"{d.gameObject.name}/{kind} active={d.gameObject.activeInHierarchy} " +
                $"tmp={(d._text != null && d._text.enabled)} canvas={(canvas != null && canvas.enabled)} " +
                $"pos={pos} text=\"{(d._text != null ? d._text.text : "")}\"");
        }

        return $"counts jack={jack} lily={lily} george={george} other={other} | " +
               string.Join(" ; ", parts);
    }

    void Awake()
    {
        _text = GetComponent<TMP_Text>();
        ResolveLabel();
        EnsureHudVisible();
        ApplyReadableStyle();
        ApplyLayout();
    }

    void Start()
    {
        if (_dedupChecked)
            return;
        _dedupChecked = true;
        DeduplicateAll();
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

        _text.textWrappingMode = TextWrappingModes.NoWrap;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.horizontalAlignment = HorizontalAlignmentOptions.Left;
    }

    bool _styleApplied;

    void ApplyReadableStyle()
    {
        if (_text == null)
            return;

        // Отдельный материал — иначе outlineWidth портит shared SDF и «расплывает» все подписи.
        if (!_styleApplied && _text.font != null)
        {
            _text.fontMaterial = new Material(_text.font.material);
            _styleApplied = true;
        }

        _text.color = Color.white;
        _text.outlineWidth = 0.12f;
        _text.outlineColor = new Color(0f, 0f, 0f, 0.85f);
        _text.enableVertexGradient = false;
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

    AgentKind ResolveKind()
    {
        if (agentKind == AgentKind.George || IsGeorgeDisplay())
            return AgentKind.George;
        if (agentKind == AgentKind.Lily || IsLilyDisplay())
            return AgentKind.Lily;
        if (agentKind == AgentKind.Jack)
            return AgentKind.Jack;
        return IsLilyDisplay() ? AgentKind.Lily : AgentKind.Jack;
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
        EnvTrainingAgentRole role = IsGeorgeDisplay()
            ? EnvTrainingAgentRole.George
            : (IsLilyDisplay() ? EnvTrainingAgentRole.Lily : EnvTrainingAgentRole.Jack);

        if (role == EnvTrainingAgentRole.George)
            agent = FindActiveGeorge();
        else
            agent = IsLilyDisplay() ? FindActiveLily() : FindActiveJack();

        if (_text == null)
            return;

        // Нельзя SetActive(false) на своём GO — Update умрёт навсегда, а дубликаты останутся светиться.
        bool show = agent != null && TrainingEnvSpace.ShouldShowHudForRole(role);
        if (_text.enabled != show)
            _text.enabled = show;
        if (!show)
            return;

        _text.text = $"{label} {agent.GetCumulativeReward():F2}";
    }

    static AgentGoToHouseDiscrete FindActiveGeorge() =>
        TrainingEnvSpace.FindPresentationGeorge();

    static AgentGoToHouseDiscrete FindActiveJack() =>
        TrainingEnvSpace.FindPresentationJack();

    static LilyScript FindActiveLily() => TrainingEnvSpace.FindPresentationLily();
}
