/// <summary>
/// Режим обучения Jack внутри одной среды Env.
/// </summary>
public enum JackTrainingMode
{
  /// <summary>Presentation Env — стрим. Auto: Env (1) wood … Env (11) George heat.</summary>
  Auto,
  /// <summary>Survival, utility, зомби — для стрима.</summary>
  Full,
  /// <summary>Только рубка дерева, короткий эпизод.</summary>
  WoodOnly,
  /// <summary>Только еда (овцы), короткий эпизод.</summary>
  FoodOnly,
  /// <summary>Только вода (GoalWater + источник), короткий эпизод.</summary>
  WaterOnly,
  /// <summary>Дерево + еда с переключением utility (без зомби).</summary>
  WoodFoodSwitch,
  /// <summary>Только драка с зомби, спавнер зомби всегда активен.</summary>
  ZombieOnly
}
