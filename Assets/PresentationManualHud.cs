using UnityEngine;

/// <summary>
/// Экранная кнопка: включить ручное управление (WASD) на presentation.
/// M — то же. P — переключить Jack/George (кто слушает WASD).
/// </summary>
public sealed class PresentationManualHud : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (TrainingEnvSpace.IsStreamingSurvivalMode)
            return;
        if (Object.FindFirstObjectByType<PresentationManualHud>() != null)
            return;
        var go = new GameObject(nameof(PresentationManualHud));
        Object.DontDestroyOnLoad(go);
        go.AddComponent<PresentationManualHud>();
    }

    void Update()
    {
        if (TrainingEnvSpace.IsStreamingSurvivalMode)
            return;
        if (Input.GetKeyDown(KeyCode.M))
            ManualPlayControl.TogglePresentationManual();
    }

    void OnGUI()
    {
        if (TrainingEnvSpace.IsStreamingSurvivalMode)
            return;
        const float w = 220f;
        const float h = 36f;
        float x = Screen.width - w - 16f;
        float y = 16f;

        bool on = ManualPlayControl.IsPresentationManualPlayActive();
        string label = on
            ? (ManualPlayControl.GeorgeManualActive ? "Ручное: ГЕРА (M)" : "Ручное: JACK (M)")
            : "Вкл. ручное упр. (M)";

        var prev = GUI.backgroundColor;
        GUI.backgroundColor = on ? new Color(0.35f, 0.75f, 0.4f) : new Color(0.25f, 0.35f, 0.55f);
        if (GUI.Button(new Rect(x, y, w, h), label))
            ManualPlayControl.TogglePresentationManual();
        GUI.backgroundColor = prev;

        if (!on)
            return;

        if (GUI.Button(new Rect(x, y + h + 8f, w, h), ManualPlayControl.GeorgeManualActive ? "Передать Jack (P)" : "Передать Геру (P)"))
            ManualPlayControl.ToggleGeorgeManual();
    }
}
