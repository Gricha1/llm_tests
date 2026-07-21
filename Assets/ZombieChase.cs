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
    [Tooltip("На этой дистанции зомби останавливается и бьёт, не залезая на цель.")]
    [SerializeField] private float stopDistance = 1.25f;

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

    static readonly int SpeedParamHash = Animator.StringToHash("Speed");

    private CharacterController controller;
    private Rigidbody rb;
    private Animator animator;
    private bool _speedParamResolved;
    private bool _hasSpeedParam;
    private float verticalVelocity;
    private bool useRigidbody;
    private float _baseMoveSpeed;
    private bool _baseMoveSpeedCaptured;
    private float _moveSpeedMultiplier = 1f;
    private float _walkAnimIntent;
    private int _pathIndex;
    private float _pathWaitLeft;
    private bool _lockWorldY;
    private float _lockedWorldY;
    private bool _lockEnvLocalY;
    private Transform _lockEnvRoot;
    private float _lockedEnvLocalY;
    private bool _lockParentLocalY;
    private float _lockedParentLocalY;

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

        var agents = envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(false);
        for (int i = 0; i < agents.Length; i++)
        {
            var agent = agents[i];
            if (!IsValidChaseAgent(agent))
                continue;

            if (TrainingEnvSpace.IsGeorgeAgent(agent))
                continue;

            jackTarget = agent.transform;
            break;
        }

        var lily = envRoot.GetComponentInChildren<LilyScript>(false);
        if (lily != null && lily.Hp > 0)
            lilyTarget = lily.transform;
    }

    static bool IsValidChaseAgent(AgentGoToHouseDiscrete agent)
    {
        if (agent == null || !agent.gameObject.activeInHierarchy)
            return false;
        if (TwitchEphemeralEffects.IsTwitchClone(agent))
            return false;
        return agent.IsAliveForTwitch;
    }

    static bool IsValidChaseLily(LilyScript lily)
    {
        return lily != null && lily.gameObject.activeInHierarchy && lily.Hp > 0;
    }

    float GetEffectiveStopDistance() => stopDistance;

    static void TryPickNearest(Transform candidate, Vector3 fromPos, ref Transform best, ref float bestDist)
    {
        if (candidate == null || !candidate.gameObject.activeInHierarchy)
            return;

        // Только XZ — иначе при «парящем» Y дистанция ломается и зомби не идёт.
        Vector3 p = candidate.position;
        float dx = p.x - fromPos.x;
        float dz = p.z - fromPos.z;
        float d = Mathf.Sqrt(dx * dx + dz * dz);
        if (d < bestDist)
        {
            bestDist = d;
            best = candidate;
        }
    }

    public void EnableAgentChaseMode()
    {
        followPath = false;
        stayInPlace = false;
        // Не лочить текущий transform.y — шаблон/клон часто уже на 0 от CC.
    }

    /// <summary>CityScene: не давать гравитации CC утянуть Y к ~0 (меш тогда «под землёй»).</summary>
    public void LockWorldY(float worldY)
    {
        _lockEnvLocalY = false;
        _lockEnvRoot = null;
        _lockParentLocalY = false;
        _lockWorldY = true;
        _lockedWorldY = worldY;
        ApplyLockedWorldY();
    }

    /// <summary>
    /// Лес / Env (N): держать локальную высоту земли Env.
    /// Абсолютный world Y ломается, когда Env двигают (−400 → presentation).
    /// </summary>
    public void LockEnvLocalY(Transform envRoot, float localY)
    {
        if (envRoot == null)
        {
            LockWorldY(transform.position.y);
            return;
        }

        _lockWorldY = false;
        _lockParentLocalY = false;
        _lockEnvLocalY = true;
        _lockEnvRoot = envRoot;
        _lockedEnvLocalY = localY;
        ApplyLockedEnvLocalY();
    }

    /// <summary>Лес: ребёнок спавнера — всегда local Y=0 (высота = спавнер = Джек).</summary>
    public void LockParentLocalY(float localY = 0f)
    {
        _lockWorldY = false;
        _lockEnvLocalY = false;
        _lockEnvRoot = null;
        _lockParentLocalY = true;
        _lockedParentLocalY = localY;
        ApplyLockedParentLocalY();
    }

    void ApplyLockedWorldY()
    {
        if (!_lockWorldY)
            return;

        if (controller == null)
            controller = GetComponent<CharacterController>();

        Vector3 p = transform.position;
        if (Mathf.Abs(p.y - _lockedWorldY) < 0.0001f)
            return;

        var cc = controller != null ? controller : GetComponentInChildren<CharacterController>();
        bool ccOn = cc != null && cc.enabled;
        if (cc != null)
            cc.enabled = false;

        p.y = _lockedWorldY;
        transform.position = p;

        if (cc != null)
            cc.enabled = ccOn;
    }

    void ApplyLockedEnvLocalY()
    {
        if (!_lockEnvLocalY || _lockEnvRoot == null)
            return;

        if (controller == null)
            controller = GetComponent<CharacterController>();

        Vector3 local = _lockEnvRoot.InverseTransformPoint(transform.position);
        if (Mathf.Abs(local.y - _lockedEnvLocalY) < 0.0001f)
            return;

        var cc = controller != null ? controller : GetComponentInChildren<CharacterController>();
        bool ccOn = cc != null && cc.enabled;
        if (cc != null)
            cc.enabled = false;

        local.y = _lockedEnvLocalY;
        transform.position = _lockEnvRoot.TransformPoint(local);

        if (cc != null)
            cc.enabled = ccOn;
    }

    void ApplyLockedParentLocalY()
    {
        if (!_lockParentLocalY || transform.parent == null)
            return;

        Vector3 lp = transform.localPosition;
        if (Mathf.Abs(lp.y - _lockedParentLocalY) < 0.0001f)
            return;

        var cc = controller != null ? controller : GetComponentInChildren<CharacterController>();
        bool ccOn = cc != null && cc.enabled;
        if (cc != null)
            cc.enabled = false;

        lp.y = _lockedParentLocalY;
        transform.localPosition = lp;

        if (cc != null)
            cc.enabled = ccOn;
    }

    void ApplyHeightLock()
    {
        if (_lockParentLocalY)
            ApplyLockedParentLocalY();
        else if (_lockEnvLocalY)
            ApplyLockedEnvLocalY();
        else if (_lockWorldY)
            ApplyLockedWorldY();
    }

    public void SetMoveSpeedMultiplier(float multiplier)
    {
        CaptureBaseMoveSpeedIfNeeded();
        _moveSpeedMultiplier = Mathf.Max(0.05f, multiplier);
        moveSpeed = _baseMoveSpeed * _moveSpeedMultiplier;
    }

    void CaptureBaseMoveSpeedIfNeeded()
    {
        if (_baseMoveSpeedCaptured)
            return;

        _baseMoveSpeed = moveSpeed;
        _baseMoveSpeedCaptured = true;
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
        _baseMoveSpeedCaptured = false;
        _moveSpeedMultiplier = 1f;
        _speedParamResolved = false;
        _hasSpeedParam = false;
    }

    void ResolveWalkSpeedParam()
    {
        _speedParamResolved = true;
        _hasSpeedParam = false;
        if (animator == null)
            return;

        for (int i = 0; i < animator.parameterCount; i++)
        {
            var p = animator.GetParameter(i);
            if (p.type == AnimatorControllerParameterType.Float && p.nameHash == SpeedParamHash)
            {
                _hasSpeedParam = true;
                return;
            }
        }
    }

    private void Start()
    {
        controller = GetComponent<CharacterController>();
        rb = GetComponent<Rigidbody>();
        animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
        if (animator != null)
            animator.applyRootMotion = false;
        useRigidbody = (controller == null && rb != null);
        _speedParamResolved = false;
        ResolveWalkSpeedParam();

        // Не трогаем jackTarget, если уже назначен в инспекторе (CityScene: Jack часто вне Env).
        if (jackTarget == null)
            ResolveTargetsFromEnv();
        if (jackTarget == null)
        {
            var presentationJack = TrainingEnvSpace.FindPresentationJack();
            if (presentationJack != null)
                jackTarget = presentationJack.transform;
        }

        // Вне Env (CityScene) — держим текущую высоту, иначе CC провалит Y≈0.
        // Если спавнер уже вызвал Lock* — не перезаписывать.
        if (!_lockWorldY && !_lockEnvLocalY && !_lockParentLocalY
            && TrainingEnvSpace.FindRoot(transform) == null)
            LockWorldY(transform.position.y);
    }

    private void LateUpdate()
    {
        ApplyWalkAnimatorSpeed();
        ApplyHeightLock();
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
            ApplyHeightLock();
            return;
        }

        Transform target = GetClosestTarget();
        if (target == null)
        {
            // Цель могла появиться позже спавна (Jack ещё не active) — перепривязать.
            if (jackTarget == null)
                ResolveTargetsFromEnv();
            target = GetClosestTarget();
        }

        if (target == null)
        {
            _walkAnimIntent = 0f;
            ApplyHeightLock();
            return;
        }

        Vector3 pos = transform.position;
        Vector3 targetPos = target.position;
        Vector3 delta = targetPos - pos;
        delta.y = 0f;

        if (delta.sqrMagnitude < 0.001f)
        {
            _walkAnimIntent = 0f;
            ApplyHeightLock();
            return;
        }

        Vector3 dir = delta.normalized;
        float dist = delta.magnitude;
        float stopAt = GetEffectiveStopDistance();

        Quaternion targetRot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            targetRot,
            rotationSpeed * Time.deltaTime
        );

        if (dist <= stopAt)
        {
            _walkAnimIntent = 0f;
            ApplyHeightLock();
            return;
        }

        _walkAnimIntent = 1f;

        bool ccUsable = controller != null && controller.enabled;
        if (ccUsable)
        {
            if (_lockWorldY || _lockEnvLocalY || _lockParentLocalY)
            {
                // Только горизонталь — без гравитации, иначе Y уезжает.
                controller.Move(dir * (moveSpeed * Time.deltaTime));
                ApplyHeightLock();
            }
            else
            {
                if (controller.isGrounded && verticalVelocity < 0f)
                    verticalVelocity = -2f;
                else
                    verticalVelocity += gravity * Time.deltaTime;
                Vector3 move = dir * moveSpeed + Vector3.up * verticalVelocity;
                controller.Move(move * Time.deltaTime);
            }
        }
        else if (useRigidbody)
        {
            // FixedUpdate
        }
        else
        {
            Vector3 next = transform.position + dir * (moveSpeed * Time.deltaTime);
            transform.position = next;
            ApplyHeightLock();
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
        float dist = delta.magnitude;
        float stopAt = GetEffectiveStopDistance();

        if (dist <= stopAt)
        {
            rb.linearVelocity = new Vector3(0f, rb.linearVelocity.y, 0f);
            _walkAnimIntent = 0f;
            return;
        }

        Vector3 vel = dir * moveSpeed;
        vel.y = rb.linearVelocity.y + gravity * Time.fixedDeltaTime;
        if (vel.y < -20f) vel.y = -20f;
        rb.linearVelocity = vel;
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
        if (animator == null)
            return;

        if (!_speedParamResolved)
            ResolveWalkSpeedParam();
        if (!_hasSpeedParam)
            return;

        if (Time.time < stunnedUntilTime)
        {
            animator.SetFloat(SpeedParamHash, 0f);
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
            animator.SetFloat(SpeedParamHash, target, walkAnimSpeedDamp, Time.deltaTime);
        else
            animator.SetFloat(SpeedParamHash, target);
    }

    /// <summary>Ближайший живой Jack, George или Lily в этом Env.</summary>
    private Transform GetClosestTarget()
    {
        Vector3 pos = transform.position;

        Transform best = null;
        float bestDist = float.MaxValue;

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot != null)
        {
            var agents = envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(false);
            for (int i = 0; i < agents.Length; i++)
            {
                if (!IsValidChaseAgent(agents[i]))
                    continue;
                TryPickNearest(agents[i].transform, pos, ref best, ref bestDist);
            }

            var lilies = envRoot.GetComponentsInChildren<LilyScript>(false);
            for (int i = 0; i < lilies.Length; i++)
            {
                if (!IsValidChaseLily(lilies[i]))
                    continue;
                TryPickNearest(lilies[i].transform, pos, ref best, ref bestDist);
            }

            if (best != null)
                return best;
        }

        TryPickNearest(jackTarget, pos, ref best, ref bestDist);
        TryPickNearest(lilyTarget, pos, ref best, ref bestDist);
        return best;
    }
}
