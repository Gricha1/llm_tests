using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// K / #menu — меню переключения между средами Env (0 = стрим, 1 = Jack дрова, …).
/// #env_N — сразу выбрать среду N.
/// </summary>
public sealed class PresentationEnvSwitcher : MonoBehaviour
{
    const int MaxSelectableCopyIndex = 11;

    static readonly string[] TaskLabels =
    {
        "0 — Стрим (все герои)  #env_0",
        "1 — Jack: дрова  #env_1",
        "2 — Jack: еда  #env_2",
        "3 — Jack: вода  #env_3",
        "4 — Jack: зомби  #env_4",
        "5 — Lily: еда  #env_5",
        "6 — Lily: вода  #env_6",
        "7 — Lily: костёр  #env_7",
        "8 — Lily: цветы  #env_8",
        "9 — George: еда  #env_9",
        "10 — George: вода  #env_10",
        "11 — George: костёр  #env_11",
    };

    static PresentationEnvSwitcher _instance;
    static GUIStyle _boxStyle;
    static GUIStyle _buttonStyle;
    static GUIStyle _activeButtonStyle;

    bool _menuOpen;
    Rect _windowRect = new Rect(16f, 80f, 340f, 400f);
    Coroutine _delayedZombieStart;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.IsEnvViewSwitcherAllowed())
            return;
        if (TrainingEnvSpace.PresentationRoot == null)
            return;
        if (_instance != null)
            return;

        var go = new GameObject(nameof(PresentationEnvSwitcher));
        _instance = go.AddComponent<PresentationEnvSwitcher>();
    }

    public static void ToggleMenu()
    {
        EnsureInstance();
        if (_instance == null)
            return;
        _instance._menuOpen = !_instance._menuOpen;
    }

    public static void SetMenuOpen(bool open)
    {
        EnsureInstance();
        if (_instance == null)
            return;
        _instance._menuOpen = open;
    }

    public static void SelectEnvFromCommand(int copyIndex)
    {
        EnsureInstance();
        if (_instance == null)
            return;
        _instance.SelectEnv(copyIndex);
    }

    /// <summary>Повторный старт зомби после отложенного OnEpisodeBegin.</summary>
    public void RequestDelayedZombieSpawnerStart(Transform env)
    {
        if (_delayedZombieStart != null)
            StopCoroutine(_delayedZombieStart);
        _delayedZombieStart = StartCoroutine(DelayedZombieSpawnerStart(env));
    }

    public static void CancelDelayedZombieSpawnerStart()
    {
        if (_instance == null || _instance._delayedZombieStart == null)
            return;
        _instance.StopCoroutine(_instance._delayedZombieStart);
        _instance._delayedZombieStart = null;
    }

    System.Collections.IEnumerator DelayedZombieSpawnerStart(Transform env)
    {
        yield return null;
        yield return null;
        TrainingEnvSpace.ForceStartJackZombieSpawners(env);
        _delayedZombieStart = null;
    }

    static void EnsureInstance()
    {
        if (_instance != null)
            return;
        if (!TrainingEnvSpace.IsEnvViewSwitcherAllowed())
            return;
        Bootstrap();
    }

    void Update()
    {
        if (!TrainingEnvSpace.IsEnvViewSwitcherAllowed())
            return;

        if (WasKeyPressed(KeyCode.K))
            _menuOpen = !_menuOpen;

        if (_menuOpen && WasKeyPressed(KeyCode.Escape))
            _menuOpen = false;
    }

    void OnGUI()
    {
        if (!_menuOpen || !TrainingEnvSpace.IsEnvViewSwitcherAllowed())
            return;

        EnsureStyles();
        _windowRect = GUILayout.Window(
            GetInstanceID(),
            _windowRect,
            DrawWindow,
            "Среды (K / #menu — закрыть)");
    }

    void DrawWindow(int id)
    {
        GUILayout.Label("Выбери среду для просмотра:");
        GUILayout.Space(4f);

        for (int i = 0; i <= MaxSelectableCopyIndex; i++)
        {
            bool active = TrainingEnvSpace.IsDebugEnvFocusActive
                && TrainingEnvSpace.DebugFocusedCopyIndex == i;
            DrawEnvButton(TaskLabels[i], i, active);
        }

        GUI.DragWindow();
    }

    void DrawEnvButton(string label, int copyIndex, bool active)
    {
        var style = active ? _activeButtonStyle : _buttonStyle;
        if (GUILayout.Button(label, style, GUILayout.Height(28f)))
            SelectEnv(copyIndex);
    }

    void SelectEnv(int copyIndex)
    {
        if (copyIndex < 0 || copyIndex > MaxSelectableCopyIndex)
        {
            Debug.LogWarning($"[EnvSwitcher] среда {copyIndex} вне диапазона 0–{MaxSelectableCopyIndex}");
            return;
        }

        TrainingEnvSpace.SetDebugFocusedEnv(copyIndex);
        var task = EnvTrainingConfig.ResolveAutoTaskForCopyIndex(copyIndex);
        var mode = task switch
        {
            EnvTrainingTask.JackWood => "режим=WoodOnly: 10 дров → дом → греться (heat<20) → снова рубить (одна опция дерево)",
            EnvTrainingTask.JackFood => "режим=FoodOnly (фикс. шаги, не конец на 1 овце)",
            EnvTrainingTask.JackWater => "режим=WaterOnly (фикс. шаги, награда за воду, не конец на 1й добыче)",
            EnvTrainingTask.JackZombie => "режим=ZombieOnly",
            _ => $"task={task}"
        };
        Debug.Log($"[EnvSwitcher] Среда {copyIndex}: {task}. {mode}");
    }

    static void EnsureStyles()
    {
        if (_boxStyle != null)
            return;

        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = 13,
            alignment = TextAnchor.UpperLeft,
        };

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleLeft,
        };

        _activeButtonStyle = new GUIStyle(_buttonStyle)
        {
            fontStyle = FontStyle.Bold,
        };
    }

    static bool WasKeyPressed(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            var control = key switch
            {
                KeyCode.K => Keyboard.current.kKey,
                KeyCode.Escape => Keyboard.current.escapeKey,
                _ => null
            };
            if (control != null && control.wasPressedThisFrame)
                return true;
        }
#endif
        return Input.GetKeyDown(key);
    }
}
