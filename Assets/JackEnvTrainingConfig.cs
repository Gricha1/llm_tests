using UnityEngine;

/// <summary>
/// Профиль задачи на корне Env. Jack читает его через TrainingEnvSpace.FindRoot.
/// Mode = Auto: только переименуй копии — Env, Env (1)… Env (4).
///   0,2 → wood | 1 → food | 3 → wood+food switch | 4+ → zombie.
/// </summary>
public sealed class JackEnvTrainingConfig : MonoBehaviour
{
  [SerializeField] private JackTrainingMode mode = JackTrainingMode.Auto;

  [Header("Simple modes (WoodOnly / FoodOnly / ZombieOnly)")]
  [SerializeField] private int simpleMaxSteps = 400;
  [SerializeField] private float simpleEpisodeTimeoutSeconds = 45f;
  [SerializeField] private bool endOnSuccess = true;
  [SerializeField] private bool freezeNeeds = true;
  [SerializeField] private float successReward = 5f;
  [SerializeField] private float stepPenalty = -0.001f;

  [Header("ZombieOnly (Env 4)")]
  [SerializeField] private int zombieMaxSteps = 3000;
  [SerializeField] private float zombieEpisodeTimeoutSeconds = 180f;
  [SerializeField] private bool zombieEndOnKill = false;
  [SerializeField] private int zombieImmediateSpawnCount = 2;

  public JackTrainingMode Mode => mode;
  public int SimpleMaxSteps => simpleMaxSteps;
  public float SimpleEpisodeTimeoutSeconds => simpleEpisodeTimeoutSeconds;
  public int ZombieMaxSteps => zombieMaxSteps;
  public float ZombieEpisodeTimeoutSeconds => zombieEpisodeTimeoutSeconds;
  public bool ZombieEndOnKill => zombieEndOnKill;
  public int ZombieImmediateSpawnCount => zombieImmediateSpawnCount;
  public bool EndOnSuccess => endOnSuccess;
  public bool FreezeNeeds => freezeNeeds;
  public float SuccessReward => successReward;
  public float StepPenalty => stepPenalty;

  public JackTrainingMode ResolveMode()
  {
    if (mode != JackTrainingMode.Auto)
      return mode;

    if (TrainingEnvSpace.HasMultipleTrainingEnvs()
        && TrainingEnvSpace.IsPresentationEnv(transform))
      return JackTrainingMode.Full;

    int copyIndex = TrainingEnvSpace.GetEnvCopyIndex(transform);

    // Play в Editor без mlagents-learn — первая Env остаётся полной игрой.
    if (!TrainingEnvSpace.HasMultipleTrainingEnvs()
        && copyIndex == 0
        && TrainingEnvSpace.IsPresentationEnv(transform)
        && !TrainingEnvSpace.IsMlAgentsTrainingActive())
      return JackTrainingMode.Full;

    return ResolveAutoModeForCopyIndex(copyIndex);
  }

  public static JackTrainingMode ResolveAutoModeForCopyIndex(int copyIndex)
  {
    switch (copyIndex)
    {
      case 0:
      case 2:
        return JackTrainingMode.WoodOnly;
      case 1:
        return JackTrainingMode.FoodOnly;
      case 3:
        return JackTrainingMode.WoodFoodSwitch;
      case 4:
      case 5:
        return JackTrainingMode.ZombieOnly;
      default:
        return copyIndex % 2 == 1 ? JackTrainingMode.FoodOnly : JackTrainingMode.WoodOnly;
    }
  }

  public bool IsSimpleMode(JackTrainingMode resolved) =>
    resolved == JackTrainingMode.WoodOnly
    || resolved == JackTrainingMode.FoodOnly
    || resolved == JackTrainingMode.ZombieOnly;
}
