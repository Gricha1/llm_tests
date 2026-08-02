using UnityEngine;

/// <summary>#add fire — вкл/выкл вечный костёр у дома (повтор команды или клавиша 6).</summary>
public static class TwitchPermanentFire
{
    public const float BurnSeconds = 99999f;
    /// <summary>Обычный костёр с дров на presentation не дольше этого (сек).</summary>
    public const float MaxPresentationBurnSeconds = 90f;

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
        // Гасим ВСЕХ Jack в presentation — иначе primary≠тот, кто горит, и остаётся «вечный» таймер.
        ExtinguishAllPresentationJacks();
    }

    public static void ExtinguishAllPresentationJacks()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root == null)
        {
            var jack = TrainingEnvSpace.FindPresentationPrimaryJack()
                ?? TrainingEnvSpace.FindPresentationJack();
            jack?.ExtinguishCampfire();
            return;
        }

        var agents = root.GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < agents.Length; i++)
        {
            var jack = agents[i];
            if (jack == null || TrainingEnvSpace.IsGeorgeAgent(jack))
                continue;
            if (TwitchEphemeralEffects.IsTwitchClone(jack))
                continue;
            jack.ExtinguishCampfire();
        }
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

    /// <summary>
    /// Убрать хвост вечного костра (99999) после #add fire off.
    /// </summary>
    public static void SanitizePresentationBurn(AgentGoToHouseDiscrete jack, ref float burnSeconds)
    {
        if (jack == null || !TrainingEnvSpace.IsPresentationTransform(jack.transform))
            return;
        if (ShouldKeepLit(jack))
            return;

        // Хвост после #add fire off: ~99999.
        if (burnSeconds > MaxPresentationBurnSeconds * 2f)
            burnSeconds = 0f;
    }
}
