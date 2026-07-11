using UnityEngine;

/// <summary>
/// Idle / walk для Polytope Hero: двигает параметр Speed в Animator (как Jack/Lily).
/// CharacterController сам по себе анимации не включает.
/// </summary>
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(CharacterController))]
public class PolytopeHeroLocomotion : MonoBehaviour
{
    [SerializeField] float walkSpeedThreshold = 0.05f;
    [SerializeField] float speedForFullWalk = 2f;
    [SerializeField] float speedDamp = 0.12f;
    [SerializeField] bool allowWasdTest = true;
    [SerializeField] float testMoveSpeed = 3f;

    Animator animator;
    CharacterController controller;

    void Awake()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<CharacterController>();
    }

    void Start()
    {
        if (animator != null)
            animator.SetFloat("Speed", 0f);
    }

    void Update()
    {
        if (animator == null)
            return;

        if (allowWasdTest && Application.isPlaying)
            ApplyTestInput();

        var vel = controller != null ? controller.velocity : Vector3.zero;
        vel.y = 0f;
        float target = vel.magnitude <= walkSpeedThreshold
            ? 0f
            : Mathf.Clamp01(vel.magnitude / Mathf.Max(0.01f, speedForFullWalk));

        if (speedDamp > 0f)
            animator.SetFloat("Speed", target, speedDamp, Time.deltaTime);
        else
            animator.SetFloat("Speed", target);
    }

    void ApplyTestInput()
    {
        if (GetComponent<AgentGoToHouseDiscrete>() != null || GetComponent<LilyScript>() != null)
            return;

        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        if (Mathf.Abs(h) < 0.01f && Mathf.Abs(v) < 0.01f)
            return;

        var move = new Vector3(h, 0f, v);
        if (move.sqrMagnitude > 1f)
            move.Normalize();

        if (move.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(move), 10f * Time.deltaTime);

        controller.Move(move * (testMoveSpeed * Time.deltaTime));
    }
}
