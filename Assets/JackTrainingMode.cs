/// <summary>
/// Режим обучения Jack внутри одной среды Env.
/// </summary>
public enum JackTrainingMode
{
  /// <summary>Presentation Env — стрим. Копии: Env→wood, Env (1)→food, Env (2)→wood, Env (3)→wood+food, Env (4)→zombie.</summary>
  Auto,
  /// <summary>Survival, utility, зомби — для стрима.</summary>
  Full,
  /// <summary>Только рубка дерева, короткий эпизод.</summary>
  WoodOnly,
  /// <summary>Только еда (овцы), короткий эпизод.</summary>
  FoodOnly,
  /// <summary>Дерево + еда с переключением utility (без зомби). Env (3).</summary>
  WoodFoodSwitch,
  /// <summary>Только драка с зомби, спавнер зомби всегда активен.</summary>
  ZombieOnly
}
