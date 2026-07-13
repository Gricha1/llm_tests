using UnityEngine;

/// <summary>
/// George — вода, еда, огонь (без дерева и зомби).
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(Animator))]
public sealed class GeorgeScript : AgentGoToHouseDiscrete
{
    protected override string OptionIconObjectName => "GeorgeOptionIcon";

    void Start()
    {
        NormalizeFoodHeatDecayIntervals();
        TrainingEnvSpace.CapturePresentationSpawn(transform);
        EnsureGeorgeOptionSprites();
    }

    public override void OnEpisodeBegin()
    {
        base.OnEpisodeBegin();
        EnsureGeorgeOptionSprites();
        UpdateOptionIconVisual();
    }

    void EnsureGeorgeOptionSprites()
    {
        var jack = TrainingEnvSpace.FindPresentationPrimaryJack();
        if (jack != null)
            jack.ShareOptionSpritesWith(this);

        // У Jack часто нет water/heat в инспекторе — добираем с Lily после share.
        ResolveMissingOptionSprites();
        EnsureOptionIconRenderer();
        UpdateOptionIconVisual();
    }
}
