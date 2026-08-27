using System.Collections.Generic;
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
    [Tooltip("Дистанция до центра цели, на которой зомби останавливается (меш FatZombie широкий — держим маленькой).")]
    [SerializeField] private float stopDistance = 0.55f;

    [Header("Separation")]
    [Tooltip("Не скучиваться: мягкий разнос, если CharacterController тоньше меша.")]
    [SerializeField] private float separationRadius = 1.25f;
    [SerializeField] private float separationStrength = 2.2f;

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

    private Transform _lastChaseTarget;
    private string _lastChaseTargetKind = "";
    private string _lastChaseTargetName = "";
    private float _lastChaseTargetDist = -1f;

    public Transform LastChaseTarget => _lastChaseTarget;
    public string LastChaseTargetKind => _lastChaseTargetKind;
    public string LastChaseTargetName => _lastChaseTargetName;
    public float LastChaseTargetDist => _lastChaseTargetDist;

    public struct ChaseCandidate
    {
        public string kind;
        public string name;
        public float x;
        public float z;
        public float dist;
        /// <summary>ok или skip:reason</summary>
        public string status;
    }

    public void RememberChaseTarget(Transform target)
    {
        _lastChaseTarget = target;
        if (target == null)
        {
            _lastChaseTargetKind = "";
            _lastChaseTargetName = "";
            _lastChaseTargetDist = -1f;
            return;
        }

        DescribeTarget(target, transform.position, out _lastChaseTargetKind, out _lastChaseTargetName, out _lastChaseTargetDist);
    }

    public static void DescribeTarget(
        Transform target, Vector3 fromPos, out string kind, out string name, out float dist)
    {
        kind = "unknown";
        name = target != null ? target.name : "";
        dist = -1f;
        if (target == null)
            return;

        Vector3 p = target.position;
        float dx = p.x - fromPos.x;
        float dz = p.z - fromPos.z;
        dist = Mathf.Sqrt(dx * dx + dz * dz);

        var follower = target.GetComponentInParent<StreamingSurvivalPlayer>();
        if (follower != null)
        {
            kind = "follower";
            name = string.IsNullOrEmpty(follower.Username) ? follower.name : follower.Username;
            return;
        }

        var lily = target.GetComponentInParent<LilyScript>();
        if (lily != null)
        {
            kind = "lily";
            name = "Lily";
            return;
        }

        var agent = target.GetComponentInParent<AgentGoToHouseDiscrete>();
        if (agent != null)
        {
            kind = TrainingEnvSpace.IsGeorgeAgent(agent) ? "george" : "jack";
            name = kind == "george" ? "George" : "Jack";
            return;
        }
    }

    /// <summary>Все кандидаты целей с этого зомби (для SNAP / UI).</summary>
    public void CollectChaseCandidates(List<ChaseCandidate> into)
    {
        if (into == null)
            return;
        CollectChaseCandidatesFrom(transform.position, TrainingEnvSpace.FindRoot(transform), into);
    }

    public static void CollectChaseCandidatesFrom(
        Vector3 fromPos, Transform zombieEnvRoot, List<ChaseCandidate> into)
    {
        if (into == null)
            return;

        void Add(string kind, string name, Transform t, string status)
        {
            if (t == null)
                return;
            float dx = t.position.x - fromPos.x;
            float dz = t.position.z - fromPos.z;
            into.Add(new ChaseCandidate
            {
                kind = kind,
                name = name,
                x = t.position.x,
                z = t.position.z,
                dist = Mathf.Sqrt(dx * dx + dz * dz),
                status = status,
            });
        }

        var envRoot = zombieEnvRoot;
        if (envRoot != null)
        {
            var agents = envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(false);
            for (int i = 0; i < agents.Length; i++)
            {
                var a = agents[i];
                if (a == null) continue;
                string kind = TrainingEnvSpace.IsGeorgeAgent(a) ? "george" : "jack";
                string status = IsValidChaseAgent(a) ? "ok" : "skip:invalid_agent";
                Add(kind, kind == "george" ? "George" : "Jack", a.transform, status);
            }

            var lilies = envRoot.GetComponentsInChildren<LilyScript>(false);
            for (int i = 0; i < lilies.Length; i++)
            {
                var lily = lilies[i];
                if (lily == null) continue;
                string status = IsValidChaseLily(lily) ? "ok" : "skip:invalid_lily";
                Add("lily", "Lily", lily.transform, status);
            }
        }

        var followers = GetCachedFollowers();
        if (followers != null)
        {
            for (int i = 0; i < followers.Length; i++)
            {
                var p = followers[i];
                if (p == null) continue;
                string status = FollowerChaseStatus(p, zombieEnvRoot);
                Add("follower", string.IsNullOrEmpty(p.Username) ? p.name : p.Username, p.transform, status);
            }
        }
        else
        {
            var ctrl = StreamingSurvivalController.Instance;
            ctrl?.ForEachPlayer(p =>
            {
                if (p == null) return;
                string status = FollowerChaseStatus(p, zombieEnvRoot);
                Add("follower", string.IsNullOrEmpty(p.Username) ? p.name : p.Username, p.transform, status);
            });
        }
    }

    public static string FollowerChaseStatus(StreamingSurvivalPlayer p, Transform zombieEnvRoot)
    {
        if (p == null) return "skip:null";
        if (!p.gameObject.activeInHierarchy) return "skip:inactive";
        if (!p.enabled) return "skip:component_disabled";
        if (p.Hp <= 0) return "skip:hp0";
        if (!IsValidChaseFollower(p, zombieEnvRoot))
            return "skip:filter";
        return "ok";
    }

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
        return lily != null && lily.gameObject.activeInHierarchy && lily.Hp > 0 && !lily.IsInDeathState;
    }

    static bool IsValidChaseTransform(Transform candidate, Transform zombieEnvRoot)
    {
        if (candidate == null || !candidate.gameObject.activeInHierarchy)
            return false;

        var lily = candidate.GetComponentInParent<LilyScript>();
        if (lily != null)
            return IsValidChaseLily(lily);

        var agent = candidate.GetComponentInParent<AgentGoToHouseDiscrete>();
        if (agent != null)
            return IsValidChaseAgent(agent);

        var follower = candidate.GetComponentInParent<StreamingSurvivalPlayer>();
        if (follower != null)
            return IsValidChaseFollower(follower, zombieEnvRoot);

        return false;
    }

    void PruneStaleSerializedTargets()
    {
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (!IsValidChaseTransform(jackTarget, envRoot))
            jackTarget = null;
        if (!IsValidChaseTransform(lilyTarget, envRoot))
            lilyTarget = null;
    }

    float GetEffectiveStopDistance(Transform target)
    {
        // Зазор от поверхности цели: при #size коллайдер Джека растёт — иначе зомби
        // упирается в CC и «не достаёт» до центра с старым stopDistance ~0.4.
        float gap = stopDistance > 0.05f ? stopDistance : 0.4f;
        gap = Mathf.Clamp(gap, 0.3f, 0.5f);
        return gap + EstimateHeroBodyRadius(target);
    }

    /// <summary>Горизонтальный радиус тела героя (CharacterController после #size).</summary>
    public static float EstimateHeroBodyRadius(Transform target)
    {
        if (target == null)
            return 0.35f;

        var cc = target.GetComponent<CharacterController>();
        if (cc != null && cc.radius > 0.05f)
            return cc.radius;

        float scale = Mathf.Max(Mathf.Abs(target.lossyScale.x), Mathf.Abs(target.lossyScale.z));
        return 0.35f * Mathf.Max(1f, scale);
    }

    /// <summary>После спавна: подобрать дистанцию атаки/остановки.</summary>
    public void ConfigureApproach(float stopDist)
    {
        stopDistance = Mathf.Clamp(stopDist, 0.3f, 0.5f);
    }

    void ApplyZombieSeparation()
    {
        if (controller == null || !controller.enabled || separationRadius <= 0.01f)
            return;

        int layerMask = 1 << gameObject.layer;
        if (layerMask == 0)
            layerMask = ~0;

        var hits = Physics.OverlapSphere(transform.position, separationRadius, layerMask);
        if (hits == null || hits.Length == 0)
            return;

        Vector3 push = Vector3.zero;
        int count = 0;
        for (int i = 0; i < hits.Length; i++)
        {
            var col = hits[i];
            if (col == null)
                continue;
            var other = col.GetComponentInParent<ZombieChase>();
            if (other == null || other == this)
                continue;

            Vector3 d = transform.position - other.transform.position;
            d.y = 0f;
            float dist = d.magnitude;
            if (dist < 0.001f)
            {
                d = new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f));
                dist = 0.001f;
            }

            if (dist >= separationRadius)
                continue;

            float w = 1f - (dist / separationRadius);
            push += d.normalized * w;
            count++;
        }

        if (count <= 0 || push.sqrMagnitude < 0.0001f)
            return;

        controller.Move(push.normalized * (separationStrength * Time.deltaTime));
    }

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

        PruneStaleSerializedTargets();

        Transform target = GetClosestTarget();
        RememberChaseTarget(target);
        if (target == null)
        {
            // Цель могла появиться позже спавна (Jack ещё не active) — перепривязать.
            if (jackTarget == null)
                ResolveTargetsFromEnv();
            target = GetClosestTarget();
            RememberChaseTarget(target);
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
        float stopAt = GetEffectiveStopDistance(target);

        Quaternion targetRot = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            targetRot,
            rotationSpeed * Time.deltaTime
        );

        if (dist <= stopAt)
        {
            _walkAnimIntent = 0f;
            ApplyZombieSeparation();
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
                ApplyZombieSeparation();
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
                ApplyZombieSeparation();
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

        PruneStaleSerializedTargets();

        Transform target = GetClosestTarget();
        RememberChaseTarget(target);
        if (target == null) return;

        Vector3 delta = target.position - transform.position;
        delta.y = 0f;
        if (delta.sqrMagnitude < 0.001f) return;
        Vector3 dir = delta.normalized;
        float dist = delta.magnitude;
        float stopAt = GetEffectiveStopDistance(target);

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

    /// <summary>Для SNAP/UI: тот же выбор, что в Update.</summary>
    public Transform PickClosestTargetPublic() => GetClosestTarget();

    /// <summary>Ближайший живой Jack/George/Lily или стрим-фолловер.</summary>
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
        }

        // Фолловеры со стрима — всегда в пуле целей (не только Jack/Lily).
        ConsiderStreamingFollowers(pos, envRoot, ref best, ref bestDist);

        if (IsValidChaseTransform(jackTarget, envRoot))
            TryPickNearest(jackTarget, pos, ref best, ref bestDist);
        if (IsValidChaseTransform(lilyTarget, envRoot))
            TryPickNearest(lilyTarget, pos, ref best, ref bestDist);
        return best;
    }

    static StreamingSurvivalPlayer[] _followerCache;
    static int _followerCacheFrame = -1;

    /// <summary>Кэш фолловеров на кадр — общий для chase и attack.</summary>
    public static StreamingSurvivalPlayer[] GetCachedFollowers()
    {
        int frame = Time.frameCount;
        if (_followerCacheFrame != frame)
        {
            _followerCacheFrame = frame;
            _followerCache = Object.FindObjectsByType<StreamingSurvivalPlayer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        }
        return _followerCache;
    }

    static void ConsiderStreamingFollowers(
        Vector3 fromPos, Transform zombieEnvRoot, ref Transform best, ref float bestDist)
    {
        var followers = GetCachedFollowers();
        if (followers == null || followers.Length == 0)
        {
            var ctrl = StreamingSurvivalController.Instance;
            if (ctrl == null)
                return;
            var tmp = new List<StreamingSurvivalPlayer>(8);
            ctrl.ForEachPlayer(p =>
            {
                if (p != null)
                    tmp.Add(p);
            });
            for (int i = 0; i < tmp.Count; i++)
            {
                if (!IsValidChaseFollower(tmp[i], zombieEnvRoot))
                    continue;
                TryPickNearest(tmp[i].transform, fromPos, ref best, ref bestDist);
            }
            return;
        }

        for (int i = 0; i < followers.Length; i++)
        {
            var p = followers[i];
            if (!IsValidChaseFollower(p, zombieEnvRoot))
                continue;
            TryPickNearest(p.transform, fromPos, ref best, ref bestDist);
        }
    }

    static bool IsValidChaseFollower(StreamingSurvivalPlayer p, Transform zombieEnvRoot)
    {
        if (p == null || !p.gameObject.activeInHierarchy)
            return false;
        // Не требуем enabled: TickGameplay иногда идёт с контроллера.
        if (p.IsDeadToZombies || p.Hp <= 0)
            return false;

        // Стрим / live OBS / unified: любой живой фолловер — цель.
        // Без Env-фильтра: иначе mysticggx / SSPlayer вне FindRoot зомби игнорируются.
        if (TrainingEnvSpace.UseUnifiedFollowers
            || TrainingEnvSpace.IsStreamOnlyMode
            || TrainingEnvSpace.IsLivePresentationForObs)
            return true;

        if (zombieEnvRoot == null)
            return true;

        var root = TrainingEnvSpace.FindRoot(p.transform);
        if (root != null && root != zombieEnvRoot)
            return false;
        return true;
    }
}
