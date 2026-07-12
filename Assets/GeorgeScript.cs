using UnityEngine;

/// <summary>
/// George — вода, еда, огонь (без дерева и зомби).
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public sealed class GeorgeScript : AgentGoToHouseDiscrete
{
    protected override string OptionIconObjectName => "GeorgeOptionIcon";
}
