using UnityEngine;

/// <summary>
/// Выбор камеры для billboard UI над агентом: приоритет — та, что реально смотрит на точку (лучший dot forward · направление на цель).
/// Учитывает только enabled-камеры; опционально — явная камера из инспектора (если включена).
/// </summary>
public static class BillboardIconCamera
{
    /// <summary>
    /// Временная переопределённая камера (например, на время захвата кадра).
    /// Если задана и активна — имеет приоритет над автоматическим выбором.
    /// </summary>
    public static Camera CaptureOverride { get; set; }

    /// <param name="billboardWorldPos">Мировая позиция иконки (или якорь над агентом).</param>
    /// <param name="preferred">Если задана и активна — всегда она.</param>
    /// <param name="minForwardDot">Минимальный «угол обзора»; ниже — не считаем камеру подходящей и идём в fallback.</param>
    public static Camera Resolve(Vector3 billboardWorldPos, Camera preferred, float minForwardDot = 0.05f)
    {
        if (CaptureOverride != null && CaptureOverride.enabled && CaptureOverride.gameObject.activeInHierarchy)
            return CaptureOverride;

        if (preferred != null && preferred.enabled && preferred.gameObject.activeInHierarchy)
            return preferred;

        Camera bestFacing = null;
        float bestScore = minForwardDot;

        foreach (var c in Camera.allCameras)
        {
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;

            Vector3 toTarget = billboardWorldPos - c.transform.position;
            if (toTarget.sqrMagnitude < 1e-8f) continue;

            float score = Vector3.Dot(c.transform.forward, toTarget.normalized);
            if (score > bestScore)
            {
                bestScore = score;
                bestFacing = c;
            }
        }

        if (bestFacing != null)
            return bestFacing;

        if (Camera.main != null && Camera.main.enabled && Camera.main.gameObject.activeInHierarchy)
            return Camera.main;

        Camera bestDepth = null;
        float dMax = float.MinValue;
        foreach (var c in Camera.allCameras)
        {
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
            if (c.depth > dMax)
            {
                dMax = c.depth;
                bestDepth = c;
            }
        }

        return bestDepth;
    }
}
