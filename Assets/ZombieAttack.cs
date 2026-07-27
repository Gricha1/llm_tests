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

        // Запас под #size Джека (CC radius ×5): иначе OverlapSphere не дотягивает до центра.
        float radius = GetScanDamageRadius();
        Collider[] hits = Physics.OverlapSphere(transform.position, radius);
        if (hits == null || hits.Length == 0)
            return;

        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i] == null)
                continue;
            TryDamage(hits[i].gameObject);
            return;
        }
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

    private void TryDamage(GameObject other)
    {
        var chase = GetComponentInParent<ZombieChase>() ?? GetComponentInChildren<ZombieChase>();
        if (chase != null && chase.IsStunned)
            return;

        var target = other.GetComponentInParent<IHasHp>();
        if (target == null || !IsHeroAttackTarget(target)) return;
        if (Time.time - lastHitTime < hitCooldown * _hitCooldownMultiplier) return;

        lastHitTime = Time.time;
        TryPlayAttackAnim();

        if (pendingHitRoutine != null)
            StopCoroutine(pendingHitRoutine);
        pendingHitRoutine = StartCoroutine(ApplyDelayedDamage(target, chase));
    }

    private System.Collections.IEnumerator ApplyDelayedDamage(IHasHp target, ZombieChase chase)
    {
        float delay = Mathf.Max(0f, damageDelaySeconds);
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        pendingHitRoutine = null;

        if (target == null) yield break;
        if (!IsHeroAttackTarget(target)) yield break;
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
        if ((a - b).sqrMagnitude > r * r) yield break;

        int damage = Mathf.Max(1, Mathf.RoundToInt(target.MaxHp / 5f * _damageMultiplier));
        target.TakeDamage(damage);
        HeroDamageFeedback.Play(targetTr);
    }


    static bool IsHeroAttackTarget(IHasHp target)

    {

        if (target == null || target is ZombieHealth)

            return false;



        return target is AgentGoToHouseDiscrete || target is LilyScript;

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


