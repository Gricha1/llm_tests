using UnityEngine;

/// <summary>
/// При контакте зомби с персонажем (Jack/Lily) снимает 1/5 его макс. HP. Кулдаун между ударами.
/// Вешать на того же зомби, что и ZombieChase. Нужен CharacterController или коллайдер для контакта.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ZombieAttack : MonoBehaviour
{
    [SerializeField] private float hitCooldown = 1f;
    [SerializeField] private float damageDelaySeconds = 0.5f;
    [SerializeField] private float damageRadius = 1.2f;
    private float lastHitTime = -999f;
    [SerializeField] private string attackLeftTrigger = "AttackL";
    [SerializeField] private string attackRightTrigger = "AttackR";
    private Animator animator;
    private Coroutine pendingHitRoutine;

    private void Start()
    {
        animator = GetComponentInChildren<Animator>() ?? GetComponent<Animator>();
    }

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        TryDamage(hit.gameObject);
    }

    private void OnCollisionEnter(Collision collision)
    {
        TryDamage(collision.gameObject);
    }

    private void TryDamage(GameObject other)
    {
        var chase = GetComponentInParent<ZombieChase>() ?? GetComponentInChildren<ZombieChase>();
        if (chase != null && chase.IsStunned)
            return;

        var target = other.GetComponentInParent<IHasHp>();
        if (target == null) return;
        if (Time.time - lastHitTime < hitCooldown) return;

        lastHitTime = Time.time;
        TryPlayAttackAnim();

        // Откладываем нанесение урона: если цель всё ещё рядом спустя delay — снимаем HP.
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
        if (chase != null && chase.IsStunned) yield break;

        // Проверяем, что цель всё ещё рядом.
        var targetMb = target as MonoBehaviour;
        if (targetMb == null) yield break;
        Transform targetTr = targetMb.transform;
        if (targetTr == null) yield break;

        Vector3 a = transform.position;
        Vector3 b = targetTr.position;
        a.y = 0f;
        b.y = 0f;
        float r = Mathf.Max(0.01f, damageRadius);
        if ((a - b).sqrMagnitude > r * r) yield break;

        int damage = Mathf.Max(1, target.MaxHp / 5);
        target.TakeDamage(damage);
        GameSfx.PlayZombieHitAgent(source: targetTr);
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
