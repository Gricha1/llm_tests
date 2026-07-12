using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public class AgentGoToHouseDiscrete : Agent, IHasHp
{


    [Header("Spawner")]
    [SerializeField] private TreeSpawner treeSpawner;

    [SerializeField] private SheepSpawner sheepSpawner;

    [Header("Spawn")]
    [Tooltip("Если true — Jack спавнится у дома в фиксированной позиции/повороте (не случайно).")]
    [SerializeField] private bool spawnAtHouseFixed = false;

    [SerializeField] private Vector3 fixedSpawnPosition = new Vector3(3.95f, -5.228786f, 14.87f);
    [SerializeField] private Vector3 fixedSpawnEuler = new Vector3(0f, 177.644073f, 0f);

    [Header("HUD")]
    [SerializeField] private bool showHudHpTopLeft = true;

    [Header("Option Sampling (Wood/Food/Zombie)")]
    [Tooltip("Если true, опция (дерево/еда) выбирается по utility+softmax sampling каждые 20 шагов.")]
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
    [SerializeField] private Sprite optionWoodSprite;
    [SerializeField] private Sprite optionFoodSprite;
    [SerializeField] private Sprite optionZombieSprite;
    [SerializeField] private Sprite optionWaterSprite;
    [SerializeField] private Sprite optionHeatSprite;
    [SerializeField] private Vector3 optionIconOffset = new Vector3(0f, 2.2f, 0f);
    [SerializeField] private float optionWoodIconScale = 0.55f;
    [SerializeField] private float optionFoodIconScale = 0.35f;
    [SerializeField] private float optionZombieIconScale = 0.8f;
    [SerializeField] private float optionWaterIconScale = 0.35f;
    [SerializeField] private float optionHeatIconScale = 0.45f;
    [Tooltip("Доп. множитель размера иконки, когда камера захвата/просмотра = CamOnJack.")]
    [SerializeField] private float optionIconScaleMultiplierCamOnJack = 0.55f;
    [SerializeField] private int optionIconSortingOrder = 100;
    [SerializeField] private bool optionIconFaceCamera = true;
    [Tooltip("Если задано — иконка разворачивается к этой камере; иначе MainCamera или камера с максимальным depth.")]
    [SerializeField] private Camera optionIconBillboardCamera;
    [Tooltip("Снять галочку, чтобы скрыть спрайт задачи (дерево/еда) над агентом.")]
    [SerializeField] private bool showOptionTaskIcon = true;

    private bool _lastShowOptionTaskIcon = true;
    private float _optionIconBaseScale = 0.35f;

    [SerializeField] private float eatDistance = 1.2f; // дистанция до овечки
    [SerializeField] private LayerMask sheepLayer;     // слой овечки

    private float prevSheepDist;
    private float prevZombieDist;
    private float prevWaterDist;
    private float prevHouseDist;

    /// <summary>0=дерево, 1=еда, 2=зомби, 3=вода, 4=огонь (только George).</summary>
    public const int OptionWood = 0;
    public const int OptionFood = 1;
    public const int OptionZombie = 2;
    public const int OptionWater = 3;
    public const int OptionHeat = 4;
    public const int OptionCount = 5;
    public const string GeorgeBehaviorName = "GeorgeLowLevelAgent";

    protected virtual string OptionIconObjectName => "JackOptionIcon";


    [Header("Fire / House")]
    [SerializeField] private float houseRadius = 1.2f;
    [SerializeField] private float moveTowardsHouseWhenColdRewardScale = 1f;
    [SerializeField] private float warmthGainInterval = 0.5f;
    [SerializeField] private float warmthRewardAtCampfire = 5f;
    [Tooltip("Сколько секунд горит один заряд дров (одна единица wood) у дома. Больше — дольше сжигание.")]
    [SerializeField] private float burnInterval = 0.35f;
    [Tooltip("Множитель длительности горения (таймер на костре). 5 = в 5 раз дольше.")]
    [SerializeField] private float burnDurationMultiplier = 5f;
    private int heatPerWood = 2;        // сколько тепла даёт 1 дерево

    private float burnTimer;
    private float _campfireBurnSecondsRemaining;
    private float _warmthGainTimer;
    public float CampfireBurnSecondsRemaining => _campfireBurnSecondsRemaining;

    [Header("Fire VFX")]
    [SerializeField] private GameObject fireVfx;
    [SerializeField] private float fireVfxOffDelaySeconds = 2.0f;
    [SerializeField] private Vector3 fireVfxHomeOffset = new Vector3(0f, 0.15f, 0f);
    private float fireVfxOffTimer;

    [Header("Wood")]
    [SerializeField] private float chopDistance = 1.5f;
    [SerializeField] private float chopReward = 200.0f;
    [SerializeField] private LayerMask treeLayer;
    public int wood;

    [Header("Water")]
    [SerializeField] private float waterCollectDistance = 2.5f;
    [SerializeField] private float waterCollectReward = 8f;
    [SerializeField] private int waterPerCollect = 1;
    public int water;
    [SerializeField] private float waterDecayInterval = 5.0f;
    private float waterTimer;


    [Header("Target")]
    [SerializeField] private Transform houseTarget;

    [Header("Heat")]
    [SerializeField] private float heatDecayInterval = 5.0f; // секунд на 1 единицу тепла
    public int heat;
    private float heatTimer;

    [Header("Freezing (heat = 0)")]
    [SerializeField] private float freezeDamageInterval = 0.25f;
    [SerializeField] private int freezeDamageAmount = 1;
    [SerializeField] private float freezePenaltyPerTick = -0.5f;
    private float _freezeTimer;

    [Header("Hunger / Satiety")]
    [SerializeField] private int maxSatiety = 20;  // шкала для UI/наблюдений, не лимит инвентаря
    public int satiety;                               // текущая сытость
    private float satietyTimer;
    [SerializeField] private float satietyDecayInterval = 5.0f; // секунд на 1 единицу сытости

    [Header("Starving (satiety = 0)")]
    [SerializeField] private float hungerDamageInterval = 0.25f;
    [SerializeField] private int hungerDamageAmount = 1;
    [SerializeField] private float hungerPenaltyPerTick = -0.5f;
    private float _hungerTimer;

    [Header("Dehydrated (water = 0)")]
    [SerializeField] private float thirstDamageInterval = 0.25f;
    [SerializeField] private int thirstDamageAmount = 1;
    private float _thirstTimer;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float rotationSpeed = 120f;
    
    [Header("Training")]
    [Tooltip("If true, Jack is frozen in place (no movement/rotation). Useful for training setups.")]
    [SerializeField] private bool frozen_jack = false;
    float _movementStunUntilTime;

    [Header("Reward")]
    [SerializeField] private float reachDistance = 1.2f;
    [SerializeField] private float reachReward = 10f;

    [SerializeField] private int maxWood = 10; // цель для задачи «идти к огню», не лимит инвентаря
    [SerializeField] private int maxHeat = 30;

    [Header("HP")]
    [SerializeField] private int maxHp = 100;
    [Tooltip("Штраф за каждый полученный урон (один вызов TakeDamage). 0 = выкл.")]
    [SerializeField] private float hpLossPenaltyPerHit = 10f;
    public int hp { get; private set; }
    public int Hp => hp;
    public int MaxHp => maxHp;
    public bool IsAliveForTwitch => hp > 0 && !_deathSequenceStarted;

    /// <summary>Инициализация Twitch-клона без EndEpisode (не трогает оригинал и других клонов).</summary>
    public void BootstrapTwitchCloneFrom(AgentGoToHouseDiscrete source, Vector3 spawnPos, Quaternion spawnRot)
    {
        _deathSequenceStarted = false;
        float reach = source != null ? source.TwitchReachMultiplier : 1f;
        SetTwitchReachMultiplier(reach);
        if (source != null)
            SetTwitchMoveSpeedMultiplier(source.TwitchMoveSpeedMultiplier);
        transform.SetPositionAndRotation(spawnPos, spawnRot);

        if (animator == null)
            animator = GetComponent<Animator>();
        if (animator != null)
            animator.speed = 1f;

        prevPosition = spawnPos;
        stepCount = 0;
        _survivalPhase = 1;
        _episodeStartTime = Time.unscaledTime;
        _lastChopActionForAnim = 0;
        _doCooldownRemaining = 0f;
        _pendingTwitchJumpHeights = 0;
        _forwardPushTimeLeft = 0f;
        _forwardPushSpeed = 0f;

        wood = 0;
        satiety = maxSatiety / 2;
        water = satiety + 6;
        satietyTimer = 0f;
        waterTimer = 0f;
        heat = maxHeat;
        heatTimer = 0f;
        burnTimer = 0f;
        _campfireBurnSecondsRemaining = 0f;
        fireVfxOffTimer = 0f;
        _freezeTimer = 0f;
        _hungerTimer = 0f;
        _thirstTimer = 0f;
        // Клон не должен ссылаться на общий костёр presentation Env.
        fireVfx = null;
        GameSfx.StopFireLoop(transform);

        prevTreeDist = 0f;
        prevSheepDist = 0f;
        prevWaterDist = 0f;
        hp = maxHp;

        if (source != null)
        {
            _lastNonZombieOption = source._lastNonZombieOption;
        }

        UpdateOptionIconVisual();
    }

    float _twitchReachMultiplier = 1f;
    public float TwitchReachMultiplier => _twitchReachMultiplier;

    float _baseMoveSpeed;
    bool _moveSpeedBaseCaptured;
    float _twitchMoveSpeedMultiplier = 1f;
    public float TwitchMoveSpeedMultiplier => _twitchMoveSpeedMultiplier;

    internal void SetTwitchReachMultiplier(float mult) =>
        _twitchReachMultiplier = Mathf.Max(0.1f, mult);

    internal void SetTwitchMoveSpeedMultiplier(float mult)
    {
        CaptureBaseMoveSpeedIfNeeded();
        _twitchMoveSpeedMultiplier = Mathf.Clamp(mult, 1f, 5f);
        moveSpeed = _baseMoveSpeed * _twitchMoveSpeedMultiplier;
    }

    internal void ResetTwitchMoveSpeed()
    {
        if (_moveSpeedBaseCaptured)
            moveSpeed = _baseMoveSpeed;
        _twitchMoveSpeedMultiplier = 1f;
    }

    void CaptureBaseMoveSpeedIfNeeded()
    {
        if (_moveSpeedBaseCaptured)
            return;
        _baseMoveSpeed = moveSpeed;
        _moveSpeedBaseCaptured = true;
    }

    float ChopReach => chopDistance * _twitchReachMultiplier;
    float EatReach => eatDistance * _twitchReachMultiplier;
    float WaterCollectReach => waterCollectDistance * _twitchReachMultiplier;
    float HouseReach => houseRadius * _twitchReachMultiplier;
    float ZombieDoReach => zombieNearbyRadiusOnDo * _twitchReachMultiplier;
    float ZombieKnockbackReach => zombieKnockbackRadiusOnDo * _twitchReachMultiplier;

    bool UsesGeorgeSurvivalOptions => TrainingEnvSpace.IsGeorgeAgent(this);

    bool IsAllowedOption(int option)
    {
        if (UsesGeorgeSurvivalOptions)
            return option == OptionFood || option == OptionWater || option == OptionHeat;
        return option == OptionWood || option == OptionFood || option == OptionZombie || option == OptionWater;
    }

    bool ControlsSharedCampfire =>
        fireVfx != null
        && !TwitchEphemeralEffects.IsTwitchClone(this)
        && TrainingEnvSpace.IsPresentationTransform(transform);

    public int currentOptionTrain = 1; // 0 = дерево, 1 = еда, 2 = зомби

    public int currentOption = 1; // 0 = дерево, 1 = еда, 2 = зомби

    int _lastNonZombieOption = OptionWood;
    protected static bool HeuristicOptionsLocked;
    static int _globalManualKeysFrame = -1;

    // Отслеживание наград для каждой опции
    private float lastRewardForOption0 = 0f;
    private float lastRewardForOption1 = 0f;
    private float lastRewardForOption2 = 0f;
    private float currentStepReward = 0f;
    private float accumulatedRewardForOption0 = 0f;
    private float accumulatedRewardForOption1 = 0f;
    private float accumulatedRewardForOption2 = 0f;

    // Публичные свойства (используются для наблюдений/утилит)
    public int maxWoodPublic => maxWood;
    public int MaxWood => maxWood;
    public bool IsWoodGatherGoalReached => wood >= maxWood;
    public int maxHeatPublic => maxHeat;
    public int maxSatietyPublic => maxSatiety;
    public Transform houseTargetPublic => houseTarget;

    private float prevTreeDist;

    [Header("Survival Task")]
    [SerializeField] private float survivalGoalSeconds = 120f;
    [SerializeField] private float phase1CompleteReward = 5f;
    [SerializeField] private float phase2CompleteReward = 5f;
    [SerializeField] private float nightmareZombieSpeedMultiplier = 3f;
    [SerializeField] private float nightmareBossScaleMultiplier = 3f;
    [SerializeField] private float nightmareBossHpMultiplier = 5f;
    [SerializeField] private float nightmareBossSpeedMultiplier = 5f;
    [SerializeField] private float nightmareBossAttackDamageMultiplier = 2.5f;
    [SerializeField] private float nightmareBossAttackCooldownMultiplier = 0.6f;
    [SerializeField] private float nightmareBossSpawnIntervalSeconds = 10f;
    [SerializeField] private ZombieSpawner zombieSpawner;
    [Tooltip("Второй спавнер (ZombieSpawner_2) — только Env (4) / ZombieOnly.")]
    [SerializeField] private ZombieSpawner zombieSpawnerSecondary;
    const string PrimaryZombieSpawnerName = "ZombieSpawner";
    static readonly string[] SecondaryZombieSpawnerNames = { "ZombieSpawner_2", "zombie_spawner_2" };
    private float _episodeStartTime;
    private int _survivalPhase = 1;
    private float _nextNightmareBossSpawnTime = float.PositiveInfinity;

    EnvTrainingConfig _trainingConfig;
    JackTrainingMode _resolvedTrainingMode = JackTrainingMode.Full;

    public JackTrainingMode ResolvedTrainingMode => _resolvedTrainingMode;
    bool IsFullTrainingMode => _resolvedTrainingMode == JackTrainingMode.Full;
    bool IsWoodFoodSwitchMode => _resolvedTrainingMode == JackTrainingMode.WoodFoodSwitch;
    bool IsOptionSwitchTrainingMode => IsFullTrainingMode || IsWoodFoodSwitchMode;
    bool IsZombieTrainingMode => _resolvedTrainingMode == JackTrainingMode.ZombieOnly;
    bool IsSimpleTrainingMode =>
        _resolvedTrainingMode == JackTrainingMode.WoodOnly
        || _resolvedTrainingMode == JackTrainingMode.FoodOnly
        || _resolvedTrainingMode == JackTrainingMode.WaterOnly
        || _resolvedTrainingMode == JackTrainingMode.ZombieOnly;

    bool IsGeorgeSimpleTrainingMode =>
        UsesGeorgeSurvivalOptions && _trainingConfig != null && _trainingConfig.IsGeorgeSimpleTask();

    public float SurvivalGoalSeconds => survivalGoalSeconds;
    public float SurvivalTotalSeconds => survivalGoalSeconds * 3f;
    public float SurvivalElapsedSeconds => Mathf.Max(0f, Time.unscaledTime - _episodeStartTime);
    public float SurvivalProgress01 =>
        survivalGoalSeconds > 0f
            ? Mathf.Clamp01(SurvivalElapsedSeconds / SurvivalTotalSeconds)
            : 0f;
    public float Phase1EndProgress => 1f / 3f;
    public float Phase2EndProgress => 2f / 3f;
    public int SurvivalPhase => _survivalPhase;
    public bool IsSurvivalPhase2 => _survivalPhase >= 2;
    public bool IsSurvivalPhase3 => _survivalPhase >= 3;
    public string SurvivalTaskLabel
    {
        get
        {
            if (IsSurvivalPhase3)
                return "Задача: выжить в кошмаре";
            if (IsSurvivalPhase2)
                return "Задача: выжить 2 минуты в зомби апокалипсисе";
            return "Задача: учимся выживать";
        }
    }

    private int stepCount;

    private CharacterController controller;
    private Animator animator;

    private bool ControllerReady => controller != null && controller.enabled;

    private Vector3 prevPosition;

    
    private float verticalVelocity;
    [SerializeField] private float gravity = -9.81f;

    [Header("Twitch chat")]
    [Tooltip("#up=N — прыжок на N ростов (1–5). #forward=N — толчок вперёд (1–5, 5 = макс.).")]
    int _pendingTwitchJumpHeights;
    float _forwardPushSpeed;
    float _forwardPushTimeLeft;

    [Header("Animation")]
    [Tooltip("Имя Trigger в Animator (должен совпадать с параметром в Animator Controller).")]
    [SerializeField] private string doActionAnimTrigger = "Do";
    [Tooltip("Минимум секунд между срабатываниями DO (анимация + попытка добычи).")]
    [SerializeField] private float doActionCooldownSeconds = 0.45f;
    [Tooltip("Штраф за DO без результата (не дрова/еда/удар по зомби). ~−0.1: спам ≈ −0.2/с, один удачный DO +10.")]
    [SerializeField] private float emptyDoActionPenalty = -0.1f;
    [Tooltip("Сглаживание параметра Speed в Animator (0 = без сглаживания — быстрее включение walk).")]
    [SerializeField] private float walkAnimSpeedDamp = 0f;
    private int _lastChopActionForAnim;
    private float _doCooldownRemaining;
    private float _lastPlanarMoveInput;

    [Header("DO vs Zombie")]
    [Tooltip("Если при выполнении DO рядом есть зомби — агент получает урон.")]
    [SerializeField] private bool damageOnDoIfZombieNearby = false;
    [SerializeField] private float zombieNearbyRadiusOnDo = 1.5f;
    [SerializeField] private int zombieDamageOnDo = 0;
    [SerializeField] private LayerMask zombieLayer;

    [Header("Zombie Option")]
    [Tooltip("Если зомби ближе этой дистанции — включается опция «атака» [1,1].")]
    [SerializeField] private float optionZombieActivationRadius = 6f;
    [SerializeField] private float zombieApproachRewardScale = 0.5f;

    [Header("DO hits Zombie (knockback)")]
    [Tooltip("Если при выполнении DO рядом есть зомби — отталкиваем зомби немного назад.")]
    [SerializeField] private bool knockbackZombieOnDo = true;
    [SerializeField] private float zombieKnockbackRadiusOnDo = 1.6f;
    [SerializeField] private float zombieKnockbackDistanceOnDo = 0.65f;
    [SerializeField] private float zombieKnockbackUpOnDo = 0.18f;
    [SerializeField] private float zombieKnockbackImpulseOnDo = 2.0f;
    [SerializeField] private float zombieKnockbackUpFactorOnDo = 0.45f;
    [SerializeField] private float zombieStunSecondsOnDo = 1.0f;
    [Tooltip("Если true — урон подбирается так, чтобы зомби умирал ровно за 2 удара DO (ceil(MaxHp/2)).")]
    [SerializeField] private bool zombieDiesInTwoDoHits = true;
    [Tooltip("Фиксированный урон по зомби при DO (используется если zombieDiesInTwoDoHits = false).")]
    [SerializeField] private int zombieDamageToZombieOnDo = 1;
    [Tooltip("Награда за попадание DO по зомби без убийства (опция зомби).")]
    [SerializeField] private float rewardOnZombieHitDo = 0f;
    [Tooltip("Награда за убийство зомби при активной опции зомби.")]
    [SerializeField] private float rewardOnZombieKillDo = 10f;
    [Tooltip("Зомби за спиной не бьются DO. dot(forward, к зомби): 0 = полусфера впереди, 0.5 ≈ 60°, 1 = строго вперёд.")]
    [SerializeField] [Range(0f, 1f)] private float zombieDoMinForwardDot = 0.5f;
    [Header("Friendly fire (DO)")]
    [SerializeField] private int allyDoDamage = 10;
    [SerializeField] private float friendlyFireDoPenalty = -10f;
    [SerializeField] private float allyKnockbackDistance = 0.55f;
    [SerializeField] private float allyKnockbackUp = 0.12f;

    public void StunForSeconds(float seconds)
    {
        if (seconds <= 0f)
            return;
        _movementStunUntilTime = Mathf.Max(_movementStunUntilTime, Time.time + seconds);
    }

    bool CanMoveThisStep => !frozen_jack && Time.time >= _movementStunUntilTime;
    bool CanRotateThisStep => !frozen_jack;

    public override void Initialize()
    {
        controller = GetComponent<CharacterController>();
        ResolveAnimatorReference();
        _lastShowOptionTaskIcon = showOptionTaskIcon;
        _lastChopActionForAnim = 0;
        _doCooldownRemaining = 0f;

        // Никогда не снимаем HP Джеку за его собственное DO (даже если в инспекторе остались старые сериализованные значения).
        damageOnDoIfZombieNearby = false;
        zombieDamageOnDo = 0;

        if (zombieLayer.value == 0)
        {
            int zombieLayerId = LayerMask.NameToLayer("Zombie");
            if (zombieLayerId >= 0)
                zombieLayer = 1 << zombieLayerId;
        }

        // Jack проходит сквозь цветы: коллизия слой Jack ↔ Flower отключена. Lily на другом слое — застревает и собирает.
        // Важно: у Jack в инспекторе должен быть слой, отличный от Lily (напр. Jack = "Player", Lily = "Default").
        int flowerLayerId = LayerMask.NameToLayer("Flower");
        if (flowerLayerId >= 0)
            Physics.IgnoreLayerCollision(gameObject.layer, flowerLayerId, true);

        EnsureOptionIconRenderer();
        UpdateOptionIconVisual();
        AgentFootsteps.EnsureOn(gameObject);

        CacheTrainingConfig();
        ResolveTrainingMode();
        ApplyEpisodeStepLimit();
        EnsureGeorgeTrainingBehaviorName();
    }

    void EnsureGeorgeTrainingBehaviorName()
    {
        if (!UsesGeorgeSurvivalOptions)
            return;

        var bp = GetComponent<BehaviorParameters>();
        if (bp != null && bp.BehaviorName != GeorgeBehaviorName)
            bp.BehaviorName = GeorgeBehaviorName;
    }

    private void Awake()
    {
        // Только presentation-Jack управляет HUD; копии Env (1)… не должны его гасить.
        if (TrainingEnvSpace.IsPresentationTransform(transform))
            HudHpBars.SetGlobalEnabled(showHudHpTopLeft);
        EnsureCampfireAudio();
        EnsureCampfireLocalBinding();
        EnsureCampfireBurnTimerDisplay();
    }

    void CacheTrainingConfig()
    {
        if (_trainingConfig != null)
            return;

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot == null)
            return;

        _trainingConfig = envRoot.GetComponent<EnvTrainingConfig>();
        if (_trainingConfig == null)
            _trainingConfig = envRoot.gameObject.AddComponent<EnvTrainingConfig>();
    }

    void ResolveTrainingMode()
    {
        CacheTrainingConfig();
        _resolvedTrainingMode = _trainingConfig != null
            ? _trainingConfig.ResolveJackMode()
            : JackTrainingMode.Full;
    }

    public void EnsureTrainingCampfireLit(float seconds)
    {
        if (seconds <= 0f || fireVfx == null)
            return;

        EnsureCampfireLocalBinding();
        _campfireBurnSecondsRemaining = seconds;
        SetCampfireVisible(true);
    }

    void ApplyEpisodeStepLimit()
    {
        if (IsFullTrainingMode && survivalGoalSeconds > 0f)
            MaxStep = 0;
        else if (IsZombieTrainingMode)
            MaxStep = _trainingConfig != null ? _trainingConfig.ZombieMaxSteps : 3000;
        else if (IsWoodFoodSwitchMode)
            MaxStep = _trainingConfig != null ? _trainingConfig.SimpleMaxSteps : 400;
        else if (IsGeorgeSimpleTrainingMode)
            MaxStep = _trainingConfig != null ? _trainingConfig.SimpleMaxSteps : 400;
        else if (IsSimpleTrainingMode)
            MaxStep = _trainingConfig != null ? _trainingConfig.SimpleMaxSteps : 400;
        else if (survivalGoalSeconds > 0f)
            MaxStep = 0;
    }

    void EnsureCampfireAudio()
    {
        if (fireVfx == null)
            return;

        if (fireVfx.GetComponent<CampfireLoopAudio>() == null)
            fireVfx.AddComponent<CampfireLoopAudio>();

        EnsureCampfireBurnTimerDisplay();
    }

    void EnsureCampfireBurnTimerDisplay()
    {
        var display = GetComponent<CampfireBurnTimerDisplay>();
        if (display == null)
            display = gameObject.AddComponent<CampfireBurnTimerDisplay>();

        Transform anchor = fireVfx != null ? fireVfx.transform : houseTarget;
        display.Bind(anchor, this);
    }

    /// <summary>Костёр живёт в локальных координатах Env, привязан к HomeSpot.</summary>
    public void EnsureCampfireLocalBinding()
    {
        if (fireVfx == null || houseTarget == null || TwitchEphemeralEffects.IsTwitchClone(this))
            return;

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot == null)
            return;

        TrainingEnvSpace.EnsureDescendantOfEnv(fireVfx.transform, transform);

        var fireTransform = fireVfx.transform;
        fireTransform.localPosition = TrainingEnvSpace.AnchorLocalPosition(envRoot, houseTarget, fireVfxHomeOffset);
        fireTransform.localRotation = Quaternion.identity;
    }

    void SetCampfireVisible(bool visible)
    {
        if (!ControlsSharedCampfire)
            return;

        if (visible)
        {
            EnsureCampfireLocalBinding();
            fireVfx.SetActive(true);
            fireVfxOffTimer = fireVfxOffDelaySeconds;
            foreach (var renderer in fireVfx.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = true;
            foreach (var ps in fireVfx.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps != null && !ps.isPlaying)
                    ps.Play(true);
            }
            return;
        }

        fireVfx.SetActive(false);
    }

    bool ShouldShowOptionTaskIcon() =>
        showOptionTaskIcon
        && TrainingEnvSpace.IsPresentationTransform(transform)
        && !TwitchEphemeralEffects.IsTwitchClone(this);

    protected virtual void EnsureOptionIconRenderer()
    {
        if (!ShouldShowOptionTaskIcon()) return;
        if (optionIconRenderer != null) return;
        if (optionWoodSprite == null && optionFoodSprite == null && optionZombieSprite == null
            && optionWaterSprite == null && optionHeatSprite == null) return;

        var existing = transform.Find(OptionIconObjectName);
        if (existing != null)
        {
            optionIconRenderer = existing.GetComponent<SpriteRenderer>();
            if (optionIconRenderer == null)
                optionIconRenderer = existing.gameObject.AddComponent<SpriteRenderer>();
            optionIconRenderer.sortingOrder = optionIconSortingOrder;
            ApplyOptionIconLocalScale();
            return;
        }

        var go = new GameObject(OptionIconObjectName);
        go.transform.SetParent(transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = optionIconSortingOrder;
        optionIconRenderer = sr;
        ApplyOptionIconLocalScale();
    }

    private void ApplyOptionIconLocalScale()
    {
        if (optionIconRenderer == null) return;
        float s = currentOptionTrain switch
        {
            OptionWood => optionWoodIconScale,
            OptionFood => optionFoodIconScale,
            OptionZombie => ResolveZombieOptionIconScale(),
            OptionWater => ResolveWaterOptionIconScale(),
            OptionHeat => optionHeatIconScale,
            _ => optionFoodIconScale
        };
        _optionIconBaseScale = Mathf.Max(0.01f, s);
        optionIconRenderer.transform.localScale = Vector3.one * s;
    }

    float ResolveWaterOptionIconScale()
    {
        if (optionFoodSprite == null || optionWaterSprite == null)
            return optionWaterIconScale;

        float foodSize = Mathf.Max(optionFoodSprite.bounds.size.x, optionFoodSprite.bounds.size.y);
        float waterSize = Mathf.Max(optionWaterSprite.bounds.size.x, optionWaterSprite.bounds.size.y);
        if (waterSize < 1e-4f)
            return optionWaterIconScale;

        return optionFoodIconScale * (foodSize / waterSize);
    }

    float ResolveZombieOptionIconScale()
    {
        if (optionFoodSprite == null || optionZombieSprite == null)
            return optionZombieIconScale;

        float foodSize = Mathf.Max(optionFoodSprite.bounds.size.x, optionFoodSprite.bounds.size.y);
        float zombieSize = Mathf.Max(optionZombieSprite.bounds.size.x, optionZombieSprite.bounds.size.y);
        if (zombieSize < 1e-4f)
            return optionZombieIconScale;

        // Подгоняем зомби под размер стикера еды, не трогая еду и дерево.
        return optionFoodIconScale * (foodSize / zombieSize);
    }

    private void Update()
    {
        if (_doCooldownRemaining > 0f)
            _doCooldownRemaining -= Time.deltaTime;

        if (showOptionTaskIcon != _lastShowOptionTaskIcon)
        {
            _lastShowOptionTaskIcon = showOptionTaskIcon;
            UpdateOptionIconVisual();
        }

        if (TrainingEnvSpace.IsPresentationTransform(transform)
            && !TwitchEphemeralEffects.IsTwitchClone(this))
            ProcessGlobalManualKeysOnce();

        ProcessHeuristicOptionKeys();
        if (TrainingEnvSpace.IsPresentationTransform(transform)
            && !TwitchEphemeralEffects.IsTwitchClone(this)
            && !HeuristicOptionsLocked)
            RefreshTrainingOption();

        ProcessTwitchJump();
        UpdateNightmareBossSpawns();
    }

    void UpdateNightmareBossSpawns()
    {
        if (_deathSequenceStarted || IsSimpleTrainingMode || IsWoodFoodSwitchMode || _survivalPhase < 3)
            return;
        if (survivalGoalSeconds <= 0f || nightmareBossSpawnIntervalSeconds <= 0f)
            return;
        if (Time.unscaledTime < _nextNightmareBossSpawnTime)
            return;

        SpawnNightmareBossZombie();
        _nextNightmareBossSpawnTime = Time.unscaledTime + nightmareBossSpawnIntervalSeconds;
    }

    protected virtual void ProcessHeuristicOptionKeys()
    {
        if (!IsManualOptionControlActive())
            return;

        if (TrainingEnvSpace.IsGeorgeAgent(this))
        {
            if (HeuristicOptionsLocked && Input.GetKeyDown(KeyCode.Y))
                CycleHeuristicOption();

            if (Input.GetKeyDown(KeyCode.Alpha0))
            {
                SetOption(OptionWater);
                HeuristicOptionsLocked = true;
            }

            return;
        }

        if (HeuristicOptionsLocked && Input.GetKeyDown(KeyCode.E))
            CycleHeuristicOption();

        if (Input.GetKeyDown(KeyCode.Alpha0))
        {
            SetOption(OptionWater);
            HeuristicOptionsLocked = true;
        }
    }

    protected static void ProcessGlobalManualKeysOnce()
    {
        if (Time.frameCount == _globalManualKeysFrame)
            return;

        if (Input.GetKeyDown(KeyCode.L))
        {
            HeuristicOptionsLocked = !HeuristicOptionsLocked;
            _globalManualKeysFrame = Time.frameCount;
            if (!HeuristicOptionsLocked)
            {
                var jack = TrainingEnvSpace.FindPresentationJack();
                jack?.RefreshTrainingOption(force: true);
                var george = TrainingEnvSpace.FindPresentationGeorge();
                george?.RefreshTrainingOption(force: true);
            }
            return;
        }

        if (Input.GetKeyDown(KeyCode.P))
        {
            ManualPlayControl.ToggleGeorgeManual();
            _globalManualKeysFrame = Time.frameCount;
        }
    }

    protected virtual bool IsManualOptionControlActive()
    {
        if (!TrainingEnvSpace.IsPresentationTransform(transform))
            return false;
        if (TwitchEphemeralEffects.IsTwitchClone(this))
            return false;

        var bp = GetComponent<BehaviorParameters>();
        if (bp != null && bp.BehaviorType == BehaviorType.HeuristicOnly)
            return true;

        // Play в Editor без mlagents-learn — тоже ручное управление опциями.
        return !Academy.IsInitialized || !Academy.Instance.IsCommunicatorOn;
    }

    protected void CycleHeuristicOption()
    {
        if (UsesGeorgeSurvivalOptions)
        {
            int next = currentOptionTrain switch
            {
                OptionFood => OptionWater,
                OptionWater => OptionHeat,
                OptionHeat => OptionFood,
                _ => OptionFood
            };
            SetOption(next);
            return;
        }

        int nextJack = currentOptionTrain switch
        {
            OptionWood => OptionFood,
            OptionFood => OptionZombie,
            OptionZombie => OptionWater,
            OptionWater => OptionWood,
            _ => OptionWood
        };
        SetOption(nextJack);
    }

    /// <summary>Один прыжок из Twitch #up=N: высота = N × рост персонажа (N 1–5).</summary>
    public void RequestTwitchUp(int heightInBodyHeights)
    {
        if (!TrainingEnvSpace.IsPresentationTransform(transform))
            return;
        if (TwitchEphemeralEffects.IsTwitchClone(this))
            return;

        _pendingTwitchJumpHeights = Mathf.Clamp(heightInBodyHeights, 1, 5);
    }

    /// <summary>Толчок вперёд из Twitch #forward=N (1 слабо, 5 максимум).</summary>
    public void RequestTwitchForward(int strength)
    {
        if (!TrainingEnvSpace.IsPresentationTransform(transform))
            return;
        if (TwitchEphemeralEffects.IsTwitchClone(this))
            return;

        strength = Mathf.Clamp(strength, 1, 5);
        float t = (strength - 1) / 4f;
        _forwardPushSpeed = Mathf.Lerp(moveSpeed * 4f, moveSpeed * 16f, t);
        _forwardPushTimeLeft = 0.45f;
    }

    Vector3 GetTwitchForwardPushVelocity()
    {
        if (_forwardPushTimeLeft <= 0f || !CanMoveThisStep)
            return Vector3.zero;

        _forwardPushTimeLeft -= Time.deltaTime;
        return transform.forward * _forwardPushSpeed;
    }

    void ProcessTwitchJump()
    {
        if (_pendingTwitchJumpHeights <= 0 || !ControllerReady || !controller.isGrounded)
            return;

        float height = controller.height * _pendingTwitchJumpHeights;
        verticalVelocity = Mathf.Sqrt(2f * Mathf.Abs(gravity) * Mathf.Max(0.1f, height));
        _pendingTwitchJumpHeights = 0;
    }

    public override void OnEpisodeBegin()
    {
        _deathSequenceStarted = false;
        SetTwitchReachMultiplier(1f);
        ResetTwitchMoveSpeed();
        bool isTwitchClone = TwitchEphemeralEffects.IsTwitchClone(this);

        if (TrainingEnvSpace.IsPresentationTransform(transform) && !isTwitchClone)
        {
            DeathFreeze.EnsureEnvSimulationRunning();
            DeathFreeze.UnfreezeWorld();
            AgentDeathOverlay.Hide();
            BackgroundMusic.ResumeMusic();
            var primary = TrainingEnvSpace.FindPresentationPrimaryJack();
            if (primary == this)
                TwitchEphemeralEffects.OnPresentationJackEpisodeBegin(this);
        }

        ResolveAnimatorReference();
        if (animator != null)
            animator.speed = 1f;
        _movementStunUntilTime = 0f;

        // Локальные координаты относительно корня Env (как в инспекторе у Jack).
        const float groundY = -5.228786f;
        float minX = -2f;
        float maxX = 14f;
        float minZ = 10f;
        float maxZ = 17f;

        if (!isTwitchClone)
        {
            // Одна Env — не телепортируем, остаётся позиция из сцены (~4, -5.2, 15).
            bool skipTeleport = !spawnAtHouseFixed && !TrainingEnvSpace.HasMultipleTrainingEnvs();

            if (!skipTeleport)
            {
                controller.enabled = false;
                if (spawnAtHouseFixed)
                {
                    transform.position = TrainingEnvSpace.LocalToWorld(transform, fixedSpawnPosition);
                    transform.rotation = TrainingEnvSpace.LocalToWorldRotation(transform, fixedSpawnEuler);
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
        }

        prevPosition = transform.position;
        stepCount = 0;
        _survivalPhase = 1;
        if (TrainingEnvSpace.IsPresentationTransform(transform))
            BackgroundMusic.SetSurvivalPhase(1);
        _nextNightmareBossSpawnTime = float.PositiveInfinity;
        _episodeStartTime = Time.unscaledTime;
        _lastChopActionForAnim = 0;
        _doCooldownRemaining = 0f;
        _pendingTwitchJumpHeights = 0;
        _forwardPushTimeLeft = 0f;
        _forwardPushSpeed = 0f;

        wood = 0;
        satiety = maxSatiety / 2;
        water = satiety + 6;
        satietyTimer = 0f;
        waterTimer = 0f;
        heat = maxHeat;
        heatTimer = 0f;

        burnTimer = 0f;
        _campfireBurnSecondsRemaining = 0f;
        fireVfxOffTimer = 0f;
        _freezeTimer = 0f;
        _hungerTimer = 0f;
        _thirstTimer = 0f;
        if (ControlsSharedCampfire)
            SetCampfireVisible(false);
        GameSfx.StopFireLoop(transform);

        prevTreeDist = 0f;
        prevSheepDist = 0f;
        prevWaterDist = 0f;
        prevZombieDist = 0f;
        prevHouseDist = -1f;
        _warmthGainTimer = 0f;
        WaterGoalPath.Get(transform)?.ResetAgent(transform);
        _lastNonZombieOption = UsesGeorgeSurvivalOptions ? OptionFood : OptionWood;
        HeuristicOptionsLocked = false;

        hp = maxHp;

        EnvTrainingConfig.Get(transform)?.ApplyForEpisodeBegin();

        ResolveTrainingMode();
        ApplyEpisodeStepLimit();

        if (UsesGeorgeSurvivalOptions)
        {
            StopZombieSpawnerForEpisode();
            if (_trainingConfig != null && _trainingConfig.IsGeorgeSimpleTask())
            {
                int fixedOption = _trainingConfig.ResolveFixedJackOption();
                currentOptionTrain = fixedOption;
                currentOption = fixedOption;
            }
            else if (useUtilitySoftmaxSampling)
            {
                int prevOption = currentOptionTrain;
                int sampled = SampleGeorgeOptionUtilitySoftmax(currentOptionTrain);
                ResetWaterGoalIfEntered(prevOption, sampled);
                currentOptionTrain = sampled;
                currentOption = sampled;
            }
            else
            {
                int[] georgeOptions = { OptionFood, OptionWater, OptionHeat };
                int randomOption = georgeOptions[Random.Range(0, georgeOptions.Length)];
                currentOptionTrain = randomOption;
                currentOption = randomOption;
            }
        }
        else if (IsZombieTrainingMode)
        {
            currentOptionTrain = OptionZombie;
            currentOption = OptionZombie;
            StartZombieSpawnerForEpisode(ShouldIncludeSecondaryZombieSpawner());
        }
        else
        {
            StopZombieSpawnerForEpisode();

            if (_resolvedTrainingMode == JackTrainingMode.WoodOnly)
            {
                currentOptionTrain = 0;
                currentOption = 0;
            }
            else if (_resolvedTrainingMode == JackTrainingMode.FoodOnly)
            {
                currentOptionTrain = 1;
                currentOption = 1;
            }
            else if (_resolvedTrainingMode == JackTrainingMode.WaterOnly)
            {
                currentOptionTrain = OptionWater;
                currentOption = OptionWater;
            }
            else if (useUtilitySoftmaxSampling)
            {
                int prevOption = currentOptionTrain;
                int sampled = SampleOptionUtilitySoftmax(currentOptionTrain);
                ResetWaterGoalIfEntered(prevOption, sampled);
                currentOptionTrain = sampled;
                currentOption = sampled;
            }
            else
            {
                int randomOption = Random.Range(0, 2);
                currentOptionTrain = randomOption;
                currentOption = randomOption;
            }
        }

        if ((IsSimpleTrainingMode || IsGeorgeSimpleTrainingMode)
            && (_trainingConfig == null || _trainingConfig.FreezeNeeds))
        {
            satiety = maxSatiety / 2;
            heat = maxHeat / 2;
        }

        UpdateOptionIconVisual();

        lastRewardForOption0 = 0f;
        lastRewardForOption1 = 0f;
        lastRewardForOption2 = 0f;
        currentStepReward = 0f;
        accumulatedRewardForOption0 = 0f;
        accumulatedRewardForOption1 = 0f;
        accumulatedRewardForOption2 = 0f;

        if (!isTwitchClone)
        {
            EnsureSpawners();
            var envRoot = TrainingEnvSpace.FindRoot(transform);
            PresentationWorldReset.ResetSpawners(envRoot);
            if (IsZombieTrainingMode)
                StartZombieSpawnerForEpisode(ShouldIncludeSecondaryZombieSpawner());
        }
    }

    void EnsureSpawners()
    {
        if (sheepSpawner == null)
        {
            var envRoot = TrainingEnvSpace.FindRoot(transform);
            if (envRoot != null)
                sheepSpawner = envRoot.GetComponentInChildren<SheepSpawner>(true);
        }

        if (treeSpawner == null)
        {
            var envRoot = TrainingEnvSpace.FindRoot(transform);
            if (envRoot != null)
                treeSpawner = envRoot.GetComponentInChildren<TreeSpawner>(true);
        }
    }

    private bool GetNearestSheep(out GameObject nearestSheep, out float distance)
    {
        nearestSheep = null;
        distance = float.MaxValue;

        Collider[] hits = Physics.OverlapSphere(
            transform.position,
            20f,          // радиус поиска овечек
            sheepLayer
        );

        if (hits.Length == 0)
            return false;

        foreach (var hit in hits)
        {
            if (hit == null) continue;
            float d = Vector3.Distance(transform.position, hit.transform.position);
            if (d < distance)
            {
                distance = d;
                nearestSheep = hit.gameObject;
            }
        }

        return true;
    }

    private bool IsTreeNearby()
    {
        Vector3 origin = transform.position;
        Collider[] hits = Physics.OverlapSphere(origin, ChopReach, treeLayer);

        foreach (var c in hits)
        {
            if (c == null) continue;
            if (HarvestReachDistance(origin, c) <= ChopReach)
                return true;
        }

        return false;
    }

    public bool HasTreesNearby()
    {
        return IsTreeNearby();
    }

    public bool HasSheepNearby()
    {
        float r = EatReach * 2f;
        Vector3 origin = transform.position;
        Collider[] hits = Physics.OverlapSphere(origin, r, sheepLayer);
        foreach (var c in hits)
        {
            if (c == null) continue;
            if (HarvestReachDistance(origin, c) <= r)
                return true;
        }
        return false;
    }

    public float GetDistanceToNearestSheep()
    {
        if (GetNearestSheep(out GameObject sheep, out float distance))
        {
            return distance;
        }
        return 999f; // большое значение, если овец нет
    }

    public float GetDistanceToNearestTree()
    {
        if (GetNearestTree(out GameObject tree, out float distance))
        {
            return distance;
        }
        return 999f; // большое значение, если деревьев нет
    }

    void ResetWaterGoalIfEntered(int prevOption, int newOption)
    {
        if (newOption == OptionWater && prevOption != OptionWater)
            WaterGoalPath.Get(transform)?.ResetAgent(transform);
    }

    public void SetOption(int option)
    {
        if (!IsAllowedOption(option))
        {
            Debug.LogWarning($"AgentGoToHouseDiscrete.SetOption: Некорректная опция {option}, игнорируем");
            return;
        }

        int prevOption = currentOptionTrain;
        
        if (option == OptionWood || option == OptionFood)
            _lastNonZombieOption = option;

        if (option == OptionWater && prevOption != OptionWater)
            WaterGoalPath.Get(transform)?.ResetAgent(transform);

        currentOptionTrain = option;
        currentOption = option;
        
        // Дополнительная синхронизация для надежности
        if (currentOptionTrain != currentOption)
        {
            currentOption = currentOptionTrain;
        }

        UpdateOptionIconVisual();
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

                float mul = (cam.name == "CamOnJack")
                    ? Mathf.Clamp(optionIconScaleMultiplierCamOnJack, 0.05f, 2f)
                    : 1f;
                optionIconRenderer.transform.localScale = Vector3.one * (_optionIconBaseScale * mul);
            }
        }
    }

    void ResolveAnimatorReference()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (animator == null)
            animator = GetComponentInChildren<Animator>(true);
        if (animator == null)
            return;

        animator.applyRootMotion = false;
        if (animator.runtimeAnimatorController != null)
            return;

        var fallback = DefaultHeroAnimatorController.ForAgent(this);
        if (fallback != null)
            animator.runtimeAnimatorController = fallback;
    }

    private void ApplyWalkAnimatorSpeed()
    {
        if (animator == null || controller == null) return;

        if (!CanMoveThisStep)
        {
            if (walkAnimSpeedDamp > 0f)
                animator.SetFloat("Speed", 0f, walkAnimSpeedDamp, Time.deltaTime);
            else
                animator.SetFloat("Speed", 0f);
            return;
        }

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

        switch (currentOptionTrain)
        {
            case OptionWood:
                optionIconRenderer.sprite = optionWoodSprite;
                optionIconRenderer.enabled = optionWoodSprite != null;
                break;
            case OptionFood:
                optionIconRenderer.sprite = optionFoodSprite;
                optionIconRenderer.enabled = optionFoodSprite != null;
                break;
            case OptionZombie:
                optionIconRenderer.sprite = optionZombieSprite;
                optionIconRenderer.enabled = optionZombieSprite != null;
                break;
            case OptionWater:
                optionIconRenderer.sprite = optionWaterSprite;
                optionIconRenderer.enabled = optionWaterSprite != null;
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

    private bool _deathSequenceStarted;

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
            if (TwitchEphemeralEffects.IsTwitchClone(this))
            {
                TwitchEphemeralEffects.NotifyCloneDestroyed(gameObject);
                Destroy(gameObject);
                return;
            }

            if (TrainingEnvSpace.IsPresentationTransform(transform) && !TrainingEnvSpace.IsGeorgeAgent(this))
                TwitchEphemeralEffects.OnPresentationJackDeath();

            EvalEpisodeTracker.NotifyEpisodeEnded();
            AgentDeathOverlay.ShowAndEndEpisode(this, AgentDeathOverlay.GetDeathMessageFor(this));
        }
    }

    /// <summary>
    /// Получить последнюю награду для указанной опции
    /// </summary>
    public float GetLastRewardForOption(int option)
    {
        if (option == 0)
            return lastRewardForOption0;
        if (option == 1)
            return lastRewardForOption1;
        return lastRewardForOption2;
    }

    protected static float GetOptionObservationBit0(int option) => (option & 1) != 0 ? 1f : 0f;
    protected static float GetOptionObservationBit1(int option) => (option & 2) != 0 ? 1f : 0f;
    protected static float GetOptionObservationBit2(int option) => (option & 4) != 0 ? 1f : 0f;

    bool HasZombieInActivationRadius() =>
        GetNearestZombieForOption(optionZombieActivationRadius, out _, out _);

    public void RefreshTrainingOption(bool force = false)
    {
        if (HeuristicOptionsLocked && !force)
            return;

        if (UsesGeorgeSurvivalOptions)
            return;

        if (IsZombieTrainingMode)
            return;

        if (IsFullTrainingMode && HasZombieInActivationRadius())
        {
            if (currentOptionTrain != OptionZombie)
            {
                if (currentOptionTrain == OptionWood || currentOptionTrain == OptionFood)
                    _lastNonZombieOption = currentOptionTrain;
                currentOptionTrain = OptionZombie;
                currentOption = OptionZombie;
                UpdateOptionIconVisual();
            }
            return;
        }

        if (IsFullTrainingMode && currentOptionTrain == OptionZombie)
        {
            currentOptionTrain = _lastNonZombieOption;
            currentOption = _lastNonZombieOption;
            prevZombieDist = 0f;
            UpdateOptionIconVisual();
        }

        if (!IsOptionSwitchTrainingMode)
            return;

        if (IsWoodGatherGoalReached)
        {
            if (currentOptionTrain != OptionWood || currentOption != OptionWood)
            {
                currentOptionTrain = OptionWood;
                currentOption = OptionWood;
                UpdateOptionIconVisual();
            }
        }
    }

    bool GetNearestZombieForOption(float maxRange, out GameObject nearestZombie, out float distance)
    {
        nearestZombie = null;
        distance = float.MaxValue;
        if (maxRange <= 0f)
            return false;

        Vector3 origin = transform.position;
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (TryFindNearestZombie(origin, maxRange, envRoot, true, ref nearestZombie, ref distance))
            return true;

        // Один Env: зомби могли остаться в корне сцены после старого спавна.
        if (envRoot != null && !TrainingEnvSpace.HasMultipleTrainingEnvs())
            return TryFindNearestZombie(origin, maxRange, null, false, ref nearestZombie, ref distance);

        return false;
    }

    static bool TryFindNearestZombie(
        Vector3 origin,
        float maxRange,
        Transform envRoot,
        bool requireEnvScope,
        ref GameObject nearestZombie,
        ref float distance)
    {
        var chases = Object.FindObjectsOfType<ZombieChase>();

        for (int i = 0; i < chases.Length; i++)
        {
            var chase = chases[i];
            if (chase == null || !chase.gameObject.activeInHierarchy)
                continue;
            if (requireEnvScope && envRoot != null && !TrainingEnvSpace.IsDescendantOf(chase.transform, envRoot))
                continue;

            float d = HorizontalDistanceTo(origin, chase.transform.position);
            if (d > maxRange || d >= distance)
                continue;

            distance = d;
            nearestZombie = chase.gameObject;
        }

        return nearestZombie != null;
    }

    static float HorizontalDistanceTo(Vector3 from, Vector3 to)
    {
        from.y = 0f;
        to.y = 0f;
        return Vector3.Distance(from, to);
    }

    /// <summary>
    /// Деревья висят под TreeSpawner; transform.root = спавнер → нельзя Destroy(root).
    /// Нужен один инстанс — прямой ребёнок спавнера.
    /// </summary>
    private GameObject GetTreeInstanceRoot(Collider hit)
    {
        Transform t = hit.transform;
        if (treeSpawner != null)
        {
            Transform sp = treeSpawner.transform;
            for (; t != null; t = t.parent)
            {
                if (t.parent == sp)
                    return t.gameObject;
            }
        }
        return hit.gameObject;
    }

    private GameObject GetSheepInstanceRoot(Collider hit)
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

    /// <summary>
    /// Дистанция добычи: до ближайшей точки на коллайдере, а не до pivot (центр дерева недостижим).
    /// </summary>
    private static float HarvestReachDistance(Vector3 from, Collider c)
    {
        return Vector3.Distance(from, c.ClosestPoint(from));
    }

    private bool TryEatSheep()
    {
        Vector3 origin = transform.position;
        Collider[] hits = Physics.OverlapSphere(origin, EatReach, sheepLayer);

        GameObject bestRoot = null;
        float bestDist = float.MaxValue;
        foreach (var c in hits)
        {
            if (c == null) continue;
            float d = HarvestReachDistance(origin, c);
            if (d > EatReach) continue;
            GameObject root = GetSheepInstanceRoot(c);
            if (d < bestDist)
            {
                bestDist = d;
                bestRoot = root;
            }
        }

        if (bestRoot == null)
            return false;

        satiety += 2;
        Destroy(bestRoot);
        sheepSpawner?.NotifySheepEaten();
        return true;
    }

    bool TryCollectWater()
    {
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (!WaterSource.TryCollect(transform, WaterCollectReach, envRoot, out int amount))
            return false;

        water += Mathf.Max(1, waterPerCollect > 0 ? waterPerCollect : amount);
        return true;
    }


    private bool GetNearestTree(out GameObject nearestTree, out float distance)
    {
        nearestTree = null;
        distance = float.MaxValue;

        Collider[] hits = Physics.OverlapSphere(
            transform.position,
            20f, // радиус поиска больше дистанции рубки
            treeLayer
        );

        if (hits.Length == 0)
            return false;

        foreach (var hit in hits)
        {
            if (hit == null) continue;
            float d = Vector3.Distance(transform.position, hit.transform.position);
            if (d < distance)
            {
                distance = d;
                nearestTree = hit.gameObject;
            }
        }

        return true;
    }


    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.position);
        sensor.AddObservation(transform.forward);

        sensor.AddObservation(Mathf.Clamp01((float)wood / Mathf.Max(1, maxWood)));
        sensor.AddObservation((float)heat / maxHeat); // [0,1]
        sensor.AddObservation(Mathf.Clamp01((float)satiety / Mathf.Max(1, maxSatiety))); // сытость [0,1]

        bool onHouse =
        Vector3.Distance(transform.position, houseTarget.position) <= HouseReach;

        sensor.AddObservation(onHouse ? 1f : 0f);
        // рядом ли дерево
        bool nearTree = IsTreeNearby();
        sensor.AddObservation(nearTree ? 1f : 0f);

        // Опции: 3 бита (0=дерево, 1=еда, 2=зомби, 3=вода)
        sensor.AddObservation(GetOptionObservationBit0(currentOption));
        sensor.AddObservation(GetOptionObservationBit1(currentOption));
        sensor.AddObservation(GetOptionObservationBit2(currentOption));
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (_deathSequenceStarted)
            return;

        // Utility sampling: пересэмпливаем каждые 20 шагов (если включено)
        if (IsOptionSwitchTrainingMode && useUtilitySoftmaxSampling && !HeuristicOptionsLocked
            && !IsZombieTrainingMode && !UsesGeorgeSurvivalOptions
            && stepCount > 0 && (stepCount % 20) == 0)
        {
            if (!IsWoodGatherGoalReached && (!IsFullTrainingMode || !HasZombieInActivationRadius()))
            {
                int prevOption = currentOptionTrain;
                int sampled = SampleOptionUtilitySoftmax(currentOptionTrain);
                ResetWaterGoalIfEntered(prevOption, sampled);
                currentOptionTrain = sampled;
                currentOption = sampled;
                if (sampled == OptionWood || sampled == OptionFood)
                    _lastNonZombieOption = sampled;
                UpdateOptionIconVisual();
            }
        }
        else if (UsesGeorgeSurvivalOptions && useUtilitySoftmaxSampling && !HeuristicOptionsLocked
            && stepCount > 0 && (stepCount % 20) == 0)
        {
            int prevOption = currentOptionTrain;
            int sampled = SampleGeorgeOptionUtilitySoftmax(currentOptionTrain);
            ResetWaterGoalIfEntered(prevOption, sampled);
            currentOptionTrain = sampled;
            currentOption = sampled;
            UpdateOptionIconVisual();
        }

        RefreshTrainingOption();
        
        int moveAction = actions.DiscreteActions[0];
        int rotateAction = actions.DiscreteActions[1];
        int chopAction   = actions.DiscreteActions[2];

        if (TrainingEnvSpace.IsPresentationTransform(transform) && !TwitchEphemeralEffects.IsTwitchClone(this))
            TrainingPolicyStats.RecordJackActions(moveAction, rotateAction, chopAction);

        bool chopJustPressed = chopAction == 1 && _lastChopActionForAnim != 1;
        bool doReady = chopJustPressed && _doCooldownRemaining <= 0f;

        if (doReady && doActionAnimTrigger.Length > 0)
            animator.SetTrigger(doActionAnimTrigger);

        float moveInput = 0f;
        float rotateInput = 0f;

        // --- Движение ---
        if (CanMoveThisStep)
        {
            if (moveAction == 1) moveInput = 1f;
            else if (moveAction == 3) moveInput = -1f;
        }

        // --- Поворот ---
        if (CanRotateThisStep)
        {
            if (rotateAction == 1) rotateInput = 1f;
            else if (rotateAction == 3) rotateInput = -1f;
        }

        if (CanRotateThisStep)
            transform.Rotate(0f, rotateInput * rotationSpeed * Time.deltaTime, 0f);

        // --- Гравитация ---
        if (ControllerReady)
        {
            if (controller.isGrounded)
            {
                if (verticalVelocity < 0f)
                    verticalVelocity = -2f; // прижимаем к земле
            }
            else
            {
                verticalVelocity += gravity * Time.deltaTime;
            }

            // --- Итоговое движение ---
            Vector3 pushVel = GetTwitchForwardPushVelocity();
            Vector3 move = CanMoveThisStep
                ? (transform.forward * moveInput * moveSpeed + pushVel + Vector3.up * verticalVelocity)
                : (Vector3.up * verticalVelocity);

            controller.Move(move * Time.deltaTime);
            float planarSpeed = CanMoveThisStep ? Mathf.Max(Mathf.Abs(moveInput) * moveSpeed, pushVel.magnitude) : 0f;
            AgentFootsteps.NotifyMovement(gameObject, planarSpeed);
        }

        _lastPlanarMoveInput = moveInput;

        // --- Reward: прогресс к дому ---
        float prevDist = Vector3.Distance(prevPosition, houseTarget.position);
        float currDist = Vector3.Distance(transform.position, houseTarget.position);

        currentStepReward = 0f; // сбрасываем награду за шаг

        // Добыча: нарастающий фронт DO + кулдаун; только цель текущей опции; дистанция по коллайдеру.
        int currentOptionSnapshot = currentOptionTrain;

        bool choppedTree = false;
        bool gainedWoodFromChop = false;
        bool ateSheep = false;
        bool collectedWater = false;

        bool hitZombieOnDo = false;
        bool zombieKilledOnDo = false;
        bool hitAllyOnDo = false;
        int woodBeforeChop = wood;
        if (doReady)
        {
            if (damageOnDoIfZombieNearby && zombieDamageOnDo > 0 && IsZombieNearbyForDo())
            {
                TakeDamage(zombieDamageOnDo);
                HeroDamageFeedback.Play(transform);
            }

            hitAllyOnDo = TryDamageAllyOnDo();

            if (knockbackZombieOnDo && !UsesGeorgeSurvivalOptions)
                hitZombieOnDo = KnockbackNearbyZombiesOnDo(out zombieKilledOnDo);

            woodBeforeChop = wood;
            choppedTree = TryChopTree(out gainedWoodFromChop);
            ateSheep = TryEatSheep();
            collectedWater = TryCollectWater();

            _doCooldownRemaining = Mathf.Max(0f, doActionCooldownSeconds);

            if (emptyDoActionPenalty < 0f)
            {
                bool usefulDo = (!UsesGeorgeSurvivalOptions && hitZombieOnDo && currentOptionSnapshot == OptionZombie)
                    || (!UsesGeorgeSurvivalOptions && gainedWoodFromChop && currentOptionSnapshot == OptionWood)
                    || (ateSheep && currentOptionSnapshot == OptionFood)
                    || collectedWater
                    || (!UsesGeorgeSurvivalOptions && hitAllyOnDo);
                if (!usefulDo)
                {
                    AddReward(emptyDoActionPenalty);
                    currentStepReward += emptyDoActionPenalty;
                }
            }
        }
        
        // Reward за рубку дерева — только если опция = дерево (0).
        // Дерево ломается при любом DO; звук — при любой успешной рубке.
        if (choppedTree)
            GameSfx.PlayWood(source: transform);

        // +10 за рубку только пока не набран порог maxWood; дальше — награда за путь к дому.
        if (choppedTree && gainedWoodFromChop && currentOptionSnapshot == OptionWood && woodBeforeChop < maxWood)
        {
            float reward = 10.0f;
            AddReward(reward);
            FloatingRewardPopup.ShowGotWood(transform, reward);
            currentStepReward += reward;
            accumulatedRewardForOption0 += reward;
            lastRewardForOption0 = accumulatedRewardForOption0;
        }
        
        // Reward за поедание овцы - только если опция = еда (1)
        // Используем snapshot опции для защиты от изменения во время выполнения
        if (ateSheep && currentOptionSnapshot == OptionFood)
        {
            float reward = 10.0f;
            AddReward(reward);
            GameSfx.PlayFood(source: transform);
            FloatingRewardPopup.ShowGotFood(transform, reward);
            currentStepReward += reward;
            accumulatedRewardForOption1 += reward;
            lastRewardForOption1 = accumulatedRewardForOption1;
        }

        if (collectedWater && waterCollectReward != 0f)
        {
            AddReward(waterCollectReward);
            GameSfx.PlayFood(source: transform);
            FloatingRewardPopup.ShowGotWater(transform, waterCollectReward);
            currentStepReward += waterCollectReward;
            if (currentOptionSnapshot == OptionWater)
            {
                accumulatedRewardForOption0 += waterCollectReward;
                lastRewardForOption0 = accumulatedRewardForOption0;
            }
        }

        if (hitZombieOnDo && currentOptionSnapshot == OptionZombie && !UsesGeorgeSurvivalOptions)
        {
            if (zombieKilledOnDo && rewardOnZombieKillDo != 0f)
            {
                AddReward(rewardOnZombieKillDo);
                currentStepReward += rewardOnZombieKillDo;
                accumulatedRewardForOption2 += rewardOnZombieKillDo;
                lastRewardForOption2 = accumulatedRewardForOption2;
                FloatingRewardPopup.ShowZombieKill(transform, rewardOnZombieKillDo);
            }
            else if (rewardOnZombieHitDo != 0f)
            {
                AddReward(rewardOnZombieHitDo);
                currentStepReward += rewardOnZombieHitDo;
                accumulatedRewardForOption2 += rewardOnZombieHitDo;
                lastRewardForOption2 = accumulatedRewardForOption2;
            }
        }

        if (currentOptionTrain == OptionWood)
        {
            // Набрали порог дров — ведём к дому/костру (wood может расти без лимита).
            if (wood >= maxWood)
            {
                float reward = prevDist - currDist;
                AddReward(reward);
                currentStepReward += reward;
                accumulatedRewardForOption0 += reward;
                lastRewardForOption0 = accumulatedRewardForOption0;
            }
            else
            {
                if (GetNearestTree(out GameObject tree, out float currTreeDist))
                {
                    if (prevTreeDist > 0f)
                    {
                        float delta = prevTreeDist - currTreeDist;
                        float reward = delta * 0.5f; // коэффициент подбирается
                        AddReward(reward);
                        currentStepReward += reward;
                        accumulatedRewardForOption0 += reward;
                        lastRewardForOption0 = accumulatedRewardForOption0;
                    }

                    prevTreeDist = currTreeDist;
                }
                else
                {
                    prevTreeDist = 0f;
                }
            }
        }


        if (currentOptionTrain == OptionFood)
        {
            if (GetNearestSheep(out GameObject sheep, out float currSheepDist))
            {
                if (prevSheepDist > 0f)
                {
                    float delta = prevSheepDist - currSheepDist;

                    // подошёл ближе → reward +
                    float reward = delta * 0.5f;
                    AddReward(reward);
                    currentStepReward += reward;
                    accumulatedRewardForOption1 += reward;
                    lastRewardForOption1 = accumulatedRewardForOption1;
                }

                prevSheepDist = currSheepDist;
            }
            else
            {
                prevSheepDist = 0f;
            }
        }

        if (currentOptionTrain == OptionZombie)
        {
            if (GetNearestZombieForOption(optionZombieActivationRadius, out _, out float currZombieDist))
            {
                if (prevZombieDist > 0f)
                {
                    float delta = prevZombieDist - currZombieDist;
                    float reward = delta * zombieApproachRewardScale;
                    AddReward(reward);
                    currentStepReward += reward;
                    accumulatedRewardForOption2 += reward;
                    lastRewardForOption2 = accumulatedRewardForOption2;
                }

                prevZombieDist = currZombieDist;
            }
            else
            {
                prevZombieDist = 0f;
            }
        }

        if (currentOptionTrain == OptionWater)
        {
            var waterPath = WaterGoalPath.Get(transform);
            if (waterPath != null)
            {
                waterPath.ProcessStep(transform, reward =>
                {
                    AddReward(reward);
                    currentStepReward += reward;
                });
                prevWaterDist = 0f;
            }
            else
            {
                var envRoot = TrainingEnvSpace.FindRoot(transform);
                if (WaterSource.TryFindNearestDistance(transform, envRoot, out float currWaterDist))
                {
                    if (prevWaterDist > 0f)
                    {
                        float delta = prevWaterDist - currWaterDist;
                        float reward = delta * 0.3f;
                        AddReward(reward);
                        currentStepReward += reward;
                    }

                    prevWaterDist = currWaterDist;
                }
                else
                {
                    prevWaterDist = 0f;
                }
            }
        }

        if (UsesGeorgeSurvivalOptions && currentOptionTrain == OptionHeat && houseTarget != null)
        {
            float currHouseDist = Vector3.Distance(transform.position, houseTarget.position);
            if (prevHouseDist > 0f)
            {
                float reward = (prevHouseDist - currHouseDist) * moveTowardsHouseWhenColdRewardScale;
                AddReward(reward);
                currentStepReward += reward;
            }
            prevHouseDist = currHouseDist;
        }
        else
        {
            prevHouseDist = -1f;
        }

        prevPosition = transform.position;
        
        stepCount++;

        if (IsSimpleTrainingMode && !UsesGeorgeSurvivalOptions)
        {
            TryEndSimpleTrainingEpisode(choppedTree, gainedWoodFromChop, ateSheep, zombieKilledOnDo, collectedWater);
            _lastChopActionForAnim = chopAction;
            return;
        }

        if (IsGeorgeSimpleTrainingMode)
        {
            TryEndGeorgeSimpleTrainingEpisode(ateSheep, collectedWater);
            _lastChopActionForAnim = chopAction;
            return;
        }

        if (IsWoodFoodSwitchMode)
            ApplyWoodFoodSwitchStepPenalty();

        if (!_deathSequenceStarted && !IsWoodFoodSwitchMode && survivalGoalSeconds > 0f
            && SurvivalElapsedSeconds >= SurvivalTotalSeconds)
        {
            EndEpisode();
            return;
        }

        if (!_deathSequenceStarted && !IsWoodFoodSwitchMode && _survivalPhase == 1 && survivalGoalSeconds > 0f
            && SurvivalElapsedSeconds >= survivalGoalSeconds)
        {
            EnterSurvivalPhase2();
        }

        if (!_deathSequenceStarted && !IsWoodFoodSwitchMode && _survivalPhase == 2 && survivalGoalSeconds > 0f
            && SurvivalElapsedSeconds >= survivalGoalSeconds * 2f)
        {
            EnterSurvivalPhase3();
        }

        satietyTimer += Time.deltaTime;
        if (satietyTimer >= satietyDecayInterval)
        {
            satiety = Mathf.Max(0, satiety - 1);
            satietyTimer = 0f;
        }

        heatTimer += Time.deltaTime;
        if (heatTimer >= heatDecayInterval)
        {
            heat = Mathf.Max(0, heat - 1);
            heatTimer = 0f;
        }

        waterTimer += Time.deltaTime;
        if (waterTimer >= waterDecayInterval)
        {
            water = Mathf.Max(0, water - 1);
            waterTimer = 0f;
        }

        UpdateFreezing();
        UpdateStarving();
        UpdateDehydration();
        if (UsesGeorgeSurvivalOptions)
            UpdateGeorgeWarmthAtCampfire();

        bool onHouse = Vector3.Distance(transform.position, houseTarget.position) <= HouseReach;

        if (ControlsSharedCampfire)
        {
            if (onHouse && wood > 0)
                DepositWoodIntoCampfire();

            if (_campfireBurnSecondsRemaining > 0f)
            {
                SetCampfireVisible(true);
                _campfireBurnSecondsRemaining -= Time.deltaTime;

                burnTimer += Time.deltaTime;
                if (burnTimer >= burnInterval)
                {
                    burnTimer -= burnInterval;
                    if (heat < maxHeat)
                        heat = Mathf.Min(maxHeat, heat + heatPerWood);
                    if (currentOptionTrain == OptionWood)
                    {
                        float reward = 5.0f;
                        AddReward(reward);
                        FloatingRewardPopup.ShowWarmedUp(transform, reward);
                        currentStepReward += reward;
                        accumulatedRewardForOption0 += reward;
                        lastRewardForOption0 = accumulatedRewardForOption0;
                    }
                }
            }
            else
            {
                _campfireBurnSecondsRemaining = 0f;
                burnTimer = 0f;

                if (fireVfx != null && fireVfx.activeSelf)
                {
                    if (fireVfxOffTimer <= 0f)
                        fireVfxOffTimer = fireVfxOffDelaySeconds;

                    fireVfxOffTimer -= Time.deltaTime;
                    if (fireVfxOffTimer <= 0f)
                        SetCampfireVisible(false);
                }
            }
        }

        // Лимит эпизода: фаза 1 = survivalGoalSeconds, фаза 2 и 3 по survivalGoalSeconds; или Max Step если survivalGoalSeconds = 0.

        _lastChopActionForAnim = chopAction;
    }

    void ApplyWoodFoodSwitchStepPenalty()
    {
        if (_trainingConfig == null || _trainingConfig.StepPenalty == 0f)
            return;

        float penalty = _trainingConfig.StepPenalty;
        AddReward(penalty);
        currentStepReward += penalty;
        if (currentOptionTrain == OptionWood)
        {
            accumulatedRewardForOption0 += penalty;
            lastRewardForOption0 = accumulatedRewardForOption0;
        }
        else if (currentOptionTrain == OptionFood)
        {
            accumulatedRewardForOption1 += penalty;
            lastRewardForOption1 = accumulatedRewardForOption1;
        }
    }

    void TryEndSimpleTrainingEpisode(
        bool choppedTree,
        bool gainedWoodFromChop,
        bool ateSheep,
        bool zombieKilledOnDo,
        bool collectedWater)
    {
        if (_trainingConfig != null && _trainingConfig.StepPenalty != 0f)
        {
            float penalty = _trainingConfig.StepPenalty;
            AddReward(penalty);
            currentStepReward += penalty;
            if (_resolvedTrainingMode == JackTrainingMode.WoodOnly)
            {
                accumulatedRewardForOption0 += penalty;
                lastRewardForOption0 = accumulatedRewardForOption0;
            }
            else if (_resolvedTrainingMode == JackTrainingMode.FoodOnly)
            {
                accumulatedRewardForOption1 += penalty;
                lastRewardForOption1 = accumulatedRewardForOption1;
            }
            else if (_resolvedTrainingMode == JackTrainingMode.ZombieOnly)
            {
                accumulatedRewardForOption2 += penalty;
                lastRewardForOption2 = accumulatedRewardForOption2;
            }
        }

        if (_trainingConfig != null && _trainingConfig.EndOnSuccess)
        {
            if (_resolvedTrainingMode == JackTrainingMode.WoodOnly && choppedTree && gainedWoodFromChop)
            {
                float bonus = _trainingConfig.SuccessReward;
                if (bonus != 0f)
                {
                    AddReward(bonus);
                    currentStepReward += bonus;
                    accumulatedRewardForOption0 += bonus;
                    lastRewardForOption0 = accumulatedRewardForOption0;
                }
                EndEpisode();
                return;
            }

            if (_resolvedTrainingMode == JackTrainingMode.FoodOnly && ateSheep)
            {
                float bonus = _trainingConfig.SuccessReward;
                if (bonus != 0f)
                {
                    AddReward(bonus);
                    currentStepReward += bonus;
                    accumulatedRewardForOption1 += bonus;
                    lastRewardForOption1 = accumulatedRewardForOption1;
                }
                EndEpisode();
                return;
            }

            if (_resolvedTrainingMode == JackTrainingMode.WaterOnly
                && (collectedWater || IsWaterPathComplete()))
            {
                float bonus = _trainingConfig.SuccessReward;
                if (bonus != 0f)
                {
                    AddReward(bonus);
                    currentStepReward += bonus;
                }
                EndEpisode();
                return;
            }

            if (_resolvedTrainingMode == JackTrainingMode.ZombieOnly && zombieKilledOnDo)
            {
                if (_trainingConfig != null && !_trainingConfig.ZombieEndOnKill)
                    return;

                float bonus = _trainingConfig != null ? _trainingConfig.SuccessReward : 5f;
                if (bonus != 0f)
                {
                    AddReward(bonus);
                    currentStepReward += bonus;
                    accumulatedRewardForOption2 += bonus;
                    lastRewardForOption2 = accumulatedRewardForOption2;
                }
                EndEpisode();
                return;
            }
        }

        if (_trainingConfig != null
            && GetSimpleEpisodeTimeoutSeconds() > 0f
            && SurvivalElapsedSeconds >= GetSimpleEpisodeTimeoutSeconds())
        {
            EndEpisode();
        }
    }

    float GetSimpleEpisodeTimeoutSeconds()
    {
        if (_trainingConfig == null)
            return 0f;
        if (_resolvedTrainingMode == JackTrainingMode.ZombieOnly)
            return _trainingConfig.ZombieEpisodeTimeoutSeconds;
        return _trainingConfig.SimpleEpisodeTimeoutSeconds;
    }

    bool IsWaterPathComplete()
    {
        var path = WaterGoalPath.Get(transform);
        return path != null && path.HasCompletedPath(transform);
    }

    void TryEndGeorgeSimpleTrainingEpisode(bool ateSheep, bool collectedWater)
    {
        if (_trainingConfig == null)
            return;

        if (_trainingConfig.StepPenalty != 0f)
        {
            float penalty = _trainingConfig.StepPenalty;
            AddReward(penalty);
            currentStepReward += penalty;
        }

        if (!_trainingConfig.EndOnSuccess)
            return;

        var task = _trainingConfig.ResolveTask();
        if (task == EnvTrainingTask.GeorgeFood && ateSheep)
        {
            if (_trainingConfig.SuccessReward != 0f)
                AddReward(_trainingConfig.SuccessReward);
            EndEpisode();
            return;
        }

        if (task == EnvTrainingTask.GeorgeWater && (collectedWater || IsWaterPathComplete()))
        {
            if (_trainingConfig.SuccessReward != 0f)
                AddReward(_trainingConfig.SuccessReward);
            EndEpisode();
            return;
        }

        if (task == EnvTrainingTask.GeorgeHeat && IsGeorgeNearBurningCampfire())
        {
            if (_trainingConfig.SuccessReward != 0f)
                AddReward(_trainingConfig.SuccessReward);
            EndEpisode();
            return;
        }

        if (_trainingConfig.SimpleEpisodeTimeoutSeconds > 0f
            && SurvivalElapsedSeconds >= _trainingConfig.SimpleEpisodeTimeoutSeconds)
            EndEpisode();
    }

    void EnsureZombieSpawners()
    {
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot != null)
        {
            if (zombieSpawner == null)
                zombieSpawner = FindZombieSpawnerInEnv(envRoot, PrimaryZombieSpawnerName);
            if (zombieSpawnerSecondary == null)
                zombieSpawnerSecondary = FindSecondaryZombieSpawnerInEnv(envRoot);
            var envConfig = envRoot.GetComponent<EnvTrainingConfig>();
            if (envConfig != null
                && envConfig.ResolveJackMode() == JackTrainingMode.ZombieOnly
                && zombieSpawnerSecondary == null)
            {
                Debug.LogWarning(
                    $"[{envRoot.name}] ZombieSpawner_2 не найден внутри Env — положи объект с таким именем в корень среды.");
            }
            return;
        }

        if (TrainingEnvSpace.IsPresentationTransform(transform))
        {
            var presentationRoot = TrainingEnvSpace.PresentationRoot;
            if (presentationRoot != null)
            {
                if (zombieSpawner == null)
                    zombieSpawner = FindZombieSpawnerInEnv(presentationRoot, PrimaryZombieSpawnerName);
                if (zombieSpawnerSecondary == null)
                    zombieSpawnerSecondary = FindSecondaryZombieSpawnerInEnv(presentationRoot);
            }

            if (zombieSpawner == null)
                zombieSpawner = ZombieSpawner.FindPresentationZombieSpawner();
        }
    }

    static ZombieSpawner FindZombieSpawnerInEnv(Transform envRoot, string objectName)
    {
        if (envRoot == null || string.IsNullOrEmpty(objectName))
            return null;

        var spawners = envRoot.GetComponentsInChildren<ZombieSpawner>(true);
        for (int i = 0; i < spawners.Length; i++)
        {
            var spawner = spawners[i];
            if (spawner != null
                && string.Equals(spawner.gameObject.name, objectName, System.StringComparison.OrdinalIgnoreCase))
                return spawner;
        }

        return null;
    }

    static ZombieSpawner FindSecondaryZombieSpawnerInEnv(Transform envRoot)
    {
        if (envRoot == null)
            return null;

        var spawners = envRoot.GetComponentsInChildren<ZombieSpawner>(true);
        ZombieSpawner fallback = null;
        for (int i = 0; i < spawners.Length; i++)
        {
            var spawner = spawners[i];
            if (spawner == null)
                continue;

            string name = spawner.gameObject.name;
            if (string.Equals(name, PrimaryZombieSpawnerName, System.StringComparison.OrdinalIgnoreCase))
                continue;

            for (int n = 0; n < SecondaryZombieSpawnerNames.Length; n++)
            {
                if (string.Equals(name, SecondaryZombieSpawnerNames[n], System.StringComparison.OrdinalIgnoreCase))
                    return spawner;
            }

            if (fallback == null
                && (name.IndexOf("Spawner_2", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || name.EndsWith("_2", System.StringComparison.OrdinalIgnoreCase)))
            {
                fallback = spawner;
            }
        }

        return fallback;
    }

    static void DeactivateZombieSpawner(ZombieSpawner spawner)
    {
        if (spawner == null)
            return;

        spawner.ClearZombies();
        spawner.gameObject.SetActive(false);
    }

    static void ActivateZombieSpawner(ZombieSpawner spawner, bool trainingBurst, int immediateSpawnCount)
    {
        if (spawner == null)
            return;

        spawner.ClearZombies();
        if (!spawner.gameObject.activeSelf)
            spawner.gameObject.SetActive(true);

        if (trainingBurst)
            spawner.StartTrainingEpisode(immediateSpawnCount);
        else
            spawner.ResetForNewEpisode();
    }

    void StopZombieSpawnerForEpisode()
    {
        EnsureZombieSpawners();
        DeactivateZombieSpawner(zombieSpawner);
        DeactivateZombieSpawner(zombieSpawnerSecondary);
    }

    bool ShouldIncludeSecondaryZombieSpawner()
    {
        if (!IsZombieTrainingMode)
            return false;

        EnsureZombieSpawners();
        return zombieSpawnerSecondary != null;
    }

    void StartZombieSpawnerForEpisode(bool includeSecondarySpawner)
    {
        EnsureZombieSpawners();
        int immediate = _trainingConfig != null ? _trainingConfig.ZombieImmediateSpawnCount : 2;
        bool trainingBurst = IsZombieTrainingMode;

        ActivateZombieSpawner(zombieSpawner, trainingBurst, immediate);
        if (includeSecondarySpawner)
            ActivateZombieSpawner(zombieSpawnerSecondary, trainingBurst, immediate);
        else
            DeactivateZombieSpawner(zombieSpawnerSecondary);
    }

    void EnterSurvivalPhase2()
    {
        if (_survivalPhase >= 2)
            return;

        _survivalPhase = 2;

        if (phase1CompleteReward != 0f)
        {
            AddReward(phase1CompleteReward);
            currentStepReward += phase1CompleteReward;
            accumulatedRewardForOption0 += phase1CompleteReward;
            accumulatedRewardForOption1 += phase1CompleteReward;
            lastRewardForOption0 = accumulatedRewardForOption0;
            lastRewardForOption1 = accumulatedRewardForOption1;
        }

        if (TrainingEnvSpace.IsPresentationTransform(transform))
        {
            SurvivalPhaseAnnouncement.ShowPhase2();
            BackgroundMusic.SetSurvivalPhase(2);
        }

        StartZombieSpawnerForEpisode(includeSecondarySpawner: false);
    }

    void EnterSurvivalPhase3()
    {
        if (_survivalPhase >= 3)
            return;

        _survivalPhase = 3;

        if (phase2CompleteReward != 0f)
        {
            AddReward(phase2CompleteReward);
            currentStepReward += phase2CompleteReward;
            accumulatedRewardForOption0 += phase2CompleteReward;
            accumulatedRewardForOption1 += phase2CompleteReward;
            lastRewardForOption0 = accumulatedRewardForOption0;
            lastRewardForOption1 = accumulatedRewardForOption1;
        }

        if (TrainingEnvSpace.IsPresentationTransform(transform))
        {
            SurvivalPhaseAnnouncement.ShowPhase3();
            BackgroundMusic.SetSurvivalPhase(3);
        }

        ActivateSecondaryZombieSpawner();
        ApplyNightmareZombieSpeedToEnv();
        SpawnNightmareBossZombie();
        _nextNightmareBossSpawnTime = Time.unscaledTime + nightmareBossSpawnIntervalSeconds;
    }

    void ActivateSecondaryZombieSpawner()
    {
        EnsureZombieSpawners();
        if (zombieSpawnerSecondary == null)
            return;

        int immediate = _trainingConfig != null ? _trainingConfig.ZombieImmediateSpawnCount : 2;
        bool trainingBurst = IsZombieTrainingMode;
        ActivateZombieSpawner(zombieSpawnerSecondary, trainingBurst, immediate);
    }

    void ApplyNightmareZombieSpeedToEnv()
    {
        EnsureZombieSpawners();
        float speedMult = Mathf.Max(1f, nightmareZombieSpeedMultiplier);

        if (zombieSpawner != null)
            zombieSpawner.SetSpawnMoveSpeedMultiplier(speedMult);
        if (zombieSpawnerSecondary != null)
            zombieSpawnerSecondary.SetSpawnMoveSpeedMultiplier(speedMult);

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot == null)
            return;

        var chases = envRoot.GetComponentsInChildren<ZombieChase>(true);
        for (int i = 0; i < chases.Length; i++)
        {
            if (chases[i] != null)
                chases[i].SetMoveSpeedMultiplier(speedMult);
        }
    }

    void SpawnNightmareBossZombie()
    {
        EnsureZombieSpawners();
        if (zombieSpawner == null)
            return;

        zombieSpawner.SpawnBossZombie(
            nightmareBossScaleMultiplier,
            nightmareBossHpMultiplier,
            nightmareBossSpeedMultiplier,
            nightmareBossAttackDamageMultiplier,
            nightmareBossAttackCooldownMultiplier);
    }

    /// <summary>Play-тест: V — перейти к следующему этапу выживания.</summary>
    public void AdvanceSurvivalPhaseDebug()
    {
        if (_deathSequenceStarted || IsSimpleTrainingMode || IsWoodFoodSwitchMode || survivalGoalSeconds <= 0f)
            return;

        if (_survivalPhase == 1)
        {
            _episodeStartTime = Time.unscaledTime - survivalGoalSeconds;
            EnterSurvivalPhase2();
        }
        else if (_survivalPhase == 2)
        {
            _episodeStartTime = Time.unscaledTime - survivalGoalSeconds * 2f;
            EnterSurvivalPhase3();
        }
    }

    void UpdateFreezing()
    {
        if (_deathSequenceStarted || heat > 0)
        {
            _freezeTimer = 0f;
            return;
        }

        _freezeTimer += Time.deltaTime;
        if (_freezeTimer < freezeDamageInterval)
            return;

        _freezeTimer = 0f;

        if (freezePenaltyPerTick != 0f)
        {
            AddReward(freezePenaltyPerTick);
            currentStepReward += freezePenaltyPerTick;
            accumulatedRewardForOption0 += freezePenaltyPerTick;
            accumulatedRewardForOption1 += freezePenaltyPerTick;
            lastRewardForOption0 = accumulatedRewardForOption0;
            lastRewardForOption1 = accumulatedRewardForOption1;
        }

        FloatingRewardPopup.ShowFreezing(transform, freezePenaltyPerTick);

        if (freezeDamageAmount > 0)
            TakeDamage(freezeDamageAmount, applyHpLossPenalty: false);
    }

    void UpdateStarving()
    {
        if (_deathSequenceStarted || satiety > 0)
        {
            _hungerTimer = 0f;
            return;
        }

        _hungerTimer += Time.deltaTime;
        if (_hungerTimer < hungerDamageInterval)
            return;

        _hungerTimer = 0f;

        if (hungerPenaltyPerTick != 0f)
        {
            AddReward(hungerPenaltyPerTick);
            currentStepReward += hungerPenaltyPerTick;
            accumulatedRewardForOption0 += hungerPenaltyPerTick;
            accumulatedRewardForOption1 += hungerPenaltyPerTick;
            lastRewardForOption0 = accumulatedRewardForOption0;
            lastRewardForOption1 = accumulatedRewardForOption1;
        }

        FloatingRewardPopup.ShowHungry(transform, hungerPenaltyPerTick);

        if (hungerDamageAmount > 0)
            TakeDamage(hungerDamageAmount, applyHpLossPenalty: false);
    }

    void UpdateDehydration()
    {
        if (_deathSequenceStarted || water > 0)
        {
            _thirstTimer = 0f;
            return;
        }

        _thirstTimer += Time.deltaTime;
        if (_thirstTimer < thirstDamageInterval)
            return;

        _thirstTimer = 0f;

        if (thirstDamageAmount > 0)
            TakeDamage(thirstDamageAmount, applyHpLossPenalty: false);
    }

    private int SampleOptionUtilitySoftmax(int currentOpt)
    {
        float heatRatio = maxHeat > 0 ? (float)heat / maxHeat : 0f;
        float satietyRatio = maxSatiety > 0 ? Mathf.Clamp01((float)satiety / maxSatiety) : 1f;

        float needHeat = Mathf.Clamp01(1f - heatRatio);
        float needFood = Mathf.Clamp01(1f - satietyRatio);

        float accessWood = DistanceToAccess(GetDistanceToNearestTree());
        float accessFood = DistanceToAccess(GetDistanceToNearestSheep());

        float stickWood = currentOpt == 0 ? 1f : 0f;
        float stickFood = currentOpt == 1 ? 1f : 0f;

        float epsWood = Random.Range(-noise, noise);
        float epsFood = Random.Range(-noise, noise);

        // 0 = дерево (лес): основной драйвер — потребность в тепле.
        // 1 = еда: основной драйвер — потребность в еде (сытости).
        float uWood = 2.5f * needHeat + 1.0f * accessWood + stickinessBonus * stickWood + epsWood;
        float uFood = 2.5f * needFood + 1.0f * accessFood + stickinessBonus * stickFood + epsFood;

        return SoftmaxSample2(uWood, uFood, Mathf.Max(0.0001f, tau));
    }

    int SampleGeorgeOptionUtilitySoftmax(int currentOpt)
    {
        float heatRatio = maxHeat > 0 ? (float)heat / maxHeat : 0f;
        float satietyRatio = maxSatiety > 0 ? Mathf.Clamp01((float)satiety / maxSatiety) : 1f;
        float waterRatio = Mathf.Clamp01(water / 12f);

        float needHeat = Mathf.Clamp01(1f - heatRatio);
        float needFood = Mathf.Clamp01(1f - satietyRatio);
        float needWater = Mathf.Clamp01(1f - waterRatio);

        float accessFood = DistanceToAccess(GetDistanceToNearestSheep());
        float accessWater = DistanceToAccess(prevWaterDist > 0f ? prevWaterDist : 999f);
        float accessHouse = DistanceToAccess(houseTarget != null
            ? Vector3.Distance(transform.position, houseTarget.position)
            : 999f);

        float stickFood = currentOpt == OptionFood ? 1f : 0f;
        float stickWater = currentOpt == OptionWater ? 1f : 0f;
        float stickHeat = currentOpt == OptionHeat ? 1f : 0f;

        float epsFood = Random.Range(-noise, noise);
        float epsWater = Random.Range(-noise, noise);
        float epsHeat = Random.Range(-noise, noise);

        float uFood = 2.5f * needFood + 1.0f * accessFood + stickinessBonus * stickFood + epsFood;
        float uWater = 2.5f * needWater + 1.0f * accessWater + stickinessBonus * stickWater + epsWater;
        float uHeat = 2.5f * needHeat + 1.0f * accessHouse + stickinessBonus * stickHeat + epsHeat;

        return SoftmaxSample3(uFood, uWater, uHeat, OptionFood, OptionWater, OptionHeat, Mathf.Max(0.0001f, tau));
    }

    AgentGoToHouseDiscrete FindCampfireJackInEnv()
    {
        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot == null)
            return null;

        var agents = envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(false);
        for (int i = 0; i < agents.Length; i++)
        {
            var agent = agents[i];
            if (agent == null || TrainingEnvSpace.IsGeorgeAgent(agent) || TwitchEphemeralEffects.IsTwitchClone(agent))
                continue;
            return agent;
        }

        return null;
    }

    bool IsGeorgeNearBurningCampfire()
    {
        var jack = FindCampfireJackInEnv();
        if (jack == null || jack.CampfireBurnSecondsRemaining <= 0f || houseTarget == null)
            return false;

        return Vector3.Distance(transform.position, houseTarget.position) <= HouseReach;
    }

    void UpdateGeorgeWarmthAtCampfire()
    {
        if (_deathSequenceStarted || currentOptionTrain != OptionHeat)
        {
            _warmthGainTimer = 0f;
            return;
        }

        if (!IsGeorgeNearBurningCampfire())
        {
            _warmthGainTimer = 0f;
            return;
        }

        _warmthGainTimer += Time.deltaTime;
        if (_warmthGainTimer < warmthGainInterval)
            return;

        _warmthGainTimer = 0f;
        if (heat < maxHeat)
            heat = Mathf.Min(maxHeat, heat + 1);
        if (warmthRewardAtCampfire != 0f)
        {
            AddReward(warmthRewardAtCampfire);
            FloatingRewardPopup.ShowWarmedUp(transform, warmthRewardAtCampfire);
        }
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
        Collider[] hits = Physics.OverlapSphere(origin, ZombieDoReach, zombieLayer);
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

    bool TryDamageAllyOnDo()
    {
        if (allyDoDamage <= 0 && Mathf.Approximately(friendlyFireDoPenalty, 0f))
            return false;

        Vector3 origin = transform.position;
        float radius = Mathf.Max(zombieKnockbackRadiusOnDo, 1.6f);
        Collider[] hits = Physics.OverlapSphere(origin, radius);
        if (hits == null || hits.Length == 0)
            return false;

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        LilyScript bestLily = null;
        AgentGoToHouseDiscrete bestAlly = null;
        float bestDist = float.MaxValue;

        for (int i = 0; i < hits.Length; i++)
        {
            var c = hits[i];
            if (c == null)
                continue;
            if (!IsZombieColliderInFrontForDo(origin, c))
                continue;

            var lily = c.GetComponentInParent<LilyScript>();
            if (lily != null)
            {
                if (envRoot != null && !TrainingEnvSpace.IsDescendantOf(lily.transform, envRoot))
                    continue;
                float d = Vector3.Distance(origin, c.ClosestPoint(origin));
                if (d < bestDist)
                {
                    bestDist = d;
                    bestLily = lily;
                    bestAlly = null;
                }

                continue;
            }

            var ally = c.GetComponentInParent<AgentGoToHouseDiscrete>();
            if (ally == null || ally == this)
                continue;
            if (envRoot != null && !TrainingEnvSpace.IsDescendantOf(ally.transform, envRoot))
                continue;

            float dist = Vector3.Distance(origin, c.ClosestPoint(origin));
            if (dist < bestDist)
            {
                bestDist = dist;
                bestAlly = ally;
                bestLily = null;
            }
        }

        if (bestLily == null && bestAlly == null)
            return false;

        if (allyDoDamage > 0)
        {
            if (bestLily != null)
            {
                bestLily.TakeDamage(allyDoDamage);
                HeroDamageFeedback.Play(bestLily.transform);
            }
            else
            {
                bestAlly.TakeDamage(allyDoDamage);
                HeroDamageFeedback.Play(bestAlly.transform);
            }
        }

        if (bestLily != null)
            ApplyFriendlyKnockbackOnly(bestLily.transform);
        else if (bestAlly != null)
            ApplyFriendlyKnockbackOnly(bestAlly.transform);

        if (friendlyFireDoPenalty != 0f)
        {
            AddReward(friendlyFireDoPenalty);
            currentStepReward += friendlyFireDoPenalty;
        }

        return true;
    }

    void ApplyFriendlyKnockbackOnly(Transform victim)
    {
        if (victim == null)
            return;

        var lily = victim.GetComponentInParent<LilyScript>();
        if (lily != null)
        {
            lily.ApplyFriendlyKnockbackFrom(transform.position, allyKnockbackDistance, allyKnockbackUp);
            return;
        }

        Vector3 dir = victim.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-4f)
            dir = transform.forward;
        dir.Normalize();

        Vector3 delta = dir * allyKnockbackDistance + Vector3.up * allyKnockbackUp;
        var cc = victim.GetComponent<CharacterController>();
        if (cc != null && cc.enabled)
            cc.Move(delta);
        else
            victim.position += delta;

        var ally = victim.GetComponent<AgentGoToHouseDiscrete>();
        if (ally != null)
            ally.StunForSeconds(0.35f);
    }

    private bool KnockbackNearbyZombiesOnDo(out bool zombieKilled)
    {
        zombieKilled = false;
        if (zombieLayer.value == 0) return false;
        if (zombieKnockbackRadiusOnDo <= 0f) return false;
        if (zombieKnockbackDistanceOnDo <= 0f && zombieKnockbackImpulseOnDo <= 0f) return false;

        Vector3 origin = transform.position;
        Collider[] hits = Physics.OverlapSphere(origin, ZombieKnockbackReach, zombieLayer);
        if (hits == null || hits.Length == 0) return false;

        // Выбираем ОДНОГО ближайшего зомби (именно объект с ZombieChase),
        // чтобы за один DO был один "удар" и урон шёл в правильный ZombieHealth.
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
        if (bestZombie == null) return false;

        GameSfx.PlayJackLilyHitZombie(source: transform);

        // Ищем ZombieHealth на том же корне, что и ZombieChase (не на случайном коллайдере).
        var zhBest = bestZombie.GetComponent<ZombieHealth>()
            ?? bestZombie.GetComponentInParent<ZombieHealth>()
            ?? bestZombie.GetComponentInChildren<ZombieHealth>();
        if (zhBest == null && bestZombieCollider != null)
        {
            zhBest = bestZombieCollider.GetComponent<ZombieHealth>()
                ?? bestZombieCollider.GetComponentInParent<ZombieHealth>()
                ?? bestZombieCollider.GetComponentInChildren<ZombieHealth>();
        }

        bool zombieKilledLocal = false;
        if (zhBest != null)
        {
            int damage = zombieDamageToZombieOnDo;
            if (zombieDiesInTwoDoHits)
                damage = Mathf.Max(1, Mathf.CeilToInt(zhBest.MaxHp / 2f));
            if (damage > 0)
            {
                int prevHp = zhBest.Hp;
                zhBest.TakeDamage(damage, transform.position);
                // После Destroy(gameObject) ссылка становится «fake null» — не читаем Hp напрямую.
                if (prevHp > 0 && (zhBest == null || zhBest.Hp <= 0))
                    zombieKilledLocal = true;
            }
        }

        // Гарантия: 2 удара DO без ZombieHealth. Не вызываем, если зомби уже уничтожен по HP.
        if (!zombieKilledLocal && bestZombie != null && bestZombie.RegisterJackDoHitAndMaybeDie(2))
            zombieKilledLocal = true;

        zombieKilled = zombieKilledLocal;

        Transform zt = bestZombie.transform;
        Vector3 dir = (zt.position - origin);
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = zt.forward;
        dir = dir.normalized;

        // Предпочитаем CharacterController, если он есть (как и в ZombieChase)
        var zControllerBest = bestZombie.GetComponent<CharacterController>();
        if (zControllerBest != null && zControllerBest.enabled)
        {
            Vector3 delta = dir * zombieKnockbackDistanceOnDo;
            delta.y = zombieKnockbackUpOnDo;
            zControllerBest.Move(delta);
            if (zombieStunSecondsOnDo > 0f)
                bestZombie.Stun(zombieStunSecondsOnDo);
            return true;
        }

        var zRbBest = bestZombie.GetComponent<Rigidbody>();
        if (zRbBest != null)
        {
            Vector3 forceDir = (dir + Vector3.up * Mathf.Max(0f, zombieKnockbackUpFactorOnDo)).normalized;
            zRbBest.AddForce(forceDir * zombieKnockbackImpulseOnDo, ForceMode.Impulse);
            if (zombieStunSecondsOnDo > 0f)
                bestZombie.Stun(zombieStunSecondsOnDo);
        }

        return true;
    }

    private bool TryChopTree(out bool gainedWood)
    {
        gainedWood = false;

        Vector3 origin = transform.position;
        Collider[] hits = Physics.OverlapSphere(origin, ChopReach, treeLayer);

        GameObject bestRoot = null;
        float bestDist = float.MaxValue;
        foreach (var c in hits)
        {
            if (c == null) continue;
            float d = HarvestReachDistance(origin, c);
            if (d > ChopReach) continue;
            GameObject root = GetTreeInstanceRoot(c);
            if (d < bestDist)
            {
                bestDist = d;
                bestRoot = root;
            }
        }

        if (bestRoot == null)
            return false;

        wood++;
        gainedWood = true;
        treeSpawner?.NotifyTreeChopped(bestRoot);
        Destroy(bestRoot);
        return true;
    }

    void DepositWoodIntoCampfire()
    {
        if (wood <= 0)
            return;

        _campfireBurnSecondsRemaining += wood * burnInterval * burnDurationMultiplier;
        wood = 0;
        SetCampfireVisible(true);
    }


    protected virtual bool IsManualWasdControlActive()
    {
        if (TrainingEnvSpace.IsGeorgeAgent(this))
            return ManualPlayControl.GeorgeManualActive;
        return !ManualPlayControl.GeorgeManualActive;
    }

    protected virtual bool GetHeuristicChopPressed() =>
        Input.GetMouseButton(0);

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        ActionSegment<int> discreteActions = actionsOut.DiscreteActions;

        // Клоны не читают клавиатуру — иначе повторяют каждое действие оригинала.
        if (TwitchEphemeralEffects.IsTwitchClone(this))
        {
            discreteActions[0] = 2;
            discreteActions[1] = 2;
            discreteActions[2] = 0;
            return;
        }

        if (!IsManualWasdControlActive())
        {
            discreteActions[0] = 2;
            discreteActions[1] = 2;
            discreteActions[2] = 0;
            return;
        }

        // --- Движение только W / S (не стрелки) ---
        int moveAction = 2; // стоять
        if (Input.GetKey(KeyCode.W))
            moveAction = 1;   // вперёд
        else if (Input.GetKey(KeyCode.S))
            moveAction = 3;   // назад

        // --- Поворот только A / D (не стрелки) ---
        int rotateAction = 2; // не крутиться
        if (Input.GetKey(KeyCode.D))
            rotateAction = 1; // вправо
        else if (Input.GetKey(KeyCode.A))
            rotateAction = 3; // влево

        int chopAction = GetHeuristicChopPressed() ? 1 : 0;

        discreteActions[0] = moveAction;
        discreteActions[1] = rotateAction;
        discreteActions[2] = chopAction;
    }

}
