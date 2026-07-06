using UnityEngine;

public class CarForwardMover : MonoBehaviour
{
    [SerializeField] private float speed = 2.0f;
    [Tooltip("Если у объекта есть Rigidbody, двигать через MovePosition в FixedUpdate.")]
    [SerializeField] private bool useRigidbodyIfPresent = true;

    private Rigidbody _rb;

    private void Awake()
    {
        if (useRigidbodyIfPresent)
            _rb = GetComponent<Rigidbody>();
    }

    private void Update()
    {
        if (_rb != null) return;
        transform.position += transform.forward * (speed * Time.deltaTime);
    }

    private void FixedUpdate()
    {
        if (_rb == null) return;
        _rb.MovePosition(_rb.position + transform.forward * (speed * Time.fixedDeltaTime));
    }
}

