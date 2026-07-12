using UnityEngine;

/// <summary>
/// Здоровье зомби. После 2 попаданий (2 выстрела) зомби умирает. Реализует IHasHp для полоски HP.
/// </summary>
public class ZombieHealth : MonoBehaviour, IHasHp
{
    [SerializeField] private int maxHp = 2;
    private int hp;

    public int Hp => hp;
    public int MaxHp => maxHp;

    public void ConfigureMaxHp(int newMaxHp)
    {
        maxHp = Mathf.Max(1, newMaxHp);
        hp = maxHp;
    }

    [Header("Dismemberment (optional)")]
    [Tooltip("Отвалится при первом попадании (когда HP станет maxHp-1).")]
    [SerializeField] private Transform armToDetach;
    [Tooltip("Отвалится при смертельном ударе (когда HP <= 0).")]
    [SerializeField] private Transform headToDetach;
    [SerializeField] private float detachImpulse = 2.5f;
    [SerializeField] private float detachDestroyAfterSeconds = 8f;

    private bool armDetached;
    private bool headDetached;

    [Header("Hit Reaction")]
    [Tooltip("Триггер в Animator Zombie.controller для реакции на урон (например: \"Hit\").")]
    [SerializeField] private string hitAnimTrigger = "Hit";
    [Tooltip("Минимум секунд между срабатываниями hit-анимации.")]
    [SerializeField] private float hitAnimCooldownSeconds = 0.15f;
    private float lastHitAnimTime = -999f;
    private Animator animator;

    [Header("Knockback")]
    [Tooltip("Небольшой отлёт при попадании. Для CharacterController применяется через Move, для Rigidbody — через AddForce.")]
    [SerializeField] private bool enableKnockback = true;
    [SerializeField] private float knockbackDistance = 0.35f;
    [SerializeField] private float knockbackUp = 0.05f;
    [SerializeField] private float knockbackImpulse = 1.25f;
    private CharacterController controller;
    private Rigidbody rb;

    private void OnEnable()
    {
        hp = maxHp;
        armDetached = false;
        headDetached = false;
    }

    private void Start()
    {
        animator = GetComponentInChildren<Animator>() ?? GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
        rb = GetComponent<Rigidbody>();
    }

    /// <summary>Наносит урон. При HP &lt;= 0 зомби уничтожается.</summary>
    public void TakeDamage(int amount)
    {
        TakeDamage(amount, transform.position);
    }

    /// <summary>Наносит урон с учётом источника (для отлёта от попадания).</summary>
    public void TakeDamage(int amount, Vector3 damageSourcePosition)
    {
        if (amount <= 0) return;

        int prevHp = hp;
        hp -= amount;

        TryPlayHitAnim();
        TryKnockback(damageSourcePosition);

        // 1) Первый удар: отваливается рука
        if (!armDetached && prevHp == maxHp && hp <= maxHp - 1)
        {
            armDetached = true;
            DetachPart(armToDetach);
        }

        // 2) Смертельный удар: отваливается голова, затем зомби уничтожается
        if (!headDetached && hp <= 0)
        {
            headDetached = true;
            DetachPart(headToDetach);
            Destroy(gameObject);
        }
    }

    private void TryPlayHitAnim()
    {
        if (animator == null) return;
        if (string.IsNullOrEmpty(hitAnimTrigger)) return;
        if (Time.time - lastHitAnimTime < hitAnimCooldownSeconds) return;
        if (!HasAnimatorTrigger(animator, hitAnimTrigger)) return;

        animator.SetTrigger(hitAnimTrigger);
        lastHitAnimTime = Time.time;
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

    private void TryKnockback(Vector3 damageSourcePosition)
    {
        if (!enableKnockback) return;
        if (knockbackDistance <= 0f && knockbackImpulse <= 0f) return;

        Vector3 from = damageSourcePosition;
        Vector3 dir = transform.position - from;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f)
            dir = -transform.forward;
        dir = dir.normalized;

        Vector3 delta = dir * Mathf.Max(0f, knockbackDistance);
        delta.y = knockbackUp;

        if (controller != null && controller.enabled)
        {
            controller.Move(delta);
            return;
        }

        if (rb != null)
        {
            rb.AddForce((dir + Vector3.up * 0.15f).normalized * Mathf.Max(0f, knockbackImpulse), ForceMode.Impulse);
        }
    }

    private void DetachPart(Transform part)
    {
        if (part == null) return;

        part.SetParent(null, true);

        var rb = part.GetComponent<Rigidbody>();
        if (rb == null) rb = part.gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = true;
        rb.mass = 0.25f;

        if (part.GetComponent<Collider>() == null)
        {
            // Простой коллайдер по bounds рендера (если есть), иначе default.
            var r = part.GetComponentInChildren<Renderer>();
            var box = part.gameObject.AddComponent<BoxCollider>();
            if (r != null)
            {
                box.center = part.InverseTransformPoint(r.bounds.center);
                box.size = r.bounds.size;
            }
        }

        rb.AddForce((transform.forward + Vector3.up * 0.25f).normalized * detachImpulse, ForceMode.Impulse);

        if (detachDestroyAfterSeconds > 0f)
            Destroy(part.gameObject, detachDestroyAfterSeconds);
    }
}
