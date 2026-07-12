using UnityEngine;

/// <summary>
/// Звук и красная вспышка при уроне Jack / Lily / George.
/// </summary>
public static class HeroDamageFeedback
{
    public static void Play(Transform hero)
    {
        if (hero == null)
            return;

        AgentHitFlash.GetOrCreate(hero.gameObject).Flash();
        GameSfx.PlayZombieHitAgent(source: hero);
    }
}
