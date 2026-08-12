using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>Таблица игроков справа.</summary>
public sealed class StreamingSurvivalPlayersTable : MonoBehaviour
{
    TMP_Text _body;

    void Awake()
    {
        Build();
    }

    public void Rebuild(List<(string user, string actionName)> rows)
    {
        if (_body == null) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<b>Players in game</b>");
        if (rows == null || rows.Count == 0)
            sb.AppendLine("(пусто)");
        else
        {
            for (int i = 0; i < rows.Count; i++)
                sb.AppendLine($"{rows[i].user} | {rows[i].actionName}");
        }
        _body.text = sb.ToString();
    }

    void Build()
    {
        var canvasGo = new GameObject("SS_PlayersCanvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 910;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        var panel = new GameObject("Panel");
        panel.transform.SetParent(canvasGo.transform, false);
        var rt = panel.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-16f, -16f);
        rt.sizeDelta = new Vector2(360f, 420f);
        var bg = panel.AddComponent<Image>();
        bg.color = new Color(0.04f, 0.06f, 0.09f, 0.88f);

        var textGo = new GameObject("Body");
        textGo.transform.SetParent(panel.transform, false);
        var trt = textGo.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(14f, 12f);
        trt.offsetMax = new Vector2(-14f, -12f);
        _body = textGo.AddComponent<TextMeshProUGUI>();
        _body.fontSize = 20;
        _body.color = Color.white;
        _body.alignment = TextAlignmentOptions.TopLeft;
        _body.richText = true;
        if (TMP_Settings.defaultFontAsset != null)
            _body.font = TMP_Settings.defaultFontAsset;
        Rebuild(null);
    }
}
