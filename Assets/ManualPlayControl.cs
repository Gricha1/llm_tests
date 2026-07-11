/// <summary>
/// Глобальные переключатели ручного Play: P — управление Герой (WASD+ПКМ), L — блок авто-смены опций.
/// </summary>
public static class ManualPlayControl
{
    public static bool GeorgeManualActive { get; private set; }

    public static void ToggleGeorgeManual() =>
        GeorgeManualActive = !GeorgeManualActive;
}
