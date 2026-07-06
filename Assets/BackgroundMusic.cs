using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Фоновая музыка при Play: loop, DontDestroyOnLoad.
/// </summary>
public sealed class BackgroundMusic : MonoBehaviour
{
    const string ResourcesPath = "Music/CelticElfMusic";
    const string StreamingRelativePath = "Music/CelticElfMusic.mp3";

    [SerializeField] [Range(0f, 1f)] private float volume = 0.65f;

    static BackgroundMusic _instance;
    AudioSource _source;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindObjectOfType<BackgroundMusic>() != null)
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

        _source = gameObject.AddComponent<AudioSource>();
        _source.loop = true;
        _source.playOnAwake = false;
        _source.volume = volume / 3f;
        _source.spatialBlend = 0f;
        _source.priority = 0;

        EnsureAudioListener();
        StartCoroutine(StartMusicRoutine());
    }

    static void EnsureAudioListener()
    {
        var listeners = FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < listeners.Length; i++)
        {
            if (listeners[i] != null && listeners[i].enabled)
                return;
        }

        _instance.gameObject.AddComponent<AudioListener>();
    }

    IEnumerator StartMusicRoutine()
    {
        var clip = Resources.Load<AudioClip>(ResourcesPath);
        if (clip != null)
        {
            yield return PlayClipWhenReady(clip, "Resources");
            yield break;
        }

        string streamingPath = Path.Combine(Application.streamingAssetsPath, StreamingRelativePath);
        if (!File.Exists(streamingPath))
        {
            Debug.LogWarning(
                "BackgroundMusic: mp3 не найден. Нужен файл:\n" +
                $"  Assets/Resources/Music/CelticElfMusic.mp3\n" +
                $"  или Assets/StreamingAssets/{StreamingRelativePath}");
            yield break;
        }

        using var req = UnityWebRequestMultimedia.GetAudioClip(streamingPath, AudioType.MPEG);
        if (req.downloadHandler is DownloadHandlerAudioClip handler)
            handler.streamAudio = true;

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"BackgroundMusic: ошибка загрузки mp3: {req.error}");
            yield break;
        }

        clip = DownloadHandlerAudioClip.GetContent(req);
        if (clip == null)
        {
            Debug.LogWarning("BackgroundMusic: AudioClip пустой после загрузки.");
            yield break;
        }

        _source.clip = clip;
        _source.Play();
        Debug.Log($"BackgroundMusic: играет (StreamingAssets), isPlaying={_source.isPlaying}");
    }

    IEnumerator PlayClipWhenReady(AudioClip clip, string sourceLabel)
    {
        _source.clip = clip;

        if (clip.loadState == AudioDataLoadState.Unloaded)
            clip.LoadAudioData();

        float timeout = 60f;
        float waited = 0f;
        while (clip.loadState == AudioDataLoadState.Loading && waited < timeout)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        _source.Play();

        // Streaming-клип может начать чуть позже — повторяем Play().
        for (int i = 0; i < 5 && !_source.isPlaying; i++)
        {
            yield return null;
            _source.Play();
        }

        if (_source.isPlaying)
            Debug.Log($"BackgroundMusic: играет ({sourceLabel}), длина {clip.length:F0} с");
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
