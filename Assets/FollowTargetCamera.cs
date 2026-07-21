using UnityEngine;

public sealed class FollowTargetCamera : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField] private Vector3 positionOffset = new Vector3(0f, 2.6f, -3.2f);
    [SerializeField] private Vector3 lookAtOffset = new Vector3(0f, 1.4f, 0f);
    [SerializeField] private float positionLerp = 12f;
    [SerializeField] private bool keepUpright = true;

    public Transform Target
    {
        get => target;
        set => target = value;
    }

    private void LateUpdate()
    {
        if (target == null)
            return;

        var desiredPos = target.TransformPoint(positionOffset);
        transform.position = Vector3.Lerp(transform.position, desiredPos, 1f - Mathf.Exp(-positionLerp * Time.deltaTime));

        var lookAt = target.position + lookAtOffset;
        var dir = lookAt - transform.position;
        if (dir.sqrMagnitude < 0.0001f)
            return;

        var rot = Quaternion.LookRotation(dir.normalized, Vector3.up);
        if (keepUpright)
        {
            var e = rot.eulerAngles;
            rot = Quaternion.Euler(e.x, e.y, 0f);
        }
        transform.rotation = rot;
    }
}
