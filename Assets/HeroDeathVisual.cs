using System.Collections;
using UnityEngine;
using Unity.MLAgents;

/// <summary>
/// Presentation: падение героя на землю при смерти (без смены AnimatorController).
/// </summary>
public sealed class HeroDeathVisual : MonoBehaviour
{
    const float FallSeconds = 1.15f;

    public enum DeathFallMode
    {
        /// <summary>Назад на спину (Jack / human follower).</summary>
        Back,
        /// <summary>На бок (волк).</summary>
        Side,
    }

    Quaternion _restRotation;
    bool _hasRest;
    bool _playing;
    DeathFallMode _mode = DeathFallMode.Back;
    Coroutine _routine;
    CharacterController _cc;
    bool _ccWasEnabled;

    public static void Play(Agent agent, DeathFallMode mode = DeathFallMode.Back)
    {
        Play((MonoBehaviour)agent, mode);
    }

    public static void Play(MonoBehaviour host, DeathFallMode mode = DeathFallMode.Back)
    {
        if (host == null)
            return;

        var visual = host.GetComponent<HeroDeathVisual>();
        if (visual == null)
            visual = host.gameObject.AddComponent<HeroDeathVisual>();
        visual.BeginFall(mode);
    }

    public static void Clear(Agent agent)
    {
        Clear((MonoBehaviour)agent);
    }

    public static void Clear(MonoBehaviour host)
    {
        if (host == null)
            return;
        var visual = host.GetComponent<HeroDeathVisual>();
        if (visual != null)
            visual.Restore();
    }

    void BeginFall(DeathFallMode mode = DeathFallMode.Back)
    {
        if (_playing)
            return;

        _mode = mode;
        if (!_hasRest)
        {
            _restRotation = transform.rotation;
            _hasRest = true;
        }

        _cc = GetComponent<CharacterController>();
        if (_cc != null)
        {
            _ccWasEnabled = _cc.enabled;
            _cc.enabled = false;
        }

        var anim = GetComponentInChildren<Animator>(true);
        if (anim != null)
        {
            anim.SetFloat("Speed", 0f);
            anim.speed = 0.35f;
        }

        if (_routine != null)
            StopCoroutine(_routine);
        _routine = StartCoroutine(FallRoutine());
    }

    IEnumerator FallRoutine()
    {
        _playing = true;
        Quaternion start = transform.rotation;
        Quaternion end;
        if (_mode == DeathFallMode.Side)
        {
            // Волк: на бок (roll), не вперёд/на спину.
            float roll = Random.value < 0.5f ? 88f : -88f;
            end = start * Quaternion.Euler(0f, 0f, roll);
        }
        else
        {
            // Human: падение назад на спину.
            end = start * Quaternion.Euler(88f, 0f, Random.Range(-12f, 12f));
        }
        float t = 0f;
        while (t < FallSeconds)
        {
            t += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / FallSeconds));
            transform.rotation = Quaternion.Slerp(start, end, k);
            yield return null;
        }

        transform.rotation = end;
        var anim = GetComponentInChildren<Animator>(true);
        if (anim != null)
            anim.speed = 0f;
        _routine = null;
    }

    void Restore()
    {
        if (_routine != null)
        {
            StopCoroutine(_routine);
            _routine = null;
        }

        _playing = false;
        if (_hasRest)
            transform.rotation = _restRotation;
        _hasRest = false;

        if (_cc != null)
            _cc.enabled = _ccWasEnabled;

        var anim = GetComponentInChildren<Animator>(true);
        if (anim != null)
            anim.speed = 1f;
    }
}
