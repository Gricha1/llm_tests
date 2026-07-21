using UnityEngine;

/// <summary>#add fire — вкл/выкл вечный костёр у дома (повтор команды или клавиша 6).</summary>
public static class TwitchPermanentFire
{
    public const float BurnSeconds = 99999f;

    static bool _enabled;

    public static bool IsEnabled => _enabled;

    public static bool Toggle()
    {
        if (_enabled)
            Disable();
        else
            Enable();
        return _enabled;
    }

    public static void Enable()
    {
        _enabled = true;
        ApplyTo(TrainingEnvSpace.FindPresentationPrimaryJack());
    }

    public static void Disable()
    {
        _enabled = false;
        var jack = TrainingEnvSpace.FindPresentationPrimaryJack();
        if (jack == null)
            jack = TrainingEnvSpace.FindPresentationJack();
        if (jack != null)
            jack.ExtinguishCampfire();
    }

    public static bool ShouldKeepLit(AgentGoToHouseDiscrete jack)
    {
        return _enabled
            && jack != null
            && TrainingEnvSpace.IsPresentationTransform(jack.transform)
            && !TwitchEphemeralEffects.IsTwitchClone(jack);
    }

    public static void ApplyTo(AgentGoToHouseDiscrete jack)
    {
        if (!ShouldKeepLit(jack))
            return;

        jack.EnsureTrainingCampfireLit(BurnSeconds);
    }
}
