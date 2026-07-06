using UnityEngine;

/// <summary>
/// Выполняет команды Twitch в presentation Env.
/// </summary>
public sealed class TwitchChatGameBridge : MonoBehaviour
{
    const int MaxTrees = 10;
    const int MaxUpHeight = 5;
    const int MaxForwardStrength = 5;
    const int MaxZombiesPerCommand = 10;
    const int MaxJackClones = 5;
    const int MaxSizeLevel = 5;

    [SerializeField] private float treeSpawnRadius = 7f;
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
            case "show_metrics":
                HandleShowMetrics(cmd);
                break;
            default:
                Debug.Log($"[TwitchChat] неизвестная команда: #{cmd.CommandName}={cmd.IntValue}");
                break;
        }
    }

    void HandleAddTree(TwitchChatCommand cmd)
    {
        int count = Mathf.Clamp(cmd.IntValue, 1, MaxTrees);
        var jack = TrainingEnvSpace.FindInPresentation<AgentGoToHouseDiscrete>();
        var spawner = TrainingEnvSpace.FindInPresentation<TreeSpawner>();

        if (jack == null || spawner == null)
        {
            Debug.LogWarning("[TwitchChat] add_tree: нет Jack или TreeSpawner в presentation Env");
            return;
        }

        int spawned = spawner.SpawnTreesNear(jack.transform.position, count, treeSpawnRadius);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #add_tree={count} → деревьев +{spawned} у Джека");
    }

    void HandleUp(TwitchChatCommand cmd)
    {
        int height = Mathf.Clamp(cmd.IntValue, 1, MaxUpHeight);
        var jack = TrainingEnvSpace.FindInPresentation<AgentGoToHouseDiscrete>();
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
        var jack = TrainingEnvSpace.FindInPresentation<AgentGoToHouseDiscrete>();
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
        var jack = TrainingEnvSpace.FindInPresentation<AgentGoToHouseDiscrete>();
        var spawner = FindPresentationZombieSpawner();

        if (jack == null || spawner == null)
        {
            Debug.LogWarning("[TwitchChat] zombie: нет Jack или ZombieSpawner в presentation Env");
            return;
        }

        int spawned = spawner.SpawnZombiesNear(jack.transform.position, count, zombieSpawnRadius);
        if (spawned == 0)
            Debug.LogWarning("[TwitchChat] zombie: не создано (проверьте zombiePrefab на ZombieSpawner)");
        else
            Debug.Log($"[TwitchChat] {cmd.DisplayName}: #zombie={count} → зомби +{spawned}");
    }

    void HandleCloneJack(TwitchChatCommand cmd)
    {
        int count = Mathf.Clamp(cmd.IntValue, 1, MaxJackClones);
        var jack = TrainingEnvSpace.FindInPresentation<AgentGoToHouseDiscrete>();
        if (jack == null)
        {
            Debug.LogWarning("[TwitchChat] clone_jack: Jack не найден в presentation Env");
            return;
        }

        TwitchEphemeralEffects.SpawnJackClones(jack, count);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #clone_jack={count}");
    }

    void HandleSize(TwitchChatCommand cmd)
    {
        int sizeLevel = Mathf.Clamp(cmd.IntValue, 1, MaxSizeLevel);
        var jack = TrainingEnvSpace.FindInPresentation<AgentGoToHouseDiscrete>();
        if (jack == null)
        {
            Debug.LogWarning("[TwitchChat] size: Jack не найден в presentation Env");
            return;
        }

        TwitchEphemeralEffects.ApplyJackSize(jack, sizeLevel);
        float mult = TwitchEphemeralEffects.SizeLevelToMultiplier(sizeLevel);
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #size={sizeLevel} (×{mult:0.#})");
    }

    void HandleShowMetrics(TwitchChatCommand cmd)
    {
        TrainingMetricsBurstOverlay.Toggle();
        Debug.Log($"[TwitchChat] {cmd.DisplayName}: #show metrics → переключить графики");
    }

    static ZombieSpawner FindPresentationZombieSpawner()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root == null)
            return Object.FindObjectOfType<ZombieSpawner>();

        var spawners = root.GetComponentsInChildren<ZombieSpawner>(true);
        for (int i = 0; i < spawners.Length; i++)
        {
            if (spawners[i] != null && spawners[i].gameObject.name == "ZombieSpawner")
                return spawners[i];
        }

        return spawners.Length > 0 ? spawners[0] : null;
    }
}
