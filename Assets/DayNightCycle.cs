using UnityEngine;

/// <summary>
/// Плавный цикл освещения: утро → день → вечер → ночь → утро.
/// Вешается на Directional Light (или подхватывается автоматически через RenderSettings.sun).
/// </summary>
[DisallowMultipleComponent]
public sealed class DayNightCycle : MonoBehaviour
{
    [SerializeField] private bool animate = true;
    [Tooltip("Длительность полного цикла суток в реальных секундах.")]
    [SerializeField] private float cycleDurationSeconds = 420f;
    [SerializeField] [Range(0f, 1f)] private float startTime01 = 0.28f;
    [SerializeField] private float sunYaw = -30f;

    [Header("Day")]
    [SerializeField] private Color dayLightColor = new Color(1f, 0.96f, 0.88f, 1f);
    [SerializeField] private float dayLightIntensity = 1.05f;
    [SerializeField] private float dayAmbientIntensity = 1f;
    [SerializeField] private Color dayAmbientSky = new Color(0.55f, 0.65f, 0.78f, 1f);

    [Header("Night")]
    [SerializeField] private Color nightLightColor = new Color(0.72f, 0.78f, 1f, 1f);
    [SerializeField] private float nightLightIntensity = 0.38f;
    [SerializeField] private float nightAmbientIntensity = 0.82f;
    [SerializeField] private Color nightAmbientSky = new Color(0.16f, 0.2f, 0.32f, 1f);

    [Header("Sun arc")]
    [SerializeField] private float daySunPitch = 58f;
    [SerializeField] private float nightSunPitch = -18f;

    Light _light;
    float _time01;
    bool _manualOverride;
    bool _manualIsNight;

    public float TimeOfDay01 => _time01;
    public bool IsNight => _manualOverride ? _manualIsNight : EvaluateDayBlend(_time01) < 0.35f;

    public static DayNightCycle FindInstance()
    {
        var sun = RenderSettings.sun;
        if (sun != null)
        {
            var onSun = sun.GetComponent<DayNightCycle>();
            if (onSun != null)
                return onSun;
        }

        return Object.FindObjectOfType<DayNightCycle>();
    }

    public static void ToggleDayNightGlobal()
    {
        var cycle = FindInstance();
        if (cycle != null)
            cycle.ToggleDayNight();
    }

    public void ToggleDayNight()
    {
        bool isCurrentlyDay = _manualOverride
            ? !_manualIsNight
            : EvaluateDayBlend(_time01) >= 0.35f;

        _manualOverride = true;
        _manualIsNight = isCurrentlyDay;
        _time01 = _manualIsNight ? 0f : 0.5f;
        ApplyLighting();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var sun = RenderSettings.sun;
        if (sun == null)
            return;

        if (sun.GetComponent<DayNightCycle>() != null)
            return;

        sun.gameObject.AddComponent<DayNightCycle>();
    }

    void Awake()
    {
        _light = GetComponent<Light>();
        if (_light == null)
            _light = RenderSettings.sun;

        _time01 = Mathf.Repeat(startTime01, 1f);
        ApplyLighting();
    }

    void Update()
    {
        if (_manualOverride || !animate || cycleDurationSeconds <= 0f)
            return;

        _time01 = Mathf.Repeat(_time01 + Time.deltaTime / cycleDurationSeconds, 1f);
        ApplyLighting();
    }

    public void SetTimeOfDay01(float value)
    {
        _manualOverride = false;
        _time01 = Mathf.Repeat(value, 1f);
        ApplyLighting();
    }

    void ApplyLighting()
    {
        float dayBlend = EvaluateDayBlend(_time01);

        if (_light != null)
        {
            _light.color = Color.Lerp(nightLightColor, dayLightColor, dayBlend);
            _light.intensity = Mathf.Lerp(nightLightIntensity, dayLightIntensity, dayBlend);
        }

        float pitch = Mathf.Lerp(nightSunPitch, daySunPitch, dayBlend);
        transform.rotation = Quaternion.Euler(pitch, sunYaw, 0f);

        RenderSettings.ambientIntensity = Mathf.Lerp(nightAmbientIntensity, dayAmbientIntensity, dayBlend);
        RenderSettings.ambientSkyColor = Color.Lerp(nightAmbientSky, dayAmbientSky, dayBlend);
    }

    static float EvaluateDayBlend(float time01)
    {
        // 0 / 1 = ночь, 0.25 = рассвет, 0.5 = полдень, 0.75 = закат.
        float wave = Mathf.Sin((time01 - 0.25f) * Mathf.PI * 2f);
        return Mathf.Clamp01((wave + 1f) * 0.5f);
    }
}
