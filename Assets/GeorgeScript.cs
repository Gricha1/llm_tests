using UnityEngine;

/// <summary>
/// George — вода, еда, огонь (без дерева и зомби).
/// Логика стикеров в AgentGoToHouseDiscrete.EnsureGeorgeOptionVisuals —
/// работает и когда на объекте только базовый скрипт (GeorgeHero в сцене).
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public sealed class GeorgeScript : AgentGoToHouseDiscrete
{
    protected override string OptionIconObjectName => "GeorgeOptionIcon";
}
