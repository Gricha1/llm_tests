using UnityEngine;

/// <summary>
/// Legacy component on LilyRewardText — отключён, если на том же объекте есть RewardDisplay.
/// </summary>
public class LilyRewardDisplay : MonoBehaviour
{
    void Awake()
    {
        if (GetComponent<RewardDisplay>() != null)
            enabled = false;
    }
}
