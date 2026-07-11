using UnityEngine;

/// <summary>
/// George — те же опции и логика, что у Jack (дерево/еда/зомби/вода), отдельный агент для обучения.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public sealed class GeorgeScript : AgentGoToHouseDiscrete
{
    protected override string OptionIconObjectName => "GeorgeOptionIcon";
}
