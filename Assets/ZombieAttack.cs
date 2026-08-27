using UnityEngine;



/// <summary>

/// При контакте зомби с персонажем (Jack/Lily/George) снимает 1/5 его макс. HP. Кулдаун между ударами.

/// Вешать на того же зомби, что и ZombieChase. Нужен CharacterController или коллайдер для контакта.

/// </summary>

[RequireComponent(typeof(Collider))]

public class ZombieAttack : MonoBehaviour

{

    [SerializeField] private float hitCooldown = 1f;

    [SerializeField] private float damageDelaySeconds = 0.5f;

    [SerializeField] private float damageRadius = 1.9f;

    [SerializeField] private float attackCheckInterval = 0.2f;

    private float lastHitTime = -999f;

    private float _nextAttackCheckTime;

    private float _damageMultiplier = 1f;

    private float _hitCooldownMultiplier = 1f;

    [SerializeField] private string attackLeftTrigger = "AttackL";

    [SerializeField] private string attackRightTrigger = "AttackR";

    private Animator animator;

    private Coroutine pendingHitRoutine;



    private void Start()
    {
        animator = GetComponentInChildren<Animator>() ?? GetComponent<Animator>();
        if (damageRadius < 1.7f)
            damageRadius = 1.9f;
    }

    public void ConfigureCombat(float damageMultiplier, float hitCooldownMultiplier = 1f)
    {
        _damageMultiplier = Mathf.Max(0.1f, damageMultiplier);
        _hitCooldownMultiplier = Mathf.Clamp(hitCooldownMultiplier, 0.1f, 2f);
    }

    public void ConfigureDamageRadius(float radius)
    {
        damageRadius = Mathf.Max(0.5f, radius);
    }

    private void Update()
    {
        if (Time.time < _nextAttackCheckTime)
            return;

        _nextAttackCheckTime = Time.time + attackCheckInterval;
        TryAttackNearbyTargets();
    }



    private void OnControllerColliderHit(ControllerColliderHit hit)

    {

        TryDamage(hit.gameObject);

    }



    private void OnCollisionEnter(Collision collision)

    {

        TryDamage(collision.gameObject);

    }



    void TryAttackNearbyTargets()
    {
        var chase = GetComponentInParent<ZombieChase>() ?? GetComponentInChildren<ZombieChase>();
        if (chase != null && chase.IsStunned)
            return;

        if (Time.time - lastHitTime < hitCooldown * _hitCooldownMultiplier)
            return;

        // 1) Явный поиск ближайшей валидной цели (герои + стрим-фолловеры) —
        //    CharacterController фолловеров OverlapSphere иногда не видит.
        float scanR = GetScanDamageRadius();
        var nearest = FindNearestAttackable(scanR);
        if (nearest != null && TryDamage(nearest.gameObject))
            return;

        // 2) Fallback: physics overlap (Jack CC / коллайдеры).
        Collider[] hits = Physics.OverlapSphere(transform.position, scanR);
        if (hits == null || hits.Length == 0)
            return;

        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i] == null)
                continue;
            if (TryDamage(hits[i].gameObject))
                return;
        }
    }

    MonoBehaviour FindNearestAttackable(float scanRadius)
    {
        Vector3 pos = transform.position;
        float bestDist = scanRadius;
        MonoBehaviour best = null;

        var envRoot = TrainingEnvSpace.FindRoot(transform);
        if (envRoot != null)
        {
            var agents = envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(false);
            for (int i = 0; i < agents.Length; i++)
            {
                var a = agents[i];
                if (a == null || !a.gameObject.activeInHierarchy || a.Hp <= 0 || !a.IsAliveForTwitch)
                    continue;
                if (TwitchEphemeralEffects.IsTwitchClone(a))
                    continue;
                float d = HorizDist(pos, a.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = a;
                }
            }

            var lilies = envRoot.GetComponentsInChildren<LilyScript>(false);
            for (int i = 0; i < lilies.Length; i++)
            {
                var lily = lilies[i];
                if (lily == null || !lily.gameObject.activeInHierarchy || lily.Hp <= 0 || lily.IsInDeathState)
                    continue;
                float d = HorizDist(pos, lily.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = lily;
                }
            }
        }

        var followers = ZombieChase.GetCachedFollowers();
        if (followers != null)
        {
            for (int i = 0; i < followers.Length; i++)
            {
                var p = followers[i];
                if (p == null || !p.isActiveAndEnabled || p.IsDeadToZombies || p.Hp <= 0)
                    continue;
                float d = HorizDist(pos, p.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
        }

        return best;
    }

    static float HorizDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    float GetScanDamageRadius() => damageRadius + 2.5f;

    float GetEffectiveDamageRadius(Transform target)
    {
        float baseR = damageRadius;
        float targetR = ZombieChase.EstimateHeroBodyRadius(target);
        float selfR = 0.45f;
        var cc = GetComponentInChildren<CharacterController>();
        if (cc != null && cc.radius > 0.05f)
            selfR = cc.radius;
        // Достать до центра, стоя у поверхности большого коллайдера.
        return Mathf.Max(baseR, targetR + selfR + 0.4f);
    }

    static bool IsAttackableTarget(IHasHp target)
    {
        if (target == null || !IsHeroAttackTarget(target))
            return false;

        if (target is StreamingSurvivalPlayer follower)
            return !follower.IsDeadToZombies && follower.Hp > 0;

        if (target is LilyScript lily)
            return lily.Hp > 0 && !lily.IsInDeathState;

        if (target is AgentGoToHouseDiscrete agent)
            return agent.IsAliveForTwitch && agent.Hp > 0;

        var mb = target as MonoBehaviour;
        return mb != null && mb.gameObject.activeInHierarchy && target.Hp > 0;
    }

    private bool TryDamage(GameObject other)
    {
        var chase = GetComponentInParent<ZombieChase>() ?? GetComponentInChildren<ZombieChase>();
        if (chase != null && chase.IsStunned)
            return false;

        var target = other.GetComponentInParent<IHasHp>();
        if (target == null || !IsAttackableTarget(target)) return false;
        if (Time.time - lastHitTime < hitCooldown * _hitCooldownMultiplier) return false;

        lastHitTime = Time.time;
        TryPlayAttackAnim();

        if (pendingHitRoutine != null)
            StopCoroutine(pendingHitRoutine);
        pendingHitRoutine = StartCoroutine(ApplyDelayedDamage(target, chase));
        return true;
    }

    private System.Collections.IEnumerator ApplyDelayedDamage(IHasHp target, ZombieChase chase)
    {
        float delay = Mathf.Max(0f, damageDelaySeconds);
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        pendingHitRoutine = null;

        if (target == null) yield break;
        if (!IsAttackableTarget(target)) yield break;
        if (chase != null && chase.IsStunned) yield break;

        var targetMb = target as MonoBehaviour;
        if (targetMb == null) yield break;
        Transform targetTr = targetMb.transform;
        if (targetTr == null) yield break;

        Vector3 a = transform.position;
        Vector3 b = targetTr.position;
        a.y = 0f;
        b.y = 0f;
        float r = GetEffectiveDamageRadius(targetTr);
        // Фолловеры часто успевают отойти за damageDelay — не отменяем удар по ним.
        bool isFollower = target is StreamingSurvivalPlayer;
        if (!isFollower && (a - b).sqrMagnitude > r * r)
            yield break;

        if (isFollower)
        {
            // Ровно 2 удара до смерти, без зависимости от MaxHp / multiplier.
            ((StreamingSurvivalPlayer)target).RegisterZombieHit();
        }
        else
        {
            int damage = Mathf.Max(1, Mathf.RoundToInt(target.MaxHp / 5f * _damageMultiplier));
            target.TakeDamage(damage);
        }
        HeroDamageFeedback.Play(targetTr);
    }


    static bool IsHeroAttackTarget(IHasHp target)

    {

        if (target == null || target is ZombieHealth)

            return false;



        return target is AgentGoToHouseDiscrete
            || target is LilyScript
            || target is StreamingSurvivalPlayer;

    }



    private void TryPlayAttackAnim()

    {

        if (animator == null) return;

        string trigger = (Random.value < 0.5f) ? attackLeftTrigger : attackRightTrigger;

        if (string.IsNullOrEmpty(trigger)) return;

        if (!HasAnimatorTrigger(animator, trigger)) return;

        animator.SetTrigger(trigger);

    }



    private static bool HasAnimatorTrigger(Animator a, string triggerName)

    {

        foreach (var p in a.parameters)

        {

            if (p.type == AnimatorControllerParameterType.Trigger && p.name == triggerName)

                return true;

        }

        return false;

    }

}


