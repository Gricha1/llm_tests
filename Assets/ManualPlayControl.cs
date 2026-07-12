/// <summary>
/// Глобальные переключатели ручного Play: P — управление Герой (WASD+ЛКМ), L — блок авто-смены опций.
/// </summary>
public static class ManualPlayControl
{
    public static bool GeorgeManualActive { get; private set; }

    public static void ToggleGeorgeManual() =>
        GeorgeManualActive = !GeorgeManualActive;
}
