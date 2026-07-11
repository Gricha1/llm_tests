using UnityEngine;

/// <summary>
/// Источник воды (озеро/пруд). DO рядом с коллайдером — сбор воды.
/// </summary>
[DisallowMultipleComponent]
public sealed class WaterSource : MonoBehaviour
{
    [SerializeField] private int amountPerCollect = 1;

    Collider _collider;

    public int AmountPerCollect => Mathf.Max(1, amountPerCollect);

    void Awake()
    {
        EnsureCollider();
    }

    void OnValidate()
    {
        EnsureCollider();
    }

    public void EnsureCollider()
    {
        _collider = GetComponent<Collider>();
        if (_collider != null)
            return;

        var box = gameObject.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(10f, 0.25f, 10f);
        box.center = Vector3.zero;
        _collider = box;
    }

    public static bool TryCollect(Transform collector, float reach, Transform envRoot, out int amount)
    {
        amount = 0;
        if (collector == null || reach <= 0f)
            return false;

        Vector3 origin = collector.position;
        float searchRadius = reach + 8f;
        var hits = Physics.OverlapSphere(origin, searchRadius, ~0, QueryTriggerInteraction.Collide);

        WaterSource bestSource = null;
        float bestDist = float.MaxValue;

        for (int i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            if (hit == null)
                continue;

            var source = hit.GetComponent<WaterSource>() ?? hit.GetComponentInParent<WaterSource>();
            if (source == null)
                continue;

            if (envRoot != null && !TrainingEnvSpace.IsDescendantOf(source.transform, envRoot))
                continue;

            source.EnsureCollider();
            var col = source._collider != null ? source._collider : hit;
            float dist = ReachDistance(origin, col);
            if (dist > reach || dist >= bestDist)
                continue;

            bestDist = dist;
            bestSource = source;
        }

        if (bestSource == null)
            return false;

        amount = bestSource.AmountPerCollect;
        return true;
    }

    static float ReachDistance(Vector3 from, Collider col)
    {
        if (col == null)
            return float.MaxValue;
        return Vector3.Distance(from, col.ClosestPoint(from));
    }

    public static bool TryFindNearestDistance(Transform origin, Transform envRoot, out float distance)
    {
        distance = float.MaxValue;
        if (origin == null)
            return false;

        const float searchRadius = 80f;
        var hits = Physics.OverlapSphere(origin.position, searchRadius, ~0, QueryTriggerInteraction.Collide);
        bool found = false;

        for (int i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            if (hit == null)
                continue;

            var source = hit.GetComponent<WaterSource>() ?? hit.GetComponentInParent<WaterSource>();
            if (source == null)
                continue;

            if (envRoot != null && !TrainingEnvSpace.IsDescendantOf(source.transform, envRoot))
                continue;

            source.EnsureCollider();
            var col = source._collider != null ? source._collider : hit;
            float dist = Vector3.Distance(origin.position, col.ClosestPoint(origin.position));
            if (dist >= distance)
                continue;

            distance = dist;
            found = true;
        }

        return found;
    }
}
