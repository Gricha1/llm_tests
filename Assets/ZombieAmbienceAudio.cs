using UnityEngine;

/// <summary>
/// Зацикленный ambient, пока на карте есть живые зомби (presentation Env при обучении).
/// </summary>
public sealed class ZombieAmbienceAudio : MonoBehaviour
{
    const string ResourcesPath = "Sfx/zombie_going";
    const float CheckInterval = 0.35f;

    static ZombieAmbienceAudio _instance;

    [SerializeField] [Range(0f, 1f)] private float volume = 0.55f;

    AudioSource _source;
    AudioClip _clip;
    float _nextCheckTime;
    bool _playing;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.ShouldPlayAmbientAudio())
            return;

        if (FindFirstObjectByType<ZombieAmbienceAudio>() != null)
            return;

        var go = new GameObject(nameof(ZombieAmbienceAudio));
        go.AddComponent<ZombieAmbienceAudio>();
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

        _source = gameObject.AddComponent<AudioSource>();
        _source.loop = true;
        _source.playOnAwake = false;
        _source.spatialBlend = 0f;
        _source.volume = volume;
        _source.priority = 64;

        _clip = Resources.Load<AudioClip>(ResourcesPath);
        if (_clip == null)
            Debug.LogWarning($"ZombieAmbienceAudio: не найден Resources/{ResourcesPath}.mp3");
        else if (_clip.loadState == AudioDataLoadState.Unloaded)
            _clip.LoadAudioData();
    }

    void Update()
    {
        if (_clip == null || _source == null)
            return;

        if (Time.unscaledTime < _nextCheckTime)
            return;

        _nextCheckTime = Time.unscaledTime + CheckInterval;

        bool wantPlay = ShouldPlayAmbience() && CountLiveZombies() > 0;
        if (wantPlay == _playing)
            return;

        _playing = wantPlay;
        if (wantPlay)
        {
            _source.clip = _clip;
            _source.volume = volume;
            if (!_source.isPlaying)
                _source.Play();
        }
        else if (_source.isPlaying)
        {
            _source.Stop();
        }
    }

    static bool ShouldPlayAmbience()
    {
        if (!TrainingEnvSpace.ShouldPlayAmbientAudio())
            return false;

        if (!TrainingEnvSpace.HasMultipleTrainingEnvs())
            return true;

        return TrainingEnvSpace.PresentationRoot != null;
    }

    static int CountLiveZombies()
    {
        if (TrainingEnvSpace.HasMultipleTrainingEnvs())
            return CountLiveZombiesInRoot(TrainingEnvSpace.PresentationRoot);

        int count = 0;
        foreach (var health in Object.FindObjectsByType<ZombieHealth>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (health != null && health.Hp > 0)
                count++;
        }

        return count;
    }

    static int CountLiveZombiesInRoot(Transform root)
    {
        if (root == null)
            return 0;

        int count = 0;
        foreach (var health in root.GetComponentsInChildren<ZombieHealth>(false))
        {
            if (health != null && health.Hp > 0)
                count++;
        }

        return count;
    }
}
