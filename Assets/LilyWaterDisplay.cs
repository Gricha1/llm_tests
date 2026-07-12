using UnityEngine;
using TMPro;

public class LilyWaterDisplay : MonoBehaviour
{
    [Tooltip("Если false — стикер/счётчик воды не отображается.")]
    [SerializeField] private bool show = true;
    [SerializeField] private LilyScript lily;
    [SerializeField] private TMP_SpriteAsset spriteAsset;
    [SerializeField] private float pulseSpeed = 5f;
    [SerializeField] private float pulseMinSizePercent = 85f;
    [SerializeField] private float pulseMaxSizePercent = 135f;

    TMP_Text _text;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void BootstrapAll()
    {
        var displays = Resources.FindObjectsOfTypeAll<LilyWaterDisplay>();
        for (int i = 0; i < displays.Length; i++)
        {
            if (displays[i] != null)
                displays[i].EnsureHudVisible();
        }
    }

    void Awake()
    {
        _text = GetComponent<TMP_Text>();
        if (_text != null)
            _text.richText = true;
        EnsureHudVisible();
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

    void LateUpdate()
    {
        if (_text == null)
            return;

        if (lily == null || !lily.isActiveAndEnabled)
            lily = TrainingEnvSpace.FindPresentationLily();

        if (lily == null)
            return;

        if (!show)
        {
            if (_text.enabled)
                _text.enabled = false;
            return;
        }

        if (!_text.enabled)
            _text.enabled = true;

        if (spriteAsset != null && _text.spriteAsset != spriteAsset)
            _text.spriteAsset = spriteAsset;

        if (lily.WaterCount <= 0)
        {
            float wave = (Mathf.Sin(Time.unscaledTime * pulseSpeed) + 1f) * 0.5f;
            int sizePct = Mathf.RoundToInt(Mathf.Lerp(pulseMinSizePercent, pulseMaxSizePercent, wave));
            _text.text = $"<size={sizePct}%><sprite=0></size> {lily.WaterCount}";
        }
        else
        {
            _text.text = $"<sprite=0> {lily.WaterCount}";
        }
    }
}
