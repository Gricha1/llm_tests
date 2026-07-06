/// <summary>
/// Режим обучения Jack внутри одной среды Env.
/// </summary>
public enum JackTrainingMode
{
  /// <summary>Presentation Env — полная игра. Копии Env (N) — Wood/Food по индексу.</summary>
  Auto,
  /// <summary>Survival, utility, зомби — для стрима.</summary>
  Full,
  /// <summary>Только рубка дерева, короткий эпизод.</summary>
  WoodOnly,
  /// <summary>Только еда (овцы), короткий эпизод.</summary>
  FoodOnly
}
