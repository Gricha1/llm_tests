using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Персонаж зрителя Streaming Survival: ходьба с анимацией, путь к воде через GoalWater,
/// рубка дерева, очередь действий (N воды → N дерева).
/// </summary>
public sealed class StreamingSurvivalPlayer : MonoBehaviour
{
    public string Username { get; private set; } = "viewer";
    public string Action { get; private set; } = "idle";
    public string ActionName { get; private set; } = "Ждёт у базы";
    public string PhaseName => _phase.ToString();

    CharacterController _cc;
    Animator _anim;
    TextMesh _label;
    Transform _labelTf;
    Vector3 _home;
    enum Phase
    {
        IdleStand,
        IdleWander,
        GoHomeFirst,
        GoWaterWp,
        GoTarget,
        Work,
        GoBase,
        GoHouse,
        MoveDemo,
    }
    Phase _phase = Phase.IdleStand;
    Vector3 _target;
    float _workUntil;
    float _retargetAt;
    float _speed = 3.2f;
    float _logPosAt;
    const float LogPosInterval = 1.25f;
    float _animSpeed;
    Transform _sheepVictim;
    GameObject _treeVictim;
    bool _allowWander;
    int _needAmount = 1;
    int _doneAmount;
    readonly Queue<QueuedStep> _queue = new Queue<QueuedStep>();
    WaterGoalPath _waterPath;

    public const float MaxAllowedCharacterSpeed = 8.0f;
    public const float MaxAllowedPositionJump = 3.0f;
    public float LastJump { get; private set; }
    public float LastSpeed { get; private set; }
    public string LastTeleportReason { get; private set; } = "";
    public string LastRecoveryStatus { get; private set; } = "";
    Vector3 _prevSamplePos;
    float _prevSampleTime;
    bool _hasPrevSample;

    /// <summary>
    /// Direct position changes allowed only for setup / explicit user respawn.
    /// stuck_recovery is NEVER an allowed teleport reason in normal gameplay.
    /// </summary>
    static readonly HashSet<string> AllowedTeleportReasons = new HashSet<string>
    {
        "join_spawn",
        "scenario_setup",
        "scenario_reset",
        "negative_forest",
        "manual_respawn_command",
        "round_reset",
        "round_start",
    };

    const float MaxStuckNudge = 1.5f;

    static string NormalizeTeleportReason(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return "";
        if (reason == "round_start") return "round_reset";
        if (reason == "scenario_reset" || reason == "negative_forest") return "scenario_setup";
        if (reason == "stuck_recovery") return ""; // never allowed as teleport
        return reason;
    }

    /// <summary>Whitelist-only teleport. Illegal reasons are denied and logged as events.</summary>
    public bool ControlledTeleport(Vector3 worldPos, string reason)
    {
        string raw = reason ?? "";
        string norm = NormalizeTeleportReason(raw);
        if (string.IsNullOrEmpty(norm)
            || (!AllowedTeleportReasons.Contains(raw) && !AllowedTeleportReasons.Contains(norm)))
        {
            LastRecoveryStatus = $"ERROR: illegal teleport ({raw})";
            Debug.LogError(
                $"[SSPos] illegal_teleport_denied user={Username} reason={raw} " +
                $"to=({worldPos.x:F2},{worldPos.z:F2}) action={Action}");
            StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(
                Username, "illegal_teleport", this);
            return false;
        }

        string useReason = AllowedTeleportReasons.Contains(norm) ? norm : raw;
        if (_cc != null && _cc.enabled)
            _cc.enabled = false;
        Vector3 from = transform.position;
        transform.position = worldPos;
        if (_cc != null)
            _cc.enabled = true;
        SnapToGround();
        _home = transform.position;
        if (!StreamingSurvivalResourceGuard.IsResourceAction(Action)
            || string.IsNullOrEmpty(CurrentTargetId))
        {
            _target = transform.position;
        }
        SetAnimSpeed(0f);
        LastTeleportReason = useReason;
        LastJump = Horiz(from, transform.position);
        LastRecoveryStatus = $"TELEPORT: {useReason}";
        _hasPrevSample = false;
        Debug.Log(
            $"[SSPos] controlled_teleport user={Username} reason={useReason} " +
            $"from=({from.x:F2},{from.z:F2}) to=({transform.position.x:F2},{transform.position.z:F2})");
        var rec = StreamingSurvivalTrajectoryRecorder.Instance;
        if (rec != null)
        {
            rec.EmitEvent(Username, "controlled_teleport", this);
            rec.EmitEvent(Username, useReason, this);
        }
        return true;
    }

    /// <summary>
    /// Normal gameplay stuck recovery: stop current action, overlay hint. Never teleport.
    /// Remaining queued plan steps are preserved (advance to next).
    /// </summary>
    void MarkStuckAndIdle(string context = "")
    {
        var rec = StreamingSurvivalTrajectoryRecorder.Instance;
        rec?.EmitEvent(Username, "stuck_detected", this);
        rec?.EmitEvent(Username, "character_stuck", this);
        LastRecoveryStatus = "застрял\n#do перезагрузи персонажа";
        SetAnimSpeed(0f);
        _homeStuckTries = 0;
        _homeStuckSince = -1f;
        _waterStuckTries = 0;
        _waterStuckSince = -1f;
        Debug.LogWarning(
            $"[SSPos] character_stuck user={Username} action={Action} ctx={context} " +
            $"pos=({transform.position.x:F2},{transform.position.z:F2}) queue={_queue.Count} — no teleport");
        // Movement-only may abandon the stuck step and continue the plan (viewer intent).
        // Resource/other stuck: idle in place and wait for #do перезагрузи персонажа.
        if (IsMovementOnlyAction(Action) && _queue.Count > 0)
        {
            AdvanceQueueOrIdle();
            LastRecoveryStatus = "застрял\n#do перезагрузи персонажа";
            RefreshLabel();
            return;
        }
        ApplyStep("idle", "застрял — #do перезагрузи персонажа", 1);
    }

    /// <summary>Legacy entry — routes through ControlledTeleport.</summary>
    public void TeleportTo(Vector3 worldPos, string reason)
    {
        ControlledTeleport(worldPos, reason);
    }

    bool TrySafeNudge(Vector3 preferDir, float maxDist = MaxStuckNudge, bool waterEscape = false)
    {
        preferDir.y = 0f;
        if (preferDir.sqrMagnitude < 0.0001f)
            preferDir = transform.forward;
        preferDir.Normalize();
        Vector3 origin = transform.position;
        // Water: prefer east/north escapes. Never accept south/west into the fence trap.
        Vector3[] candidates = waterEscape
            ? (origin.z < 18.0f
                ? new[]
                {
                    // Far mid corridor: north only — avoid the x≈12.6 dead pocket.
                    Vector3.forward * maxDist,
                    new Vector3(0.15f, 0f, 1f).normalized * maxDist,
                    new Vector3(-0.15f, 0f, 1f).normalized * maxDist,
                    preferDir.z >= 0f ? preferDir * maxDist : Vector3.forward * maxDist,
                }
                : origin.z < 19.6f
                ? new[]
                {
                    // Gate: prefer east-north around the fence (west corridor is a dead pocket).
                    new Vector3(0.9f, 0f, 0.7f).normalized * maxDist,
                    new Vector3(0.75f, 0f, 1f).normalized * maxDist,
                    new Vector3(0.55f, 0f, 1f).normalized * maxDist,
                    Vector3.forward * maxDist,
                    preferDir * maxDist,
                }
                : origin.x > 11.4f
                ? new[]
                {
                    new Vector3(-0.7f, 0f, 1f).normalized * maxDist,
                    new Vector3(-0.4f, 0f, 1f).normalized * maxDist,
                    Vector3.forward * maxDist,
                    preferDir * maxDist,
                }
                : new[]
                {
                    Vector3.forward * maxDist,
                    new Vector3(0.25f, 0f, 1f).normalized * maxDist,
                    new Vector3(0.55f, 0f, 0.85f).normalized * maxDist,
                    preferDir * maxDist,
                })
            : new[]
            {
                preferDir * maxDist,
                -preferDir * maxDist,
                new Vector3(-preferDir.z, 0f, preferDir.x) * maxDist,
                new Vector3(preferDir.z, 0f, -preferDir.x) * maxDist,
                preferDir * (maxDist * 0.6f),
            };
        foreach (var off in candidates)
        {
            Vector3 dest = origin + off;
            dest.y = origin.y;
            if (waterEscape)
            {
                // West of fence / sheep yard: MUST be allowed to push EAST into the gap.
                // Old rules rejected dest.x > origin.x+0.35 and froze agents at ~(13,14).
                if (origin.x < 15.5f && origin.z < 20.0f)
                {
                    if (dest.x < 9.5f) continue;
                    if (dest.z < 13.0f || dest.z > 17.5f) continue;
                    // Prefer east/north toward GoalWater1 mouth — never force west.
                    if (dest.x < origin.x - 0.15f) continue;
                }
                else if (origin.z < 19.6f)
                {
                    if (dest.x > 16.8f) continue;
                    if (dest.x < 10.2f) continue;
                    if (dest.z < origin.z - 0.15f) continue;
                }
                else
                {
                    bool allowWest = origin.x > 11.4f;
                    if (!allowWest && dest.x < origin.x - 0.05f) continue;
                    if (allowWest && dest.x < 10.3f) continue;
                    if (dest.x < 9.5f) continue;
                    if (dest.z < origin.z - 0.05f) continue;
                }
            }
            if (Physics.Raycast(dest + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 8f))
                dest.y = hit.point.y;
            // Small CharacterController step — not a teleport.
            Vector3 delta = dest - transform.position;
            delta.y = 0f;
            if (delta.sqrMagnitude < 0.01f) continue;
            Vector3 before = transform.position;
            if (_cc != null && _cc.enabled)
                _cc.Move(delta + Vector3.down * 0.2f);
            else
                transform.position = dest;
            Vector3 after = transform.position;
            Vector3 moved = after - origin;
            moved.y = 0f;
            // Water escapes west of fence: must gain east toward the gap.
            bool rejectWater = false;
            if (waterEscape && origin.x < 15.5f && origin.z < 20.0f)
                rejectWater = moved.x < 0.25f && moved.sqrMagnitude < 0.2f;
            else if (waterEscape && origin.z < 19.6f)
                rejectWater = moved.z < -0.05f || transform.position.x > 16.8f;
            else if (waterEscape && origin.x > 11.4f)
                rejectWater = moved.z < -0.05f || (moved.z < 0.12f && moved.x > 0.05f);
            else if (waterEscape)
                rejectWater = moved.z < -0.05f || ((moved.x < 0.15f && moved.z < 0.15f) || moved.x < -0.05f);
            if (rejectWater)
            {
                Vector3 back = before - after;
                back.y = 0f;
                if (_cc != null && _cc.enabled)
                    _cc.Move(back);
                else
                    transform.position = before;
                continue;
            }
            if (Horiz(origin, transform.position) > 0.2f)
            {
                StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(
                    Username, "recovery_attempt", this);
                Debug.Log(
                    $"[SSPos] safe_nudge user={Username} d={Horiz(origin, transform.position):F2}");
                return true;
            }
        }
        return false;
    }

    void TrackMotionSample()
    {
        Vector3 p = transform.position;
        float now = Time.time;
        if (_hasPrevSample)
        {
            float dt = Mathf.Max(0.0001f, now - _prevSampleTime);
            float jump = Horiz(_prevSamplePos, p);
            LastJump = jump;
            LastSpeed = jump / dt;
        }
        _prevSamplePos = p;
        _prevSampleTime = now;
        _hasPrevSample = true;
    }

    /// <summary>Stop navigation so negative tests cannot walk into a real credit.</summary>
    public void FreezeForNegativeTest()
    {
        _phase = Phase.IdleStand;
        SetAnimSpeed(0f);
        ReachedResourceFlag = false;
        WorkStartedNearTarget = false;
        if (StreamingSurvivalResourceGuard.IsResourceAction(Action))
            CollectState = StreamingSurvivalResourceGuard.CollectState.Denied;
        RefreshLabel();
    }

    public Vector3 WorldPos => transform.position;
    public Vector3 CurrentTarget => _target;
    public string CurrentTargetType { get; private set; } = "";
    public string CurrentTargetId { get; private set; } = "";
    public Vector3 ResourceAnchor => _hasResourceAnchor ? _resourceAnchor : _target;
    bool _hasResourceAnchor;
    Vector3 _resourceAnchor;
    public StreamingSurvivalResourceGuard.CollectState CollectState { get; private set; }
        = StreamingSurvivalResourceGuard.CollectState.None;
    public bool ReachedResourceFlag { get; private set; }
    public bool WorkStartedNearTarget { get; private set; }
    public float LastGuardDistance { get; private set; } = -1f;
    public bool CanCompleteResourceNow { get; private set; }

    Vector3 _interactionPos;
    bool _hasInteractionPos;

    void SetTargetMeta(string type, string id, Vector3 interactionPos, Vector3? objectAnchor = null)
    {
        CurrentTargetType = type ?? "";
        CurrentTargetId = id ?? "";
        _target = interactionPos;
        _interactionPos = interactionPos;
        _hasInteractionPos = true;
        _resourceAnchor = objectAnchor ?? interactionPos;
        _hasResourceAnchor = true;
    }

    public float DistanceToCurrentTarget()
    {
        // For water corridor, steer target (_target) may be a waypoint; report distance
        // to the stable interaction stand so checkers don't see false teleports.
        Vector3 t = _target;
        if (_hasInteractionPos
            && (CurrentTargetType == "water_source"
                || CurrentTargetType == "tree"
                || CurrentTargetType == "home_interaction_point"
                || CurrentTargetType == "campfire_slot"))
            t = _interactionPos;
        float dx = transform.position.x - t.x;
        float dz = transform.position.z - t.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Test-only: try to complete work while far from target (must NOT credit).</summary>
    public bool TryForceCompleteWorkFarForTest(out string reason)
    {
        reason = "";
        var ctrl = StreamingSurvivalController.Instance;
        if (ctrl == null)
        {
            reason = "no_controller";
            return false;
        }
        // Leave character where they are; do not teleport.
        ReachedResourceFlag = false;
        WorkStartedNearTarget = false;
        CollectState = StreamingSurvivalResourceGuard.CollectState.MovingToResource;
        OnWorkDone(ctrl);
        reason = "forced_far_complete_attempted";
        return true;
    }

    struct QueuedStep
    {
        public string Action;
        public string ActionName;
        public int Amount;
    }

    public void Setup(string username, string action, string actionName)
    {
        Username = string.IsNullOrEmpty(username) ? "viewer" : username;
        _cc = GetComponent<CharacterController>();
        _anim = GetComponentInChildren<Animator>(true);
        PrepareAnimator();
        SnapToGround();
        _home = transform.position;
        EnsureLabel();
        SetAction(action, actionName, 1, null);
    }

    void PrepareAnimator()
    {
        if (_anim == null)
            _anim = GetComponentInChildren<Animator>(true);
        if (_anim == null) return;
        _anim.applyRootMotion = false;
        _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        _anim.SetFloat("Speed", 0f);
    }

    void SnapToGround()
    {
        StreamingSurvivalTrajectoryRecorder.SampleGround(
            this, out float groundY, out _, out _, out _, out bool grounded, out _);
        if (!grounded) return;
        Vector3 p = transform.position;
        float skin = 0.08f;
        var cc = _cc != null ? _cc : GetComponent<CharacterController>();
        if (cc != null)
            skin = Mathf.Max(0.02f, cc.skinWidth);
        p.y = groundY + skin;
        if (cc != null && cc.enabled)
        {
            cc.enabled = false;
            transform.position = p;
            cc.enabled = true;
        }
        else
            transform.position = p;
    }

    /// <summary>queueCsv: "collect_water:10;collect_wood:10" или null.</summary>
    public void SetAction(string action, string actionName, int amount = 1, string queueCsv = null)
    {
        _queue.Clear();
        if (!string.IsNullOrEmpty(queueCsv))
        {
            foreach (var part in queueCsv.Split(';'))
            {
                var p = part.Trim();
                if (string.IsNullOrEmpty(p)) continue;
                var bits = p.Split(':');
                string a = bits[0].Trim().ToLowerInvariant();
                int n = 1;
                if (bits.Length > 1) int.TryParse(bits[1].Trim(), out n);
                if (n < 1) n = 1;
                _queue.Enqueue(new QueuedStep
                {
                    Action = a,
                    ActionName = NameFor(a),
                    Amount = n,
                });
            }
        }

        if (_queue.Count > 0)
        {
            var first = _queue.Dequeue();
            ApplyStep(first.Action, first.ActionName, first.Amount);
            return;
        }

        ApplyStep(
            string.IsNullOrEmpty(action) ? "idle" : action.ToLowerInvariant(),
            string.IsNullOrEmpty(actionName) ? action : actionName,
            amount < 1 ? 1 : amount);
    }

    static string NameFor(string action)
    {
        switch (action)
        {
            case "collect_water": return "Добывает воду";
            case "collect_wood": return "Рубит дерево";
            case "collect_stone": return "Добывает камень";
            case "collect_food": return "Собирает еду";
            case "kill_sheep": return "Убивает овечек";
            case "build_campfire": return "Ставит костёр";
            case "go_home":
            case "go_to_base": return "Идёт к дому";
            case "go_to_water": return "Идёт к воде";
            case "go_to_campfire": return "Идёт к костру";
            case "go_to_tree": return "Идёт к дереву";
            case "go_to_sheep": return "Идёт к овцам";
            case "manual_respawn":
            case "respawn_character": return "Перезагрузка";
            case "walk_circle":
            case "circle": return "Ходит кругом";
            case "walk_forward": return "Идёт вперёд";
            case "walk_back": return "Идёт назад";
            case "patrol": return "Патрулирует";
            case "spin_in_place": return "Крутится";
            case "idle": return "Ждёт у базы";
            case "attack_user": return "Атакует игрока";
            default: return action;
        }
    }

    static bool IsMovementOnlyAction(string action)
    {
        switch ((action ?? "").ToLowerInvariant())
        {
            case "go_home":
            case "go_to_base":
            case "go_to_water":
            case "go_to_campfire":
            case "go_to_tree":
            case "go_to_sheep":
                return true;
            default:
                return false;
        }
    }

    void ApplyStep(string action, string actionName, int amount)
    {
        Action = string.IsNullOrEmpty(action) ? "idle" : action.ToLowerInvariant();
        if (Action == "circle") Action = "walk_circle";
        if (Action == "go_to_base") Action = "go_home";
        if (Action == "respawn_character") Action = "manual_respawn";
        ActionName = string.IsNullOrEmpty(actionName) ? NameFor(Action) : actionName;
        _needAmount = amount < 1 ? 1 : amount;
        _doneAmount = 0;
        ReachedResourceFlag = false;
        WorkStartedNearTarget = false;
        CollectState = StreamingSurvivalResourceGuard.IsResourceAction(Action)
            ? StreamingSurvivalResourceGuard.CollectState.MovingToResource
            : StreamingSurvivalResourceGuard.CollectState.None;
        _allowWander = ActionName.IndexOf("Гуля", System.StringComparison.OrdinalIgnoreCase) >= 0
            || ActionName.IndexOf("гуля", System.StringComparison.OrdinalIgnoreCase) >= 0;
        if (_needAmount > 1)
            ActionName = $"{ActionName} ({_doneAmount}/{_needAmount})";
        RefreshLabel();
        Vector3 p = transform.position;
        Debug.Log(
            $"[SSPos] set_action user={Username} action={Action} amount={_needAmount} " +
            $"pos=({p.x:F2},{p.y:F2},{p.z:F2})");
        StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(Username, "started_action", this);
        ResumeLastAction();
    }

    void AdvanceQueueOrIdle()
    {
        if (_queue.Count > 0)
        {
            var next = _queue.Dequeue();
            ApplyStep(next.Action, next.ActionName, next.Amount);
            return;
        }
        ApplyStep("idle", "Ждёт у базы", 1);
    }

    float _phaseEnteredAt;

    Vector3 _lockedWorkTarget;
    bool _hasLockedWorkTarget;

    // water route: spawn(0) → mid → GoalWater1
    readonly System.Collections.Generic.List<Vector3> _waterRoute = new System.Collections.Generic.List<Vector3>(8);
    int _waterWpIndex;
    int _waterDetourSign = 1;
    float _waterStuckSince = -1f;
    Vector3 _waterStuckPos;
    int _waterStuckTries;
    float _homeStuckSince = -1f;
    Vector3 _homeStuckPos;
    int _homeStuckTries;

    // move demos
    string _demoKind;
    Vector3 _demoCenter;
    float _demoAngle;
    float _demoUntil;


    public void ResumeLastAction()
    {
        _sheepVictim = null;
        _treeVictim = null;
        _waterPath = null;
        _phaseEnteredAt = Time.time;
        _hasLockedWorkTarget = false;
        _waterStuckSince = -1f;
        _homeStuckSince = -1f;
        _homeStuckTries = 0;

        if (Action == "idle")
        {
            _phase = Phase.IdleStand;
            _allowWander = false;
            _retargetAt = float.MaxValue;
            _target = transform.position;
            SetTargetMeta("home", "idle_stand", transform.position);
            SetAnimSpeed(0f);
            return;
        }

        if (Action == "manual_respawn")
        {
            var c = StreamingSurvivalController.Instance;
            Vector3 spawn = SpawnPos(c);
            ControlledTeleport(spawn, "manual_respawn_command");
            LastRecoveryStatus = "";
            ApplyStep("idle", "Ждёт у базы", 1);
            return;
        }

        if (Action == "build_campfire" || Action == "go_to_campfire")
        {
            Vector3 fire = CampfirePos(StreamingSurvivalController.Instance);
            _phase = Phase.GoHouse;
            SetTargetMeta("campfire_slot", "campfire_slot_0", fire);
            _target = fire;
            Debug.Log($"[SSPos] campfire_goto user={Username} action={Action} fire=({fire.x:F2},{fire.z:F2})");
            return;
        }

        if (Action == "go_home")
        {
            Vector3 house = HousePos(StreamingSurvivalController.Instance);
            _phase = Phase.GoHouse;
            SetTargetMeta("home_interaction_point", "home_0", house);
            Debug.Log($"[SSPos] go_home_target user={Username} house=({house.x:F2},{house.z:F2}) from=({transform.position.x:F2},{transform.position.z:F2})");
            return;
        }

        if (Action == "walk_circle" || Action == "walk_forward"
            || Action == "walk_back" || Action == "spin_in_place" || Action == "patrol")
        {
            BeginMoveDemo(Action);
            return;
        }

        if (Action == "collect_water" || Action == "go_to_water")
        {
            // Strict GoalWater1 → GoalWater2 → GoalWater3 → pond corridor.
            _waterPath = null; // do not let training WaterGoalPath override SS corridor
            BuildWaterRoute();
            _waterWpIndex = 0;
            _waterStuckTries = 0;
            _waterStuckSince = -1f;
            _phase = Phase.GoWaterWp;
            Vector3 wp = _waterRoute.Count > 0 ? _waterRoute[0] : transform.position;
            Vector3 lake = _waterRoute.Count > 0
                ? _waterRoute[_waterRoute.Count - 1]
                : (FindWater() ?? wp);
            var regW = StreamingSurvivalWorldRegistry.Instance?.GetNearestWater(transform.position);
            if (regW != null && regW.Position.z >= 28f)
                lake = ApproachableWaterStand(regW);
            // While west of fence, first target is always the gap — not lake.
            if (WestOfWaterFence(transform.position))
                wp = new Vector3(15.45f, transform.position.y, 15.35f);
            SetTargetMeta(
                "water_source",
                regW != null ? regW.Id : "water_0",
                WestOfWaterFence(transform.position) ? wp : lake,
                regW != null ? regW.Position : (Vector3?)null);
            _target = wp;
            _phaseEnteredAt = Time.time;
            Debug.Log(
                $"[SSPos] water_route user={Username} action={Action} " +
                $"n={_waterRoute.Count} " +
                $"wp0=({_target.x:F2},{_target.z:F2}) lake=({lake.x:F2},{lake.z:F2})");
            return;
        }

        if (Action == "go_to_tree" || Action == "go_to_sheep")
        {
            _phase = Phase.GoTarget;
            _hasLockedWorkTarget = false;
            _target = ResolveMovementTarget();
            return;
        }

        _phase = Phase.GoTarget;
        _target = ResolveWorkTarget();
    }

    void EnsureLabel()
    {
        if (_label != null) return;
        var go = new GameObject("NameLabel");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, 2.15f, 0f);
        _labelTf = go.transform;
        _label = go.AddComponent<TextMesh>();
        _label.characterSize = 0.048f;
        _label.fontSize = 48;
        _label.anchor = TextAnchor.LowerCenter;
        _label.alignment = TextAlignment.Center;
        _label.color = new Color(1f, 0.95f, 0.55f);
        _label.fontStyle = FontStyle.Bold;
    }

    void RefreshLabel()
    {
        EnsureLabel();
        if (_label == null) return;
        float dist = DistanceToCurrentTarget();
        float radius = StreamingSurvivalResourceGuard.RadiusForAction(Action);
        bool inside = dist <= radius && radius > 0f;
        if (StreamingSurvivalResourceGuard.IsResourceAction(Action))
        {
            var g = StreamingSurvivalResourceGuard.CanComplete(
                Action, CurrentTargetType, CurrentTargetId, transform.position, _target,
                ReachedResourceFlag || inside, WorkStartedNearTarget || (_phase == Phase.Work && inside),
                _hasResourceAnchor ? _resourceAnchor : (Vector3?)null);
            CanCompleteResourceNow = g.Ok;
            LastGuardDistance = g.Distance >= 0f ? g.Distance : dist;
        }
        else
        {
            CanCompleteResourceNow = false;
            LastGuardDistance = dist;
        }
        // Stream overlay: nickname only (no debug Action/State/TELEPORT dump).
        _label.text = string.IsNullOrEmpty(Username) ? "viewer" : Username;
    }

    void Update()
    {
        var ctrl = StreamingSurvivalController.Instance;
        if (ctrl == null) return;
        if (_anim == null) PrepareAnimator();
        TrackMotionSample();

        if (Time.time >= _logPosAt)
        {
            _logPosAt = Time.time + LogPosInterval;
            Vector3 p = transform.position;
            Vector3 t = _target;
            Debug.Log(
                $"[SSPos] tick user={Username} pos=({p.x:F2},{p.y:F2},{p.z:F2}) " +
                $"phase={_phase} action={Action} target=({t.x:F2},{t.y:F2},{t.z:F2}) " +
                $"speed={LastSpeed:F1} jump={LastJump:F1}");
        }

        switch (_phase)
        {
            case Phase.IdleStand:
                SetAnimSpeed(0f);
                if (Action == "build_campfire")
                    TryBuildCampfire(ctrl);
                break;

            case Phase.IdleWander:
                DoIdleWander(ctrl);
                break;

            case Phase.GoHomeFirst:
                if (Arrived(_target, 1.6f) || Time.time - _phaseEnteredAt > 8f)
                {
                    _phase = Phase.GoTarget;
                    _hasLockedWorkTarget = false;
                    _target = ResolveWorkTarget();
                    _phaseEnteredAt = Time.time;
                }
                else
                    MoveToward(_target);
                break;

            case Phase.GoWaterWp:
                TickWaterRoute(ctrl);
                break;

            case Phase.GoTarget:
                if (!_hasLockedWorkTarget)
                {
                    _lockedWorkTarget = IsMovementOnlyAction(Action) && (Action == "go_to_tree" || Action == "go_to_sheep")
                        ? ResolveMovementTarget()
                        : ResolveWorkTarget();
                    _hasLockedWorkTarget = true;
                    _phaseEnteredAt = Time.time;
                    if (StreamingSurvivalResourceGuard.IsResourceAction(Action))
                        CollectState = StreamingSurvivalResourceGuard.CollectState.MovingToResource;
                }
                _target = _lockedWorkTarget;
                RefreshLabel();
                if (IsMovementOnlyAction(Action)
                    && (Action == "go_to_tree" || Action == "go_to_sheep")
                    && Arrived(_target, 2.0f))
                {
                    Debug.Log(
                        $"[SSPos] action_done user={Username} action={Action} ok=1 " +
                        $"pos=({transform.position.x:F2},{transform.position.z:F2})");
                    StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(Username, "movement_arrived", this);
                    AdvanceQueueOrIdle();
                    break;
                }
                if (TryBeginWorkAtResource(ctrl))
                    break;
                // Near stand but blocked — repath / nudge, never snap-teleport to target.
                float dStand = DistanceToCurrentTarget();
                if (StreamingSurvivalResourceGuard.IsResourceAction(Action)
                    && Action != "collect_water"
                    && dStand <= 2.2f
                    && Time.time - _phaseEnteredAt > 2.5f)
                {
                    Vector3 toStand = _target - transform.position;
                    if (!TrySafeNudge(toStand, 1.2f))
                    {
                        _hasLockedWorkTarget = false;
                        _phaseEnteredAt = Time.time;
                    }
                    if (TryBeginWorkAtResource(ctrl))
                        break;
                }
                if (StreamingSurvivalResourceGuard.IsResourceAction(Action)
                    && Action != "collect_water"
                    && Time.time - _phaseEnteredAt > 8f)
                {
                    _hasLockedWorkTarget = false;
                    _phaseEnteredAt = Time.time;
                    break;
                }
                MoveToward(_target);
                break;

            case Phase.Work:
                SetAnimSpeed(0f);
                RefreshLabel();
                // Left interaction radius mid-work → cancel
                if (StreamingSurvivalResourceGuard.IsResourceAction(Action))
                {
                    float rad = StreamingSurvivalResourceGuard.RadiusForAction(Action);
                    float d = DistanceToCurrentTarget();
                    if (rad > 0f && d > rad)
                    {
                        Debug.LogWarning(
                            $"[ResourceGuard] work_cancelled_too_far user={Username} action={Action} d={d:F1}");
                        StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(
                            Username, "work_cancelled_too_far", this);
                        CollectState = StreamingSurvivalResourceGuard.CollectState.MovingToResource;
                        WorkStartedNearTarget = false;
                        ReachedResourceFlag = false;
                        _phase = Action == "collect_water" ? Phase.GoWaterWp : Phase.GoTarget;
                        _phaseEnteredAt = Time.time;
                        _hasLockedWorkTarget = false;
                        break;
                    }
                }
                if (Time.time >= _workUntil)
                {
                    OnWorkDone(ctrl);
                    _hasLockedWorkTarget = false;
                    AfterDeliver(ctrl);
                }
                break;

            case Phase.GoBase:
                if (Arrived(_target, 2.4f) || Time.time - _phaseEnteredAt > 6f)
                {
                    // ресурс уже начислен в OnWorkDone — здесь только цикл задачи
                    AfterDeliver(ctrl);
                }
                else
                    MoveToward(_target);
                break;

            case Phase.GoHouse:
            {
                bool toFire = Action == "build_campfire" || Action == "go_to_campfire";
                Vector3 house = toFire ? CampfirePos(ctrl) : HousePos(ctrl);
                _target = house;
                SetTargetMeta(
                    toFire ? "campfire_slot" : "home_interaction_point",
                    toFire ? "campfire_slot_0" : "home_0",
                    house);
                float distHome = Horiz(transform.position, house);
                Vector3 pNow = transform.position;
                if (_homeStuckSince < 0f)
                {
                    _homeStuckSince = Time.time;
                    _homeStuckPos = pNow;
                }
                else if (Horiz(pNow, _homeStuckPos) > 0.45f)
                {
                    _homeStuckSince = Time.time;
                    _homeStuckPos = pNow;
                }

                // From water/north: exit pond south/east first, then approach house.
                // Never teleport — only walk + small nudge, then idle if still stuck.
                if (Action == "go_home" && pNow.z > house.z + 3.5f)
                {
                    Vector3 exitPond = new Vector3(
                        Mathf.Clamp(Mathf.Max(pNow.x, 9.5f), 9.5f, 14f),
                        pNow.y,
                        Mathf.Min(pNow.z - 1.5f, 22.5f));
                    Vector3 eastBypass = new Vector3(14.0f, pNow.y, Mathf.Clamp(pNow.z, house.z + 3f, 23f));
                    Vector3 southGate = new Vector3(
                        Mathf.Clamp(Mathf.Max(pNow.x, 9.0f), 9.0f, 13f),
                        pNow.y,
                        house.z + 2.2f);

                    Vector3 via = pNow.z > 23.0f ? exitPond
                        : (pNow.x < 12.5f && pNow.z > house.z + 5f ? eastBypass : southGate);
                    _target = via;
                    if (!Arrived(via, 1.8f))
                    {
                        if (Time.time - _homeStuckSince > 3.5f)
                        {
                            StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(
                                Username, "stuck_detected", this);
                            if (_homeStuckTries < 3 && TrySafeNudge(via - pNow, MaxStuckNudge))
                            {
                                _homeStuckTries++;
                                _homeStuckSince = Time.time;
                                _homeStuckPos = transform.position;
                                break;
                            }
                            if (_homeStuckTries >= 3 || Time.time - _homeStuckSince > 10f)
                            {
                                MarkStuckAndIdle("go_home_corridor");
                                break;
                            }
                            // Recompute: shift further east then retry.
                            _target = eastBypass;
                        }
                        MoveToward(_target);
                        break;
                    }
                }

                bool noProgress = Action == "go_home"
                    && Time.time - _homeStuckSince > 5f
                    && distHome > 3.5f;
                if (noProgress)
                {
                    StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(
                        Username, "stuck_detected", this);
                    Vector3 toHome = house - transform.position;
                    if (_homeStuckTries < 3 && TrySafeNudge(toHome, MaxStuckNudge))
                    {
                        _homeStuckTries++;
                        _homeStuckSince = Time.time;
                        _homeStuckPos = transform.position;
                        _target = house;
                        break;
                    }
                    MarkStuckAndIdle("go_home_no_progress");
                    break;
                }
                if (Arrived(house, 2.8f))
                {
                    Debug.Log(
                        $"[SSPos] action_done user={Username} action={Action} ok=1 " +
                        $"pos=({transform.position.x:F2},{transform.position.z:F2})");
                    _homeStuckTries = 0;
                    _homeStuckSince = -1f;
                    if (Action == "build_campfire")
                    {
                        _phase = Phase.IdleStand;
                        SetAnimSpeed(0f);
                    }
                    else if (Action == "go_home" || Action == "go_to_campfire")
                        AdvanceQueueOrIdle();
                    else
                        ApplyStep("idle", "У дома", 1);
                }
                else if (Time.time - _phaseEnteredAt > 50f)
                {
                    StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(
                        Username, "stuck_detected", this);
                    MarkStuckAndIdle("go_home_timeout");
                }
                else
                    MoveToward(house);
                break;
            }

            case Phase.MoveDemo:
                TickMoveDemo();
                break;
        }
    }

    bool TrySetWaterWaypoint()
    {
        if (_waterPath == null)
            _waterPath = WaterGoalPath.Get(transform);
        if (_waterPath == null)
            return false;
        if (_waterPath.TryGetFollowerWaypoint(transform, 2.6f, out Vector3 wp, centerOnly: true))
        {
            _target = wp;
            return true;
        }
        return false;
    }

    float ArriveDistForAction()
    {
        float r = StreamingSurvivalResourceGuard.RadiusForAction(Action);
        // Trees/sheep have solid colliders — stand is ~1.35m from trunk center.
        // Must allow at least standDist (+slack) or work never starts (wood delta stays 0).
        if (Action == "collect_wood" || Action == "go_to_tree"
            || Action == "collect_food" || Action == "kill_sheep" || Action == "go_to_sheep")
            return Mathf.Max(r, 1.55f);
        if (r > 0f) return r;
        return 1.4f;
    }

    bool TryBeginWorkAtResource(StreamingSurvivalController ctrl)
    {
        if (!StreamingSurvivalResourceGuard.IsResourceAction(Action))
            return false;
        if (Action == "collect_stone")
            return false;
        float arrive = ArriveDistForAction();
        float dist = DistanceToCurrentTarget();
        if (dist > arrive)
            return false;
        // Water credit only at the pond approach (not forest/gate z≈19–21).
        if (Action == "collect_water" && transform.position.z < 24.5f)
            return false;

        ReachedResourceFlag = true;
        WorkStartedNearTarget = true;
        CollectState = StreamingSurvivalResourceGuard.CollectState.ReachedResource;
        StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(Username, "reached_resource", this);
        _phase = Phase.Work;
        CollectState = StreamingSurvivalResourceGuard.CollectState.WorkingAtResource;
        _phaseEnteredAt = Time.time;
        _workUntil = Time.time + ctrl.WorkDuration;
        SetAnimSpeed(0f);
        TriggerDo();
        StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(Username, "work_started", this);
        RefreshLabel();
        Debug.Log(
            $"[SSPos] work_started user={Username} action={Action} target={CurrentTargetId} d={dist:F2}");
        return true;
    }

    void AfterDeliver(StreamingSurvivalController ctrl)
    {
        if (CollectState == StreamingSurvivalResourceGuard.CollectState.Denied)
        {
            if (StreamingSurvivalResourceGuard.IsResourceAction(Action) && Action != "collect_stone")
            {
                _phase = Action == "collect_water" ? Phase.GoWaterWp : Phase.GoTarget;
                _hasLockedWorkTarget = false;
                CollectState = StreamingSurvivalResourceGuard.CollectState.MovingToResource;
                ReachedResourceFlag = false;
                WorkStartedNearTarget = false;
                return;
            }
        }

        _doneAmount++;
        if (_needAmount > 1)
        {
            ActionName = $"{NameFor(Action)} ({_doneAmount}/{_needAmount})";
            RefreshLabel();
        }

        if (_doneAmount >= _needAmount)
        {
            if (_queue.Count > 0)
            {
                AdvanceQueueOrIdle();
                return;
            }
            // Добыча/охота/костёр — крутим пока зритель не сменит #do
            if (Action == "collect_water" || Action == "collect_wood"
                || Action == "collect_food" || Action == "kill_sheep"
                || Action == "build_campfire")
            {
                _doneAmount = 0;
                if (_needAmount < 1) _needAmount = 1;
                ActionName = NameFor(Action);
                ReachedResourceFlag = false;
                WorkStartedNearTarget = false;
                CollectState = StreamingSurvivalResourceGuard.IsResourceAction(Action)
                    ? StreamingSurvivalResourceGuard.CollectState.MovingToResource
                    : StreamingSurvivalResourceGuard.CollectState.None;
                RefreshLabel();
                ResumeLastAction();
                return;
            }
            AdvanceQueueOrIdle();
            return;
        }

        ReachedResourceFlag = false;
        WorkStartedNearTarget = false;
        CollectState = StreamingSurvivalResourceGuard.CollectState.MovingToResource;
        if (Action == "collect_water")
        {
            // Stay at the pond for remaining count — never rebuild spawn→corridor route.
            _waterRoute.Clear();
            var regW = StreamingSurvivalWorldRegistry.Instance?.GetNearestWater(transform.position);
            if (regW != null && regW.Position.z >= 28f)
            {
                Vector3 stand = ApproachableWaterStand(regW);
                SetTargetMeta("water_source", regW.Id, stand, regW.Position);
                _lockedWorkTarget = stand;
                _hasLockedWorkTarget = true;
                _waterRoute.Add(stand);
                _target = stand;
            }
            _waterWpIndex = 0;
            _phase = Phase.GoWaterWp;
            _phaseEnteredAt = Time.time;
            _waterStuckSince = -1f;
            _waterStuckTries = 0;
            if (TryBeginWorkAtResource(ctrl))
                return;
            return;
        }
        _phase = Phase.GoTarget;
        _hasLockedWorkTarget = false;
        _lockedWorkTarget = ResolveWorkTarget();
        _hasLockedWorkTarget = true;
        _target = _lockedWorkTarget;
        _phaseEnteredAt = Time.time;
    }

    void LateUpdate()
    {
        if (_labelTf == null) return;
        var cam = BillboardIconCamera.Resolve(_labelTf.position, null);
        if (cam != null)
            _labelTf.rotation = cam.transform.rotation;
    }

    void DoIdleWander(StreamingSurvivalController ctrl)
    {
        if (Time.time >= _retargetAt)
        {
            _retargetAt = Time.time + Random.Range(2f, 4f);
            Vector3 c = BasePos(ctrl);
            _target = c + new Vector3(Random.Range(-2.5f, 2.5f), 0f, Random.Range(-2.5f, 2.5f));
        }
        if (!Arrived(_target, 0.6f))
            MoveToward(_target);
        else
            SetAnimSpeed(0f);
    }

    float _nextHeatPulseAt;

    void TryBuildCampfire(StreamingSurvivalController ctrl)
    {
        Vector3 baseP = HousePos(ctrl);
        if (Horiz(transform.position, baseP) > 3.2f)
        {
            _phase = Phase.GoHouse;
            _target = baseP;
            return;
        }

        var fire = GameObject.Find("SS_Campfire");
        if (fire == null)
        {
            // ждём пока команда накопит дерево
            if (ctrl.Wood < 3) return;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "SS_Campfire";
            // у двери южнее дома — рядом с каменной плитой
            go.transform.position = baseP + new Vector3(-0.9f, 0.25f, -0.15f);
            go.transform.localScale = new Vector3(0.8f, 0.25f, 0.8f);
            var r = go.GetComponent<Renderer>();
            if (r != null) r.material.color = new Color(1f, 0.35f, 0.1f);
            ctrl.AddResource("heat", 2);
            ActionName = "Поддерживает костёр";
            RefreshLabel();
            _nextHeatPulseAt = Time.time + 1.5f;
            Debug.Log($"[SSPos] campfire_built user={Username} pos=({go.transform.position.x:F2},{go.transform.position.z:F2})");
            return;
        }

        // поддерживаем огонь → тепло без списания дерева (иначе не набрать wood+heat=10)
        if (Time.time >= _nextHeatPulseAt && ctrl.Heat < ctrl.GoalAmount)
        {
            _nextHeatPulseAt = Time.time + 1.5f;
            ctrl.AddResource("heat", 1);
            ActionName = $"Поддерживает костёр ({ctrl.Heat}/{ctrl.GoalAmount})";
            RefreshLabel();
        }
        SetAnimSpeed(0f);
    }

    void OnWorkDone(StreamingSurvivalController ctrl)
    {
        var guard = StreamingSurvivalResourceGuard.CanComplete(
            Action,
            CurrentTargetType,
            CurrentTargetId,
            transform.position,
            _target,
            ReachedResourceFlag,
            WorkStartedNearTarget,
            _hasResourceAnchor ? _resourceAnchor : (Vector3?)null);
        LastGuardDistance = guard.Distance;
        CanCompleteResourceNow = guard.Ok;

        if (!guard.Ok)
        {
            CollectState = StreamingSurvivalResourceGuard.CollectState.Denied;
            Debug.LogWarning(
                $"[ResourceGuard] DENIED user={Username} action={Action} reason={guard.Reason} " +
                $"distance={guard.Distance:F1} target={CurrentTargetId}");
            StreamingSurvivalTrajectoryRecorder.Instance?.EmitGuard(
                Username, "resource_guard_denied", this, guard);
            StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(
                Username, "invalid_resource_completion", this);
            RefreshLabel();
            return;
        }

        StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(Username, "work_finished", this);
        StreamingSurvivalTrajectoryRecorder.Instance?.EmitGuard(
            Username, "resource_guard_pass", this, guard);

        if (Action == "collect_wood")
            ChopTreeVictim();
        if (Action == "kill_sheep" || Action == "collect_food")
        {
            if (_sheepVictim != null)
            {
                Destroy(_sheepVictim.gameObject);
                _sheepVictim = null;
            }
        }
        Deliver(ctrl);
        CollectState = StreamingSurvivalResourceGuard.CollectState.ResourceAdded;
        StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(Username, "resource_added", this);
        RefreshLabel();
    }

    void ChopTreeVictim()
    {
        if (_treeVictim == null)
        {
            var spawner0 = TrainingEnvSpace.FindInPresentation<TreeSpawner>()
                ?? Object.FindFirstObjectByType<TreeSpawner>();
            if (spawner0 != null
                && spawner0.TryGetNearestAliveTree(transform.position, out GameObject near, out _))
            {
                float d = Horiz(transform.position, near.transform.position);
                if (d <= StreamingSurvivalResourceGuard.WoodInteractionRadius + 0.5f)
                    _treeVictim = near;
            }
        }
        if (_treeVictim == null) return;
        var spawner = TrainingEnvSpace.FindInPresentation<TreeSpawner>()
            ?? Object.FindFirstObjectByType<TreeSpawner>();
        spawner?.NotifyTreeChopped(_treeVictim);
        Debug.Log(
            $"[SSPos] chop user={Username} tree=({_treeVictim.transform.position.x:F2},{_treeVictim.transform.position.z:F2})");
        Destroy(_treeVictim);
        _treeVictim = null;
    }

    void Deliver(StreamingSurvivalController ctrl)
    {
        switch (Action)
        {
            case "collect_water":
                ctrl.AddResource("water");
                Debug.Log(
                    $"[SSPos] water_credited user={Username} pos=({transform.position.x:F2},{transform.position.z:F2}) target={CurrentTargetId}");
                break;
            case "collect_wood":
                ctrl.AddResource("wood");
                break;
            case "collect_stone":
                Debug.LogWarning($"[ResourceGuard] DENIED user={Username} action=collect_stone reason=stone_disabled");
                break;
            case "collect_food":
            case "kill_sheep":
                ctrl.AddResource("food");
                break;
        }
    }

    Vector3 ResolveWorkTarget()
    {
        var reg = StreamingSurvivalWorldRegistry.Instance;
        switch (Action)
        {
            case "collect_water":
            {
                var w = reg != null ? reg.GetNearestWater(transform.position) : null;
                if (w != null && w.Position.z >= 28f)
                {
                    Vector3 stand = ApproachableWaterStand(w);
                    SetTargetMeta("water_source", w.Id, stand, w.Position);
                    return stand;
                }
                var found = FindWater();
                SetTargetMeta("water_source", "water_goal", found ?? _home, found);
                return found ?? _home;
            }
            case "collect_wood":
            {
                var t = reg != null ? reg.GetNearestTree(transform.position) : null;
                if (t != null)
                {
                    Vector3 stand = ApproachStand(transform.position, t.Position, 1.35f);
                    SetTargetMeta("tree", t.Id, stand, t.Position);
                    return stand;
                }
                var tree = FindTree();
                if (tree.HasValue)
                {
                    Vector3 stand = ApproachStand(transform.position, tree.Value, 1.35f);
                    SetTargetMeta("tree", "tree_fallback", stand, tree.Value);
                    return stand;
                }
                SetTargetMeta("tree", "tree_fallback", _home, _home);
                return _home;
            }
            case "collect_stone":
            {
                var s = reg != null ? reg.GetNearestStone(transform.position) : null;
                if (s != null)
                {
                    SetTargetMeta("stone", s.Id, s.Position, s.Position);
                    return s.Position;
                }
                SetTargetMeta("stone", "stone_0", _home + new Vector3(4.5f, 0f, -2f));
                return _target;
            }
            case "collect_food":
            case "kill_sheep":
            {
                var sh = reg != null ? reg.GetNearestSheep(transform.position) : null;
                if (sh != null)
                {
                    Vector3 stand = ApproachStand(transform.position, sh.Position, 1.35f);
                    SetTargetMeta("sheep", sh.Id, stand, sh.Position);
                    return stand;
                }
                var sheep = FindSheep();
                if (sheep.HasValue)
                {
                    Vector3 stand = ApproachStand(transform.position, sheep.Value, 1.35f);
                    SetTargetMeta("sheep", "sheep_fallback", stand, sheep.Value);
                    return stand;
                }
                return _home;
            }
            default:
                return _home;
        }
    }

    Vector3 ResolveMovementTarget()
    {
        var reg = StreamingSurvivalWorldRegistry.Instance;
        switch (Action)
        {
            case "go_to_tree":
            {
                var t = reg != null ? reg.GetNearestTree(transform.position) : null;
                if (t != null)
                {
                    Vector3 stand = ApproachStand(transform.position, t.Position, 1.35f);
                    SetTargetMeta("tree", t.Id, stand, t.Position);
                    return stand;
                }
                var tree = FindTree();
                if (tree.HasValue)
                {
                    Vector3 stand = ApproachStand(transform.position, tree.Value, 1.35f);
                    SetTargetMeta("tree", "tree_fallback", stand, tree.Value);
                    return stand;
                }
                SetTargetMeta("tree", "tree_missing", _home, _home);
                return _home;
            }
            case "go_to_sheep":
            {
                var sh = reg != null ? reg.GetNearestSheep(transform.position) : null;
                if (sh != null)
                {
                    Vector3 stand = ApproachStand(transform.position, sh.Position, 1.35f);
                    SetTargetMeta("sheep", sh.Id, stand, sh.Position);
                    return stand;
                }
                var sheep = FindSheep();
                if (sheep.HasValue)
                {
                    Vector3 stand = ApproachStand(transform.position, sheep.Value, 1.35f);
                    SetTargetMeta("sheep", "sheep_fallback", stand, sheep.Value);
                    return stand;
                }
                return _home;
            }
            default:
                return ResolveWorkTarget();
        }
    }

    static Vector3 ApproachStand(Vector3 from, Vector3 anchor, float standDist)
    {
        Vector3 d = from - anchor;
        d.y = 0f;
        if (d.sqrMagnitude < 0.0001f)
            d = Vector3.back;
        return new Vector3(anchor.x, from.y, anchor.z) + d.normalized * standDist;
    }

    Vector3 BasePos(StreamingSurvivalController ctrl)
    {
        return HousePos(ctrl);
    }

    Vector3 CampfirePos(StreamingSurvivalController ctrl)
    {
        var slots = StreamingSurvivalWorldRegistry.Instance?.GetByType(
            StreamingSurvivalWorldRegistry.ObjType.CampfireSlot);
        if (slots != null && slots.Count > 0 && slots[0] != null)
            return slots[0].Position;
        if (ctrl != null)
            return ctrl.HouseWorld + new Vector3(-0.2f, 0f, -1.7f);
        return _home + new Vector3(-0.2f, 0f, -1.7f);
    }

    Vector3 HousePos(StreamingSurvivalController ctrl)
    {
        // Door / stone pad by the campfire — same as WorldRegistry home interaction.
        if (ctrl != null)
        {
            Vector3 h = ctrl.HouseWorld;
            var home = StreamingSurvivalWorldRegistry.Instance?.GetHome();
            if (home != null)
            {
                Vector3 ip = home.InteractionPosition;
                // Must stay by the cabin (reject legacy mid-yard ~4,15 lerp-to-spawn).
                if (Horiz(ip, h) <= 4.0f)
                    return ip;
            }
            // Fallback porch: south of cabin, slightly east of campfire offset.
            return h + new Vector3(0.7f, 0f, -1.65f);
        }
        return _home;
    }

    Vector3 SpawnPos(StreamingSurvivalController ctrl)
    {
        if (ctrl != null)
            return ctrl.FollowerSpawnWorld;
        return _home;
    }

    static bool IsWaterPathPoint(Vector3 p)
    {
        // поляна → GoalWater checkpoints → GoalWater3 / pond rim
        return p.z >= 10f && p.z <= 34.0f && p.x > -6f && p.x < 28f;
    }


    void BuildWaterRoute()
    {
        _waterRoute.Clear();
        var ctrl = StreamingSurvivalController.Instance;
        Vector3 from = transform.position;
        float y = from.y;
        Vector3 spawn = SpawnPos(ctrl);
        spawn = new Vector3(spawn.x, y, spawn.z);

        // Hard corridor: never walk the west face of the mid-yard fence (x≈14, z≈18–27).
        // Always: south to gap → GoalWater1 → GoalWater2 → GoalWater3/pond stand.
        Vector3 gw1 = new Vector3(16.5f, y, 16.4f);
        Vector3 gw2 = new Vector3(17.0f, y, 25.7f);
        Vector3 gw3 = new Vector3(21.1f, y, 32.8f);
        Vector3? g1 = FindGoalWaterByNames(new[] { "GoalWater1" });
        Vector3? g2 = FindGoalWaterByNames(new[] { "GoalWater2" });
        Vector3? g3 = FindGoalWaterByNames(new[] { "GoalWater3" });
        if (g1.HasValue) gw1 = new Vector3(g1.Value.x, y, g1.Value.z);
        if (g2.HasValue) gw2 = new Vector3(g2.Value.x, y, g2.Value.z);
        if (g3.HasValue) gw3 = new Vector3(g3.Value.x, y, g3.Value.z);

        var regW = StreamingSurvivalWorldRegistry.Instance?.GetNearestWater(from);
        Vector3 stand = (regW != null && regW.Position.z >= 28f)
            ? ApproachableWaterStand(regW)
            : new Vector3(19.1f, y, 29.3f);

        bool westOfFence = from.x < 15.5f && from.z < 28.0f;
        bool pastGap = from.x >= 15.5f && from.z >= 15.8f;

        if (westOfFence && !pastGap)
        {
            // Sparse corridor: one mouth point then GW1 — close pairs + Arrived(2m) froze agents.
            Vector3 mouth = new Vector3(15.2f, y, 15.3f);
            if (Horiz(from, mouth) > 1.2f)
                _waterRoute.Add(mouth);
            _waterRoute.Add(gw1);
        }
        else if (!pastGap)
        {
            _waterRoute.Add(new Vector3(15.2f, y, 15.3f));
            _waterRoute.Add(gw1);
        }
        else if (from.z < gw1.z + 1.0f || from.x < 16.0f)
        {
            _waterRoute.Add(gw1);
        }

        if (from.z < gw2.z - 1.0f || from.x < 15.5f)
            _waterRoute.Add(gw2);
        if (from.z < stand.z - 1.0f)
        {
            _waterRoute.Add(gw3);
            _waterRoute.Add(stand);
        }
        else
            _waterRoute.Add(stand);

        // Drop near-duplicates.
        for (int i = _waterRoute.Count - 1; i >= 1; i--)
        {
            if (Horiz(_waterRoute[i], _waterRoute[i - 1]) < 1.2f)
                _waterRoute.RemoveAt(i);
        }

        if (_waterRoute.Count == 0)
            _waterRoute.Add(stand);

        Debug.Log(
            $"[SSPos] water_route user={Username} corridor n={_waterRoute.Count} " +
            $"wp0=({_waterRoute[0].x:F1},{_waterRoute[0].z:F1}) " +
            $"last=({_waterRoute[_waterRoute.Count - 1].x:F1},{_waterRoute[_waterRoute.Count - 1].z:F1})");
    }

    static bool WestOfWaterFence(Vector3 p)
    {
        // Strictly west of gap mouth (15.2). Old threshold 15.5 overlapped
        // water_gap_crossed at 15.35 → rebuild loop, traj stuck at F near fence.
        return p.x < 15.15f && p.z < 28.0f;
    }

    static Vector3 WaterGapSouth(float y) => new Vector3(14.2f, y, 14.6f);
    static Vector3 WaterGapMouth(float y) => new Vector3(15.2f, y, 15.3f);

    static Vector3 ApproachableWaterStand(StreamingSurvivalWorldRegistry.WorldObject water)
    {
        Vector3 pos = water.Position;
        Vector3 stand = water.InteractionPosition;
        // South rim of GoalWater3 on the pond side of the fence (≈ 18–20, 29–30).
        // Reject legacy west-of-fence stands (~12,27) — character looked "stuck by a tree".
        Vector3 forced = new Vector3(
            Mathf.Clamp(pos.x - 2.0f, 17.0f, 20.5f),
            stand.y,
            Mathf.Clamp(pos.z - 3.5f, 28.5f, 31.0f));
        if (stand.x >= 16.5f && stand.x <= 22.0f && stand.z >= 28.0f && stand.z <= 31.5f)
            return stand;
        return forced;
    }

    Vector3? FindWaterSourceStandPos()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        var src = root != null
            ? root.GetComponentsInChildren<WaterSource>(true)
            : Object.FindObjectsByType<WaterSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (src == null || src.Length == 0) return null;

        WaterSource best = null;
        float bestD = float.MaxValue;
        Vector3 from = transform.position;
        for (int i = 0; i < src.Length; i++)
        {
            var s = src[i];
            if (s == null || !s.gameObject.activeInHierarchy) continue;
            Vector3 wp = s.transform.position;
            if (wp.z < 20f || wp.z > 40f) continue;
            float d = Horiz(from, wp);
            if (d < bestD) { bestD = d; best = s; }
        }
        if (best == null) return null;
        Vector3 w = best.transform.position;
        // South rim, pond side of fence (not west tree/gate pocket).
        Vector3 stand = new Vector3(
            Mathf.Clamp(w.x - 2.0f, 17.0f, 20.5f),
            from.y,
            Mathf.Clamp(w.z - 3.5f, 28.5f, 31.0f));
        return IsWaterPathPoint(stand) ? stand : (Vector3?)null;
    }

    bool IsNearRealWater(float reach = 5.5f)
    {
        Vector3 p = transform.position;
        // спавн/дом — точно не вода
        if (p.z < 26.5f || p.x < 15.5f)
            return false;
        if (!IsWaterPathPoint(p))
            return false;

        var root = TrainingEnvSpace.PresentationRoot;
        if (WaterSource.TryCollect(transform, reach + 2f, root, out _))
            return true;

        Vector3? g3 = FindGoalWaterByNames(new[] { "GoalWater3" });
        if (g3.HasValue && Horiz(p, g3.Value) <= reach + 2.5f)
            return true;

        var regW = StreamingSurvivalWorldRegistry.Instance?.GetNearestWater(p);
        if (regW != null && regW.Position.z >= 28f)
        {
            Vector3 stand = ApproachableWaterStand(regW);
            if (Horiz(p, stand) <= reach && Horiz(p, regW.Position) <= 10f)
                return true;
        }
        return false;
    }

    void TickWaterRoute(StreamingSurvivalController ctrl)
    {
        if (_waterRoute.Count == 0)
        {
            BuildWaterRoute();
            _waterWpIndex = 0;
        }

        Vector3 p = transform.position;
        // Keep registry water as completion meta — but never steer to it while west of fence.
        var regW = StreamingSurvivalWorldRegistry.Instance?.GetNearestWater(p);
        Vector3 stand = (regW != null && regW.Position.z >= 28f)
            ? ApproachableWaterStand(regW)
            : new Vector3(19.1f, p.y, 29.3f);
        _lockedWorkTarget = stand;

        RefreshLabel();

        if (_waterWpIndex < 0)
            _waterWpIndex = 0;

        // West of fence with empty / exhausted route → rebuild gap corridor (never dash to lake).
        if (WestOfWaterFence(p) && _waterWpIndex >= _waterRoute.Count)
        {
            BuildWaterRoute();
            _waterWpIndex = 0;
            _waterStuckTries = 0;
            _waterStuckSince = -1f;
        }

        // Finished corridor only on pond side.
        if (_waterWpIndex >= _waterRoute.Count)
        {
            if (WestOfWaterFence(p))
            {
                BuildWaterRoute();
                _waterWpIndex = 0;
            }
            else if (regW != null)
            {
                Vector3 standGo = ApproachableWaterStand(regW);
                _target = standGo;
                SetTargetMeta("water_source", regW.Id, standGo, regW.Position);
                if (Action == "go_to_water"
                    && Arrived(standGo, StreamingSurvivalResourceGuard.WaterInteractionRadius + 0.45f)
                    && p.x >= 16.0f
                    && p.z >= 27.5f)
                {
                    Debug.Log(
                        $"[SSPos] action_done user={Username} action=go_to_water ok=1 " +
                        $"pos=({p.x:F2},{p.z:F2})");
                    StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(Username, "movement_arrived", this);
                    AdvanceQueueOrIdle();
                    return;
                }
                if (TryBeginWorkAtResource(ctrl))
                    return;
                MoveToward(standGo);
                return;
            }
            else
            {
                BuildWaterRoute();
                _waterWpIndex = 0;
            }
        }

        if (_waterWpIndex >= _waterRoute.Count)
        {
            // Still no route — hold gap mouth rather than lake while west.
            Vector3 hold = WestOfWaterFence(p) ? WaterGapMouth(p.y) : stand;
            _target = hold;
            MoveToward(hold);
            return;
        }

        // While west of the mid-yard fence: ONLY steer to the gap mouth.
        // Never rewrite GW2/lake into "mouth" and Arrived() — that auto-skipped the whole route.
        // Do NOT rebuild route on "crossed" here: WestOfWaterFence ends at x<15.15 so
        // the next frame simply follows GW1+ (old crossed@15.35 + west@15.5 = infinite loop).
        if (WestOfWaterFence(p))
        {
            Vector3 mouth = WaterGapMouth(p.y);
            _target = mouth;
            SetTargetMeta("water_source", "gap_gw1", mouth, stand);

            if (_waterStuckSince < 0f)
            {
                _waterStuckSince = Time.time;
                _waterStuckPos = p;
            }
            else if (Horiz(p, _waterStuckPos) > 0.35f)
            {
                _waterStuckSince = Time.time;
                _waterStuckPos = p;
            }

            if (Time.time - _waterStuckSince > 1.2f)
            {
                _waterStuckTries++;
                StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(
                    Username, "stuck_detected", this);
                // Walk around blockers (sheep) — never disable collisions / clip through.
                Vector3 push = mouth - p;
                push.y = 0f;
                if (p.z > 15.6f)
                    push = new Vector3(mouth.x - p.x, 0f, 14.6f - p.z); // south then east
                else if (push.sqrMagnitude < 0.01f)
                    push = Vector3.right;
                Debug.Log(
                    $"[SSPos] water_gap_recover user={Username} " +
                    $"pos=({p.x:F2},{p.z:F2}) → mouth around");
                TrySafeNudge(push.normalized * 2.0f, 2.0f, waterEscape: true);
                _waterStuckSince = Time.time;
            }

            MoveToward(mouth);
            return;
        }

        const float waterArrive = 1.15f;
        Vector3 wp = _waterRoute[_waterWpIndex];
        _target = wp;
        if (regW != null)
            SetTargetMeta("water_source", regW.Id, stand, regW.Position);
        else
            SetTargetMeta("water_source", "gap_gw1", wp, stand);

        if (_waterStuckSince < 0f)
        {
            _waterStuckSince = Time.time;
            _waterStuckPos = p;
        }
        else if (Horiz(p, _waterStuckPos) > 0.45f)
        {
            _waterStuckSince = Time.time;
            _waterStuckPos = p;
        }

        if (Time.time - _waterStuckSince > 2.0f && !Arrived(wp, waterArrive + 0.4f))
        {
            StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(
                Username, "stuck_detected", this);
            _waterStuckTries++;
            if (_waterStuckTries <= 3
                && TrySafeNudge(wp - p, 2.0f, waterEscape: true))
            {
                _waterStuckSince = Time.time - 0.4f;
                return;
            }
            Debug.Log(
                $"[SSPos] water_skip user={Username} i={_waterWpIndex} " +
                $"pos=({p.x:F2},{p.z:F2}) wp=({wp.x:F1},{wp.z:F1})");
            _waterWpIndex++;
            _waterStuckTries = 0;
            _waterStuckSince = Time.time;
            return;
        }

        if (Arrived(wp, waterArrive))
        {
            // Require being on the pond side for late checkpoints.
            if (wp.x >= 16.0f && p.x < 15.3f)
            {
                MoveToward(wp);
                return;
            }
            Debug.Log(
                $"[SSPos] water_wp_ok user={Username} i={_waterWpIndex} " +
                $"pos=({p.x:F2},{p.z:F2})");
            _waterWpIndex++;
            _waterStuckTries = 0;
            _waterStuckSince = -1f;
            _phaseEnteredAt = Time.time;
            return;
        }

        MoveToward(wp);
    }

    void BeginMoveDemo(string kind)
    {
        _demoKind = kind;
        _demoCenter = transform.position;
        _demoAngle = 0f;
        _demoUntil = Time.time + (kind == "patrol" ? 14f : (kind == "walk_circle" || kind == "circle" ? 22f : 8f));
        _phase = Phase.MoveDemo;
        _phaseEnteredAt = Time.time;
        if (kind == "walk_forward" || kind == "patrol")
            _target = transform.position + transform.forward * 6f;
        else if (kind == "walk_back")
            _target = transform.position - transform.forward * 5f;
        else
            _target = transform.position;
        if (kind == "walk_circle" || kind == "circle")
        {
            // Circle around current position — never teleport in normal gameplay.
            _demoAngle = 0f;
            float r0 = 2.4f;
            _demoCenter = transform.position;
            SetTargetMeta("home", "circle_center", _demoCenter);
            _target = _demoCenter + new Vector3(r0, 0f, 0f);
        }
        else if (kind == "patrol")
            SetTargetMeta("home", "patrol", _target);
        else
            SetTargetMeta("home", kind, _target);
        Debug.Log($"[SSPos] move_demo user={Username} kind={kind}");
    }

    void TickMoveDemo()
    {
        if (Time.time >= _demoUntil)
        {
            Debug.Log($"[SSPos] action_done user={Username} action={Action} ok=1");
            SetAnimSpeed(0f);
            StreamingSurvivalTrajectoryRecorder.Instance?.EmitEvent(Username, "plan_completed", this);
            AdvanceQueueOrIdle();
            return;
        }
        if (_demoKind == "spin_in_place")
        {
            transform.Rotate(0f, 120f * Time.deltaTime, 0f, Space.World);
            SetAnimSpeed(0.15f);
            return;
        }
        if (_demoKind == "walk_circle" || _demoKind == "circle")
        {
            // Walk the circle via CharacterController — no transform.position snaps.
            _demoAngle += 1.35f * Time.deltaTime;
            float r = 2.4f;
            Vector3 next = _demoCenter + new Vector3(Mathf.Cos(_demoAngle) * r, 0f, Mathf.Sin(_demoAngle) * r);
            next.y = transform.position.y;
            _target = next;
            MoveToward(next);
            return;
        }
        if (_demoKind == "patrol")
        {
            if (Arrived(_target, 0.7f))
            {
                // reverse direction
                Vector3 away = transform.position - _demoCenter;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = transform.forward;
                _target = transform.position - away.normalized * 5f;
            }
            MoveToward(_target);
            return;
        }
        // forward / back
        if (Arrived(_target, 0.7f))
        {
            if (_demoKind == "walk_forward")
                _target = transform.position + transform.forward * 4f;
            else
                _target = transform.position - transform.forward * 4f;
        }
        MoveToward(_target);
    }

    void TriggerDo()
    {
        if (_anim == null) PrepareAnimator();
        if (_anim == null) return;
        _anim.ResetTrigger("Do");
        _anim.SetTrigger("Do");
        Debug.Log($"[SSPos] anim_do user={Username} action={Action}");
    }

    Vector3? FindWater()
    {
        // цель сбора — у воды (GoalWater3 / пруд), не спавн
        string[] prefer = { "GoalWater3", "GoalWater2", "GoalWater1" };
        var found = FindGoalWaterByNames(prefer);
        if (found.HasValue)
            return found;

        var ctrl = StreamingSurvivalController.Instance;
        if (ctrl != null)
        {
            // рядом с цветами / GoalWater1 (~z=17)
            Vector3 fallback = ctrl.FollowerSpawnWorld + new Vector3(0f, 0f, 4.2f);
            Debug.LogWarning(
                $"[SSPos] water_fallback user={Username} pos=({fallback.x:F2},{fallback.z:F2})");
            return fallback;
        }
        return null;
    }

    Vector3? FindGoalWaterByNames(string[] names)
    {
        var root = TrainingEnvSpace.PresentationRoot
            ?? TrainingEnvSpace.ActiveViewEnvRoot;
        Transform[] all;
        if (root != null)
            all = root.GetComponentsInChildren<Transform>(true);
        else
            all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        Vector3 from = transform.position;
        for (int n = 0; n < names.Length; n++)
        {
            string want = names[n];
            Transform best = null;
            // предпочитаем точку ближе к пруду (больший z), не ближайшую к агенту у забора
            float bestScore = float.MinValue;
            for (int i = 0; i < all.Length; i++)
            {
                var tr = all[i];
                if (tr == null || tr.name != want) continue;
                Vector3 p = tr.position;
                // GoalWater3 sits on/near the pond (z can be >31); still accept by name.
                if (!IsWaterPathPoint(p) && !(want == "GoalWater3" && p.z >= 28f && p.z <= 40f))
                    continue;
                float score = p.z * 10f - Mathf.Abs(p.x - 10f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = tr;
                }
            }
            if (best != null)
            {
                Vector3 p = best.position;
                return new Vector3(p.x, from.y, p.z);
            }
        }
        return null;
    }


    Vector3? FindTree()
    {
        // Только деревья из TreeSpawner presentation Env (не декорации за забором).
        var spawner = TrainingEnvSpace.FindInPresentation<TreeSpawner>()
            ?? Object.FindFirstObjectByType<TreeSpawner>();
        if (spawner != null
            && spawner.TryGetNearestAliveTree(transform.position, out GameObject tree, out Vector3 pos))
        {
            if (_treeVictim != tree)
            {
                _treeVictim = tree;
                Debug.Log(
                    $"[SSPos] target_tree user={Username} tree=({pos.x:F2},{pos.y:F2},{pos.z:F2}) " +
                    $"from=({transform.position.x:F2},{transform.position.z:F2})");
            }
            return pos;
        }
        _treeVictim = null;
        Debug.LogWarning($"[SSPos] no_spawner_tree user={Username}");
        return null;
    }

    Vector3? FindSheep()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        var sheep = root != null
            ? root.GetComponentsInChildren<SheepWander>(true)
            : Object.FindObjectsByType<SheepWander>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        Transform best = null;
        float bestD = float.MaxValue;
        if (sheep != null)
        {
            for (int i = 0; i < sheep.Length; i++)
            {
                var s = sheep[i];
                if (s == null || !s.gameObject.activeInHierarchy) continue;
                if (s.GetComponent<ViewerSimpleAgent>() != null) continue;
                float d = Horiz(transform.position, s.transform.position);
                if (d < bestD)
                {
                    bestD = d;
                    best = s.transform;
                }
            }
        }
        _sheepVictim = best;
        return best != null ? best.position : (Vector3?)null;
    }

    Vector3? FindByName(string needle)
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root == null) return null;
        var all = root.GetComponentsInChildren<Transform>(true);
        Transform best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < all.Length; i++)
        {
            var tr = all[i];
            if (tr == null) continue;
            if (tr.name.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            float d = Horiz(transform.position, tr.position);
            if (d < bestD)
            {
                bestD = d;
                best = tr;
            }
        }
        if (best != null)
            _treeVictim = best.gameObject;
        return best != null ? best.position : (Vector3?)null;
    }

    Vector3? Nearest(Component[] comps)
    {
        Component best = null;
        float bestD = float.MaxValue;
        if (comps == null) return null;
        for (int i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            float d = Horiz(transform.position, c.transform.position);
            if (d < bestD)
            {
                bestD = d;
                best = c;
            }
        }
        return best != null ? best.transform.position : (Vector3?)null;
    }

    bool Arrived(Vector3 world, float dist)
    {
        return Horiz(transform.position, world) <= dist;
    }

    void MoveToward(Vector3 world)
    {
        Vector3 flat = world - transform.position;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f)
        {
            SetAnimSpeed(0f);
            return;
        }
        Vector3 dir = flat.normalized;
        Vector3 move = dir * (_speed * Time.deltaTime);
        if (_cc != null && _cc.enabled)
            _cc.Move(move + Vector3.down * 9.81f * Time.deltaTime);
        else
            transform.position += move;
        // Always snap if fallen below sampled ground or far below target Y.
        StreamingSurvivalTrajectoryRecorder.SampleGround(
            this, out float gy, out float bottom, out float clr, out bool below, out _, out _);
        if (below || transform.position.y < -8f || (world.y - transform.position.y > 2.5f && clr < -0.05f))
            SnapToGround();
        transform.rotation = Quaternion.Slerp(
            transform.rotation, Quaternion.LookRotation(dir), 8f * Time.deltaTime);
        SetAnimSpeed(1f);
    }

    void SetAnimSpeed(float target)
    {
        _animSpeed = Mathf.MoveTowards(_animSpeed, target, 6f * Time.deltaTime);
        if (_anim != null)
        {
            _anim.applyRootMotion = false;
            _anim.SetFloat("Speed", _animSpeed);
        }
    }

    static float Horiz(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }
}
