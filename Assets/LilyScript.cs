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

    [Header("Option Sampling (Flowers/Kiss)")]
    [Tooltip("Если true, опция (цветы/поцелуй) выбирается по utility+softmax sampling каждые 20 шагов.")]
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

    private bool _lastShowOptionTaskIcon = true;

    [Header("Heat / House (опция «тепло»)")]
    [SerializeField] private Transform houseTarget;
    [SerializeField] private float houseRadius = 1.2f;
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
    [SerializeField] private float waterCollectDistance = 2.5f;
    [SerializeField] private float waterCollectReward = 8f;
    [SerializeField] private int waterPerCollect = 1;
    public int WaterCount { get; private set; }
    [SerializeField] private float waterDecayInterval = 5f;
    private float waterDecayTimer;

    [Header("Hunger / Satiety")]
    [SerializeField] private int maxSatiety = 20;
    public int Satiety { get; private set; }
    [SerializeField] private float satietyDecayInterval = 5f;
    private float satietyTimer;

    [Header("Starving (satiety = 0)")]
    [SerializeField] private float hungerDamageInterval = 0.25f;
    [SerializeField] private int hungerDamageAmount = 1;
    private float _hungerTimer;

    [Header("Heat")]
    [SerializeField] private int startHeat = 20;
    [Tooltip("Только для наблюдений ML: Heat / scale, не лимит инвентаря.")]
    [SerializeField] private int heatObservationScale = 20;
    public int Heat { get; private set; }
    [SerializeField] private float heatDecayInterval = 5f;
    private float heatTimer;

    [Header("Freezing (heat = 0)")]
    [SerializeField] private float freezeDamageInterval = 0.25f;
    [SerializeField] private int freezeDamageAmount = 1;
    private float _freezeTimer;

    [Header("Dehydrated (water = 0)")]
    [SerializeField] private float thirstDamageInterval = 0.25f;
    [SerializeField] private int thirstDamageAmount = 1;
    private float _thirstTimer;

    [Header("Animation (DO — сбор / поцелуй)")]
    [Tooltip("Trigger в Animator Lily (как у Jack).")]
    [SerializeField] private string doActionAnimTrigger = "Do";
    [Tooltip("Пауза между срабатываниями DO (сбор цветка или поцелуй).")]
    [SerializeField] private float collectActionCooldownSeconds = 0.45f;
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

    public int StepCount => stepCount;
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
        ResolveHouseTarget();

        // Как у Jack: не снимаем HP с агента за его же DO (старые значения в сцене).
        damageOnDoIfZombieNearby = false;
        zombieDamageOnDo = 0;

        if (zombieLayer.value == 0)
        {
            int zombieLayerId = LayerMask.NameToLayer("Zombie");
            if (zombieLayerId >= 0)
                zombieLayer = 1 << zombieLayerId;
        }
    }

    bool ShouldShowOptionTaskIcon() =>
        showOptionTaskIcon && TrainingEnvSpace.IsPresentationTransform(transform);

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

    private void Awake()
    {
        ConfigurePresentationControl();
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

        if (showOptionTaskIcon != _lastShowOptionTaskIcon)
        {
            _lastShowOptionTaskIcon = showOptionTaskIcon;
            UpdateOptionIconVisual();
        }

        ProcessManualOptionKeys();
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
        if (amount <= 0 || _deathSequenceStarted) return;
        hp = Mathf.Max(0, hp - amount);
        if (hpLossPenaltyPerHit != 0f)
            AddReward(-hpLossPenaltyPerHit);
        if (hp <= 0)
        {
            _deathSequenceStarted = true;
            EvalEpisodeTracker.NotifyEpisodeEnded();
            AgentDeathOverlay.ShowAndEndEpisode(this, AgentDeathOverlay.GetDeathMessageFor(this));
        }
    }

    public override void OnEpisodeBegin()
    {
        _deathSequenceStarted = false;
        if (TrainingEnvSpace.IsPresentationTransform(transform))
        {
            DeathFreeze.UnfreezeWorld();
            AgentDeathOverlay.Hide();
            BackgroundMusic.ResumeMusic();
        }

        stepCount = 0;
        _episodeStartTime = Time.unscaledTime;
        prevPosition = transform.position;
        prevFlowerDist = -1f;
        prevJackDist = -1f;
        prevWaterDist = -1f;
        prevSheepDist = -1f;
        FlowerCount = 0;
        Love = 0;
        WaterCount = 6;
        waterDecayTimer = 0f;
        Satiety = maxSatiety / 2;
        satietyTimer = 0f;
        _hungerTimer = 0f;
        Heat = startHeat;
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

        ConfigureTrainingControl();

        _trainingConfig = EnvTrainingConfig.Get(transform);
        _resolvedLilyTask = _trainingConfig != null
            ? _trainingConfig.ResolveTask()
            : EnvTrainingTask.PresentationFull;
        _trainingConfig?.ApplyForEpisodeBegin();

        if (IsLilySimpleTraining)
        {
            currentOption = _trainingConfig.ResolveFixedLilyOption();
            MaxStep = _trainingConfig.SimpleMaxSteps;
            if (_trainingConfig.FreezeNeeds)
            {
                Satiety = maxSatiety / 2;
                Heat = startHeat;
                WaterCount = 6;
            }
        }
        else if (useUtilitySoftmaxSampling)
            currentOption = SampleOptionUtilitySoftmax(currentOption);
        else
            currentOption = Random.Range(0, OptionCount);

        // Локальные координаты относительно корня Env (рядом с домом, как у Jack).
        const float groundY = -5.228786f;
        float minX = -2f;
        float maxX = 14f;
        float minZ = 10f;
        float maxZ = 17f;

        bool skipTeleport = !spawnAtFixedPosition && !followPath && !TrainingEnvSpace.HasMultipleTrainingEnvs();

        if (!skipTeleport)
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

        if (flowerSpawner != null)
            flowerSpawner.ResetFlowers();

        if (IsLilySimpleTraining)
        {
            var envRoot = TrainingEnvSpace.FindRoot(transform);
            PresentationWorldReset.ResetSpawners(envRoot);
        }

        UpdateOptionIconVisual();
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
            TakeDamage(thirstDamageAmount);
    }

    private bool GetNearestFlower(out GameObject nearestFlower, out float distance)
    {
        nearestFlower = null;
        distance = float.MaxValue;

        Collider[] hits = Physics.OverlapSphere(transform.position, MaxFlowerDistForObs, flowerLayer);
        foreach (var hit in hits)
        {
            if (hit == null || !hit.gameObject.activeInHierarchy) continue;
            float d = Vector3.Distance(transform.position, hit.transform.position);
            if (d < distance)
            {
                distance = d;
                nearestFlower = hit.gameObject;
            }
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
        Vector3 toTarget = c.ClosestPoint(origin) - origin;
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

            float d = Vector3.Distance(origin, c.ClosestPoint(origin));
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
            if (MaxStep > 0 && stepCount >= MaxStep)
            {
                EvalEpisodeTracker.NotifyEpisodeEnded();
                EndEpisode();
            }
            return;
        }

        // Utility sampling: пересэмпливаем каждые 20 шагов (если включено)
        if (!IsLilySimpleTraining && useUtilitySoftmaxSampling && stepCount > 0 && (stepCount % 20) == 0)
        {
            int prevOption = currentOption;
            currentOption = SampleOptionUtilitySoftmax(currentOption);
            ResetWaterPathIfEntered(prevOption, currentOption, transform);
            UpdateOptionIconVisual();
        }

        int moveAction = actions.DiscreteActions[0];
        int rotateAction = actions.DiscreteActions[1];
        int collectAction = actions.DiscreteActions[2];

        bool collectJustPressed = collectAction == 1 && _lastCollectAction != 1;
        bool collectDoRelevant = currentOption == OptionFlower
            || currentOption == OptionKiss
            || currentOption == OptionWater
            || currentOption == OptionFood
            || currentOption == OptionHeat;
        bool collectReady = collectJustPressed && _doCooldownRemaining <= 0f && collectDoRelevant;

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

            if (TryCollectWater())
            {
                collectedWaterThisStep = true;
                if (waterCollectReward != 0f)
                {
                    AddReward(waterCollectReward);
                    FloatingRewardPopup.ShowGotWater(transform, waterCollectReward);
                }
                GameSfx.PlayFood(source: transform);
            }

            if (TryEatSheep())
            {
                ateSheepThisStep = true;
                GameSfx.PlayFood(source: transform);
                FloatingRewardPopup.ShowGotFood(transform, 10f);
            }
        }

        // Дискретные действия как у Jack: ветка0 — 1 вперёд, 3 назад, 2 стой; ветка1 — 1/3 поворот, 2 не крутить
        float moveInput = 0f;
        if (moveAction == 1) moveInput = 1f;
        else if (moveAction == 3) moveInput = -1f;

        float rotateInput = 0f;
        if (rotateAction == 1) rotateInput = 1f;
        else if (rotateAction == 3) rotateInput = -1f;

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
            // Опция: собирать цветы — один раз на фронте DO + кулдаун
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
        else if (MaxStep > 0 && stepCount >= MaxStep)
        {
            EvalEpisodeTracker.NotifyEpisodeEnded();
            EndEpisode();
        }
    }

    private float _episodeStartTime;

    void TryEndLilySimpleTrainingEpisode(bool collectedFlower, bool collectedWater, bool ateSheep)
    {
        if (_trainingConfig == null)
            return;

        if (_trainingConfig.StepPenalty != 0f)
            AddReward(_trainingConfig.StepPenalty);

        if (_trainingConfig.EndOnSuccess)
        {
            switch (_trainingConfig.ResolveTask())
            {
                case EnvTrainingTask.LilyFlower when collectedFlower:
                case EnvTrainingTask.LilyFood when ateSheep:
                case EnvTrainingTask.LilyWater when collectedWater || IsLilyWaterPathComplete():
                case EnvTrainingTask.LilyHeat when IsOnHouse() && IsCampfireBurningInEnv():
                    if (_trainingConfig.SuccessReward != 0f)
                        AddReward(_trainingConfig.SuccessReward);
                    EvalEpisodeTracker.NotifyEpisodeEnded();
                    EndEpisode();
                    return;
            }
        }

        if (_trainingConfig.SimpleEpisodeTimeoutSeconds > 0f
            && Time.unscaledTime - _episodeStartTime >= _trainingConfig.SimpleEpisodeTimeoutSeconds)
        {
            EvalEpisodeTracker.NotifyEpisodeEnded();
            EndEpisode();
        }
    }

    bool IsLilyWaterPathComplete()
    {
        var path = WaterGoalPath.Get(transform);
        return path != null && path.HasCompletedPath(transform);
    }

    private int SampleOptionUtilitySoftmax(int currentOpt)
    {
        // need: чем меньше прогресс по "цветам/любви", тем выше потребность
        float flowerRatio = maxFlowerCount > 0 ? (float)FlowerCount / maxFlowerCount : 0f;
        float loveRatio = maxLove > 0 ? (float)Love / maxLove : 0f;
        float needFlowers = Mathf.Clamp01(1f - flowerRatio);
        float needKiss = Mathf.Clamp01(1f - loveRatio);

        // access: ближе цель -> больше access
        float distFlower = GetNearestFlower(out _, out float dF) ? dF : 999f;
        float distJack = GetDistanceToJack(out _);
        float accessFlowers = DistanceToAccess(distFlower);
        float accessKiss = DistanceToAccess(distJack);

        float stickFlowers = currentOpt == 0 ? 1f : 0f;
        float stickKiss = currentOpt == 1 ? 1f : 0f;

        float eps0 = Random.Range(-noise, noise);
        float eps1 = Random.Range(-noise, noise);

        float u0 = 2.5f * needFlowers + 1.0f * accessFlowers + stickinessBonus * stickFlowers + eps0;
        float u1 = 2.5f * needKiss + 1.0f * accessKiss + stickinessBonus * stickKiss + eps1;

        return SoftmaxSample2(u0, u1, Mathf.Max(0.0001f, tau));
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

    private static float HarvestReachDistance(Vector3 from, Collider c)
    {
        return Vector3.Distance(from, c.ClosestPoint(from));
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
        Vector3 origin = transform.position;
        Collider[] hits = Physics.OverlapSphere(origin, collectDistance, flowerLayer);

        GameObject bestRoot = null;
        float bestDist = float.MaxValue;
        foreach (var c in hits)
        {
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            float d = HarvestReachDistance(origin, c);
            if (d > collectDistance) continue;
            GameObject root = GetFlowerInstanceRoot(c);
            if (d < bestDist)
            {
                bestDist = d;
                bestRoot = root;
            }
        }

        if (bestRoot == null)
            return false;

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
        if (home != null)
            houseTarget = home;
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

        ResolveHouseTarget();
        if (houseTarget == null)
            return false;

        return Vector3.Distance(transform.position, houseTarget.position) <= houseRadius;
    }

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
        Heat++;
        if (warmthRewardAtCampfire != 0f && currentOption == OptionHeat)
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
        if (hungerDamageAmount > 0)
            TakeDamage(hungerDamageAmount);
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
        if (freezeDamageAmount > 0)
            TakeDamage(freezeDamageAmount);
    }

    bool TryCollectWater()
    {
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (!WaterSource.TryCollect(transform, waterCollectDistance, envRoot, out int amount))
            return false;

        WaterCount += Mathf.Max(1, waterPerCollect > 0 ? waterPerCollect : amount);
        return true;
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;

        int moveAction = 2;
        if (Input.GetKey(KeyCode.UpArrow))
            moveAction = 1;
        else if (Input.GetKey(KeyCode.DownArrow))
            moveAction = 3;

        int rotateAction = 2;
        if (Input.GetKey(KeyCode.RightArrow))
            rotateAction = 1;
        else if (Input.GetKey(KeyCode.LeftArrow))
            rotateAction = 3;

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
