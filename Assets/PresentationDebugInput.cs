using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Горячие клавиши для Play-теста presentation-сцены и Twitch-команд.
/// Цифры 1–0 — те же команды, что в чате (#add_tree, #zombie, #reset…).
/// P — показать/скрыть рендер параллельных Env. Z — быстро #zombie=1.
/// </summary>
public sealed class PresentationDebugInput : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var existing = Object.FindObjectOfType<PresentationDebugInput>();
        if (existing != null)
            return;

        var go = new GameObject(nameof(PresentationDebugInput));
        go.AddComponent<PresentationDebugInput>();
    }

    void Update()
    {
        if (WasKeyPressed(KeyCode.Alpha1) || WasKeyPressed(KeyCode.Keypad1))
            TwitchChatGameBridge.SimulateCommand("add_tree", 3);
        if (WasKeyPressed(KeyCode.Alpha2) || WasKeyPressed(KeyCode.Keypad2))
            TwitchChatGameBridge.SimulateCommand("add_sheep", 3);
        if (WasKeyPressed(KeyCode.Alpha3) || WasKeyPressed(KeyCode.Keypad3))
            TwitchChatGameBridge.SimulateCommand("up", 2);
        if (WasKeyPressed(KeyCode.Alpha4) || WasKeyPressed(KeyCode.Keypad4))
            TwitchChatGameBridge.SimulateCommand("forward", 3);
        if (WasKeyPressed(KeyCode.Alpha5) || WasKeyPressed(KeyCode.Keypad5))
            TwitchChatGameBridge.SimulateCommand("zombie", 2);
        if (WasKeyPressed(KeyCode.Alpha6) || WasKeyPressed(KeyCode.Keypad6))
            TwitchChatGameBridge.SimulateCommand("clone_jack", 0);
        if (WasKeyPressed(KeyCode.Alpha7) || WasKeyPressed(KeyCode.Keypad7))
            TwitchChatGameBridge.SimulateCommand("size", 2);
        if (WasKeyPressed(KeyCode.Alpha8) || WasKeyPressed(KeyCode.Keypad8))
            TwitchChatGameBridge.SimulateCommand("speed_up", 2);
        if (WasKeyPressed(KeyCode.Alpha9) || WasKeyPressed(KeyCode.Keypad9))
            TwitchChatGameBridge.SimulateCommand("show_metrics", 0);
        if (WasKeyPressed(KeyCode.Alpha0) || WasKeyPressed(KeyCode.Keypad0))
            TwitchChatGameBridge.SimulateCommand("reset", 0);

        if (WasKeyPressed(KeyCode.Z))
            TwitchChatGameBridge.SimulateCommand("zombie", 1);

        if (WasKeyPressed(KeyCode.P))
        {
            TrainingEnvSpace.ToggleParallelEnvsVisible();
            Debug.Log(TrainingEnvSpace.ParallelEnvsVisible
                ? "[Debug] P: параллельные Env видны (рендер включён)."
                : "[Debug] P: параллельные Env скрыты.");
        }
    }

    static bool WasKeyPressed(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            var control = KeyControlFromCode(key);
            if (control != null && control.wasPressedThisFrame)
                return true;
        }
#endif
        return Input.GetKeyDown(key);
    }

#if ENABLE_INPUT_SYSTEM
    static UnityEngine.InputSystem.Controls.KeyControl KeyControlFromCode(KeyCode key)
    {
        if (Keyboard.current == null)
            return null;

        return key switch
        {
            KeyCode.Alpha0 or KeyCode.Keypad0 => Keyboard.current.digit0Key,
            KeyCode.Alpha1 or KeyCode.Keypad1 => Keyboard.current.digit1Key,
            KeyCode.Alpha2 or KeyCode.Keypad2 => Keyboard.current.digit2Key,
            KeyCode.Alpha3 or KeyCode.Keypad3 => Keyboard.current.digit3Key,
            KeyCode.Alpha4 or KeyCode.Keypad4 => Keyboard.current.digit4Key,
            KeyCode.Alpha5 or KeyCode.Keypad5 => Keyboard.current.digit5Key,
            KeyCode.Alpha6 or KeyCode.Keypad6 => Keyboard.current.digit6Key,
            KeyCode.Alpha7 or KeyCode.Keypad7 => Keyboard.current.digit7Key,
            KeyCode.Alpha8 or KeyCode.Keypad8 => Keyboard.current.digit8Key,
            KeyCode.Alpha9 or KeyCode.Keypad9 => Keyboard.current.digit9Key,
            KeyCode.Z => Keyboard.current.zKey,
            KeyCode.P => Keyboard.current.pKey,
            _ => null
        };
    }
#endif
}
