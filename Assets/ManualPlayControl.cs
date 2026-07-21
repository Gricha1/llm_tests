/// <summary>
/// Ручной Play: P — кто слушает WASD на стриме (Env 0); в средах 1–4 / 9–11 выбор по меню K/#env_N.
/// Действие Jack/George — ЛКМ.
/// </summary>
public static class ManualPlayControl
{
    public static bool GeorgeManualActive { get; private set; }

    public static void ToggleGeorgeManual() =>
        GeorgeManualActive = !GeorgeManualActive;
}
