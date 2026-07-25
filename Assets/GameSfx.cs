using UnityEngine;

/// <summary>
/// Короткие игровые звуки из Resources/Sfx/.
/// </summary>
public static class GameSfx
{
    const string SfxFolder = "Sfx";

    static AudioSource _source;
    static AudioSource _heroDiedSource;
    static GameObject _root;
    static AudioClip _stepGrass;
    static AudioClip _jackLilyHit;
    static AudioClip _zombieHit;
    static AudioClip _food;
    static AudioClip _foodShort;
    static AudioClip _heroDied;
    static AudioClip _wood;
    static AudioClip _lilyKiss;
    static AudioClip _lilyKissShort;
    static float _lastHeroDiedTime = -999f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void PreloadSfx()
    {
        Ensure();
    }

    static void Ensure()
    {
        if (_root != null)
            return;

        _root = new GameObject(nameof(GameSfx));
        Object.DontDestroyOnLoad(_root);

        _source = _root.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.loop = false;
        _source.spatialBlend = 0f;
        _source.volume = 1f;
        _source.dopplerLevel = 0f;

        _heroDiedSource = _root.AddComponent<AudioSource>();
        _heroDiedSource.playOnAwake = false;
        _heroDiedSource.loop = false;
        _heroDiedSource.spatialBlend = 0f;
        _heroDiedSource.ignoreListenerPause = true;

        PreloadClip(HeroDied);
    }

    static void PreloadClip(AudioClip clip)
    {
        if (clip != null && clip.loadState == AudioDataLoadState.Unloaded)
            clip.LoadAudioData();
    }

    static AudioClip Load(string name)
    {
        return Resources.Load<AudioClip>($"{SfxFolder}/{name}");
    }

    static AudioClip StepGrass => _stepGrass ??= Load("step_grass");
    static AudioClip JackLilyHit => _jackLilyHit ??= Load("jack_lily_hit");
    static AudioClip ZombieHit => _zombieHit ??= Load("zombie_hit");
    static AudioClip Food => _food ??= Load("food");
    static AudioClip FoodShort => _foodShort ??= CreatePortionClip(Food, 0.5f);
    static AudioClip HeroDied => _heroDied ??= Load("hero_died");
    static AudioClip Wood => _wood ??= Load("wood");
    static AudioClip LilyKiss => _lilyKiss ??= Load("lily_kiss");
    static AudioClip LilyKissShort => _lilyKissShort ??= CreateDurationClip(LilyKiss, 1f);

    public static void PlayStepGrass(float volume = 0.2f, float pitchMin = 0.95f, float pitchMax = 1.05f, Transform source = null)
    {
        if (!TrainingEnvSpace.ShouldPlayFeedback(source))
            return;
        Play(StepGrass, volume, Random.Range(pitchMin, pitchMax));
    }

    public static void PlayJackLilyHitZombie(float volume = 0.5f, Transform source = null)
    {
        if (!TrainingEnvSpace.ShouldPlayFeedback(source))
            return;
        var clip = JackLilyHit != null ? JackLilyHit : ZombieHit;
        Play(clip, volume, Random.Range(0.96f, 1.04f));
    }

    /// <summary>Зомби или friendly fire ударил Jack, Lily или George.</summary>
    public static void PlayZombieHitAgent(float volume = 0.375f, Transform source = null)
    {
        if (!TrainingEnvSpace.ShouldPlayFeedback(source))
            return;
        Play(ZombieHit, volume, Random.Range(0.96f, 1.04f));
    }

    public static void PlayFood(float volume = 0.53f, Transform source = null)
    {
        if (!TrainingEnvSpace.ShouldPlayFeedback(source))
            return;
        Play(FoodShort != null ? FoodShort : Food, volume, 1f);
    }

    public static void PlayWood(float volume = 0.4f, Transform source = null)
    {
        if (!TrainingEnvSpace.ShouldPlayFeedback(source))
            return;
        Play(Wood, volume, Random.Range(0.97f, 1.03f));
    }

    public static void PlayLilyKiss(float volume = 0.425f, Transform source = null)
    {
        if (!TrainingEnvSpace.ShouldPlayFeedback(source))
            return;
        Play(LilyKissShort != null ? LilyKissShort : LilyKiss, volume, Random.Range(0.98f, 1.02f));
    }

    public static void StopFireLoop()
    {
        CampfireLoopAudio.StopAll();
    }

    public static void StopFireLoop(Transform source)
    {
        CampfireLoopAudio.StopForEnv(source);
    }

    /// <summary>Смерть Джека или Лили — сразу, отдельный канал.</summary>
    public static void PlayHeroDied(float volume = 0.9f, Transform source = null)
    {
        if (!TrainingEnvSpace.ShouldPlayFeedback(source))
            return;
        if (Time.unscaledTime - _lastHeroDiedTime < 0.5f)
            return;

        var clip = HeroDied;
        if (clip == null)
            return;

        Ensure();
        PreloadClip(clip);
        _lastHeroDiedTime = Time.unscaledTime;
        _heroDiedSource.pitch = 1f;
        _heroDiedSource.PlayOneShot(clip, volume);
    }

    static void Play(AudioClip clip, float volume, float pitch)
    {
        if (clip == null)
            return;

        Ensure();
        _source.pitch = pitch;
        _source.PlayOneShot(clip, volume);
    }

    static AudioClip CreateDurationClip(AudioClip source, float durationSeconds)
    {
        if (source == null)
            return null;

        durationSeconds = Mathf.Max(0.05f, durationSeconds);
        float portion = source.length > durationSeconds
            ? durationSeconds / source.length
            : 1f;
        return CreatePortionClip(source, portion);
    }

    static AudioClip CreatePortionClip(AudioClip source, float portion)
    {
        if (source == null)
            return null;

        portion = Mathf.Clamp(portion, 0.05f, 1f);
        int channels = source.channels;
        int frequency = source.frequency;
        int sampleCount = Mathf.Max(1, Mathf.RoundToInt(source.samples * portion));
        var data = new float[sampleCount * channels];

        if (!source.GetData(data, 0))
            return source;

        var clip = AudioClip.Create(source.name + "_short", sampleCount, channels, frequency, false);
        clip.SetData(data, 0);
        return clip;
    }
}
