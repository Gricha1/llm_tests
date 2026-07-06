using UnityEngine;

/// <summary>
/// Звуки шагов (step_grass). Один шаг на вызов NotifyMovement с кулдауном.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public sealed class AgentFootsteps : MonoBehaviour
{
    [SerializeField] [Range(0f, 1f)] private float volume = 0.4f;
    [SerializeField] private float minStepInterval = 0.32f;
    [SerializeField] private float minPlanarSpeed = 0.2f;
    [SerializeField] [Range(0.8f, 1.2f)] private float pitchMin = 0.95f;
    [SerializeField] [Range(0.8f, 1.2f)] private float pitchMax = 1.05f;

    CharacterController _controller;
    float _lastStepTime = -999f;

    public static void EnsureOn(GameObject agentRoot)
    {
        if (agentRoot == null)
            return;
        if (agentRoot.GetComponent<AgentFootsteps>() == null)
            agentRoot.AddComponent<AgentFootsteps>();
    }

    public static void NotifyMovement(GameObject agentRoot, float planarSpeed)
    {
        if (agentRoot == null)
            return;
        if (!agentRoot.TryGetComponent<AgentFootsteps>(out var footsteps))
            return;

        footsteps.TryPlayStep(planarSpeed);
    }

    void Awake()
    {
        _controller = GetComponent<CharacterController>();
    }

    void TryPlayStep(float planarSpeed)
    {
        if (planarSpeed < minPlanarSpeed)
            return;
        if (_controller == null || !_controller.isGrounded)
            return;
        if (Time.time - _lastStepTime < minStepInterval)
            return;

        GameSfx.PlayStepGrass(volume, pitchMin, pitchMax, transform);
        _lastStepTime = Time.time;
    }
}
