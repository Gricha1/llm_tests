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
    const int MaxSelectableCopyIndex = 12;

    static readonly string[] TaskLabels =
    {
        "0 — Стрим (все герои)  #env_0",
        "1 — Train: все герои  #env_1",
        "2 — Jack: дрова  #env_2",
        "3 — Jack: еда  #env_3",
        "4 — Jack: вода  #env_4",
        "5 — Jack: зомби  #env_5",
        "6 — Lily: еда  #env_6",
        "7 — Lily: вода  #env_7",
        "8 — Lily: костёр  #env_8",
        "9 — Lily: цветы  #env_9",
        "10 — George: еда  #env_10",
        "11 — George: вода  #env_11",
        "12 — George: костёр  #env_12",
    };

    static PresentationEnvSwitcher _instance;
    static GUIStyle _boxStyle;
    static GUIStyle _labelStyle;
    static GUIStyle _buttonStyle;
    static GUIStyle _activeButtonStyle;

    bool _menuOpen;
    // Ниже всех 3 HP-баров слева (Джек/Лили/Гера); CanvasScaler делает полоски выше IMGUI-пикселей.
    Rect _windowRect = new Rect(8f, 190f, 220f, 320f);
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
        // Высота по видимым пунктам (Jack-only — без Lily/George).
        const float HpBarsClearance = 190f;
        const float RowH = 20f;
        const float TitleH = 24f;
        const float Pad = 10f;
        int visible = 0;
        for (int i = 0; i <= MaxSelectableCopyIndex; i++)
        {
            if (IsMenuEntryAvailable(i))
                visible++;
        }
        float contentH = TitleH + Pad + Mathf.Max(1, visible) * RowH;
        _windowRect.width = 220f;
        _windowRect.height = contentH;
        _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Screen.width - 48f);
        _windowRect.y = Mathf.Clamp(_windowRect.y, HpBarsClearance, Screen.height - 48f);

        var prevWindow = GUI.skin.window.fontSize;
        GUI.skin.window.fontSize = 12;
        _windowRect = GUILayout.Window(
            GetInstanceID(),
            _windowRect,
            DrawWindow,
            "Среды (K)");
        GUI.skin.window.fontSize = prevWindow;
    }

    void DrawWindow(int id)
    {
        for (int i = 0; i <= MaxSelectableCopyIndex; i++)
        {
            if (!IsMenuEntryAvailable(i))
                continue;
            bool active = TrainingEnvSpace.IsDebugEnvFocusActive
                && TrainingEnvSpace.DebugFocusedCopyIndex == i;
            DrawEnvButton(TaskLabels[i], i, active);
        }
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 18f));
    }

    /// <summary>
    /// Jack-only / Lily-only / George-only — в меню только задачи этого героя.
    /// </summary>
    static bool IsMenuEntryAvailable(int copyIndex)
    {
        var task = EnvTrainingConfig.ResolveFixedMenuTaskForCopyIndex(copyIndex);

        if (EnvTrainingConfig.IsJackOnlyTasksMode())
        {
            return task == EnvTrainingTask.JackWood
                || task == EnvTrainingTask.JackFood
                || task == EnvTrainingTask.JackWater
                || task == EnvTrainingTask.JackZombie
                || task == EnvTrainingTask.PresentationFull;
        }

        if (EnvTrainingConfig.IsLilyOnlyTasksMode())
            return IsLilyMenuTask(task);

        if (EnvTrainingConfig.IsGeorgeOnlyTasksMode())
            return IsGeorgeMenuTask(task);

        if (task == EnvTrainingTask.PresentationFull
            || task == EnvTrainingTask.JackWood
            || task == EnvTrainingTask.JackFood
            || task == EnvTrainingTask.JackWater
            || task == EnvTrainingTask.JackZombie)
            return true;

        if (IsLilyMenuTask(task))
            return TrainingEnvSpace.HasLilyHeroConfiguredInScene();
        if (IsGeorgeMenuTask(task))
            return TrainingEnvSpace.HasGeorgeHeroConfiguredInScene();
        return true;
    }

    static bool IsLilyMenuTask(EnvTrainingTask task) =>
        task == EnvTrainingTask.LilyFood
        || task == EnvTrainingTask.LilyWater
        || task == EnvTrainingTask.LilyHeat
        || task == EnvTrainingTask.LilyFlower;

    static bool IsGeorgeMenuTask(EnvTrainingTask task) =>
        task == EnvTrainingTask.GeorgeFood
        || task == EnvTrainingTask.GeorgeWater
        || task == EnvTrainingTask.GeorgeHeat;

    void DrawEnvButton(string label, int copyIndex, bool active)
    {
        var style = active ? _activeButtonStyle : _buttonStyle;
        if (GUILayout.Button(label, style, GUILayout.Height(20f)))
            SelectEnv(copyIndex);
    }

    void SelectEnv(int copyIndex)
    {
        if (copyIndex < 0 || copyIndex > MaxSelectableCopyIndex)
        {
            Debug.LogWarning($"[EnvSwitcher] среда {copyIndex} вне диапазона 0–{MaxSelectableCopyIndex}");
            return;
        }
        if (!IsMenuEntryAvailable(copyIndex))
        {
            Debug.LogWarning($"[EnvSwitcher] среда {copyIndex} недоступна (нет героя / Jack-only)");
            return;
        }

        TrainingEnvSpace.SetDebugFocusedEnv(copyIndex);
        var task = EnvTrainingConfig.ResolveFixedMenuTaskForCopyIndex(copyIndex);
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
        // Всегда обновляем размеры — иначе после правок Play держит старый fontSize=18.
        _boxStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = 12,
            alignment = TextAnchor.UpperLeft,
        };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            wordWrap = true,
        };

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
            richText = true,
            fixedHeight = 22f,
        };

        _activeButtonStyle = new GUIStyle(_buttonStyle)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 12,
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
