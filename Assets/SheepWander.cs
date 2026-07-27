using UnityEngine;

public class SheepWander : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 1.0f;
    [SerializeField] private float rotationSpeed = 180f;

    [Header("Wander")]
    [SerializeField] private float changeDirInterval = 3f;

    [Header("Return to Spawn")]
    [SerializeField] private float maxDistanceFromSpawn = 12f;

    [Header("Obstacle Avoidance")]
    [SerializeField] private float obstacleCheckDistance = 0.8f;
    [SerializeField] private LayerMask obstacleLayers;

    [Header("Gravity")]
    [SerializeField] private float gravity = -9.81f;
    [SerializeField] private float groundedForce = -2f;

    private float verticalVelocity;
    private float timer;
    private Vector3 moveDir;
    private CharacterController controller;

    // Зона в local Env — после сдвига Env (N)→presentation world-центр не «уезжает» вправо.
    private bool hasSpawnArea;
    private Transform _envRoot;
    private Vector3 _spawnCenterLocal;
    private Vector3 _spawnCenterWorldFallback;

    /// <summary>Предпочтительно: центр в local Env, чтобы перенос копии не ломал возврат.</summary>
    public void SetSpawnAreaLocal(Transform envRoot, Vector3 localCenter, float maxDistance)
    {
        _envRoot = envRoot;
        _spawnCenterLocal = localCenter;
        maxDistanceFromSpawn = maxDistance;
        hasSpawnArea = envRoot != null;
        if (envRoot != null)
            _spawnCenterWorldFallback = envRoot.TransformPoint(localCenter);
    }

    /// <summary>Старый API (world). Лучше SetSpawnAreaLocal.</summary>
    public void SetSpawnArea(Vector3 center, float maxDistance)
    {
        _envRoot = TrainingEnvSpace.FindRoot(transform);
        if (_envRoot != null)
        {
            SetSpawnAreaLocal(_envRoot, _envRoot.InverseTransformPoint(center), maxDistance);
            return;
        }

        _spawnCenterWorldFallback = new Vector3(center.x, 0f, center.z);
        maxDistanceFromSpawn = maxDistance;
        hasSpawnArea = true;
    }

    Vector3 SpawnCenterXZ
    {
        get
        {
            if (_envRoot != null)
            {
                Vector3 w = _envRoot.TransformPoint(_spawnCenterLocal);
                return new Vector3(w.x, 0f, w.z);
            }

            return new Vector3(_spawnCenterWorldFallback.x, 0f, _spawnCenterWorldFallback.z);
        }
    }

    private void Start()
    {
        controller = GetComponent<CharacterController>();
        PickRandomDirection();
    }

    private void Update()
    {
        if (controller == null)
            controller = GetComponent<CharacterController>();
        if (controller == null || !controller.enabled)
            return;

        timer += Time.deltaTime;

        Vector3 posXZ = new Vector3(transform.position.x, 0f, transform.position.z);
        Vector3 center = SpawnCenterXZ;

        if (hasSpawnArea && Vector3.Distance(posXZ, center) > maxDistanceFromSpawn)
        {
            moveDir = (center - posXZ).normalized;
            timer = 0f;
        }
        else
        {
            if (Physics.Raycast(transform.position + Vector3.up * 0.2f,
                         transform.forward,
                         obstacleCheckDistance,
                         obstacleLayers))
            {
                PickRandomDirection();
                timer = 0f;
            }
            else if (timer >= changeDirInterval)
            {
                PickRandomDirection();
                timer = 0f;
            }
        }

        Quaternion targetRot = Quaternion.LookRotation(moveDir);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            targetRot,
            rotationSpeed * Time.deltaTime
        );

        Vector3 move = transform.forward * moveSpeed;

        if (controller.isGrounded)
        {
            if (verticalVelocity < 0f)
                verticalVelocity = groundedForce;
        }
        else
        {
            verticalVelocity += gravity * Time.deltaTime;
        }

        move.y = verticalVelocity;
        controller.Move(move * Time.deltaTime);
    }

    private void PickRandomDirection()
    {
        float angle = Random.Range(0f, 360f);
        moveDir = new Vector3(
            Mathf.Sin(angle * Mathf.Deg2Rad),
            0f,
            Mathf.Cos(angle * Mathf.Deg2Rad)
        ).normalized;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawLine(
            transform.position + Vector3.up * 0.2f,
            transform.position + Vector3.up * 0.2f + transform.forward * obstacleCheckDistance
        );
    }
#endif
}
