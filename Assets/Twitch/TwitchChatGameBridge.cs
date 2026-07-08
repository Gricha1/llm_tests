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
    const int MaxJackClones = 5;
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
        if (FindObjectOfType<TwitchChatGameBridge>() != null)
            return;

        var reader = TwitchChatReader.Instance;
        var go = reader != null ? reader.gameObject : new GameObject(nameof(TwitchChatGameBridge));
        if (reader == null)
            DontDestroyOnLoad(go);

        if (go.GetComponent<TwitchChatGameBridge>() == null)
            go.AddComponent<TwitchChatGameBridge>();
    }

    void HandleCommand(TwitchChatCommand cmd)
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
            case "zombie":
                HandleZombie(cmd);
                break;
            case "clone_jack":
                HandleCloneJack(cmd);
                break;
            case "size":
                HandleSize(cmd);
                break;
            case "speed_up":
                HandleSpeedUp(cmd);
                break;
            case "show_metrics":
                HandleShowMetrics(cmd);
                break;
            case "reset":
                HandleReset(cmd);
                break;
            default:
                Debug.Log($"[TwitchChat] неизвестная команда: #{cmd.CommandName}={cmd.IntValue}");
                break;
        }
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
        var jack = TrainingEnvSpace.FindPresentationJack();
        if (jack == null)
        {
            Debug.LogWarning("[TwitchChat] up: Jack не найден в presentation Env");
            return;
        }

        jack.RequestTwitchUp(height);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #up={height} (высота {height} ростов)");
    }

    void HandleForward(TwitchChatCommand cmd)
    {
        int strength = Mathf.Clamp(cmd.IntValue, 1, MaxForwardStrength);
        var jack = TrainingEnvSpace.FindPresentationJack();
        if (jack == null)
        {
            Debug.LogWarning("[TwitchChat] forward: Jack не найден в presentation Env");
            return;
        }

        jack.RequestTwitchForward(strength);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #forward={strength}");
    }

    void HandleZombie(TwitchChatCommand cmd)
    {
        int count = Mathf.Clamp(cmd.IntValue, 1, MaxZombiesPerCommand);
        var spawner = ZombieSpawner.FindPresentationZombieSpawner();

        if (spawner == null)
        {
            Debug.LogWarning("[TwitchChat] zombie: ZombieSpawner не найден");
            return;
        }

        int spawned = spawner.SpawnZombiesAtSpawner(count);
        if (spawned == 0)
            Debug.LogWarning("[TwitchChat] zombie: не создано (проверьте zombiePrefab на ZombieSpawner)");
        else
            Debug.Log($"[TwitchChat] {cmd.DisplayName}: #zombie={count} → зомби +{spawned}");
    }

    void HandleCloneJack(TwitchChatCommand cmd)
    {
        int count = Mathf.Clamp(cmd.IntValue, 1, MaxJackClones);
        var jack = TrainingEnvSpace.FindPresentationPrimaryJack();
        if (jack == null || !jack.IsAliveForTwitch)
        {
            Debug.LogWarning("[TwitchChat] clone_jack: живой Jack не найден в presentation Env");
            return;
        }

        int before = TwitchEphemeralEffects.ActiveCloneCount;
        int spawned = TwitchEphemeralEffects.SpawnJackClones(jack, count);
        if (spawned <= 0)
            Debug.LogWarning($"[TwitchChat] clone_jack: лимит клонов ({before}/{MaxJackClones})");
        else
            Debug.Log($"[TwitchChat] {cmd.DisplayName}: #clone_jack={count} → клонов +{spawned} (всего {TwitchEphemeralEffects.ActiveCloneCount})");
    }

    void HandleSize(TwitchChatCommand cmd)
    {
        int sizeLevel = Mathf.Clamp(cmd.IntValue, 1, MaxSizeLevel);
        var jack = TrainingEnvSpace.FindPresentationPrimaryJack();
        if (jack == null)
        {
            Debug.LogWarning("[TwitchChat] size: оригинальный Jack не найден в presentation Env");
            return;
        }

        TwitchEphemeralEffects.ApplyJackSize(jack, sizeLevel);
        float mult = TwitchEphemeralEffects.SizeLevelToMultiplier(sizeLevel);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #size={sizeLevel} (×{mult:0.#})");
    }

    void HandleSpeedUp(TwitchChatCommand cmd)
    {
        int mult = Mathf.Clamp(cmd.IntValue, 1, MaxSpeedMultiplier);
        var jack = TrainingEnvSpace.FindPresentationPrimaryJack();
        if (jack == null)
        {
            Debug.LogWarning("[TwitchChat] speed_up: оригинальный Jack не найден в presentation Env");
            return;
        }

        TwitchEphemeralEffects.ApplyJackSpeed(jack, mult);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #speed_up={mult} (скорость ×{mult})");
    }

    void HandleShowMetrics(TwitchChatCommand cmd)
    {
        TrainingMetricsBurstOverlay.Toggle();
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #show metrics → переключить графики");
    }

    void HandleReset(TwitchChatCommand cmd)
    {
        var jack = TrainingEnvSpace.FindPresentationPrimaryJack();
        if (jack == null)
        {
            Debug.LogWarning("[TwitchChat] reset: Jack не найден в presentation Env");
            return;
        }

        var envRoot = TrainingEnvSpace.FindRoot(jack.transform);
        AgentDeathOverlay.Hide();
        DeathFreeze.EnsureEnvSimulationRunning();
        DeathFreeze.UnfreezeWorld();
        BackgroundMusic.ResumeMusic();
        TwitchEphemeralEffects.OnPresentationJackEpisodeBegin(jack);

        PresentationWorldReset.ResetSpawners(envRoot, force: true);

        jack.EndEpisode();

        var lily = envRoot != null ? envRoot.GetComponentInChildren<LilyScript>(false) : null;
        if (lily != null)
            lily.EndEpisode();

        Debug.Log(
            $"[TwitchChat] {cmd.DisplayName}: #reset → новый эпизод. {PresentationWorldReset.DescribeState(envRoot)}");
    }

}
