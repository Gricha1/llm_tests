using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Двигает камеру по точкам (children у CameraPath) в порядке в Hierarchy.
/// Позиция и поворот интерполируются к Transform каждой точки.
/// </summary>
public class CameraPathFollower : MonoBehaviour
{
    [Header("Path")]
    [Tooltip("Объект CameraPath, внутри которого лежат точки (first_point, second_point, ...).")]
    [SerializeField] private Transform pathRoot;

    private readonly List<Transform> points = new List<Transform>();

    [Header("Motion")]
    [Tooltip("Скорость движения между точками (м/с).")]
    [SerializeField] private float moveSpeed = 3.0f;

    [Tooltip("Плавность поворота вдоль отрезка. 0 = почти линейно, 1 = более мягко.")]
    [SerializeField] [Range(0f, 1f)] private float rotationEase = 0.75f;

    [Tooltip("Насколько близко к точке нужно подойти, чтобы считать её достигнутой.")]
    [SerializeField] private float arriveDistance = 0.05f;

    [Tooltip("Пауза на точке (сек).")]
    [SerializeField] private float waitAtPointSeconds = 0.0f;

    [Header("Wait Override (simple)")]
    [Tooltip("Индекс точки (0 = FirstPoint/первая). -1 = не использовать переопределение.")]
    [SerializeField] private int waitPointIndex = -1;
    [Tooltip("Сколько секунд ждать на указанной точке (waitPointIndex).")]
    [SerializeField] private float waitPointSeconds = 0.0f;

    [Header("Playback")]
    [SerializeField] private bool playOnStart = true;
    [SerializeField] private bool loop = true;

    private int _index;
    private float _waitLeft;
    private bool _playing;

    private Transform _segmentFrom;
    private Transform _segmentTo;
    private float _segmentTotalDist;

    private void Start()
    {
        RefreshPoints();
        if (points.Count > 0 && playOnStart)
        {
            // Стартуем ровно в первой точке, чтобы Recorder начинал “как стоит вначале”.
            transform.SetPositionAndRotation(points[0].position, points[0].rotation);
            _index = 1 % points.Count;
            SetupSegment(0, _index);
            _playing = true;
        }
    }

    [ContextMenu("Refresh Points From PathRoot")]
    public void RefreshPoints()
    {
        points.Clear();
        if (pathRoot == null) return;
        for (int i = 0; i < pathRoot.childCount; i++)
        {
            var t = pathRoot.GetChild(i);
            if (t != null)
                points.Add(t);
        }
    }

    private void Update()
    {
        if (!_playing) return;
        if (points == null || points.Count == 0) return;

        if (_waitLeft > 0f)
        {
            _waitLeft -= Time.deltaTime;
            return;
        }

        if (_segmentTo == null || _segmentFrom == null)
        {
            // Восстановимся, если точки были удалены/поменяны.
            SetupSegment(Mathf.Clamp(_index - 1, 0, points.Count - 1), Mathf.Clamp(_index, 0, points.Count - 1));
            return;
        }

        // Move towards segment end
        Vector3 to = _segmentTo.position - transform.position;
        float distLeft = to.magnitude;
        if (distLeft <= arriveDistance)
        {
            transform.SetPositionAndRotation(_segmentTo.position, _segmentTo.rotation);
            _waitLeft = Mathf.Max(0f, GetWaitSecondsForPoint(_index));
            Advance();
            return;
        }

        float stepLen = moveSpeed * Time.deltaTime;
        Vector3 step = to / Mathf.Max(0.0001f, distLeft) * stepLen;
        if (step.magnitude > distLeft) step = to;
        transform.position += step;

        // Rotate smoothly along the whole segment (no snap to next point on arrival)
        float traveled = Mathf.Max(0f, _segmentTotalDist - distLeft);
        float t = _segmentTotalDist <= 0.0001f ? 1f : Mathf.Clamp01(traveled / _segmentTotalDist);
        float eased = Ease01(t, rotationEase);
        transform.rotation = Quaternion.Slerp(_segmentFrom.rotation, _segmentTo.rotation, eased);
    }

    private void Advance()
    {
        int prev = _index;
        _index++;
        if (_index >= points.Count)
        {
            if (loop)
                _index = 0;
            else
                _playing = false;
        }

        if (_playing)
            SetupSegment(prev, _index);
    }

    private void SetupSegment(int fromIndex, int toIndex)
    {
        if (points == null || points.Count == 0) return;
        fromIndex = Mathf.Clamp(fromIndex, 0, points.Count - 1);
        toIndex = Mathf.Clamp(toIndex, 0, points.Count - 1);
        _segmentFrom = points[fromIndex];
        _segmentTo = points[toIndex];
        if (_segmentFrom == null || _segmentTo == null)
        {
            _segmentTotalDist = 0f;
            return;
        }
        _segmentTotalDist = Vector3.Distance(_segmentFrom.position, _segmentTo.position);
    }

    private static float Ease01(float t, float ease)
    {
        // ease=0 => линейно; ease=1 => smoothstep
        float smooth = t * t * (3f - 2f * t);
        return Mathf.Lerp(t, smooth, Mathf.Clamp01(ease));
    }

    private float GetWaitSecondsForPoint(int pointIndex)
    {
        if (waitPointIndex >= 0 && waitPointIndex == pointIndex)
            return Mathf.Max(0f, waitPointSeconds);
        return waitAtPointSeconds;
    }
}

