/// <summary>
/// Ручной Play: P — кто слушает WASD на стриме (Env 0); в средах 2–5 / 10–12 выбор по меню K/#env_N.
/// Действие Jack/George — ЛКМ.
/// </summary>
public static class ManualPlayControl
{
    public static bool GeorgeManualActive { get; private set; }

    public static void ToggleGeorgeManual() =>
        GeorgeManualActive = !GeorgeManualActive;
}
