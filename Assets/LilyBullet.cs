using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class LilyBullet : MonoBehaviour
{
    private const float MaxLifetime = 3f;

    private Rigidbody rb;
    private Transform jackTarget;
    private AgentGoToHouseDiscrete jackAgent;
    private LilyScript lilyAgent;
    private LayerMask zombieLayer;
    private LayerMask jackLayerMask;
    private float spawnTime;
    private int damage;

    /// <summary>Инициализация пули. zombieLayer и jackLayerMask нужны для опции «Зомби» (награда за зомби, штраф за Джека).</summary>
    public void Init(Vector3 direction, Transform jack, AgentGoToHouseDiscrete jackScript, int damageAmount, float speed = 15f,
        LilyScript lily = null, LayerMask zombie = default, LayerMask jackLayer = default)
    {
        jackTarget = jack;
        jackAgent = jackScript;
        lilyAgent = lily;
        zombieLayer = zombie;
        jackLayerMask = jackLayer;
        damage = damageAmount;
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = false;
        rb.linearVelocity = direction.normalized * speed;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        spawnTime = Time.time;
        var col = GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;
    }

    private void Update()
    {
        if (Time.time - spawnTime > MaxLifetime)
            Destroy(gameObject);
    }

    private void OnTriggerEnter(Collider other)
    {
        // Ищем ZombieHealth на самом объекте, на родителях или на детях (префаб зомби может иметь любую иерархию)
        var zombieHealth = other.GetComponent<ZombieHealth>()
            ?? other.GetComponentInParent<ZombieHealth>()
            ?? other.GetComponentInChildren<ZombieHealth>();
        if (zombieHealth != null)
        {
            zombieHealth.TakeDamage(damage, transform.position);
            Destroy(gameObject);
            return;
        }

        // Отладка: раскомментируйте, чтобы видеть, во что попадает пуля (если зомби не умирает)
        // Debug.Log($"Bullet hit: {other.gameObject.name}, layer={LayerMask.LayerToName(other.gameObject.layer)}");

        int layer = other.gameObject.layer;

        // Слой Jack или явный jackTarget — урон Джеку; при опции «Зомби» Lily получает штраф
        bool hitJack = ContainsLayer(jackLayerMask, layer)
            || (jackTarget != null && other.transform == jackTarget)
            || (jackAgent != null && other.GetComponentInParent<AgentGoToHouseDiscrete>() == jackAgent);
        if (hitJack)
        {
            if (jackAgent != null)
            {
                jackAgent.TakeDamage(damage);
                HeroDamageFeedback.Play(jackAgent.transform);
            }
            Destroy(gameObject);
        }
    }

    private static bool ContainsLayer(LayerMask mask, int layer)
    {
        if (mask == default) return false;
        return ((1 << layer) & mask) != 0;
    }
}
