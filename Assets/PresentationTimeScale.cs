using UnityEngine;

/// <summary>Presentation worker (OBS): отдельный time scale, train workers остаются на time-scale из mlagents.</summary>
public sealed class PresentationTimeScale : MonoBehaviour
{
    static PresentationTimeScale _instance;
    float _targetScale = 1f;
    bool _parsed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_instance != null)
            return;

        var go = new GameObject(nameof(PresentationTimeScale));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<PresentationTimeScale>();
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void EnsureParsed()
    {
        if (_parsed)
            return;

        _parsed = true;
        var env = System.Environment.GetEnvironmentVariable("FOREST_PRESENTATION_TIME_SCALE");
        if (float.TryParse(env, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float scale)
            && scale > 0f)
            _targetScale = scale;
    }

    void LateUpdate()
    {
        // Stream OBS (-forestStreamOnly) тоже нужен живой clock — не только SingleEnv worker0.
        if (!TrainingEnvSpace.IsPresentationWorkerProcess
            && !TrainingEnvSpace.IsLivePresentationForObs
            && !TrainingEnvSpace.IsStreamOnlyMode)
            return;

        EnsureParsed();
        float scale = _targetScale > 0.01f ? _targetScale : 1f;
        // Даже при DeathFreeze на стриме не оставляем timeScale=0.
        if (Time.timeScale < 0.01f || !DeathFreeze.IsFrozen)
            Time.timeScale = scale;
    }
}
