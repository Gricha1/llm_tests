using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Краткое покраснение модели при получении урона.
/// </summary>
public sealed class AgentHitFlash : MonoBehaviour
{
    [SerializeField] private float flashDuration = 0.35f;
    [SerializeField] private Color hitTint = new Color(1f, 0.15f, 0.15f, 1f);
    [SerializeField] [Range(0.1f, 1f)] private float redBlend = 0.85f;

    struct SlotState
    {
        public Renderer renderer;
        public int materialIndex;
        public Color baseColor;
        public int colorPropId;
    }

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int MainColorId = Shader.PropertyToID("_MainColor");

    SlotState[] _slots;
    MaterialPropertyBlock _mpb;
    Coroutine _flashRoutine;

    public static AgentHitFlash GetOrCreate(GameObject root)
    {
        var flash = root.GetComponent<AgentHitFlash>();
        if (flash == null)
            flash = root.AddComponent<AgentHitFlash>();
        return flash;
    }

    void Awake()
    {
        _mpb = new MaterialPropertyBlock();
        CacheRenderers();
    }

    static int ResolveColorPropId(Material mat)
    {
        if (mat == null)
            return -1;
        if (mat.HasProperty(BaseColorId))
            return BaseColorId;
        if (mat.HasProperty(ColorId))
            return ColorId;
        if (mat.HasProperty(MainColorId))
            return MainColorId;
        return -1;
    }

    void CacheRenderers()
    {
        var list = new List<SlotState>();
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            if (r is SpriteRenderer)
                continue;

            var mats = r.sharedMaterials;
            if (mats == null)
                continue;

            for (int i = 0; i < mats.Length; i++)
            {
                var mat = mats[i];
                int propId = ResolveColorPropId(mat);
                if (propId < 0)
                    continue;

                list.Add(new SlotState
                {
                    renderer = r,
                    materialIndex = i,
                    baseColor = mat.GetColor(propId),
                    colorPropId = propId
                });
            }
        }

        _slots = list.ToArray();
    }

    public void Flash()
    {
        if (_slots == null || _slots.Length == 0)
            CacheRenderers();
        if (_slots == null || _slots.Length == 0)
            return;

        if (_flashRoutine != null)
            StopCoroutine(_flashRoutine);
        _flashRoutine = StartCoroutine(FlashRoutine());
    }

    IEnumerator FlashRoutine()
    {
        float t = 0f;
        ApplyBlend(redBlend);

        while (t < flashDuration)
        {
            t += Time.unscaledDeltaTime;
            float k = flashDuration > 1e-4f ? t / flashDuration : 1f;
            ApplyBlend(redBlend * (1f - k * k));
            yield return null;
        }

        ClearTint();
        _flashRoutine = null;
    }

    void ApplyBlend(float blend)
    {
        if (_slots == null)
            return;

        blend = Mathf.Clamp01(blend);
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            if (slot.renderer == null)
                continue;

            Color c = Color.Lerp(slot.baseColor, hitTint, blend);
            slot.renderer.GetPropertyBlock(_mpb, slot.materialIndex);
            _mpb.SetColor(slot.colorPropId, c);
            slot.renderer.SetPropertyBlock(_mpb, slot.materialIndex);
        }
    }

    void ClearTint()
    {
        if (_slots == null)
            return;

        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            if (slot.renderer == null)
                continue;
            slot.renderer.SetPropertyBlock(null, slot.materialIndex);
        }
    }
}
