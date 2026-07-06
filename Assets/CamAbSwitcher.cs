using UnityEngine;

/// <summary>
/// Каждые N секунд переключает CamA и CamB в основной среде Env.
/// </summary>
public sealed class CamAbSwitcher : MonoBehaviour
{
    [SerializeField] private float switchIntervalSeconds = 20f;
    [SerializeField] private Camera cameraA;
    [SerializeField] private Camera cameraB;

    float _nextSwitchTime;
    bool _usingA = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root == null)
            return;

        if (root.GetComponentInChildren<CamAbSwitcher>(true) != null)
            return;

        var go = new GameObject(nameof(CamAbSwitcher));
        go.transform.SetParent(root, false);
        go.AddComponent<CamAbSwitcher>();
    }

    void Start()
    {
        ResolveCameras();
        ActivateA();
        _nextSwitchTime = Time.unscaledTime + switchIntervalSeconds;
    }

    void Update()
    {
        if (cameraA == null || cameraB == null)
            ResolveCameras();
        if (cameraA == null || cameraB == null)
            return;

        if (Time.unscaledTime < _nextSwitchTime)
            return;

        _nextSwitchTime = Time.unscaledTime + switchIntervalSeconds;
        if (_usingA)
            ActivateB();
        else
            ActivateA();
    }

    void ResolveCameras()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root == null)
            return;

        if (cameraA == null)
            cameraA = FindNamedCamera(root, "CamA");
        if (cameraB == null)
            cameraB = FindNamedCamera(root, "CamB");
    }

    static Camera FindNamedCamera(Transform root, string name)
    {
        foreach (var cam in root.GetComponentsInChildren<Camera>(true))
        {
            if (cam != null && cam.gameObject.name == name)
                return cam;
        }

        return null;
    }

    void ActivateA()
    {
        _usingA = true;
        SetCameraActive(cameraA, true);
        SetCameraActive(cameraB, false);
    }

    void ActivateB()
    {
        _usingA = false;
        SetCameraActive(cameraA, false);
        SetCameraActive(cameraB, true);
    }

    static void SetCameraActive(Camera cam, bool active)
    {
        if (cam == null)
            return;
        cam.gameObject.SetActive(active);
    }
}
