using UnityEngine;

/// <summary>
/// Выполняет команды Twitch в presentation Env.
/// </summary>
public sealed class TwitchChatGameBridge : MonoBehaviour
{
    const int MaxTrees = 10;
    const int MaxSheep = 20;
    const int MaxUpHeight = 5;
    const int MaxForwardStrength = 5;
    const int MaxZombiesPerCommand = 10;
    const int MaxJackClones = 1;
    const int MaxSizeLevel = 5;
    const int MaxSpeedMultiplier = 5;

    [SerializeField] private float treeSpawnRadius = 7f;
    [SerializeField] private float sheepSpawnRadius = 7f;
    [SerializeField] private float zombieSpawnRadius = 7f;

    void OnEnable()
    {
        TwitchChatReader.OnCommand += HandleCommand;
    }

    void OnDisable()
    {
        TwitchChatReader.OnCommand -= HandleCommand;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.ShouldRunPresentationOnlyServices())
            return;

        if (FindFirstObjectByType<TwitchChatGameBridge>() != null)
            return;

        var reader = TwitchChatReader.Instance;
        var go = reader != null ? reader.gameObject : new GameObject(nameof(TwitchChatGameBridge));
        if (reader == null)
            DontDestroyOnLoad(go);

        if (go.GetComponent<TwitchChatGameBridge>() == null)
            go.AddComponent<TwitchChatGameBridge>();
    }

    /// <summary>Тест в Play: те же обработчики, что для Twitch IRC.</summary>
    public static void SimulateCommand(string commandName, int intValue = 1)
    {
        var bridge = FindFirstObjectByType<TwitchChatGameBridge>();
        if (bridge == null)
        {
            Debug.LogWarning("[TwitchChat] SimulateCommand: TwitchChatGameBridge не найден");
            return;
        }

        string raw = intValue > 0 ? $"#{commandName}={intValue}" : $"#{commandName}";
        var cmd = new TwitchChatCommand("debug", "DebugPlay", raw, commandName, intValue);
        bridge.HandleCommand(cmd);
    }

    public void HandleCommand(TwitchChatCommand cmd)
    {
        switch (cmd.CommandName)
        {
            case "add_tree":
                HandleAddTree(cmd);
                break;
            case "add_sheep":
                HandleAddSheep(cmd);
                break;
            case "up":
                HandleUp(cmd);
                break;
            case "forward":
                HandleForward(cmd);
                break;
            case "add_zombie":
            case "zombie": // старый чат #zombie=
                HandleAddZombie(cmd);
                break;
            case "clone_jack":
                // Временно отключено: клон сбрасывает зомби/среду.
                Debug.LogWarning("[TwitchChat] #clone_jack временно отключён");
                break;
            case "size":
                HandleSize(cmd);
                break;
            case "speed_up":
                HandleSpeedUp(cmd);
                break;
            case "show_metrics":
            case "show_metrics_jack":
            case "show_metrics_lily":
            case "show_metrics_george":
                HandleShowMetrics(cmd);
                break;
            case "add_fire":
                HandleAddFire(cmd);
                break;
            case "reset":
                HandleReset(cmd);
                break;
            case "restart_stream":
                HandleRestartStream(cmd);
                break;
            case "menu":
                PresentationEnvSwitcher.ToggleMenu();
                Debug.Log($"[TwitchChat] {cmd.DisplayName}: #menu");
                break;
            default:
                if (TryHandleEnvSwitch(cmd))
                    break;
                Debug.Log($"[TwitchChat] неизвестная команда: #{cmd.CommandName}={cmd.IntValue}");
                break;
        }
    }

    static bool TryHandleEnvSwitch(TwitchChatCommand cmd)
    {
        string name = cmd.CommandName;
        if (string.IsNullOrEmpty(name) || !name.StartsWith("env_", System.StringComparison.Ordinal))
            return false;

        string suffix = name.Substring(4);
        if (!int.TryParse(suffix, out int envIndex))
            return false;

        PresentationEnvSwitcher.SelectEnvFromCommand(envIndex);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #env_{envIndex}");
        return true;
    }

    void HandleAddTree(TwitchChatCommand cmd)
    {
        int count = Mathf.Clamp(cmd.IntValue, 1, MaxTrees);
        var jack = TrainingEnvSpace.FindPresentationJack();
        var spawner = TrainingEnvSpace.FindInPresentation<TreeSpawner>();

        if (jack == null || spawner == null)
        {
            Debug.LogWarning("[TwitchChat] add_tree: нет Jack или TreeSpawner в presentation Env");
            return;
        }

        int spawned = spawner.SpawnTreesNear(jack.transform.position, count, treeSpawnRadius);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #add_tree={count} → деревьев +{spawned} у Джека");
    }

    void HandleAddSheep(TwitchChatCommand cmd)
    {
        int count = Mathf.Clamp(cmd.IntValue, 1, MaxSheep);
        var jack = TrainingEnvSpace.FindPresentationJack();
        var spawner = TrainingEnvSpace.FindInPresentation<SheepSpawner>();

        if (jack == null || spawner == null)
        {
            Debug.LogWarning("[TwitchChat] add_sheep: нет Jack или SheepSpawner в presentation Env");
            return;
        }

        int spawned = spawner.SpawnSheepNear(jack.transform.position, count, sheepSpawnRadius);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #add_sheep={count} → овец +{spawned} у Джека");
    }

    void HandleUp(TwitchChatCommand cmd)
    {
        int height = Mathf.Clamp(cmd.IntValue, 1, MaxUpHeight);
        var jack = TrainingEnvSpace.FindViewJack();
        if (jack == null)
        {
            Debug.Log($"[TwitchChat] up: нет активного Jack в текущей среде (на задаче Lily/George его нет)");
            return;
        }

        jack.RequestTwitchUp(height);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #up={height} (высота {height} ростов)");
    }

    void HandleForward(TwitchChatCommand cmd)
    {
        int strength = Mathf.Clamp(cmd.IntValue, 1, MaxForwardStrength);
        var jack = TrainingEnvSpace.FindViewJack();
        if (jack == null)
        {
            Debug.Log($"[TwitchChat] forward: нет активного Jack в текущей среде");
            return;
        }

        jack.RequestTwitchForward(strength);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #forward={strength}");
    }

    void HandleAddZombie(TwitchChatCommand cmd)
    {
        int count = Mathf.Clamp(cmd.IntValue, 1, MaxZombiesPerCommand);
        var envRoot = TrainingEnvSpace.ActiveViewEnvRoot ?? TrainingEnvSpace.PresentationRoot;
        var jack = TrainingEnvSpace.FindViewJack();
        ZombieSpawner spawner = null;

        if (envRoot != null)
        {
            var focusedSpawners = envRoot.GetComponentsInChildren<ZombieSpawner>(true);
            for (int i = 0; i < focusedSpawners.Length; i++)
            {
                if (focusedSpawners[i] != null
                    && string.Equals(focusedSpawners[i].gameObject.name, "ZombieSpawner",
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    spawner = focusedSpawners[i];
                    break;
                }
            }
            if (spawner == null && focusedSpawners.Length > 0)
                spawner = focusedSpawners[0];
        }

        if (spawner == null)
            spawner = ZombieSpawner.FindPresentationZombieSpawner();

        if (spawner == null)
        {
            Debug.LogWarning("[TwitchChat] add zombie: ZombieSpawner не найден");
            return;
        }

        if (jack == null)
        {
            Debug.Log("[TwitchChat] add zombie: нет активного Jack — спавн у спавнера");
        }

        Vector3 near = jack != null ? jack.transform.position : spawner.transform.position;
        int spawned = spawner.SpawnZombiesNear(near, count, zombieSpawnRadius);
        if (spawned == 0)
            Debug.LogWarning("[TwitchChat] add zombie: не создано (проверьте zombiePrefab на ZombieSpawner)");
        else
            Debug.Log($"[TwitchChat] {cmd.DisplayName}: #add zombie={count} → зомби +{spawned}");
    }

    void HandleCloneJack(TwitchChatCommand cmd)
    {
        if (TwitchEphemeralEffects.ActiveCloneCount >= MaxJackClones)
        {
            Debug.LogWarning("[TwitchChat] clone_jack: клон уже есть (максимум 1)");
            return;
        }

        var jack = TrainingEnvSpace.FindPresentationPrimaryJack();
        if (jack == null || !jack.IsAliveForTwitch)
        {
            Debug.LogWarning("[TwitchChat] clone_jack: живой Jack не найден в presentation Env");
            return;
        }

        int spawned = TwitchEphemeralEffects.SpawnJackClones(jack, 1);
        if (spawned <= 0)
            Debug.LogWarning("[TwitchChat] clone_jack: не удалось создать клона");
        else
            Debug.Log($"[TwitchChat] {cmd.DisplayName}: #clone_jack → клон Jack создан");
    }

    void HandleSize(TwitchChatCommand cmd)
    {
        int sizeLevel = Mathf.Clamp(cmd.IntValue, 1, MaxSizeLevel);
        var jack = TrainingEnvSpace.FindViewJack();
        if (jack == null)
        {
            Debug.Log(
                "[TwitchChat] size: нет активного Jack (на среде Lily/George герой скрыт — " +
                "переключись на Jack-среду или #env_0)");
            return;
        }

        TwitchEphemeralEffects.ApplyJackSize(jack, sizeLevel);
        float mult = TwitchEphemeralEffects.SizeLevelToMultiplier(sizeLevel);
        Debug.Log(
            $"[TwitchChat] {cmd.DisplayName}: #size={sizeLevel} (×{mult:0.#}) " +
            $"env={TrainingEnvSpace.FindRoot(jack.transform)?.name}");
    }

    void HandleSpeedUp(TwitchChatCommand cmd)
    {
        int mult = Mathf.Clamp(cmd.IntValue, 1, MaxSpeedMultiplier);
        var jack = TrainingEnvSpace.FindViewJack();
        if (jack == null)
        {
            Debug.Log("[TwitchChat] speed_up: нет активного Jack в текущей среде");
            return;
        }

        TwitchEphemeralEffects.ApplyJackSpeed(jack, mult);
        Debug.Log(
            $"[TwitchChat] {cmd.DisplayName}: #speed_up={mult} (скорость ×{mult}) " +
            $"env={TrainingEnvSpace.FindRoot(jack.transform)?.name}");
    }

    void HandleShowMetrics(TwitchChatCommand cmd)
    {
        var hero = TrainingMetricsBurstOverlay.MetricsHero.None;
        if (cmd.CommandName.EndsWith("_jack", System.StringComparison.Ordinal)
            || cmd.IntValue == 1)
            hero = TrainingMetricsBurstOverlay.MetricsHero.Jack;
        else if (cmd.CommandName.EndsWith("_lily", System.StringComparison.Ordinal)
            || cmd.IntValue == 2)
            hero = TrainingMetricsBurstOverlay.MetricsHero.Lily;
        else if (cmd.CommandName.EndsWith("_george", System.StringComparison.Ordinal)
            || cmd.IntValue == 3)
            hero = TrainingMetricsBurstOverlay.MetricsHero.George;

        TrainingMetricsBurstOverlay.Show(hero);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #show metrics {(hero == TrainingMetricsBurstOverlay.MetricsHero.None ? "(скрыть)" : hero.ToString())}");
    }

    void HandleAddFire(TwitchChatCommand cmd)
    {
        var jack = TrainingEnvSpace.FindPresentationPrimaryJack();
        if (jack == null)
        {
            Debug.LogWarning("[TwitchChat] add fire: Jack не найден в presentation Env");
            return;
        }

        bool on = TwitchPermanentFire.Toggle();
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #add fire → костёр {(on ? "включён" : "выключен")}");
    }

    void HandleReset(TwitchChatCommand cmd)
    {
        var envRoot = TrainingEnvSpace.ActiveViewEnvRoot;
        if (envRoot == null)
            envRoot = TrainingEnvSpace.PresentationRoot;

        if (envRoot == null)
        {
            Debug.LogWarning("[TwitchChat] reset: нет Env");
            return;
        }

        if (!envRoot.gameObject.activeSelf)
            envRoot.gameObject.SetActive(true);

        AgentDeathOverlay.Hide();
        DeathFreeze.EnsureEnvSimulationRunning();
        DeathFreeze.UnfreezeWorld();
        BackgroundMusic.ResumeMusic();

        var primaryJack = TrainingEnvSpace.FindPrimaryJackInEnv(envRoot);
        if (primaryJack != null)
            TwitchEphemeralEffects.OnPresentationJackEpisodeBegin(primaryJack);

        PresentationWorldReset.ResetSpawners(envRoot, force: true);

        // EndEpisode без OnEpisodeBegin (External Brain) не поднимает HP — форсируем полный рестарт.
        foreach (var jack in envRoot.GetComponentsInChildren<AgentGoToHouseDiscrete>(true))
        {
            if (jack == null || !jack.gameObject.activeInHierarchy)
                continue;
            if (TwitchEphemeralEffects.IsTwitchClone(jack))
                continue;
            string n = jack.gameObject.name;
            if (n == "Jack" || n == "George")
                continue;
            jack.ForceFullEpisodeRestart();
        }

        foreach (var lily in envRoot.GetComponentsInChildren<LilyScript>(true))
        {
            if (lily == null || !lily.gameObject.activeInHierarchy)
                continue;
            if (TwitchEphemeralEffects.IsTwitchClone(lily))
                continue;
            if (lily.gameObject.name == "Lily")
                continue;
            lily.ForceFullEpisodeRestart();
        }

        TrainingEnvSpace.EnsureActiveViewCamera();

        Debug.Log(
            $"[TwitchChat] {cmd.DisplayName}: #reset → {envRoot.name}. " +
            $"{PresentationWorldReset.DescribeState(envRoot)}");
    }

    void HandleRestartStream(TwitchChatCommand cmd)
    {
        if (!StreamRestartRequest.TryRequest(cmd.DisplayName, out string detail))
        {
            Debug.LogWarning($"[TwitchChat] #restart_stream отказано: {detail}");
            return;
        }

        Debug.Log(
            $"[TwitchChat] {cmd.DisplayName}: #restart_stream → флаг {detail} " +
            "(run_stream_supervised перезапустит только стрим, train не трогает)");
    }
}
