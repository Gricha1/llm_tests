using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Всплывающие надписи над агентом (экранные координаты).
/// </summary>
public sealed class FloatingRewardPopup : MonoBehaviour
{
    [SerializeField] private float floatUpSpeed = 0.8f;
    [SerializeField] private float lifetime = 1.4f;
    [SerializeField] private float minIntervalBetweenPopups = 1.2f;
    [SerializeField] private float startHeight = 2.4f;
    [SerializeField] private int fontSize = 28;
    [SerializeField] private Color textColor = new Color(0.35f, 1f, 0.45f, 1f);

    static FloatingRewardPopup _instance;
    static readonly Dictionary<int, float> LastShowTimeByAgentId = new Dictionary<int, float>();
    Canvas _canvas;

    static readonly Color TaskColor = new Color(0.35f, 1f, 0.45f, 1f);
    static readonly Color KillColor = new Color(1f, 0.28f, 0.28f, 1f);
    static readonly Color FreezeColor = new Color(0.45f, 0.75f, 1f, 1f);
    static readonly Color HungerColor = new Color(1f, 0.72f, 0.25f, 1f);

    static FloatingRewardPopup Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject(nameof(FloatingRewardPopup));
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<FloatingRewardPopup>();
            }
            return _instance;
        }
    }

    void EnsureCanvas()
    {
        if (_canvas != null)
            return;

        var canvasGo = new GameObject("FloatingRewardCanvas");
        canvasGo.transform.SetParent(transform, false);

        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 1500;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
    }

    static string WithReward(string action, float rewardAmount)
    {
        if (rewardAmount > 0f)
            return $"{action} +{rewardAmount:0} награда";
        if (rewardAmount < 0f)
            return $"{action} {rewardAmount:0} награда";
        return action;
    }

    public static void ShowGotWood(Transform agent, float rewardAmount) =>
        ShowCustom(agent, WithReward("дobыл дерево", rewardAmount), TaskColor);

    public static void ShowGotFood(Transform agent, float rewardAmount) =>
        ShowCustom(agent, WithReward("дobыл еды", rewardAmount), TaskColor);

    public static void ShowGotWater(Transform agent, float rewardAmount) =>
        ShowCustom(agent, WithReward("набрал воды", rewardAmount), FreezeColor);

    public static void ShowWarmedUp(Transform agent, float rewardAmount) =>
        ShowCustom(agent, WithReward("согрелся", rewardAmount), TaskColor);

    public static void ShowFreezing(Transform agent, float penaltyAmount) =>
        ShowCustom(agent, WithReward("замерзает", penaltyAmount), FreezeColor);

    public static void ShowHungry(Transform agent, float penaltyAmount) =>
        ShowCustom(agent, WithReward("голоден", penaltyAmount), HungerColor);

    public static void ShowCollectedFlower(Transform agent, float rewardAmount) =>
        ShowCustom(agent, WithReward("собрала цветок", rewardAmount), TaskColor);

    public static void ShowKissedJack(Transform agent, float rewardAmount) =>
        ShowCustom(agent, WithReward("пoцеловала джека", rewardAmount), TaskColor);

    public static void ShowKissedGeorge(Transform agent, float rewardAmount) =>
        ShowCustom(agent, WithReward("пoцеловала геру", rewardAmount), TaskColor);

    public static void ShowZombieKill(Transform agent, float rewardAmount) =>
        ShowCustom(agent, WithReward("убил зомби", rewardAmount), KillColor);

    public static void ShowCustom(Transform agent, string message, Color? textColor = null)
    {
        if (agent == null || string.IsNullOrEmpty(message))
            return;

        if (!TrainingEnvSpace.IsPresentationTransform(agent))
            return;

        var inst = Instance;
        int agentId = agent.GetInstanceID();
        float now = Time.time;
        if (LastShowTimeByAgentId.TryGetValue(agentId, out float lastShowTime)
            && now - lastShowTime < inst.minIntervalBetweenPopups)
        {
            return;
        }

        LastShowTimeByAgentId[agentId] = now;
        inst.EnsureCanvas();
        inst.StartCoroutine(inst.PopupRoutine(agent, message, textColor ?? inst.textColor));
    }

    IEnumerator PopupRoutine(Transform agent, string message, Color color)
    {
        var textGo = new GameObject("RewardPopup");
        textGo.transform.SetParent(_canvas.transform, false);

        var textRt = textGo.AddComponent<RectTransform>();
        textRt.sizeDelta = new Vector2(480f, 52f);

        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.text = message;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontStyle = FontStyles.Bold;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        if (TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;

        float rise = 0f;
        float t = 0f;
        Color c = color;

        while (t < lifetime && agent != null)
        {
            t += Time.deltaTime;
            rise += floatUpSpeed * Time.deltaTime;

            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 worldPos = agent.position + Vector3.up * (startHeight + rise);
                Vector3 screenPos = cam.WorldToScreenPoint(worldPos);
                if (screenPos.z > 0f)
                {
                    textRt.position = screenPos;
                    textGo.SetActive(true);
                }
                else
                {
                    textGo.SetActive(false);
                }
            }

            float alpha = 1f - Mathf.Clamp01(t / lifetime);
            c.a = color.a * alpha;
            tmp.color = c;

            yield return null;
        }

        if (textGo != null)
            Destroy(textGo);
    }

    void OnDisable()
    {
        // Если хост выключили mid-popup — не оставляем сироты на Canvas.
        if (_canvas == null)
            return;
        for (int i = _canvas.transform.childCount - 1; i >= 0; i--)
        {
            var child = _canvas.transform.GetChild(i);
            if (child != null && child.name == "RewardPopup")
                Destroy(child.gameObject);
        }
    }
}
