using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Горячие клавиши для presentation-сцены (не зависят от Jack/ML-Agents).
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
        if (!WasZPressed())
            return;

        TrySpawnZombieAtSpawner(1);
    }

    static bool WasZPressed()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.zKey.wasPressedThisFrame)
            return true;
#endif
        return Input.GetKeyDown(KeyCode.Z);
    }

    public static void TrySpawnZombieAtSpawner(int count)
    {
        var spawner = ZombieSpawner.FindPresentationZombieSpawner();
        if (spawner == null)
        {
            Debug.LogWarning("[Debug] Z: ZombieSpawner не найден");
            return;
        }

        int spawned = spawner.SpawnZombiesAtSpawner(count);
        if (spawned <= 0)
            Debug.LogWarning("[Debug] Z: зомби не создан (проверьте zombiePrefab на ZombieSpawner)");
        else
            Debug.Log($"[Debug] Z: +{spawned} зомби у домика → идут к Jack. Точка: {spawner.GetSpawnCenterWorld()}");
    }
}
