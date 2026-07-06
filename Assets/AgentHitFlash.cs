using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Краткое покраснение модели при получении урона (≈0.5 с).
/// </summary>
public sealed class AgentHitFlash : MonoBehaviour
{
    [SerializeField] private float flashDuration = 0.5f;
    [SerializeField] private Color hitTint = new Color(1f, 0.22f, 0.22f, 1f);
    [SerializeField] [Range(0.1f, 1f)] private float redBlend = 0.65f;

    struct SlotState
    {
        public Renderer renderer;
        public int materialIndex;
        public Color baseColor;
        public int colorPropId;
    }

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

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
                if (mat == null)
                    continue;

                int propId = mat.HasProperty(BaseColorId)
                    ? BaseColorId
                    : mat.HasProperty(ColorId)
                        ? ColorId
                        : -1;
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
        float half = flashDuration * 0.5f;
        float t = 0f;

        while (t < flashDuration)
        {
            t += Time.deltaTime;
            float blend;
            if (t <= half)
                blend = (half > 0f ? t / half : 1f) * redBlend;
            else
                blend = (half > 0f ? 1f - (t - half) / half : 0f) * redBlend;

            ApplyBlend(blend);
            yield return null;
        }

        ClearTint();
        _flashRoutine = null;
    }

    void ApplyBlend(float blend)
    {
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
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            if (slot.renderer == null)
                continue;
            slot.renderer.SetPropertyBlock(null, slot.materialIndex);
        }
    }
}
