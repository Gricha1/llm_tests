using UnityEngine;

/// <summary>Headless train workers: держит AudioListener и все AudioSource выключенными.</summary>
public sealed class TrainWorkerSilenceGuard : MonoBehaviour
{
    static TrainWorkerSilenceGuard _instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.IsHeadlessTrainWorkerProcess)
            return;
        if (_instance != null)
            return;

        var go = new GameObject(nameof(TrainWorkerSilenceGuard));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<TrainWorkerSilenceGuard>();
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
        TrainingEnvSpace.EnforceHeadlessSilence();
    }

    void LateUpdate()
    {
        if (!TrainingEnvSpace.IsHeadlessTrainWorkerProcess)
            return;
        TrainingEnvSpace.EnforceHeadlessSilence();
    }
}
