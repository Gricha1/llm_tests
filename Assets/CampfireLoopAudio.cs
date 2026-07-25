using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Зацикленный звук костра — включается вместе с fire VFX (OnEnable).
/// </summary>
[DisallowMultipleComponent]
public sealed class CampfireLoopAudio : MonoBehaviour
{
    static readonly List<CampfireLoopAudio> Active = new List<CampfireLoopAudio>();

    AudioSource _source;
    static AudioClip _fireClip;

    void Awake()
    {
        _source = GetComponent<AudioSource>();
        if (_source == null)
            _source = gameObject.AddComponent<AudioSource>();

        _source.loop = true;
        _source.playOnAwake = false;
        _source.spatialBlend = 0f;
        _source.volume = 0.55f;
        _source.dopplerLevel = 0f;
    }

    void OnEnable()
    {
        if (TrainingEnvSpace.IsHeadlessTrainWorkerProcess)
            return;
        if (!TrainingEnvSpace.IsPresentationTransform(transform))
            return;

        var clip = FireClip;
        if (clip == null)
            return;

        if (clip.loadState == AudioDataLoadState.Unloaded)
            clip.LoadAudioData();

        _source.clip = clip;
        if (!_source.isPlaying)
            _source.Play();

        if (!Active.Contains(this))
            Active.Add(this);
    }

    void OnDisable()
    {
        if (_source != null && _source.isPlaying)
            _source.Stop();

        Active.Remove(this);
    }

    static AudioClip FireClip => _fireClip ??= Resources.Load<AudioClip>("Sfx/fire");

    public static void StopForEnv(Transform anyInEnv)
    {
        var envRoot = TrainingEnvSpace.FindRoot(anyInEnv);
        if (envRoot == null)
        {
            StopAll();
            return;
        }

        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var campfire = Active[i];
            if (campfire == null)
            {
                Active.RemoveAt(i);
                continue;
            }

            if (!TrainingEnvSpace.IsDescendantOf(campfire.transform, envRoot))
                continue;

            if (campfire._source != null && campfire._source.isPlaying)
                campfire._source.Stop();
        }
    }

    public static void StopAll()
    {
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var campfire = Active[i];
            if (campfire == null)
            {
                Active.RemoveAt(i);
                continue;
            }

            if (campfire._source != null && campfire._source.isPlaying)
                campfire._source.Stop();
        }
    }
}
