using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Центр-верх: стикеры Water/Wood/Food/Heat (как у Jack HUD).
/// Низ: крупная задача + тёмная полоска раунда (заливка слева направо).
/// Справа-низ: follower help.
/// </summary>
public sealed class StreamingSurvivalHud : MonoBehaviour
{
    TMP_Text _water;
    TMP_Text _wood;
    TMP_Text _food;
    TMP_Text _heat;
    TMP_Text _banner;
    TMP_Text _task;
    Image _timeFill;
    TMP_SpriteAsset _waterSprite;
    TMP_SpriteAsset _woodSprite;
    TMP_SpriteAsset _foodSprite;
    TMP_SpriteAsset _heatSprite;
    bool _spritesResolved;

    void Awake()
    {
        Build();
    }

    void Update()
    {
        ResolveSpritesOnce();
        var c = StreamingSurvivalController.Instance;
        if (c == null) return;
        SetStat(_water, _waterSprite, c.Water);
        SetStat(_wood, _woodSprite, c.Wood);
        SetStat(_food, _foodSprite, c.Food);
        SetStat(_heat, _heatSprite, c.Heat);
        if (_timeFill != null)
            _timeFill.fillAmount = Mathf.Clamp01(c.RoundProgress01);
        if (_task != null)
            _task.text = c.RoundTaskText;
        if (_banner != null)
        {
            string b = c.Banner;
            _banner.text = b;
            _banner.gameObject.SetActive(!string.IsNullOrEmpty(b));
        }
    }

    void ResolveSpritesOnce()
    {
        if (_spritesResolved) return;
        var waterHud = Object.FindFirstObjectByType<WaterDisplay>(FindObjectsInactive.Include);
        if (waterHud != null) _waterSprite = waterHud.WaterSpriteAsset;
        var foodHud = Object.FindFirstObjectByType<SatietyDisplay>(FindObjectsInactive.Include);
        if (foodHud != null) _foodSprite = foodHud.FoodSpriteAsset;
        var heatHud = Object.FindFirstObjectByType<HeatDisplay>(FindObjectsInactive.Include);
        if (heatHud != null) _heatSprite = heatHud.HeatSpriteAsset;
        var woodHud = Object.FindFirstObjectByType<TreeDisplay>(FindObjectsInactive.Include);
        if (woodHud != null)
        {
            _woodSprite = woodHud.WoodSpriteAsset;
            if (_woodSprite == null)
            {
                var t = woodHud.GetComponent<TMP_Text>();
                if (t != null) _woodSprite = t.spriteAsset;
            }
        }
        if (_woodSprite == null)
        {
            var allWood = Resources.FindObjectsOfTypeAll<TreeDisplay>();
            for (int i = 0; i < allWood.Length; i++)
            {
                var td = allWood[i];
                if (td == null) continue;
                if (td.WoodSpriteAsset != null)
                {
                    _woodSprite = td.WoodSpriteAsset;
                    break;
                }
                var t = td.GetComponent<TMP_Text>();
                if (t != null && t.spriteAsset != null)
                {
                    _woodSprite = t.spriteAsset;
                    break;
                }
            }
        }
        if (_woodSprite == null)
        {
            var all = Resources.FindObjectsOfTypeAll<TMP_SpriteAsset>();
            for (int i = 0; i < all.Length; i++)
            {
                var a = all[i];
                if (a == null) continue;
                string n = a.name ?? "";
                if (n.IndexOf("forest", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("9060", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("tree", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("wood", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _woodSprite = a;
                    break;
                }
            }
        }
        // ждём пока дерево найдётся (часто позже остальных HUD)
        if (_waterSprite != null && _foodSprite != null && _heatSprite != null && _woodSprite != null)
            _spritesResolved = true;
    }

    static void SetStat(TMP_Text t, TMP_SpriteAsset sprite, int v)
    {
        if (t == null) return;
        if (sprite != null && t.spriteAsset != sprite)
            t.spriteAsset = sprite;
        if (sprite != null)
            t.text = $"<sprite=0>  {v}";
        else
            t.text = $"{v}";
    }

    void Build()
    {
        var canvasGo = new GameObject("SS_Canvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 920;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasGo.AddComponent<GraphicRaycaster>();

        // center sticker row
        var row = new GameObject("ResourceStickers");
        row.transform.SetParent(canvasGo.transform, false);
        var rrt = row.AddComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 1f);
        rrt.anchorMax = new Vector2(0.5f, 1f);
        rrt.pivot = new Vector2(0.5f, 1f);
        rrt.anchoredPosition = new Vector2(0f, -18f);
        rrt.sizeDelta = new Vector2(920f, 72f);
        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 14f;
        hlg.childAlignment = TextAnchor.MiddleCenter;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = true;

        _water = MakeSticker(row.transform, "Water");
        _wood = MakeSticker(row.transform, "Wood");
        _food = MakeSticker(row.transform, "Food");
        _heat = MakeSticker(row.transform, "Heat");

        // banner
        var banGo = new GameObject("Banner");
        banGo.transform.SetParent(canvasGo.transform, false);
        var brt = banGo.AddComponent<RectTransform>();
        brt.anchorMin = new Vector2(0.5f, 0.78f);
        brt.anchorMax = new Vector2(0.5f, 0.78f);
        brt.sizeDelta = new Vector2(980f, 64f);
        _banner = banGo.AddComponent<TextMeshProUGUI>();
        _banner.fontSize = 34;
        _banner.fontStyle = FontStyles.Bold;
        _banner.alignment = TextAlignmentOptions.Center;
        _banner.color = new Color(1f, 0.92f, 0.45f);
        ApplyFont(_banner);

        // bottom task — крупно, по центру
        var taskGo = new GameObject("RoundTask");
        taskGo.transform.SetParent(canvasGo.transform, false);
        var trt = taskGo.AddComponent<RectTransform>();
        trt.anchorMin = new Vector2(0.5f, 0f);
        trt.anchorMax = new Vector2(0.5f, 0f);
        trt.pivot = new Vector2(0.5f, 0f);
        trt.anchoredPosition = new Vector2(0f, 54f);
        trt.sizeDelta = new Vector2(1400f, 48f);
        _task = taskGo.AddComponent<TextMeshProUGUI>();
        _task.fontSize = 36;
        _task.fontStyle = FontStyles.Bold;
        _task.alignment = TextAlignmentOptions.Center;
        _task.color = new Color(0.95f, 0.97f, 1f, 0.98f);
        ApplyFont(_task);

        // time bar: тёмный фон, заливка слева направо
        var barBg = new GameObject("TimeBarBg");
        barBg.transform.SetParent(canvasGo.transform, false);
        var bbrt = barBg.AddComponent<RectTransform>();
        bbrt.anchorMin = new Vector2(0.12f, 0f);
        bbrt.anchorMax = new Vector2(0.88f, 0f);
        bbrt.pivot = new Vector2(0.5f, 0f);
        bbrt.anchoredPosition = new Vector2(0f, 18f);
        bbrt.sizeDelta = new Vector2(0f, 26f);
        var bgImg = barBg.AddComponent<Image>();
        bgImg.color = new Color(0.05f, 0.06f, 0.08f, 0.95f);

        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(barBg.transform, false);
        var frt = fillGo.AddComponent<RectTransform>();
        frt.anchorMin = Vector2.zero;
        frt.anchorMax = Vector2.one;
        frt.offsetMin = new Vector2(3f, 3f);
        frt.offsetMax = new Vector2(-3f, -3f);
        _timeFill = fillGo.AddComponent<Image>();
        _timeFill.color = new Color(0.28f, 0.72f, 0.95f, 1f);
        _timeFill.type = Image.Type.Filled;
        _timeFill.fillMethod = Image.FillMethod.Horizontal;
        _timeFill.fillOrigin = (int)Image.OriginHorizontal.Left;
        _timeFill.fillAmount = 0f;

        // follower help — справа внизу
        var helpGo = new GameObject("Help");
        helpGo.transform.SetParent(canvasGo.transform, false);
        var hrt = helpGo.AddComponent<RectTransform>();
        hrt.anchorMin = new Vector2(1f, 0f);
        hrt.anchorMax = new Vector2(1f, 0f);
        hrt.pivot = new Vector2(1f, 0f);
        hrt.anchoredPosition = new Vector2(-24f, 56f);
        hrt.sizeDelta = new Vector2(420f, 86f);
        var hbg = helpGo.AddComponent<Image>();
        hbg.color = new Color(0.05f, 0.08f, 0.12f, 0.82f);
        var ht = new GameObject("T");
        ht.transform.SetParent(helpGo.transform, false);
        var htrt = ht.AddComponent<RectTransform>();
        htrt.anchorMin = Vector2.zero;
        htrt.anchorMax = Vector2.one;
        htrt.offsetMin = new Vector2(14f, 10f);
        htrt.offsetMax = new Vector2(-14f, -10f);
        var helpText = ht.AddComponent<TextMeshProUGUI>();
        helpText.fontSize = 18;
        helpText.alignment = TextAlignmentOptions.Right;
        helpText.color = new Color(0.95f, 0.97f, 1f);
        helpText.text =
            "<b>FOLLOWER CHARACTERS</b>\n" +
            "пиши в чат:  <color=#9ecbff>#join</color>  ·  <color=#9fe7b8>#do действие</color>  ·  <color=#ffb4b4>#exit</color>\n" +
            "<color=#ffd699>#stats</color> — твоя статистика";
        helpText.richText = true;
        ApplyFont(helpText);
    }

    static TMP_Text MakeSticker(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.07f, 0.1f, 0.14f, 0.88f);
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 64f;
        le.minWidth = 180f;

        var tGo = new GameObject("T");
        tGo.transform.SetParent(go.transform, false);
        var trt = tGo.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(10f, 6f);
        trt.offsetMax = new Vector2(-10f, -6f);
        var tmp = tGo.AddComponent<TextMeshProUGUI>();
        tmp.fontSize = 24;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.richText = true;
        ApplyFont(tmp);
        return tmp;
    }

    static void ApplyFont(TMP_Text t)
    {
        if (t != null && TMP_Settings.defaultFontAsset != null)
            t.font = TMP_Settings.defaultFontAsset;
    }
}
