using UnityEngine;

/// <summary>Снижает нагрузку presentation worker (OBS) при num-envs=12.</summary>
public sealed class PresentationPerformanceTuner : MonoBehaviour
{
    static PresentationPerformanceTuner _instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.IsPresentationWorkerProcess)
            return;
        if (_instance != null)
            return;

        var go = new GameObject(nameof(PresentationPerformanceTuner));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<PresentationPerformanceTuner>();
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

        QualitySettings.shadowDistance = Mathf.Min(QualitySettings.shadowDistance, 45f);
        QualitySettings.shadowResolution = ShadowResolution.Low;
        Application.targetFrameRate = 60;
    }
}
