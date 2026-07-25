using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

/// <summary>
/// Фоновая музыка при Play: loop, DontDestroyOnLoad.
/// Forest: этап 1 — CelticElf, 2 — ZombieCity, 3 — Koshmar.
/// CityScene: сразу ZombieCitySong.
/// </summary>
public sealed class BackgroundMusic : MonoBehaviour
{
    static readonly string[] PhaseResourcePaths =
    {
        "Music/CelticElfMusic",
        "Music/ZombieCitySong",
        "Music/ZombieKoshmar",
    };

    static readonly string[] PhaseStreamingPaths =
    {
        "Music/CelticElfMusic.mp3",
        "Music/ZombieCitySong.mp3",
        "Music/ZombieKoshmar.mp3",
    };

    const float Phase2StartSeconds = 30f;
    const float Phase2VolumeMultiplier = 1.55f;

    [SerializeField] [Range(0f, 1f)] private float volume = 0.65f;

    static BackgroundMusic _instance;
    AudioSource _source;
    int _currentPhase = 1;
    Coroutine _switchRoutine;
    bool _citySceneMode;

    static bool IsCitySceneActive()
    {
        string name = SceneManager.GetActiveScene().name;
        return name != null
            && name.IndexOf("City", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.ShouldPlayAmbientAudio())
            return;

        if (FindFirstObjectByType<BackgroundMusic>() != null)
            return;

        var go = new GameObject(nameof(BackgroundMusic));
        go.AddComponent<BackgroundMusic>();
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
        _citySceneMode = IsCitySceneActive();

        _source = gameObject.AddComponent<AudioSource>();
        _source.loop = true;
        _source.playOnAwake = false;
        _source.volume = volume / 3f;
        _source.spatialBlend = 0f;
        _source.priority = 0;

        EnsureAudioListener();
        // CityScene — сразу городской трек (фаза 2), иначе лесная CelticElf.
        int startPhase = _citySceneMode ? 2 : 1;
        _currentPhase = startPhase;
        _switchRoutine = StartCoroutine(SwitchPhaseRoutine(startPhase));
    }

    static void EnsureAudioListener()
    {
        if (TrainingEnvSpace.IsHeadlessTrainWorkerProcess)
            return;

        var listeners = FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < listeners.Length; i++)
        {
            if (listeners[i] != null && listeners[i].enabled)
                return;
        }

        _instance.gameObject.AddComponent<AudioListener>();
    }

    public static void SetSurvivalPhase(int phase)
    {
        // В CityScene не переключаем на лесные треки выживания.
        if (_instance != null && _instance._citySceneMode)
            return;

        phase = Mathf.Clamp(phase, 1, PhaseResourcePaths.Length);
        if (_instance == null)
            return;

        if (_instance._currentPhase == phase)
            return;

        _instance._currentPhase = phase;
        if (_instance._switchRoutine != null)
            _instance.StopCoroutine(_instance._switchRoutine);
        _instance._switchRoutine = _instance.StartCoroutine(_instance.SwitchPhaseRoutine(phase));
    }

    IEnumerator SwitchPhaseRoutine(int phase)
    {
        int index = phase - 1;
        var clip = Resources.Load<AudioClip>(PhaseResourcePaths[index]);
        if (clip != null)
        {
            yield return PlayClipWhenReady(clip, $"phase {phase} (Resources)", phase);
            _switchRoutine = null;
            yield break;
        }

        string streamingPath = Path.Combine(Application.streamingAssetsPath, PhaseStreamingPaths[index]);
        if (!File.Exists(streamingPath))
        {
            Debug.LogWarning(
                $"BackgroundMusic: mp3 для этапа {phase} не найден. Нужен:\n" +
                $"  Assets/Resources/{PhaseResourcePaths[index]}.mp3");
            _switchRoutine = null;
            yield break;
        }

        using var req = UnityWebRequestMultimedia.GetAudioClip(streamingPath, AudioType.MPEG);
        if (req.downloadHandler is DownloadHandlerAudioClip handler)
            handler.streamAudio = true;

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"BackgroundMusic: ошибка загрузки mp3 этапа {phase}: {req.error}");
            _switchRoutine = null;
            yield break;
        }

        clip = DownloadHandlerAudioClip.GetContent(req);
        if (clip == null)
        {
            Debug.LogWarning($"BackgroundMusic: AudioClip пустой для этапа {phase}.");
            _switchRoutine = null;
            yield break;
        }

        yield return PlayClipWhenReady(clip, $"phase {phase} (StreamingAssets)", phase);
        _switchRoutine = null;
    }

    float GetVolumeForPhase(int phase)
    {
        float baseVolume = volume / 3f;
        if (phase == 2)
            return Mathf.Clamp01(baseVolume * Phase2VolumeMultiplier);
        return baseVolume;
    }

    static float GetStartTimeForPhase(int phase, AudioClip clip)
    {
        // В CityScene играем трек с начала; в лесу фаза 2 — со смещением 30с.
        if (phase != 2 || clip == null)
            return 0f;
        if (_instance != null && _instance._citySceneMode)
            return 0f;

        return Mathf.Clamp(Phase2StartSeconds, 0f, Mathf.Max(0f, clip.length - 0.05f));
    }

    IEnumerator PlayClipWhenReady(AudioClip clip, string sourceLabel, int phase)
    {
        _source.clip = clip;
        _source.volume = GetVolumeForPhase(phase);

        if (clip.loadState == AudioDataLoadState.Unloaded)
            clip.LoadAudioData();

        float timeout = 60f;
        float waited = 0f;
        while (clip.loadState == AudioDataLoadState.Loading && waited < timeout)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        _source.time = GetStartTimeForPhase(phase, clip);
        _source.Play();

        for (int i = 0; i < 5 && !_source.isPlaying; i++)
        {
            yield return null;
            _source.time = GetStartTimeForPhase(phase, clip);
            _source.Play();
        }

        if (_source.isPlaying)
        {
            float start = GetStartTimeForPhase(phase, clip);
            string startNote = start > 0.01f ? $", с {start:F0} с" : "";
            Debug.Log($"BackgroundMusic: играет ({sourceLabel}), длина {clip.length:F0} с{startNote}");
        }
        else
            Debug.LogWarning($"BackgroundMusic: Play() не стартовал ({sourceLabel}), loadState={clip.loadState}");
    }

    public static void PauseMusic()
    {
        if (_instance?._source == null)
            return;
        if (_instance._source.isPlaying)
            _instance._source.Pause();
    }

    public static void ResumeMusic()
    {
        if (_instance?._source == null || _instance._source.clip == null)
            return;
        if (!_instance._source.isPlaying)
            _instance._source.UnPause();
    }
}
