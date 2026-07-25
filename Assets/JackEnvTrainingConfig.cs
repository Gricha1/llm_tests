using UnityEngine;

/// <summary>
/// Профиль задачи на корне Env. Jack читает его через TrainingEnvSpace.FindRoot.
/// </summary>
public sealed class JackEnvTrainingConfig : MonoBehaviour
{
  [SerializeField] private JackTrainingMode mode = JackTrainingMode.Auto;

  [Header("Simple modes (WoodOnly / FoodOnly)")]
  [SerializeField] private int simpleMaxSteps = 400;
  [SerializeField] private float simpleEpisodeTimeoutSeconds = 45f;
  [SerializeField] private bool endOnSuccess = true;
  [SerializeField] private bool freezeNeeds = true;
  [SerializeField] private float successReward = 5f;
  [SerializeField] private float stepPenalty = -0.001f;

  public JackTrainingMode Mode => mode;
  public int SimpleMaxSteps => simpleMaxSteps;
  public float SimpleEpisodeTimeoutSeconds => simpleEpisodeTimeoutSeconds;
  public bool EndOnSuccess => endOnSuccess;
  public bool FreezeNeeds => freezeNeeds;
  public float SuccessReward => successReward;
  public float StepPenalty => stepPenalty;

  public JackTrainingMode ResolveMode()
  {
    if (mode != JackTrainingMode.Auto)
      return mode;

    if (TrainingEnvSpace.IsPresentationEnv(transform))
      return JackTrainingMode.Full;

    int copyIndex = TrainingEnvSpace.GetEnvCopyIndex(transform);
    if (copyIndex <= 0)
      return JackTrainingMode.Full;

    // Env (1) wood, Env (2) food, Env (3) wood, …
    return copyIndex % 2 == 1 ? JackTrainingMode.WoodOnly : JackTrainingMode.FoodOnly;
  }

  public bool IsSimpleMode(JackTrainingMode resolved) =>
    resolved == JackTrainingMode.WoodOnly || resolved == JackTrainingMode.FoodOnly;
}
