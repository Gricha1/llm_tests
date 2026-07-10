using UnityEngine;

/// <summary>
/// Зомби идёт к тому, кто ближе — Джек или Лилия. Скорость ниже, чем у них (Jack/Lily = 3, по умолчанию зомби = 1.5).
/// Нужен CharacterController или Rigidbody на объекте.
/// </summary>
public class ZombieChase : MonoBehaviour
{
    [Header("Targets")]
    [SerializeField] private Transform jackTarget;
    [SerializeField] private Transform lilyTarget;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 0.5f;
    [SerializeField] private float rotationSpeed = 120f;

    [Header("Path (optional)")]
    [Tooltip("Если true — зомби идёт по точкам внутри pathRoot (FirstPoint, SecondPoint...) вместо преследования целей.")]
    [SerializeField] private bool followPath = false;
    [SerializeField] private Transform pathRoot;
    [SerializeField] private float pathArriveDistance = 0.2f;
    [SerializeField] private float pathWaitSeconds = 0.0f;
    [SerializeField] private bool pathLoop = true;
    [Tooltip("Если pathLoop выключен, то после последней точки зомби возвращается к обычному преследованию Jack/Lily.")]
    [SerializeField] private bool resumeChaseAfterLastPoint = true;

    [Header("Idle (optional)")]
    [Tooltip("Если true — зомби стоит на месте (не идёт по пути и не преследует цели).")]
    [SerializeField] private bool stayInPlace = false;

    [Header("Gravity")]
    [SerializeField] private float gravity = -9.81f;

    [Header("Animation")]
    [Tooltip("Сглаживание Speed в Animator (0 = выкл).")]
    [SerializeField] private float walkAnimSpeedDamp = 0f;

    private CharacterController controller;
    private Rigidbody rb;
    private Animator animator;
    private float verticalVelocity;
    private bool useRigidbody;
    private float _walkAnimIntent;
    private int _pathIndex;
    private float _pathWaitLeft;

    [Header("Stun / Freeze")]
    [Tooltip("Время, до которого зомби не может двигаться (устанавливается через Stun).")]
    private float stunnedUntilTime = -999f;

    // Счётчик попаданий melee-DO от агентов (Jack/Lily): гарантируем смерть за N ударов даже без ZombieHealth.
    private int meleeDoHitsFromAgents;

    public bool IsStunned => Time.time < stunnedUntilTime;

    public void SetPresentationTargets(Transform jack, Transform lily = null)
    {
        if (jack != null)
            jackTarget = jack;
        if (lily != null)
            lilyTarget = lily;
    }

    public void ResolveTargetsFromEnv()
    {
        jackTarget = null;
        lilyTarget = null;

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot == null)
            return;

        var jacks = envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(false);
        for (int i = 0; i < jacks.Length; i++)
        {
            var jack = jacks[i];
            if (jack != null && !TwitchEphemeralEffects.IsTwitchClone(jack))
            {
                jackTarget = jack.transform;
                break;
            }
        }

        var lily = envRoot.GetComponentInChildren<LilyScript>(false);
        if (lily != null)
            lilyTarget = lily.transform;
    }

    public void EnableAgentChaseMode()
    {
        followPath = false;
        stayInPlace = false;
    }

    public void Stun(float seconds)
    {
        if (seconds <= 0f) return;
        stunnedUntilTime = Mathf.Max(stunnedUntilTime, Time.time + seconds);
    }

    public bool RegisterMeleeDoHitAndMaybeDie(int hitsToDie = 2)
    {
        meleeDoHitsFromAgents++;
        if (hitsToDie > 0 && meleeDoHitsFromAgents >= hitsToDie)
        {
            Destroy(gameObject);
            return true;
        }
        return false;
    }

    // Backward compatibility (old name used by Jack).
    public bool RegisterJackDoHitAndMaybeDie(int hitsToDie = 2) => RegisterMeleeDoHitAndMaybeDie(hitsToDie);

    private void OnEnable()
    {
        meleeDoHitsFromAgents = 0;
        _pathIndex = 0;
        _pathWaitLeft = 0f;
    }

    private void Start()
    {
        controller = GetComponent<CharacterController>();
        rb = GetComponent<Rigidbody>();
        animator = GetComponent<Animator>();
        useRigidbody = (controller == null && rb != null);

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot != null)
        {
            if (jackTarget == null || !TrainingEnvSpace.IsDescendantOf(jackTarget, envRoot))
                ResolveTargetsFromEnv();
        }
        else if (jackTarget == null)
        {
            ResolveTargetsFromEnv();
        }
    }

    private void Update()
    {
        if (Time.time < stunnedUntilTime)
        {
            _walkAnimIntent = 0f;
            return;
        }

        if (stayInPlace)
        {
            _walkAnimIntent = 0f;
            return;
        }

        if (followPath)
        {
            if (useRigidbody) return; // Rigidbody двигаем в FixedUpdate
            PathStepCharacterController();
            return;
        }

        Transform target = GetClosestTarget();
        if (target == null)
        {
            _walkAnimIntent = 0f;
            return;
        }

        Vector3 pos = transform.position;
        Vector3 targetPos = target.position;
        Vector3 delta = targetPos - pos;
        delta.y = 0f;

        if (delta.sqrMagnitude < 0.001f)
        {
            _walkAnimIntent = 0f;
            return;
        }

        Vector3 dir = delta.normalized;
        _walkAnimIntent = 1f;

        // Поворот в сторону цели
        Quaternion targetRot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            targetRot,
            rotationSpeed * Time.deltaTime
        );

        if (controller != null)
        {
            if (controller.isGrounded && verticalVelocity < 0f)
                verticalVelocity = -2f;
            else
                verticalVelocity += gravity * Time.deltaTime;
            Vector3 move = dir * moveSpeed + Vector3.up * verticalVelocity;
            controller.Move(move * Time.deltaTime);
        }
    }

    private void FixedUpdate()
    {
        if (rb == null || !useRigidbody) return;
        if (Time.time < stunnedUntilTime)
        {
            rb.linearVelocity = Vector3.zero;
            return;
        }

        if (stayInPlace)
        {
            rb.linearVelocity = Vector3.zero;
            _walkAnimIntent = 0f;
            return;
        }

        if (followPath)
        {
            PathStepRigidbody();
            return;
        }

        Transform target = GetClosestTarget();
        if (target == null) return;

        Vector3 delta = target.position - transform.position;
        delta.y = 0f;
        if (delta.sqrMagnitude < 0.001f) return;
        Vector3 dir = delta.normalized;

        Vector3 vel = dir * moveSpeed;
        vel.y = rb.linearVelocity.y + gravity * Time.fixedDeltaTime;
        if (vel.y < -20f) vel.y = -20f;
        rb.linearVelocity = vel;
    }

    private void LateUpdate()
    {
        ApplyWalkAnimatorSpeed();
    }

    private bool TryGetCurrentPathPoint(out Transform point)
    {
        point = null;
        if (pathRoot == null) return false;
        int n = pathRoot.childCount;
        if (n <= 0) return false;
        if (_pathIndex < 0 || _pathIndex >= n) _pathIndex = 0;
        point = pathRoot.GetChild(_pathIndex);
        return point != null;
    }

    private void AdvancePath()
    {
        if (pathRoot == null) return;
        int n = pathRoot.childCount;
        if (n <= 0) return;
        _pathIndex++;
        if (_pathIndex >= n)
        {
            if (pathLoop)
            {
                _pathIndex = 0;
            }
            else
            {
                // Дошли до конца пути — возвращаемся к обычному поведению
                if (resumeChaseAfterLastPoint)
                {
                    followPath = false;
                    _walkAnimIntent = 0f;
                }
                _pathIndex = n - 1;
            }
        }
    }

    private void PathStepCharacterController()
    {
        if (controller == null) return;
        if (!TryGetCurrentPathPoint(out var target))
        {
            _walkAnimIntent = 0f;
            return;
        }

        if (_pathWaitLeft > 0f)
        {
            _pathWaitLeft -= Time.deltaTime;
            _walkAnimIntent = 0f;
            return;
        }

        Vector3 pos = transform.position;
        Vector3 delta = target.position - pos;
        delta.y = 0f;
        float dist = delta.magnitude;
        if (dist <= pathArriveDistance)
        {
            _walkAnimIntent = 0f;
            _pathWaitLeft = Mathf.Max(0f, pathWaitSeconds);
            AdvancePath();
            return;
        }

        Vector3 dir = dist > 1e-6f ? delta / dist : Vector3.zero;
        _walkAnimIntent = 1f;

        Quaternion targetRot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, rotationSpeed * Time.deltaTime);

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;
        else
            verticalVelocity += gravity * Time.deltaTime;

        Vector3 move = dir * moveSpeed + Vector3.up * verticalVelocity;
        controller.Move(move * Time.deltaTime);
    }

    private void PathStepRigidbody()
    {
        if (!TryGetCurrentPathPoint(out var target))
            return;

        if (_pathWaitLeft > 0f)
        {
            _pathWaitLeft -= Time.fixedDeltaTime;
            _walkAnimIntent = 0f;
            rb.linearVelocity = new Vector3(0f, rb.linearVelocity.y, 0f);
            return;
        }

        Vector3 delta = target.position - transform.position;
        delta.y = 0f;
        float dist = delta.magnitude;
        if (dist <= pathArriveDistance)
        {
            _walkAnimIntent = 0f;
            _pathWaitLeft = Mathf.Max(0f, pathWaitSeconds);
            AdvancePath();
            rb.linearVelocity = new Vector3(0f, rb.linearVelocity.y, 0f);
            return;
        }

        Vector3 dir = dist > 1e-6f ? delta / dist : Vector3.zero;
        _walkAnimIntent = 1f;

        Quaternion targetRot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, rotationSpeed * Time.fixedDeltaTime);

        Vector3 vel = dir * moveSpeed;
        vel.y = rb.linearVelocity.y + gravity * Time.fixedDeltaTime;
        if (vel.y < -20f) vel.y = -20f;
        rb.linearVelocity = vel;
    }

    private void ApplyWalkAnimatorSpeed()
    {
        if (animator == null) return;

        if (Time.time < stunnedUntilTime)
        {
            animator.SetFloat("Speed", 0f);
            return;
        }

        float target = _walkAnimIntent;

        if (controller != null)
        {
            Vector3 v = controller.velocity;
            v.y = 0f;
            float velNorm = moveSpeed > 1e-4f ? Mathf.Clamp01(v.magnitude / moveSpeed) : 0f;
            target = Mathf.Max(target, velNorm);
        }
        else if (useRigidbody && rb != null)
        {
            Vector3 v = rb.linearVelocity;
            v.y = 0f;
            float velNorm = moveSpeed > 1e-4f ? Mathf.Clamp01(v.magnitude / moveSpeed) : 0f;
            target = Mathf.Max(target, velNorm);
        }

        if (target < 0.02f)
            target = 0f;

        if (walkAnimSpeedDamp > 0f)
            animator.SetFloat("Speed", target, walkAnimSpeedDamp, Time.deltaTime);
        else
            animator.SetFloat("Speed", target);
    }

    /// <summary>Ближайшая цель по горизонтали (XZ).</summary>
    private Transform GetClosestTarget()
    {
        Vector3 pos = transform.position;
        pos.y = 0f;

        float distJack = float.MaxValue;
        if (jackTarget != null)
        {
            var jackGo = jackTarget.gameObject;
            if (jackGo != null && jackGo.activeInHierarchy)
            {
                Vector3 j = jackTarget.position;
                j.y = 0f;
                distJack = Vector3.Distance(pos, j);
            }
        }

        float distLily = float.MaxValue;
        if (lilyTarget != null)
        {
            var lilyGo = lilyTarget.gameObject;
            if (lilyGo != null && lilyGo.activeInHierarchy)
            {
                Vector3 l = lilyTarget.position;
                l.y = 0f;
                distLily = Vector3.Distance(pos, l);
            }
        }

        if (distJack <= distLily && jackTarget != null)
            return jackTarget;
        if (lilyTarget != null)
            return lilyTarget;
        return null;
    }
}
