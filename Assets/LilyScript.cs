using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public class LilyScript : Agent, IHasHp
{
    /// <summary>0 = цветы, 1 = поцелуй, 2 = вода, 3 = еда, 4 = тепло (костёр у дома).</summary>
    public const int OptionFlower = 0;
    public const int OptionKiss = 1;
    public const int OptionWater = 2;
    public const int OptionFood = 3;
    public const int OptionHeat = 4;
    public const int OptionCount = 5;

    private int currentOption;

    EnvTrainingConfig _trainingConfig;
    EnvTrainingTask _resolvedLilyTask = EnvTrainingTask.PresentationFull;

    bool IsLilySimpleTraining =>
        _trainingConfig != null && _trainingConfig.IsLilySimpleTask();

    [Header("Option Sampling")]
    [Tooltip("Если true, опция выбирается по utility+softmax каждые 20 шагов. На presentation всегда включено.")]
    [SerializeField] private bool useUtilitySoftmaxSampling = false;
    [Tooltip("Температура softmax (0.2 = почти жёстко, 0.7 = заметно случайно).")]
    [SerializeField] [Range(0.05f, 2.0f)] private float tau = 0.2f;
    [Tooltip("Шум eps ~ Uniform[-noise, +noise], добавляется в utility.")]
    [SerializeField] [Range(0f, 2f)] private float noise = 0.15f;
    [Tooltip("Макс. дистанция для access (в метрах). Ближе = 1, дальше = 0.")]
    [SerializeField] [Range(1f, 100f)] private float accessMaxDistance = 20f;
    [Tooltip("Липкость: насколько выгодно не переключаться без причины (добавка к utility текущей опции).")]
    [SerializeField] [Range(0f, 2f)] private float stickinessBonus = 0.5f;

    [Header("Current Option Icon")]
    [SerializeField] private SpriteRenderer optionIconRenderer;
    [SerializeField] private Sprite optionFlowerSprite;
    [SerializeField] private Sprite optionKissSprite;
    [SerializeField] private Sprite optionWaterSprite;
    [SerializeField] private Sprite optionFoodSprite;
    [SerializeField] private Sprite optionHeatSprite;
    [SerializeField] private Vector3 optionIconOffset = new Vector3(0f, 2.2f, 0f);
    [SerializeField] private float optionFlowerIconScale = 0.45f;
    [SerializeField] private float optionKissIconScale = 0.45f;
    [SerializeField] private float optionWaterIconScale = 0.45f;
    [SerializeField] private float optionFoodIconScale = 0.45f;
    [SerializeField] private float optionHeatIconScale = 0.45f;
    [SerializeField] private int optionIconSortingOrder = 100;
    [SerializeField] private bool optionIconFaceCamera = true;
    [Tooltip("Если задано — иконка разворачивается к этой камере; иначе MainCamera или камера с максимальным depth.")]
    [SerializeField] private Camera optionIconBillboardCamera;
    [Tooltip("Снять галочку, чтобы скрыть иконку задачи над агентом.")]
    [SerializeField] private bool showOptionTaskIcon = true;

    internal Sprite GetOptionHeatSpriteForShare() => optionHeatSprite;
    internal Sprite GetOptionWaterSpriteForShare() => optionWaterSprite;
    internal Sprite GetOptionFoodSpriteForShare() => optionFoodSprite;

    private bool _lastShowOptionTaskIcon = true;

    [Header("Heat / House (опция «тепло»)")]
    [SerializeField] private Transform houseTarget;
    [SerializeField] private float houseRadius = 1.2f;
    [Tooltip("Радиус подогрева у костра (больше houseRadius).")]
    [SerializeField] private float campfireWarmthRadius = 3f;
    [SerializeField] private float moveTowardsHouseWhenColdRewardScale = 1f;
    [SerializeField] private float warmthRewardAtCampfire = 5f;
    [SerializeField] private float warmthGainInterval = 0.5f;
    private float _warmthGainTimer;

    [Header("Jack (опция «поцелуй»)")]
    [SerializeField] private Transform jackTarget;
    [SerializeField] private LayerMask jackLayer;
    [SerializeField] private float kissDistance = 1.4f;
    [Tooltip("Требовать, чтобы Jack был спереди Lily для засчитывания поцелуя. 1 = строго прямо, 0 = 180° (не проверять).")]
    [SerializeField] [Range(0f, 1f)] private float kissMinForwardDot = 0.5f; // ~60°
    [SerializeField] private float kissReward = 10f;
    [SerializeField] private float moveTowardsJackRewardScale = 0.3f;
    [Tooltip("На сколько секунд замирает Jack/George после поцелуя Lily.")]
    [SerializeField] private float kissVictimStunSeconds = 1f;

    [Header("Flower Spawner")]
    [SerializeField] private FlowerSpawner flowerSpawner;
    [SerializeField] private LayerMask flowerLayer;

    [Header("Collect")]
    [SerializeField] private float collectDistance = 1.8f;
    [SerializeField] private float collectReward = 10f;
    [SerializeField] private SheepSpawner sheepSpawner;
    [SerializeField] private LayerMask sheepLayer;
    [SerializeField] private float eatDistance = 1.2f;
    [SerializeField] private float sheepEatReward = 10f;
    [SerializeField] private float waterCollectDistance = 2.5f;
    [SerializeField] private float waterCollectReward = 8f;
    [SerializeField] private int waterPerCollect = 1;
    public int WaterCount { get; private set; }
    [SerializeField] private float waterDecayInterval = 5f;
    private float waterDecayTimer;

    [Header("Hunger / Satiety")]
    [SerializeField] private int maxSatiety = 20;
    public int Satiety { get; private set; }
    [SerializeField] private float satietyDecayInterval = 2.5f;
    private float satietyTimer;

    [Header("Starving (satiety = 0)")]
    [SerializeField] private float hungerDamageInterval = 0.25f;
    [SerializeField] private int hungerDamageAmount = 1;
    [SerializeField] private float hungerPenaltyPerTick = -0.5f;
    private float _hungerTimer;

    [Header("Heat")]
    [SerializeField] private int startHeat = 20;
    [Tooltip("Только для наблюдений ML: Heat / scale, не лимит инвентаря.")]
    [SerializeField] private int heatObservationScale = 20;
    public int Heat { get; private set; }
    [SerializeField] private float heatDecayInterval = 2.5f;
    private float heatTimer;

    [Header("Freezing (heat = 0)")]
    [SerializeField] private float freezeDamageInterval = 0.25f;
    [SerializeField] private int freezeDamageAmount = 1;
    [SerializeField] private float freezePenaltyPerTick = -0.5f;
    private float _freezeTimer;
    float _episodeRewardAnchor;

    [Header("Dehydrated (water = 0)")]
    [SerializeField] private float thirstDamageInterval = 0.25f;
    [SerializeField] private int thirstDamageAmount = 1;
    private float _thirstTimer;

    [Header("Animation (DO — сбор / поцелуй)")]
    [Tooltip("Trigger в Animator Lily (как у Jack).")]
    [SerializeField] private string doActionAnimTrigger = "Do";
    [Tooltip("Пауза между срабатываниями DO (сбор цветка или поцелуй).")]
    [SerializeField] private float collectActionCooldownSeconds = 0.45f;
    [Tooltip("Штраф за DO не рядом с целью. Stage1=выкл; Stage2 (-forestLilyStage2)=−5.")]
    [SerializeField] private float emptyDoActionPenalty = -5f;
    [SerializeField] private bool applyEmptyDoActionPenalty = false;
    [Tooltip("Stage2: штраф за шаг назад в food/water/heat/flower.")]
    [SerializeField] private float backwardWalkPenalty = -0.5f;
    [Tooltip("Сглаживание параметра Speed в Animator (0 = без сглаживания).")]
    [SerializeField] private float walkAnimSpeedDamp = 0f;
    private int _lastCollectAction;
    private float _doCooldownRemaining;
    private float _lastPlanarMoveInput;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float rotationSpeed = 120f;

    [Header("Rewards")]
    [SerializeField] private float moveTowardsFlowerRewardScale = 0.3f;
    [SerializeField] private float moveTowardsSheepRewardScale = 0.3f;
    [SerializeField] private float stepPenalty = -0.001f;
    [Tooltip("Плотная награда за то, что Lily смотрит в сторону Jack (dot(forward, dirToJack)). 0 = выключено.")]
    [SerializeField] private float lookAtJackRewardScale = 0.10f;
    [Tooltip("Порог dot, ниже которого награда = 0. 0.5 ~ 60°, 0.7 ~ 45°.")]
    [SerializeField] [Range(-1f, 1f)] private float lookAtJackMinDot = 0.6f;

    [Header("Счётчики (растут от действий, со временем падают)")]
    [SerializeField] private int maxFlowerCount = 20;
    [SerializeField] private float flowerDecayInterval = 8f;
    [SerializeField] private int maxLove = 100;
    [SerializeField] private float loveDecayInterval = 8f;

    [Header("Zombie (наблюдения + DO-удар, слой тот же)")]
    [SerializeField] private LayerMask zombieLayer;

    [Header("DO — удар по зомби (логика как у JackScript)")]
    [Tooltip("Если при DO рядом есть зомби — Лилия получает урон (по умолчанию выкл., как у Джека).")]
    [SerializeField] private bool damageOnDoIfZombieNearby = false;
    [SerializeField] private float zombieNearbyRadiusOnDo = 1.5f;
    [SerializeField] private int zombieDamageOnDo = 0;
    [Tooltip("Если при DO рядом есть зомби — отталкиваем ближайшего (один за раз).")]
    [SerializeField] private bool knockbackZombieOnDo = true;
    [SerializeField] private float zombieKnockbackRadiusOnDo = 1.6f;
    [SerializeField] private float zombieKnockbackDistanceOnDo = 0.65f;
    [SerializeField] private float zombieKnockbackUpOnDo = 0.18f;
    [SerializeField] private float zombieKnockbackImpulseOnDo = 2.0f;
    [SerializeField] private float zombieKnockbackUpFactorOnDo = 0.45f;
    [SerializeField] private float zombieStunSecondsOnDo = 1.0f;
    [SerializeField] private bool zombieDiesInTwoDoHits = true;
    [SerializeField] private int zombieDamageToZombieOnDo = 1;
    [SerializeField] private float rewardOnZombieHitDo = 1.0f;
    [Tooltip("Зомби за спиной не бьются DO. dot(forward, к зомби): 0 = полусфера впереди, 0.5 ≈ 60°, 1 = строго вперёд.")]
    [SerializeField] [Range(0f, 1f)] private float zombieDoMinForwardDot = 0.5f;

    [Header("HP")]
    [SerializeField] private int maxHp = 100;
    [Tooltip("Штраф за каждый полученный урон (один вызов TakeDamage). 0 = выкл.")]
    [SerializeField] private float hpLossPenaltyPerHit = 10f;
    public int hp { get; private set; }

    [Header("Curriculum (последовательное обучение)")]
    [Tooltip("Включить для первой стадии: у Lily остаются только опции 0 (цветы) и 1 (поцелуй).")]
    [SerializeField] private bool curriculumNoShootNoZombie = false;

    [Header("Spawn")]
    [Tooltip("Если true — Lily спавнится в фиксированной позиции (не случайно).")]
    [SerializeField] private bool spawnAtFixedPosition = false;
    [SerializeField] private Vector3 fixedSpawnPosition = new Vector3(14.63f, -5.3f, 13f);

    [Header("Path (cinematic)")]
    [Tooltip("Если true — Lily игнорирует действия и идёт по точкам внутри pathRoot (FirstPoint, SecondPoint...).")]
    [SerializeField] private bool followPath = false;
    [SerializeField] private Transform pathRoot;
    [SerializeField] private float pathArriveDistance = 0.15f;
    [SerializeField] private float pathWaitSeconds = 0.0f;
    [SerializeField] private bool pathLoop = true;

    /// <summary>True — режим curriculum: у Lily только опции 0/1.</summary>
    public bool CurriculumNoShootNoZombie => curriculumNoShootNoZombie;

    private CharacterController controller;
    private Animator animator;

    private bool ControllerReady => controller != null && controller.enabled;
    private float verticalVelocity;
    [SerializeField] private float gravity = -9.81f;
    private float prevFlowerDist = -1f;
    private float prevJackDist = -1f;
    private float prevWaterDist = -1f;
    private float prevSheepDist = -1f;
    private float prevHouseDist = -1f;
    private Vector3 prevPosition;
    private int stepCount;
    private float _episodeStartTime;
    private int _episodeSheepEaten;
    private int _episodeWaterCollected;
    public int EpisodeWaterCollected => _episodeWaterCollected;
    private int _lastHungerRewardStep = -1;
    private int _lastFreezeRewardStep = -1;
    private float flowerDecayTimer;
    private float loveDecayTimer;
    private int _pathIndex;
    private float _pathWaitLeft;

    public int FlowerCount { get; private set; }
    public int Love { get; private set; }
    public int Hp => hp;
    public int MaxHp => maxHp;

    private const float MaxFlowerDistForObs = 50f;
    private const float MaxJackDistForObs = 50f;
    private const float MaxZombieDistForObs = 50f;

    public new int StepCount => stepCount;
    public int GetCurrentOption() => currentOption;

    public float GetDistanceToJackNormalized()
    {
        float d = GetDistanceToJack(out _);
        return Mathf.Clamp01(d / MaxJackDistForObs);
    }

    public float GetDistanceToNearestFlowerNormalized()
    {
        if (GetNearestFlower(out _, out float dist))
            return Mathf.Clamp01(dist / MaxFlowerDistForObs);
        return 1f;
    }

    public float GetDistanceToNearestZombieNormalized()
    {
        if (GetNearestZombie(out _, out float dist))
            return Mathf.Clamp01(dist / MaxZombieDistForObs);
        return 1f;
    }

    public void SetOption(int option)
    {
        if (option != OptionFlower && option != OptionKiss && option != OptionWater && option != OptionFood && option != OptionHeat)
            return;

        int prevOption = currentOption;
        if (option == OptionWater && prevOption != OptionWater)
            WaterGoalPath.Get(transform)?.ResetAgent(transform);

        currentOption = option;
        EnsureOptionIconRenderer();
        UpdateOptionIconVisual();
    }

    static void ResetWaterPathIfEntered(int prevOption, int newOption, Transform agent)
    {
        if (newOption == OptionWater && prevOption != OptionWater)
            WaterGoalPath.Get(agent)?.ResetAgent(agent);
    }

    public override void Initialize()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponent<Animator>();
        _lastCollectAction = 0;
        _doCooldownRemaining = 0f;
        _lastShowOptionTaskIcon = showOptionTaskIcon;
        EnsureOptionIconRenderer();
        UpdateOptionIconVisual();
        AgentFootsteps.EnsureOn(gameObject);
        ResolveSheepSpawner();
        ResolveFlowerSpawner();
        ResolveHouseTarget();

        // Как у Jack: не снимаем HP с агента за его же DO (старые значения в сцене).
        damageOnDoIfZombieNearby = false;
        zombieDamageOnDo = 0;
        // Food/water как у Jack: +10 овца, +8 вода. Stage2 — emptyDO / назад.
        sheepEatReward = 10f;
        waterCollectReward = 8f;
        emptyDoActionPenalty = -5f;
        backwardWalkPenalty = -0.5f;
        applyEmptyDoActionPenalty = EnvTrainingConfig.IsLilyStage2PenaltiesMode();
        if (applyEmptyDoActionPenalty)
            Debug.Log($"[{name}] Lily Stage2: emptyDO={emptyDoActionPenalty}, backWalk={backwardWalkPenalty}");
        collectDistance = Mathf.Clamp(collectDistance, 0.5f, 2.5f);
        freezePenaltyPerTick = -0.5f;
        hungerPenaltyPerTick = -0.5f;

        if (zombieLayer.value == 0)
        {
            int zombieLayerId = LayerMask.NameToLayer("Zombie");
            if (zombieLayerId >= 0)
                zombieLayer = 1 << zombieLayerId;
        }
    }

    bool ShouldShowOptionTaskIcon() =>
        showOptionTaskIcon
        && (TrainingEnvSpace.IsPresentationTransform(transform)
            || IsLilySimpleTraining
            || TrainingEnvSpace.IsDebugEnvFocusActive);

    private void EnsureOptionIconRenderer()
    {
        if (!ShouldShowOptionTaskIcon()) return;
        if (optionIconRenderer != null) return;
        if (optionFlowerSprite == null && optionKissSprite == null && optionWaterSprite == null && optionFoodSprite == null && optionHeatSprite == null) return;

        var existing = transform.Find("LilyOptionIcon");
        if (existing != null)
        {
            optionIconRenderer = existing.GetComponent<SpriteRenderer>();
            if (optionIconRenderer == null)
                optionIconRenderer = existing.gameObject.AddComponent<SpriteRenderer>();
            optionIconRenderer.sortingOrder = optionIconSortingOrder;
            ApplyOptionIconLocalScale();
            return;
        }

        var go = new GameObject("LilyOptionIcon");
        go.transform.SetParent(transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = optionIconSortingOrder;
        optionIconRenderer = sr;
        ApplyOptionIconLocalScale();
    }

    private void ApplyOptionIconLocalScale()
    {
        if (optionIconRenderer == null) return;
        float s = currentOption switch
        {
            OptionFlower => Mathf.Max(0.01f, optionFlowerIconScale),
            OptionKiss => Mathf.Max(0.01f, optionKissIconScale),
            OptionWater => ResolveMatchedIconScale(optionWaterSprite, optionWaterIconScale),
            OptionFood => ResolveMatchedIconScale(optionFoodSprite, optionFoodIconScale),
            OptionHeat => ResolveMatchedIconScale(optionHeatSprite, optionHeatIconScale),
            _ => 0.35f
        };
        optionIconRenderer.transform.localScale = Vector3.one * s;
    }

    float ResolveMatchedIconScale(Sprite sprite, float baseScale)
    {
        if (optionFlowerSprite == null || sprite == null)
            return Mathf.Max(0.01f, baseScale);

        float refSize = Mathf.Max(optionFlowerSprite.bounds.size.x, optionFlowerSprite.bounds.size.y);
        float size = Mathf.Max(sprite.bounds.size.x, sprite.bounds.size.y);
        if (size < 1e-4f)
            return Mathf.Max(0.01f, baseScale);

        return Mathf.Max(0.01f, baseScale) * (refSize / size);
    }

    private void LateUpdate()
    {
        ApplyWalkAnimatorSpeed();

        if (!ShouldShowOptionTaskIcon() || optionIconRenderer == null) return;

        Vector3 iconPos = transform.position + optionIconOffset;
        optionIconRenderer.transform.position = iconPos;

        if (optionIconFaceCamera)
        {
            var cam = BillboardIconCamera.Resolve(iconPos, optionIconBillboardCamera);
            if (cam != null)
            {
                Vector3 toCam = cam.transform.position - iconPos;
                if (toCam.sqrMagnitude > 1e-6f)
                    optionIconRenderer.transform.rotation = Quaternion.LookRotation(toCam.normalized, cam.transform.up);
            }
        }
    }

    private void ApplyWalkAnimatorSpeed()
    {
        if (animator == null || controller == null) return;

        Vector3 v = controller.velocity;
        v.y = 0f;
        float velNorm = moveSpeed > 1e-4f ? Mathf.Clamp01(v.magnitude / moveSpeed) : 0f;
        float target = Mathf.Max(Mathf.Abs(_lastPlanarMoveInput), velNorm);
        if (_doCooldownRemaining > 0f && Mathf.Abs(_lastPlanarMoveInput) > 0.01f)
            target = Mathf.Max(target, 0.55f);
        if (target < 0.02f)
            target = 0f;

        if (walkAnimSpeedDamp > 0f)
            animator.SetFloat("Speed", target, walkAnimSpeedDamp, Time.deltaTime);
        else
            animator.SetFloat("Speed", target);
    }

    private void UpdateOptionIconVisual()
    {
        if (!ShouldShowOptionTaskIcon())
        {
            if (optionIconRenderer != null)
                optionIconRenderer.enabled = false;
            return;
        }

        EnsureOptionIconRenderer();
        if (optionIconRenderer == null) return;

        switch (currentOption)
        {
            case OptionFlower:
                optionIconRenderer.sprite = optionFlowerSprite;
                optionIconRenderer.enabled = optionFlowerSprite != null;
                break;
            case OptionKiss:
                optionIconRenderer.sprite = optionKissSprite;
                optionIconRenderer.enabled = optionKissSprite != null;
                break;
            case OptionWater:
                optionIconRenderer.sprite = optionWaterSprite;
                optionIconRenderer.enabled = optionWaterSprite != null;
                break;
            case OptionFood:
                optionIconRenderer.sprite = optionFoodSprite;
                optionIconRenderer.enabled = optionFoodSprite != null;
                break;
            case OptionHeat:
                optionIconRenderer.sprite = optionHeatSprite;
                optionIconRenderer.enabled = optionHeatSprite != null;
                break;
            default:
                optionIconRenderer.enabled = false;
                break;
        }

        ApplyOptionIconLocalScale();
    }

    private new void Awake()
    {
        ConfigurePresentationControl();
    }

    void Start()
    {
        NormalizeFoodHeatDecayIntervals();
        ApplyStreamPresentationSurvivalPace();
        TrainingEnvSpace.CapturePresentationSpawn(transform);
    }

    bool _streamPresentationPaceApplied;

    /// <summary>Только stream presentation: нужды медленнее, HP от нужд реже, тепло у костра быстрее.</summary>
    protected void ApplyStreamPresentationSurvivalPace()
    {
        if (_streamPresentationPaceApplied)
            return;
        if (!TrainingEnvSpace.UsesStreamPresentationSurvivalPace(transform))
            return;

        _streamPresentationPaceApplied = true;
        float needsMul = TrainingEnvSpace.StreamPresentationNeedsDecayIntervalMul;
        float hpMul = TrainingEnvSpace.StreamPresentationHpDamageIntervalMul;
        float warmthMul = TrainingEnvSpace.StreamPresentationWarmthGainIntervalMul;

        satietyDecayInterval *= needsMul;
        heatDecayInterval *= needsMul;
        waterDecayInterval *= needsMul;
        hungerDamageInterval *= hpMul;
        thirstDamageInterval *= hpMul;
        freezeDamageInterval *= hpMul;
        warmthGainInterval *= warmthMul;
    }

    protected void NormalizeFoodHeatDecayIntervals()
    {
        const float fastInterval = 2.5f;
        if (satietyDecayInterval >= 4.5f)
            satietyDecayInterval = fastInterval;
        if (heatDecayInterval >= 4.5f)
            heatDecayInterval = fastInterval;
    }

    private void Update()
    {
        if (_doCooldownRemaining > 0f)
            _doCooldownRemaining -= Time.unscaledDeltaTime;

        if (!TrainingEnvSpace.IsPresentationTransform(transform))
            return;

        UpdateDehydration();
        UpdateWaterDecay();
        UpdateSatietyDecay();
        UpdateStarving();
        UpdateHeatDecay();
        UpdateFreezing();
        UpdateWarmthAtCampfire();

        if (IsLilySimpleTraining)
            EnforceLilySimpleOption();

        if (showOptionTaskIcon != _lastShowOptionTaskIcon)
        {
            _lastShowOptionTaskIcon = showOptionTaskIcon;
            UpdateOptionIconVisual();
        }

        ProcessManualOptionKeys();

        if (!IsLilySimpleTraining
            && ShouldUseOptionUtilitySampling()
            && !AgentGoToHouseDiscrete.AreHeuristicOptionsLocked)
        {
            RefreshOptionTierIfNeeded();
            RefreshSurvivalSubOptionIfNeeded();
        }
    }

    void EnforceLilySimpleOption()
    {
        if (_trainingConfig == null)
            _trainingConfig = EnvTrainingConfig.Get(transform);
        if (_trainingConfig == null || !_trainingConfig.IsLilySimpleTask())
            return;

        int fixedOption = _trainingConfig.ResolveFixedLilyOption();
        if (currentOption == fixedOption)
        {
            // UnmuteEnv снова включает SpriteRenderer — переприменяем иконку.
            UpdateOptionIconVisual();
            return;
        }

        currentOption = fixedOption;
        UpdateOptionIconVisual();
    }

    void UpdateWaterDecay()
    {
        if (_deathSequenceStarted)
            return;

        waterDecayTimer += Time.unscaledDeltaTime;
        if (waterDecayTimer < waterDecayInterval)
            return;

        waterDecayTimer = 0f;
        if (WaterCount > 0)
            WaterCount--;
    }

    void ProcessManualOptionKeys()
    {
        if (!IsManualControlActive(transform))
            return;

        // Как у Jack: сначала L (ручной режим), потом T — крутить опции Lily.
        // Без lock utility каждый кадр перезаписывает currentOption.
        if (!AgentGoToHouseDiscrete.AreHeuristicOptionsLocked)
            return;

        if (Input.GetKeyDown(KeyCode.Alpha0))
            SetOption(OptionWater);

        if (Input.GetKeyDown(KeyCode.Alpha1))
            SetOption(OptionFood);

        if (Input.GetKeyDown(KeyCode.Alpha2))
            SetOption(OptionHeat);

        if (Input.GetKeyDown(KeyCode.T))
        {
            int next = currentOption switch
            {
                OptionFlower => OptionKiss,
                OptionKiss => OptionWater,
                OptionWater => OptionFood,
                OptionFood => OptionHeat,
                _ => OptionFlower
            };
            SetOption(next);
        }
    }

    private bool _deathSequenceStarted;

    public void ApplyFriendlyKnockbackFrom(Vector3 attackerWorldPos, float distance, float up)
    {
        Vector3 dir = transform.position - attackerWorldPos;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-4f)
            dir = transform.forward;
        dir.Normalize();

        Vector3 delta = dir * distance + Vector3.up * up;
        if (controller != null && controller.enabled)
            controller.Move(delta);
        else
            transform.position += delta;
    }

    public void TakeDamage(int amount)
    {
        TakeDamage(amount, applyHpLossPenalty: true);
    }

    public void TakeDamage(int amount, bool applyHpLossPenalty)
    {
        if (amount <= 0 || _deathSequenceStarted) return;
        hp = Mathf.Max(0, hp - amount);
        if (applyHpLossPenalty && hpLossPenaltyPerHit != 0f)
            AddReward(-hpLossPenaltyPerHit);
        if (hp <= 0)
        {
            _deathSequenceStarted = true;
            EvalEpisodeTracker.NotifyEpisodeEnded();
            RecordSimpleTrainingFailureIfNeeded();
            AgentDeathOverlay.ShowAndEndEpisode(this, AgentDeathOverlay.GetDeathMessageFor(this));
        }
    }

    public bool IsInDeathState => _deathSequenceStarted || hp <= 0;

    public void ForceHardRespawnFromDeath()
    {
        // См. JackScript: OnEpisodeBegin без EndEpisode не сбрасывает GetCumulativeReward().
        if (!_deathSequenceStarted && hp > 0)
            return;

        if (Mathf.Abs(GetCumulativeReward()) > 0.01f)
        {
            NotifyEpisodeEndingForStats();
            try
            {
                EndEpisode();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Lily] EndEpisode before hard respawn: {ex.Message}", this);
            }

            if (!_deathSequenceStarted && hp > 0)
                return;
        }

        OnEpisodeBegin();
    }

    /// <summary>
    /// #reset / клавиша 0: полный новый эпизод (см. Jack ForceFullEpisodeRestart).
    /// </summary>
    public void ForceFullEpisodeRestart()
    {
        NotifyEpisodeEndingForStats();
        try
        {
            EndEpisode();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Lily] EndEpisode on full restart: {ex.Message}", this);
        }

        OnEpisodeBegin();
    }

    public void NotifyEpisodeEndingForStats()
    {
        TrainingTaskSuccessTracker.ReportAgentEpisodeReward(
            GetCumulativeReward() - _episodeRewardAnchor);
    }

    public override void OnEpisodeBegin()
    {
        _deathSequenceStarted = false;
        if (TrainingEnvSpace.IsPresentationTransform(transform))
        {
            DeathFreeze.UnfreezeWorld();
            if (!AgentDeathOverlay.IsDeathSequenceRunning)
                AgentDeathOverlay.Hide();
            BackgroundMusic.ResumeMusic();
        }

        stepCount = 0;
        _episodeStartTime = Time.unscaledTime;
        _episodeSheepEaten = 0;
        _episodeWaterCollected = 0;
        prevFlowerDist = -1f;
        prevJackDist = -1f;
        prevWaterDist = -1f;
        prevSheepDist = -1f;
        FlowerCount = 0;
        Love = 0;
        waterDecayTimer = 0f;
        satietyTimer = 0f;
        _hungerTimer = 0f;
        heatTimer = 0f;
        _freezeTimer = 0f;
        flowerDecayTimer = 0f;
        loveDecayTimer = 0f;
        hp = maxHp;
        _lastCollectAction = 0;
        _doCooldownRemaining = 0f;
        _pathIndex = 0;
        _pathWaitLeft = 0f;
        _thirstTimer = 0f;
        _warmthGainTimer = 0f;
        prevHouseDist = -1f;
        WaterGoalPath.Get(transform)?.ResetAgent(transform);

        if (TryGetComponent<CharacterController>(out var cc) && !cc.enabled)
            cc.enabled = true;
        if (animator != null)
            animator.speed = 1f;

        ConfigureTrainingControl();

        _trainingConfig = EnvTrainingConfig.Get(transform);
        _resolvedLilyTask = _trainingConfig != null
            ? _trainingConfig.ResolveTask()
            : EnvTrainingTask.PresentationFull;
        _trainingConfig?.ApplyForEpisodeBegin();

        ApplyLilyEpisodeStartNeeds();

        if (IsLilySimpleTraining)
        {
            currentOption = _trainingConfig.ResolveFixedLilyOption();
            MaxStep = _trainingConfig.ResolveSimpleMaxSteps();
            // FreezeNeeds = не падают со временем; старт уже рандомный в ApplyLilyEpisodeStartNeeds.
        }
        else
        {
            if (_resolvedLilyTask == EnvTrainingTask.PresentationFull
                && TrainingEnvSpace.IsPresentationTransform(transform))
            {
                // Стрим/validate: бесконечный эпизод. Обучение: лимит шагов.
                if (Academy.Instance.IsCommunicatorOn && !TrainingEnvSpace.AllowInfinitePresentationEpisode)
                    MaxStep = _trainingConfig != null
                        ? Mathf.Max(4000, _trainingConfig.SimpleMaxSteps * 10)
                        : 4000;
                else
                    MaxStep = 0;
            }

            if (ShouldUseOptionUtilitySampling())
                currentOption = SampleOptionUtilitySoftmax(currentOption);
            else
                currentOption = Random.Range(0, OptionCount);
        }

        // Локальные координаты относительно корня Env (рядом с домом, как у Jack).
        const float groundY = -5.228786f;
        float minX = -2f;
        float maxX = 14f;
        float minZ = 10f;
        float maxZ = 17f;

        // Все Env: каждый эпизод — случайная точка (или fixed). Path — ниже.
        if (!followPath)
        {
            controller.enabled = false;
            if (spawnAtFixedPosition)
            {
                transform.position = TrainingEnvSpace.LocalToWorld(transform, fixedSpawnPosition);
                transform.rotation = TrainingEnvSpace.LocalToWorldRotation(transform, Vector3.zero);
            }
            else
            {
                float randX = Random.Range(minX, maxX);
                float randZ = Random.Range(minZ, maxZ);
                transform.position = TrainingEnvSpace.LocalToWorld(transform, new Vector3(randX, groundY, randZ));
                transform.rotation = TrainingEnvSpace.LocalToWorldRotation(transform, new Vector3(0f, Random.Range(0f, 360f), 0f));
            }
            controller.enabled = true;
        }

        prevPosition = transform.position;

        if (followPath && pathRoot != null && pathRoot.childCount > 0)
        {
            var p0 = pathRoot.GetChild(0);
            if (p0 != null)
            {
                controller.enabled = false;
                transform.SetPositionAndRotation(p0.position, p0.rotation);
                controller.enabled = true;
                _pathIndex = 1 % pathRoot.childCount;
            }
        }

        ResolveFlowerSpawner();
        if (flowerSpawner != null)
            flowerSpawner.ResetFlowers();

        if (IsLilySimpleTraining)
        {
            var envRoot = TrainingEnvSpace.FindRoot(transform);
            PresentationWorldReset.ResetSpawners(envRoot);
        }

        UpdateOptionIconVisual();
        _episodeRewardAnchor = GetCumulativeReward();
    }

    void ApplyLilyEpisodeStartNeeds()
    {
        // Presentation: как Jack/George — по 15 еды/воды/тепла.
        if (TrainingEnvSpace.IsPresentationTransform(transform))
        {
            var config = EnvTrainingConfig.Get(transform);
            if (config == null || config.ResolveTask() == EnvTrainingTask.PresentationFull)
            {
                const int start = 15;
                Satiety = start;
                WaterCount = start;
                Heat = start;
                return;
            }
        }

        Satiety = Random.Range(2, Mathf.Max(3, maxSatiety));
        WaterCount = Random.Range(2, Mathf.Max(3, maxSatiety + 10));
        Heat = Random.Range(1, Mathf.Max(2, startHeat > 0 ? startHeat : 20));
    }

    void ConfigureTrainingControl()
    {
        var bp = GetComponent<BehaviorParameters>();
        if (bp == null)
            return;

        bool mlTraining = Academy.IsInitialized && Academy.Instance.IsCommunicatorOn;
        var config = EnvTrainingConfig.Get(transform);
        var task = config != null ? config.ResolveTask() : EnvTrainingTask.PresentationFull;
        bool trainThisAgent = !mlTraining
            ? TrainingEnvSpace.IsPresentationTransform(transform)
            : EnvTrainingConfig.ShouldAgentTrain(task, EnvTrainingAgentRole.Lily);

        bp.BehaviorType = trainThisAgent && mlTraining
            ? BehaviorType.Default
            : BehaviorType.HeuristicOnly;

        var decisionRequester = GetComponent<DecisionRequester>();
        if (decisionRequester != null)
        {
            decisionRequester.enabled = trainThisAgent && mlTraining;
            decisionRequester.DecisionPeriod = mlTraining ? 5 : 1;
        }
    }

    void ConfigurePresentationControl()
    {
        ConfigureTrainingControl();
    }

    static bool IsManualControlActive(Transform t)
    {
        if (TrainingEnvSpace.IsStreamOnlyMode)
            return false;
        if (TrainingEnvSpace.IsLivePresentationForObs && TrainingEnvSpace.IsMlAgentsTrainingActive())
            return false;
        if (!TrainingEnvSpace.IsPresentationTransform(t))
            return false;

        var bp = t.GetComponent<BehaviorParameters>();
        if (bp != null && bp.BehaviorType == BehaviorType.HeuristicOnly)
            return true;

        return !Academy.IsInitialized || !Academy.Instance.IsCommunicatorOn;
    }

    void UpdateDehydration()
    {
        if (_deathSequenceStarted || WaterCount > 0)
        {
            _thirstTimer = 0f;
            return;
        }

        _thirstTimer += Time.unscaledDeltaTime;
        if (_thirstTimer < thirstDamageInterval)
            return;

        _thirstTimer = 0f;
        if (thirstDamageAmount > 0)
            TakeDamage(thirstDamageAmount, applyHpLossPenalty: false);
    }

    private bool GetNearestFlower(out GameObject nearestFlower, out float distance)
    {
        nearestFlower = null;
        distance = float.MaxValue;

        if (flowerLayer.value == 0)
            ResolveFlowerSpawner();

        Vector3 origin = transform.position;
        Collider[] hits = Physics.OverlapSphere(origin, MaxFlowerDistForObs, flowerLayer);
        foreach (var hit in hits)
        {
            if (hit == null || !hit.gameObject.activeInHierarchy) continue;
            float d = HarvestReachDistance(origin, hit);
            if (d < distance)
            {
                distance = d;
                nearestFlower = GetFlowerInstanceRoot(hit);
            }
        }

        if (nearestFlower == null && flowerSpawner != null)
        {
            nearestFlower = flowerSpawner.FindNearestFlowerInReach(origin, MaxFlowerDistForObs);
            if (nearestFlower != null)
                distance = FlowerSpawner.DistanceToFlowerBounds(origin, nearestFlower);
        }

        return nearestFlower != null;
    }

    private float GetDistanceToJack(out Vector3 dirToJack)
    {
        dirToJack = Vector3.zero;
        Transform target = ResolveJackKissTransform();
        if (target == null) return float.MaxValue;
        Vector3 delta = target.position - transform.position;
        delta.y = 0f;
        float d = delta.magnitude;
        if (d > 0.001f) dirToJack = delta.normalized;
        return d;
    }

    Transform ResolveJackKissTransform()
    {
        var jack = FindJackInEnv();
        if (jack != null)
            return jack.transform;
        if (jackTarget != null && jackTarget.gameObject.activeInHierarchy)
            return jackTarget;
        return jackTarget;
    }

    private bool IsJackInKissRange() => IsKissPartnerInRange(FindJackInEnv());

    private bool IsAnyKissTargetInRange()
    {
        if (IsKissPartnerInRange(FindJackInEnv()))
            return true;
        return IsKissPartnerInRange(FindGeorgeInEnv());
    }

    private bool IsKissPartnerInRange(AgentGoToHouseDiscrete partner)
    {
        if (partner == null)
            return false;
        return IsTargetInKissRange(partner.transform);
    }

    private bool IsTargetInKissRange(Transform target)
    {
        if (target == null) return false;
        Vector3 p = transform.position;
        Vector3 j = target.position;
        p.y = 0f;
        j.y = 0f;
        Vector3 delta = j - p;
        float d = delta.magnitude;
        if (d <= 0.0001f) return true;
        Vector3 toTarget = delta / d;
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude > 1e-6f) fwd.Normalize();
        float dot = Vector3.Dot(fwd, toTarget);
        if (kissMinForwardDot > 0f && dot < kissMinForwardDot) return false;
        if (d <= kissDistance) return true;
        if (jackLayer.value == 0) return false;
        Collider[] hits = Physics.OverlapSphere(transform.position, kissDistance, jackLayer);
        foreach (var h in hits)
            if (h != null && (h.transform == target || h.transform.IsChildOf(target)))
                return true;
        return false;
    }

    private bool TryPerformKiss(out bool kissedGeorge)
    {
        kissedGeorge = false;
        var jack = FindJackInEnv();
        var george = FindGeorgeInEnv();
        Transform jackTransform = jack != null ? jack.transform : ResolveJackKissTransform();
        bool jackInRange = jackTransform != null && IsTargetInKissRange(jackTransform);
        bool georgeInRange = george != null && IsTargetInKissRange(george.transform);
        if (!jackInRange && !georgeInRange)
            return false;

        Transform kissTarget;
        if (jackInRange && georgeInRange)
        {
            float jackDist = GetDistanceToTransform(jackTransform, out _);
            float georgeDist = GetDistanceToTransform(george.transform, out _);
            if (georgeDist < jackDist)
            {
                kissTarget = george.transform;
                kissedGeorge = true;
            }
            else
            {
                kissTarget = jackTransform;
            }
        }
        else if (georgeInRange)
        {
            kissTarget = george.transform;
            kissedGeorge = true;
        }
        else
        {
            kissTarget = jackTransform;
        }

        Love = Mathf.Min(maxLove, Love + 1);
        AddReward(kissReward);
        GameSfx.PlayLilyKiss(source: transform);
        if (kissedGeorge)
            FloatingRewardPopup.ShowKissedGeorge(transform, kissReward);
        else
            FloatingRewardPopup.ShowKissedJack(transform, kissReward);

        var stunned = kissTarget.GetComponentInParent<AgentGoToHouseDiscrete>();
        if (stunned != null)
            stunned.StunForSeconds(kissVictimStunSeconds);

        return true;
    }

    private float GetDistanceToTransform(Transform target, out Vector3 dirToTarget)
    {
        dirToTarget = Vector3.zero;
        if (target == null) return float.MaxValue;
        Vector3 delta = target.position - transform.position;
        delta.y = 0f;
        float d = delta.magnitude;
        if (d > 0.001f) dirToTarget = delta.normalized;
        return d;
    }

    private bool GetNearestZombie(out GameObject nearestZombie, out float distance)
    {
        nearestZombie = null;
        distance = float.MaxValue;
        if (zombieLayer.value == 0) return false;
        Collider[] hits = Physics.OverlapSphere(transform.position, MaxZombieDistForObs, zombieLayer);
        foreach (var hit in hits)
        {
            if (hit == null || !hit.gameObject.activeInHierarchy) continue;
            float d = Vector3.Distance(transform.position, hit.transform.position);
            if (d < distance)
            {
                distance = d;
                nearestZombie = hit.gameObject;
            }
        }
        return nearestZombie != null;
    }

    private bool IsZombieColliderInFrontForDo(Vector3 origin, Collider c)
    {
        if (c == null) return false;
        Vector3 toTarget = SafeClosestPointOnCollider(c, origin) - origin;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 1e-6f) return true;

        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return true;
        fwd.Normalize();
        toTarget.Normalize();
        return Vector3.Dot(fwd, toTarget) >= zombieDoMinForwardDot;
    }

    private bool IsZombieNearbyForDo()
    {
        if (zombieNearbyRadiusOnDo <= 0f) return false;
        if (zombieLayer.value == 0) return false;

        Vector3 origin = transform.position;
        Collider[] hits = Physics.OverlapSphere(origin, zombieNearbyRadiusOnDo, zombieLayer);
        if (hits == null || hits.Length == 0) return false;

        foreach (var c in hits)
        {
            if (c == null) continue;
            if (c.GetComponentInParent<ZombieChase>() == null) continue;
            if (!IsZombieColliderInFrontForDo(origin, c)) continue;
            return true;
        }

        return false;
    }

    /// <summary>Копия логики JackScript.KnockbackNearbyZombiesOnDo: один ближайший зомби с ZombieChase, урон + откидывание.</summary>
    private void KnockbackNearbyZombiesOnLilyDo()
    {
        if (zombieLayer.value == 0) return;
        if (zombieKnockbackRadiusOnDo <= 0f) return;
        if (zombieKnockbackDistanceOnDo <= 0f && zombieKnockbackImpulseOnDo <= 0f) return;

        Vector3 origin = transform.position;
        Collider[] hits = Physics.OverlapSphere(origin, zombieKnockbackRadiusOnDo, zombieLayer);
        if (hits == null || hits.Length == 0) return;

        ZombieChase bestZombie = null;
        Collider bestZombieCollider = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < hits.Length; i++)
        {
            var c = hits[i];
            if (c == null) continue;
            var z = c.GetComponentInParent<ZombieChase>();
            if (z == null) continue;
            if (!IsZombieColliderInFrontForDo(origin, c)) continue;

            float d = Vector3.Distance(origin, SafeClosestPointOnCollider(c, origin));
            if (d < bestDist)
            {
                bestDist = d;
                bestZombie = z;
                bestZombieCollider = c;
            }
        }
        if (bestZombie == null) return;

        GameSfx.PlayJackLilyHitZombie(source: transform);

        if (rewardOnZombieHitDo != 0f)
            AddReward(rewardOnZombieHitDo);

        var zhBest = bestZombie.GetComponent<ZombieHealth>()
            ?? bestZombie.GetComponentInParent<ZombieHealth>()
            ?? bestZombie.GetComponentInChildren<ZombieHealth>();
        if (zhBest == null && bestZombieCollider != null)
        {
            zhBest = bestZombieCollider.GetComponent<ZombieHealth>()
                ?? bestZombieCollider.GetComponentInParent<ZombieHealth>()
                ?? bestZombieCollider.GetComponentInChildren<ZombieHealth>();
        }

        bool zombieKilled = false;
        if (zhBest != null)
        {
            int damage = zombieDamageToZombieOnDo;
            if (zombieDiesInTwoDoHits)
                damage = Mathf.Max(1, Mathf.CeilToInt(zhBest.MaxHp / 2f));
            if (damage > 0)
            {
                int prevHp = zhBest.Hp;
                zhBest.TakeDamage(damage, transform.position);
                if (prevHp > 0 && (zhBest == null || zhBest.Hp <= 0))
                    zombieKilled = true;
            }
        }

        if (!zombieKilled && bestZombie != null && bestZombie.RegisterJackDoHitAndMaybeDie(2))
            zombieKilled = true;

        if (zombieKilled)
        {
            FloatingRewardPopup.ShowZombieKill(transform, rewardOnZombieHitDo);
            return;
        }

        Transform zt = bestZombie.transform;
        Vector3 dir = zt.position - origin;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = zt.forward;
        dir = dir.normalized;

        var zControllerBest = bestZombie.GetComponent<CharacterController>();
        if (zControllerBest != null && zControllerBest.enabled)
        {
            Vector3 delta = dir * zombieKnockbackDistanceOnDo;
            delta.y = zombieKnockbackUpOnDo;
            zControllerBest.Move(delta);
            if (zombieStunSecondsOnDo > 0f)
                bestZombie.Stun(zombieStunSecondsOnDo);
            return;
        }

        var zRbBest = bestZombie.GetComponent<Rigidbody>();
        if (zRbBest != null)
        {
            Vector3 forceDir = (dir + Vector3.up * Mathf.Max(0f, zombieKnockbackUpFactorOnDo)).normalized;
            zRbBest.AddForce(forceDir * zombieKnockbackImpulseOnDo, ForceMode.Impulse);
            if (zombieStunSecondsOnDo > 0f)
                bestZombie.Stun(zombieStunSecondsOnDo);
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.position);
        sensor.AddObservation(transform.forward);

        // Опции: 3 бита (до 8 значений), сейчас 0..4.
        sensor.AddObservation(GetOptionObservationBit0(currentOption));
        sensor.AddObservation(GetOptionObservationBit1(currentOption));
        sensor.AddObservation(GetOptionObservationBit2(currentOption));

        // Счётчики (нормализованные [0,1])
        sensor.AddObservation(maxFlowerCount > 0 ? (float)FlowerCount / maxFlowerCount : 0f);
        sensor.AddObservation(maxLove > 0 ? (float)Love / maxLove : 0f);
        sensor.AddObservation(Mathf.Clamp01((float)Heat / Mathf.Max(1, heatObservationScale)));
        sensor.AddObservation(Mathf.Clamp01((float)WaterCount / Mathf.Max(1, maxSatiety + 10)));
        sensor.AddObservation(IsOnHouse() ? 1f : 0f);
        sensor.AddObservation(IsCampfireBurningInEnv() ? 1f : 0f);

        // Цветы: без дистанции и направления к цветку — только флаг «в радиусе сбора»
        if (GetNearestFlower(out _, out float dist))
        {
            sensor.AddObservation(dist <= collectDistance ? 1f : 0f);
        }
        else
        {
            sensor.AddObservation(0f);
        }

        // Цель поцелуя (Jack или George) в радиусе
        sensor.AddObservation(IsAnyKissTargetInRange() ? 1f : 0f);

        // Зомби — только через лидар (компонент на агенте)
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_deathSequenceStarted)
            return;

        if (followPath)
        {
            PathStep();
            stepCount++;
            if (!IsLilySimpleTraining
                && _resolvedLilyTask != EnvTrainingTask.PresentationFull
                && MaxStep > 0
                && stepCount >= MaxStep)
            {
                EvalEpisodeTracker.NotifyEpisodeEnded();
                EndEpisode();
            }
            return;
        }

        // Сразу меняем уровень задачи, если проголодалась/замёрзла (не ждём 20 шагов).
        if (!IsLilySimpleTraining
            && ShouldUseOptionUtilitySampling()
            && !AgentGoToHouseDiscrete.AreHeuristicOptionsLocked)
        {
            RefreshOptionTierIfNeeded();
            RefreshSurvivalSubOptionIfNeeded();
        }

        // Внутри уровня — пересэмплируем каждые 20 шагов.
        if (!IsLilySimpleTraining
            && ShouldUseOptionUtilitySampling()
            && !AgentGoToHouseDiscrete.AreHeuristicOptionsLocked
            && stepCount > 0
            && (stepCount % 20) == 0)
        {
            int prevOption = currentOption;
            currentOption = SampleWithinCurrentTier(currentOption);
            ResetWaterPathIfEntered(prevOption, currentOption, transform);
            UpdateOptionIconVisual();
        }

        int moveAction = actions.DiscreteActions[0];
        int rotateAction = actions.DiscreteActions[1];
        int collectAction = actions.DiscreteActions[2];

        bool collectHeld = collectAction == 1;
        bool collectDoRelevant = currentOption == OptionFlower
            || currentOption == OptionKiss
            || currentOption == OptionWater
            || currentOption == OptionFood
            || currentOption == OptionHeat;
        bool collectReady = collectHeld && _doCooldownRemaining <= 0f && collectDoRelevant;

        if (collectReady && animator != null && doActionAnimTrigger.Length > 0)
            animator.SetTrigger(doActionAnimTrigger);

        bool collectedWaterThisStep = false;
        bool ateSheepThisStep = false;
        bool collectedFlowerThisStep = false;

        if (collectReady)
        {
            if (damageOnDoIfZombieNearby && zombieDamageOnDo > 0 && IsZombieNearbyForDo())
            {
                TakeDamage(zombieDamageOnDo);
                HeroDamageFeedback.Play(transform);
            }
            if (knockbackZombieOnDo)
                KnockbackNearbyZombiesOnLilyDo();

            if (applyEmptyDoActionPenalty && emptyDoActionPenalty < 0f && !IsNearAnyDoInteractable())
                AddReward(emptyDoActionPenalty);

            if (TryCollectWater())
            {
                collectedWaterThisStep = true;
                if (currentOption == OptionWater && waterCollectReward != 0f)
                {
                    AddReward(waterCollectReward);
                    FloatingRewardPopup.ShowGotWater(transform, waterCollectReward);
                }
                GameSfx.PlayFood(source: transform);
            }

            if (TryEatSheep())
            {
                ateSheepThisStep = true;
                if (currentOption == OptionFood && sheepEatReward != 0f)
                {
                    AddReward(sheepEatReward);
                    GameSfx.PlayFood(source: transform);
                    FloatingRewardPopup.ShowGotFood(transform, sheepEatReward);
                }
            }
        }

        // BranchSizes Lily: move=3, rotate=3, collect=2.
        // move: 0=назад, 1=вперёд, 2=стой (2 часто в старых политиках — оставляем idle);
        // rotate: 0=влево, 1=вправо, 2=стой.
        float moveInput = 0f;
        if (moveAction == 1) moveInput = 1f;
        else if (moveAction == 0) moveInput = -1f;

        // Stage2: штраф за ходьбу назад (food/water/heat/flower).
        if (EnvTrainingConfig.IsLilyStage2PenaltiesMode()
            && IsLilySimpleTraining
            && moveInput < 0f
            && backwardWalkPenalty < 0f)
        {
            AddReward(backwardWalkPenalty);
        }

        float rotateInput = 0f;
        if (rotateAction == 0) rotateInput = -1f;
        else if (rotateAction == 1) rotateInput = 1f;

        transform.Rotate(0f, rotateInput * rotationSpeed * Time.deltaTime, 0f);

        if (ControllerReady)
        {
            if (controller.isGrounded)
                verticalVelocity = verticalVelocity < 0f ? -2f : verticalVelocity;
            else
                verticalVelocity += gravity * Time.deltaTime;

            Vector3 move = transform.forward * moveInput * moveSpeed + Vector3.up * verticalVelocity;
            controller.Move(move * Time.deltaTime);
            AgentFootsteps.NotifyMovement(gameObject, Mathf.Abs(moveInput) * moveSpeed);
        }

        _lastPlanarMoveInput = moveInput;

        AddReward(stepPenalty);
        
        // Reward for facing Jack (dense shaping): only for option "kiss" (1) and when Jack is known.
        if (currentOption == 1 && lookAtJackRewardScale != 0f)
        {
            Transform lookTarget = ResolveJackKissTransform();
            if (lookTarget != null)
            {
            Vector3 toJack = lookTarget.position - transform.position;
            toJack.y = 0f;
            if (toJack.sqrMagnitude > 1e-6f)
            {
                toJack.Normalize();
                Vector3 fwd = transform.forward;
                fwd.y = 0f;
                fwd.Normalize();
                float dot = Vector3.Dot(fwd, toJack); // [-1,1]
                if (dot > lookAtJackMinDot)
                {
                    // Map [minDot..1] -> [0..1]
                    float t = (dot - lookAtJackMinDot) / Mathf.Max(1e-6f, 1f - lookAtJackMinDot);
                    AddReward(lookAtJackRewardScale * Mathf.Clamp01(t));
                }
            }
            }
        }

        // Затухание счётчиков со временем
        flowerDecayTimer += Time.deltaTime;
        if (flowerDecayTimer >= flowerDecayInterval)
        {
            flowerDecayTimer = 0f;
            if (FlowerCount > 0) FlowerCount--;
        }
        loveDecayTimer += Time.deltaTime;
        if (loveDecayTimer >= loveDecayInterval)
        {
            loveDecayTimer = 0f;
            if (Love > 0) Love--;
        }

        if (currentOption == OptionFlower)
        {
            bool collected = false;
            if (collectReady)
                collected = TryCollectFlower();

            if (collected)
            {
                collectedFlowerThisStep = true;
                FlowerCount = Mathf.Min(maxFlowerCount, FlowerCount + 1);
                AddReward(collectReward);
                FloatingRewardPopup.ShowCollectedFlower(transform, collectReward);
                prevFlowerDist = -1f;
            }
            else if (GetNearestFlower(out _, out float currDist))
            {
                if (prevFlowerDist > 0f)
                    AddReward((prevFlowerDist - currDist) * moveTowardsFlowerRewardScale);
                prevFlowerDist = currDist;
            }
            else
                prevFlowerDist = -1f;
            prevJackDist = -1f;
            prevWaterDist = -1f;
            prevSheepDist = -1f;
            prevHouseDist = -1f;
        }
        else if (currentOption == OptionKiss)
        {
            // Опция: поцелуй — один раз на фронте DO + кулдаун, в радиусе поцелуя
            float currJackDist = GetDistanceToJack(out _);
            if (collectReady && TryPerformKiss(out _))
            {
                prevJackDist = -1f;
            }
            else if (ResolveJackKissTransform() != null)
            {
                if (prevJackDist > 0f)
                    AddReward((prevJackDist - currJackDist) * moveTowardsJackRewardScale);
                prevJackDist = currJackDist;
            }
            else
                prevJackDist = -1f;
            prevFlowerDist = -1f;
            prevWaterDist = -1f;
            prevSheepDist = -1f;
            prevHouseDist = -1f;
        }
        else if (currentOption == OptionWater)
        {
            var waterPath = WaterGoalPath.Get(transform);
            if (waterPath != null)
            {
                waterPath.ProcessStep(transform, AddReward);
                prevWaterDist = -1f;
            }
            else
            {
                var envRoot = TrainingEnvSpace.FindRoot(transform);
                if (WaterSource.TryFindNearestDistance(transform, envRoot, out float currWaterDist))
                {
                    if (prevWaterDist > 0f)
                        AddReward((prevWaterDist - currWaterDist) * 0.3f);
                    prevWaterDist = currWaterDist;
                }
                else
                    prevWaterDist = -1f;
            }

            prevFlowerDist = -1f;
            prevJackDist = -1f;
            prevSheepDist = -1f;
            prevHouseDist = -1f;
        }
        else if (currentOption == OptionFood)
        {
            if (GetNearestSheep(out _, out float currSheepDist))
            {
                if (prevSheepDist > 0f)
                    AddReward((prevSheepDist - currSheepDist) * moveTowardsSheepRewardScale);
                prevSheepDist = currSheepDist;
            }
            else
                prevSheepDist = -1f;

            prevFlowerDist = -1f;
            prevJackDist = -1f;
            prevWaterDist = -1f;
            prevHouseDist = -1f;
        }
        else if (currentOption == OptionHeat)
        {
            ResolveHouseTarget();
            if (houseTarget != null)
            {
                float prevDist = Vector3.Distance(prevPosition, houseTarget.position);
                float currDist = Vector3.Distance(transform.position, houseTarget.position);
                float reward = (prevDist - currDist) * moveTowardsHouseWhenColdRewardScale;
                AddReward(reward);
                prevHouseDist = currDist;
            }
            else
                prevHouseDist = -1f;

            prevFlowerDist = -1f;
            prevJackDist = -1f;
            prevWaterDist = -1f;
            prevSheepDist = -1f;
        }
        if (collectReady)
            _doCooldownRemaining = Mathf.Max(0f, collectActionCooldownSeconds);

        _lastCollectAction = collectAction;

        prevPosition = transform.position;

        stepCount++;
        if (IsLilySimpleTraining)
        {
            TryEndLilySimpleTrainingEpisode(
                collectedFlowerThisStep,
                collectedWaterThisStep,
                ateSheepThisStep);
        }
        else if (!IsLilySimpleTraining
            && MaxStep > 0
            && stepCount >= MaxStep)
        {
            EvalEpisodeTracker.NotifyEpisodeEnded();
            NotifyEpisodeEndingForStats();
            EndEpisode();
        }
    }

    void TryEndLilySimpleTrainingEpisode(bool collectedFlower, bool collectedWater, bool ateSheep)
    {
        if (_trainingConfig == null)
            return;

        if (_trainingConfig.StepPenalty != 0f)
            AddReward(_trainingConfig.StepPenalty);

        // Не рвём на первом успехе — только MaxStep / timeout.
        // Важно: при MaxStep тоже Record, иначе Agent сам EndEpisode без SR → графики n=0.
        bool hitMaxStep = MaxStep > 0 && stepCount >= MaxStep;
        bool hitTimeout = _trainingConfig.ResolveSimpleEpisodeTimeoutSeconds() > 0f
            && Time.unscaledTime - _episodeStartTime >= _trainingConfig.ResolveSimpleEpisodeTimeoutSeconds();
        if (hitMaxStep || hitTimeout)
        {
            EvalEpisodeTracker.NotifyEpisodeEnded();
            EndLilySimpleTrainingEpisode();
        }
    }

    void EndLilySimpleTrainingEpisode()
    {
        if (_trainingConfig != null)
        {
            var task = _trainingConfig.ResolveTask();
            if (TrainingTaskSuccessTracker.ShouldTrack(task))
            {
                bool success = TrainingTaskSuccessTracker.EvaluateLilySuccess(
                    task, this, _episodeSheepEaten);
                TrainingTaskSuccessTracker.Record(task, success);
            }
        }

        NotifyEpisodeEndingForStats();
        EndEpisode();
    }

    void RecordSimpleTrainingFailureIfNeeded()
    {
        if (_trainingConfig == null)
            return;

        var task = _trainingConfig.ResolveTask();
        if (TrainingTaskSuccessTracker.ShouldTrack(task))
            TrainingTaskSuccessTracker.Record(task, false);
    }

    bool IsLilyWaterPathComplete()
    {
        var path = WaterGoalPath.Get(transform);
        return path != null && path.HasCompletedPath(transform);
    }

    /// <summary>Curriculum train: только цветы/поцелуй. На presentation/stream — полный цикл выживания.</summary>
    bool UsesLilyCurriculumLeisureOnly()
    {
        if (!curriculumNoShootNoZombie)
            return false;
        if (_resolvedLilyTask == EnvTrainingTask.PresentationFull
            && TrainingEnvSpace.IsPresentationTransform(transform))
            return false;
        return true;
    }

    bool ShouldUseOptionUtilitySampling()
    {
        if (IsLilySimpleTraining)
            return false;
        if (_resolvedLilyTask == EnvTrainingTask.PresentationFull
            && TrainingEnvSpace.IsPresentationTransform(transform))
            return true;
        if (UsesLilyCurriculumLeisureOnly())
            return useUtilitySoftmaxSampling;
        return useUtilitySoftmaxSampling;
    }

    bool IsLeisureOption(int option) =>
        option == OptionFlower || option == OptionKiss;

    bool IsSurvivalOption(int option) =>
        option == OptionFood || option == OptionWater || option == OptionHeat;

    bool ShouldUseSurvivalOptions() =>
        !UsesLilyCurriculumLeisureOnly() && !AreSurvivalNeedsSatisfied();

    void RefreshOptionTierIfNeeded()
    {
        bool survivalMode = ShouldUseSurvivalOptions();
        if (survivalMode && IsLeisureOption(currentOption))
        {
            int prevOption = currentOption;
            currentOption = SampleSurvivalOptionUtilitySoftmax(currentOption);
            ResetWaterPathIfEntered(prevOption, currentOption, transform);
            UpdateOptionIconVisual();
        }
        else if (!survivalMode && IsSurvivalOption(currentOption))
        {
            int prevOption = currentOption;
            currentOption = SampleLeisureOptionUtilitySoftmax(currentOption);
            ResetWaterPathIfEntered(prevOption, currentOption, transform);
            UpdateOptionIconVisual();
        }
    }

    bool IsWaterNeedSatisfied() => WaterCount >= 4;

    bool IsFoodNeedSatisfied() => Satiety >= Mathf.Max(1, maxSatiety / 2);

    bool IsHeatNeedSatisfied() =>
        // Раньше startHeat/2 (10 при startHeat=20): при старте presentation=15 опция Fire
        // сразу «закрыта», Лили почти не идёт к костру. Нужно почти полное тепло.
        Heat >= Mathf.Max(1, startHeat > 0 ? startHeat - 2 : 18);

    bool IsSurvivalOptionNeedSatisfied(int option) => option switch
    {
        OptionFood => IsFoodNeedSatisfied(),
        OptionWater => IsWaterNeedSatisfied(),
        OptionHeat => IsHeatNeedSatisfied(),
        _ => true
    };

    void RefreshSurvivalSubOptionIfNeeded()
    {
        if (!ShouldUseSurvivalOptions() || !IsSurvivalOption(currentOption))
            return;
        if (!IsSurvivalOptionNeedSatisfied(currentOption))
            return;

        int next = PickMostNeededSurvivalOption();
        if (next == currentOption)
            return;

        int prevOption = currentOption;
        currentOption = next;
        ResetWaterPathIfEntered(prevOption, currentOption, transform);
        UpdateOptionIconVisual();
    }

    int PickMostNeededSurvivalOption()
    {
        if (Heat <= 0)
            return OptionHeat;
        if (Satiety <= 0)
            return OptionFood;
        if (WaterCount <= 0)
            return OptionWater;
        if (!IsHeatNeedSatisfied())
            return OptionHeat;
        if (!IsFoodNeedSatisfied())
            return OptionFood;
        if (!IsWaterNeedSatisfied())
            return OptionWater;
        return SampleSurvivalOptionUtilitySoftmax(currentOption);
    }

    bool AreSurvivalNeedsSatisfied()
    {
        return IsWaterNeedSatisfied() && IsFoodNeedSatisfied() && IsHeatNeedSatisfied();
    }

    float GetNearestWaterDistance()
    {
        if (prevWaterDist > 0f)
            return prevWaterDist;

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (WaterSource.TryFindNearestDistance(transform, envRoot, out float dist))
            return dist;
        return 999f;
    }

    float GetNearestHouseDistance()
    {
        ResolveHouseTarget();
        if (houseTarget == null)
            return 999f;
        return Vector3.Distance(transform.position, houseTarget.position);
    }

    int SampleOptionUtilitySoftmax(int currentOpt)
    {
        if (UsesLilyCurriculumLeisureOnly() || AreSurvivalNeedsSatisfied())
            return SampleLeisureOptionUtilitySoftmax(currentOpt);
        return SampleSurvivalOptionUtilitySoftmax(currentOpt);
    }

    int SampleWithinCurrentTier(int currentOpt)
    {
        if (ShouldUseSurvivalOptions())
            return SampleSurvivalOptionUtilitySoftmax(currentOpt);
        return SampleLeisureOptionUtilitySoftmax(currentOpt);
    }

    int SampleSurvivalOptionUtilitySoftmax(int currentOpt)
    {
        float heatRatio = startHeat > 0 ? (float)Heat / startHeat : 0f;
        float satietyRatio = maxSatiety > 0 ? (float)Satiety / maxSatiety : 0f;
        float waterRatio = Mathf.Clamp01(WaterCount / 12f);

        float needHeat = Mathf.Clamp01(1f - heatRatio);
        float needFood = Mathf.Clamp01(1f - satietyRatio);
        float needWater = Mathf.Clamp01(1f - waterRatio);

        float accessFood = DistanceToAccess(GetNearestSheep(out _, out float dSheep) ? dSheep : 999f);
        float accessWater = DistanceToAccess(GetNearestWaterDistance());
        float accessHouse = DistanceToAccess(GetNearestHouseDistance());

        float stickFood = currentOpt == OptionFood && needFood > 0.15f ? 1f : 0f;
        float stickWater = currentOpt == OptionWater && needWater > 0.15f ? 1f : 0f;
        float stickHeat = currentOpt == OptionHeat && needHeat > 0.15f ? 1f : 0f;

        float epsFood = Random.Range(-noise, noise);
        float epsWater = Random.Range(-noise, noise);
        float epsHeat = Random.Range(-noise, noise);

        float uFood = 2.5f * needFood + 1.0f * accessFood * needFood + stickinessBonus * stickFood + epsFood;
        float uWater = 2.5f * needWater + 1.0f * accessWater * needWater + stickinessBonus * stickWater + epsWater;
        float uHeat = 2.5f * needHeat + 1.0f * accessHouse * needHeat + stickinessBonus * stickHeat + epsHeat;

        return SoftmaxSample3(uFood, uWater, uHeat, OptionFood, OptionWater, OptionHeat, Mathf.Max(0.0001f, tau));
    }

    int SampleLeisureOptionUtilitySoftmax(int currentOpt)
    {
        float flowerRatio = maxFlowerCount > 0 ? (float)FlowerCount / maxFlowerCount : 0f;
        float loveRatio = maxLove > 0 ? (float)Love / maxLove : 0f;
        float needFlowers = Mathf.Clamp01(1f - flowerRatio);
        float needKiss = Mathf.Clamp01(1f - loveRatio);

        float distFlower = GetNearestFlower(out _, out float dF) ? dF : 999f;
        float distJack = GetDistanceToJack(out _);
        float accessFlowers = DistanceToAccess(distFlower);
        float accessKiss = DistanceToAccess(distJack);

        float stickFlowers = currentOpt == OptionFlower ? 1f : 0f;
        float stickKiss = currentOpt == OptionKiss ? 1f : 0f;

        float eps0 = Random.Range(-noise, noise);
        float eps1 = Random.Range(-noise, noise);

        float u0 = 2.5f * needFlowers + 1.0f * accessFlowers + stickinessBonus * stickFlowers + eps0;
        float u1 = 2.5f * needKiss + 1.0f * accessKiss + stickinessBonus * stickKiss + eps1;

        return SoftmaxSample2(u0, u1, Mathf.Max(0.0001f, tau)) == 0 ? OptionFlower : OptionKiss;
    }

    private float DistanceToAccess(float distance)
    {
        if (float.IsNaN(distance) || float.IsInfinity(distance)) return 0f;
        if (accessMaxDistance <= 0.0001f) return 0f;
        float t = Mathf.Clamp01(distance / accessMaxDistance);
        return 1f - t;
    }

    private static int SoftmaxSample2(float u0, float u1, float temperature)
    {
        float a0 = u0 / temperature;
        float a1 = u1 / temperature;
        float m = Mathf.Max(a0, a1);
        float e0 = Mathf.Exp(a0 - m);
        float e1 = Mathf.Exp(a1 - m);
        float p0 = e0 / (e0 + e1);
        return Random.value < p0 ? 0 : 1;
    }

    static int SoftmaxSample3(float u0, float u1, float u2, int opt0, int opt1, int opt2, float temperature)
    {
        float a0 = u0 / temperature;
        float a1 = u1 / temperature;
        float a2 = u2 / temperature;
        float m = Mathf.Max(a0, Mathf.Max(a1, a2));
        float e0 = Mathf.Exp(a0 - m);
        float e1 = Mathf.Exp(a1 - m);
        float e2 = Mathf.Exp(a2 - m);
        float sum = e0 + e1 + e2;
        float r = Random.value * sum;
        if (r < e0) return opt0;
        if (r < e0 + e1) return opt1;
        return opt2;
    }

    private static Vector3 SafeClosestPointOnCollider(Collider c, Vector3 from)
    {
        if (c == null)
            return from;
        if (c is MeshCollider mesh && !mesh.convex)
            return c.bounds.ClosestPoint(from);
        if (c is BoxCollider || c is SphereCollider || c is CapsuleCollider || c is MeshCollider)
            return c.ClosestPoint(from);
        return c.bounds.ClosestPoint(from);
    }

    private static float HarvestReachDistance(Vector3 from, Collider c)
    {
        return Vector3.Distance(from, SafeClosestPointOnCollider(c, from));
    }

    bool IsNearAnyDoInteractable()
    {
        Vector3 origin = transform.position;
        if (flowerLayer.value == 0)
            ResolveFlowerSpawner();

        if (flowerLayer.value != 0)
        {
            Collider[] flowers = Physics.OverlapSphere(origin, collectDistance, flowerLayer);
            for (int i = 0; i < flowers.Length; i++)
            {
                if (flowers[i] == null || !flowers[i].gameObject.activeInHierarchy)
                    continue;
                if (HarvestReachDistance(origin, flowers[i]) <= collectDistance)
                    return true;
            }
        }

        float sheepR = eatDistance * 2f;
        Collider[] sheepHits = Physics.OverlapSphere(origin, sheepR, sheepLayer);
        for (int i = 0; i < sheepHits.Length; i++)
        {
            if (sheepHits[i] == null)
                continue;
            if (HarvestReachDistance(origin, sheepHits[i]) <= sheepR)
                return true;
        }

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (WaterSource.TryFindNearestDistance(transform, envRoot, out float waterDist)
            && waterDist <= waterCollectDistance)
            return true;

        float zombieR = Mathf.Max(zombieNearbyRadiusOnDo, zombieKnockbackRadiusOnDo);
        if (zombieR > 0f && zombieLayer.value != 0)
        {
            Collider[] zombies = Physics.OverlapSphere(origin, zombieR, zombieLayer);
            for (int i = 0; i < zombies.Length; i++)
            {
                if (zombies[i] == null)
                    continue;
                if (zombies[i].GetComponentInParent<ZombieChase>() != null
                    || zombies[i].GetComponentInParent<ZombieAttack>() != null)
                    return true;
            }
        }

        float personR = Mathf.Max(zombieKnockbackRadiusOnDo, 1.6f);
        foreach (var jack in Object.FindObjectsByType<AgentGoToHouseDiscrete>(FindObjectsSortMode.None))
        {
            if (jack == null || !jack.isActiveAndEnabled)
                continue;
            if (envRoot != null && !TrainingEnvSpace.IsDescendantOf(jack.transform, envRoot))
                continue;
            if (Vector3.Distance(origin, jack.transform.position) <= personR)
                return true;
        }

        return false;
    }

    private GameObject GetFlowerInstanceRoot(Collider hit)
    {
        Transform t = hit.transform;
        if (flowerSpawner != null)
        {
            Transform sp = flowerSpawner.transform;
            for (; t != null; t = t.parent)
            {
                if (t.parent == sp)
                    return t.gameObject;
            }
        }
        return hit.gameObject;
    }

    private bool TryCollectFlower()
    {
        if (flowerLayer.value == 0)
            ResolveFlowerSpawner();

        Vector3 origin = transform.position;
        float reach = Mathf.Clamp(collectDistance, 0.5f, 2.5f);
        Collider[] hits = Physics.OverlapSphere(origin, reach, flowerLayer);

        GameObject bestRoot = null;
        float bestDist = float.MaxValue;
        foreach (var c in hits)
        {
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            GameObject root = GetFlowerInstanceRoot(c);
            float d = FlowerSpawner.DistanceToFlowerBounds(origin, root != null ? root : c.gameObject);
            if (d > reach) continue;
            if (d < bestDist)
            {
                bestDist = d;
                bestRoot = root;
            }
        }

        if (bestRoot == null && flowerSpawner != null)
            bestRoot = flowerSpawner.FindNearestFlowerInReach(origin, reach);

        if (bestRoot == null)
            return false;

        // Финальная проверка по pivot+bounds — не собирать «через карту».
        float finalDist = FlowerSpawner.DistanceToFlowerBounds(origin, bestRoot);
        if (finalDist > reach)
            return false;

        flowerSpawner?.NotifyFlowerCollected(bestRoot);
        Destroy(bestRoot);
        return true;
    }

    bool GetNearestSheep(out GameObject nearestSheep, out float distance)
    {
        nearestSheep = null;
        distance = float.MaxValue;

        if (sheepLayer.value == 0)
            ResolveSheepSpawner();

        Collider[] hits = Physics.OverlapSphere(transform.position, eatDistance * 3f, sheepLayer);
        foreach (var hit in hits)
        {
            if (hit == null || !hit.gameObject.activeInHierarchy)
                continue;

            float d = HarvestReachDistance(transform.position, hit);
            if (d >= distance)
                continue;

            distance = d;
            nearestSheep = GetSheepInstanceRoot(hit);
        }

        return nearestSheep != null;
    }

    void ResolveSheepSpawner()
    {
        if (sheepSpawner != null)
            return;

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot != null)
            sheepSpawner = envRoot.GetComponentInChildren<SheepSpawner>(true);

        if (sheepLayer.value == 0)
        {
            int sheepLayerId = LayerMask.NameToLayer("Sheep");
            if (sheepLayerId >= 0)
                sheepLayer = 1 << sheepLayerId;
        }
    }

    void ResolveFlowerSpawner()
    {
        if (flowerSpawner != null)
            return;

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot != null)
            flowerSpawner = envRoot.GetComponentInChildren<FlowerSpawner>(true);

        if (flowerLayer.value == 0)
        {
            int flowerLayerId = LayerMask.NameToLayer("Flower");
            if (flowerLayerId >= 0)
                flowerLayer = 1 << flowerLayerId;
        }
    }

    void ResolveHouseTarget()
    {
        if (houseTarget != null)
            return;

        var jack = FindJackInEnv();
        if (jack != null && jack.houseTargetPublic != null)
        {
            houseTarget = jack.houseTargetPublic;
            return;
        }

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot == null)
            return;

        var home = envRoot.Find("HomeSpot");
        if (home == null)
        {
            foreach (var t in envRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == "HomeSpot")
                {
                    home = t;
                    break;
                }
            }
        }

        if (home != null)
        {
            houseTarget = home;
            return;
        }

        // CityScene и т.п.: создать точку дома, иначе Jack/Lily падают на houseTarget.
        var go = new GameObject("HomeSpot");
        go.tag = "House";
        go.transform.SetParent(envRoot, false);
        go.transform.position = transform.position;
        houseTarget = go.transform;
        Debug.LogWarning(
            $"[Lily] HomeSpot не найден — создан runtime в {envRoot.name}",
            this);
    }

    AgentGoToHouseDiscrete FindJackInEnv()
    {
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot == null)
            return null;

        AgentGoToHouseDiscrete hero = null;
        AgentGoToHouseDiscrete active = null;
        AgentGoToHouseDiscrete fallback = null;

        var jacks = envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < jacks.Length; i++)
        {
            var jack = jacks[i];
            if (jack == null || TrainingEnvSpace.IsGeorgeAgent(jack))
                continue;

            if (jack.gameObject.name == "JackHero" && jack.gameObject.activeInHierarchy)
                hero = jack;
            else if (jack.gameObject.activeInHierarchy && active == null)
                active = jack;
            else if (fallback == null)
                fallback = jack;
        }

        if (hero != null)
            return hero;
        if (active != null)
            return active;
        return fallback;
    }

    AgentGoToHouseDiscrete FindGeorgeInEnv()
    {
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot == null)
            return null;

        AgentGoToHouseDiscrete hero = null;
        AgentGoToHouseDiscrete named = null;
        AgentGoToHouseDiscrete active = null;
        AgentGoToHouseDiscrete fallback = null;

        var agents = envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < agents.Length; i++)
        {
            var agent = agents[i];
            if (agent == null || !TrainingEnvSpace.IsGeorgeAgent(agent))
                continue;

            if (agent.gameObject.name == "GeorgeHero" && agent.gameObject.activeInHierarchy)
                hero = agent;
            else if (agent.gameObject.name == "George")
                named = agent;
            else if (agent.gameObject.activeInHierarchy && active == null)
                active = agent;
            else if (fallback == null)
                fallback = agent;
        }

        if (hero != null)
            return hero;
        if (named != null && named.gameObject.activeInHierarchy)
            return named;
        if (active != null)
            return active;
        return fallback;
    }

    void UpdateWarmthAtCampfire()
    {
        if (_deathSequenceStarted)
        {
            _warmthGainTimer = 0f;
            return;
        }

        if (!IsNearBurningCampfire())
        {
            _warmthGainTimer = 0f;
            return;
        }

        TryGainWarmthAtCampfire();
    }

    bool IsNearBurningCampfire()
    {
        var jack = FindJackInEnv();
        if (jack == null || jack.CampfireBurnSecondsRemaining <= 0f)
            return false;

        // Как у Геры: якорь = огонь, не центр дома.
        Vector3 warmthPos = jack.GetCampfireWarmthWorldPosition();
        return Vector3.Distance(transform.position, warmthPos) <= campfireWarmthRadius;
    }

    public bool IsOnHousePublic() => IsOnHouse();

    public bool IsCampfireBurningInEnvPublic() => IsCampfireBurningInEnv();

    bool IsOnHouse()
    {
        ResolveHouseTarget();
        if (houseTarget == null)
            return false;

        return Vector3.Distance(transform.position, houseTarget.position) <= houseRadius;
    }

    float GetDistanceToHouse(out Transform target)
    {
        ResolveHouseTarget();
        target = houseTarget;
        if (houseTarget == null)
            return float.MaxValue;

        return Vector3.Distance(transform.position, houseTarget.position);
    }

    bool IsCampfireBurningInEnv()
    {
        var jack = FindJackInEnv();
        return jack != null && jack.CampfireBurnSecondsRemaining > 0f;
    }

    void TryGainWarmthAtCampfire()
    {
        _warmthGainTimer += Time.deltaTime;
        if (_warmthGainTimer < warmthGainInterval)
            return;

        _warmthGainTimer = 0f;
        int before = Heat;
        Heat++;
        // Ревард при любом нагреве: иначе при опции еда/вода костёр греет без сигнала.
        if (warmthRewardAtCampfire != 0f && Heat > before)
        {
            AddReward(warmthRewardAtCampfire);
            FloatingRewardPopup.ShowWarmedUp(transform, warmthRewardAtCampfire);
        }
    }

    static float GetOptionObservationBit0(int option) => (option & 1) != 0 ? 1f : 0f;
    static float GetOptionObservationBit1(int option) => (option & 2) != 0 ? 1f : 0f;
    static float GetOptionObservationBit2(int option) => (option & 4) != 0 ? 1f : 0f;

    GameObject GetSheepInstanceRoot(Collider hit)
    {
        var wander = hit.GetComponentInParent<SheepWander>();
        if (wander != null)
            return wander.gameObject;

        Transform t = hit.transform;
        if (sheepSpawner != null)
        {
            Transform sp = sheepSpawner.transform;
            for (; t != null; t = t.parent)
            {
                if (t.parent == sp)
                    return t.gameObject;
            }
        }

        return hit.gameObject;
    }

    bool TryEatSheep()
    {
        if (sheepLayer.value == 0)
            ResolveSheepSpawner();

        Vector3 origin = transform.position;
        Collider[] hits = Physics.OverlapSphere(origin, eatDistance, sheepLayer);

        GameObject bestRoot = null;
        float bestDist = float.MaxValue;
        foreach (var c in hits)
        {
            if (c == null) continue;
            float d = HarvestReachDistance(origin, c);
            if (d > eatDistance) continue;
            GameObject root = GetSheepInstanceRoot(c);
            if (root != null && root.GetComponentInParent<ViewerSimpleAgent>() != null)
                continue;
            if (d < bestDist)
            {
                bestDist = d;
                bestRoot = root;
            }
        }

        if (bestRoot == null)
            return false;

        Satiety = Mathf.Min(maxSatiety, Satiety + 2);
        Destroy(bestRoot);
        sheepSpawner?.NotifySheepEaten();
        _episodeSheepEaten++;
        return true;
    }

    void UpdateSatietyDecay()
    {
        if (_deathSequenceStarted)
            return;

        satietyTimer += Time.unscaledDeltaTime;
        if (satietyTimer < satietyDecayInterval)
            return;

        satietyTimer = 0f;
        if (Satiety > 0)
            Satiety--;
    }

    void UpdateStarving()
    {
        if (_deathSequenceStarted || Satiety > 0)
        {
            _hungerTimer = 0f;
            return;
        }

        _hungerTimer += Time.unscaledDeltaTime;
        if (_hungerTimer < hungerDamageInterval)
            return;

        _hungerTimer = 0f;
        if (hungerPenaltyPerTick != 0f && stepCount != _lastHungerRewardStep)
        {
            _lastHungerRewardStep = stepCount;
            AddReward(hungerPenaltyPerTick);
            FloatingRewardPopup.ShowHungry(transform, hungerPenaltyPerTick);
        }
        if (hungerDamageAmount > 0)
            TakeDamage(hungerDamageAmount, applyHpLossPenalty: false);
    }

    void UpdateHeatDecay()
    {
        if (_deathSequenceStarted)
            return;

        heatTimer += Time.unscaledDeltaTime;
        if (heatTimer < heatDecayInterval)
            return;

        heatTimer = 0f;
        if (Heat > 0)
            Heat--;
    }

    void UpdateFreezing()
    {
        if (_deathSequenceStarted || Heat > 0)
        {
            _freezeTimer = 0f;
            return;
        }

        _freezeTimer += Time.unscaledDeltaTime;
        if (_freezeTimer < freezeDamageInterval)
            return;

        _freezeTimer = 0f;
        if (freezePenaltyPerTick != 0f && stepCount != _lastFreezeRewardStep)
        {
            _lastFreezeRewardStep = stepCount;
            AddReward(freezePenaltyPerTick);
            FloatingRewardPopup.ShowFreezing(transform, freezePenaltyPerTick);
        }
        if (freezeDamageAmount > 0)
            TakeDamage(freezeDamageAmount, applyHpLossPenalty: false);
    }

    bool TryCollectWater()
    {
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (!WaterSource.TryCollect(transform, waterCollectDistance, envRoot, out int amount))
            return false;

        WaterCount += Mathf.Max(1, waterPerCollect > 0 ? waterPerCollect : amount);
        _episodeWaterCollected++;
        return true;
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;

        int moveAction = 2;
        if (Input.GetKey(KeyCode.UpArrow))
            moveAction = 1;
        else if (Input.GetKey(KeyCode.DownArrow))
            moveAction = 0;

        int rotateAction = 2;
        if (Input.GetKey(KeyCode.RightArrow))
            rotateAction = 1;
        else if (Input.GetKey(KeyCode.LeftArrow))
            rotateAction = 0;

        int collectAction = Input.GetMouseButton(1) ? 1 : 0;

        d[0] = moveAction;
        d[1] = rotateAction;
        d[2] = collectAction;
    }

    private void PathStep()
    {
        if (controller == null) controller = GetComponent<CharacterController>();
        if (animator == null) animator = GetComponent<Animator>();

        if (pathRoot == null || pathRoot.childCount == 0)
            return;

        if (_pathWaitLeft > 0f)
        {
            _pathWaitLeft -= Time.deltaTime;
            if (animator != null)
                animator.SetFloat("Speed", 0f);
            return;
        }

        if (_pathIndex < 0 || _pathIndex >= pathRoot.childCount)
            _pathIndex = 0;

        var target = pathRoot.GetChild(_pathIndex);
        if (target == null)
        {
            _pathIndex = (_pathIndex + 1) % pathRoot.childCount;
            return;
        }

        Vector3 delta = target.position - transform.position;
        delta.y = 0f;
        float dist = delta.magnitude;

        // Поворот плавно в сторону движения
        if (delta.sqrMagnitude > 1e-6f)
        {
            Quaternion look = Quaternion.LookRotation(delta.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, 1f - Mathf.Exp(-6f * Time.deltaTime));
        }

        // Гравитация + движение к точке
        if (ControllerReady)
        {
            if (controller.isGrounded)
                verticalVelocity = verticalVelocity < 0f ? -2f : verticalVelocity;
            else
                verticalVelocity += gravity * Time.deltaTime;

            float moveLen = moveSpeed * Time.deltaTime;
            Vector3 movePlanar = dist > 0.0001f ? delta.normalized * Mathf.Min(moveLen, dist) : Vector3.zero;
            Vector3 move = movePlanar + Vector3.up * verticalVelocity * Time.deltaTime;
            controller.Move(move);

            AgentFootsteps.NotifyMovement(gameObject, movePlanar.magnitude / Mathf.Max(Time.deltaTime, 1e-5f));

            if (animator != null)
            {
                float t = moveSpeed > 1e-4f ? Mathf.Clamp01(movePlanar.magnitude / (moveSpeed * Time.deltaTime + 1e-6f)) : 0f;
                if (walkAnimSpeedDamp > 0f)
                    animator.SetFloat("Speed", t, walkAnimSpeedDamp, Time.deltaTime);
                else
                    animator.SetFloat("Speed", t);
            }
        }

        if (dist <= pathArriveDistance)
        {
            _pathWaitLeft = Mathf.Max(0f, pathWaitSeconds);
            _pathIndex++;
            if (_pathIndex >= pathRoot.childCount)
                _pathIndex = pathLoop ? 0 : pathRoot.childCount - 1;
        }
    }
}
