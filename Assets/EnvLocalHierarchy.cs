using UnityEngine;

/// <summary>
/// На корне Env: проверяет локальную иерархию и привязывает костёр к HomeSpot.
/// </summary>
public sealed class EnvLocalHierarchy : MonoBehaviour
{
    static readonly string[] ExpectedDirectChildren =
    {
        "HomeSpot", "Fire", "TreeSpawner", "SheepSpawner"
    };

    void Awake()
    {
        if (!TrainingEnvSpace.IsEnvRootTransform(transform))
            return;

        ValidateHierarchy();
        BindCampfires();
    }

    void ValidateHierarchy()
    {
        if (transform.parent != null && TrainingEnvSpace.IsEnvRootName(transform.parent.name))
        {
            Debug.LogWarning(
                $"[{name}] вложен в другой Env — при дублировании среды координаты могут сбиться.");
        }

        for (int i = 0; i < ExpectedDirectChildren.Length; i++)
        {
            string childName = ExpectedDirectChildren[i];
            Transform child = transform.Find(childName);
            if (child == null)
            {
                Debug.LogWarning($"[{name}] нет дочернего объекта «{childName}».");
                continue;
            }

            if (!TrainingEnvSpace.IsDescendantOf(child, transform))
            {
                Debug.LogWarning($"[{name}] «{childName}» не в локальной иерархии Env.");
            }
        }

        var jacks = GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < jacks.Length; i++)
        {
            var jack = jacks[i];
            if (jack == null)
                continue;

            if (!TrainingEnvSpace.IsDescendantOf(jack.transform, transform))
            {
                Debug.LogWarning($"[{name}] Jack «{jack.name}» вне иерархии Env.");
                continue;
            }

            TrainingEnvSpace.EnsureDescendantOfEnv(jack.transform, transform);
        }

        var spawners = GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < spawners.Length; i++)
        {
            var spawner = spawners[i];
            if (spawner == null)
                continue;

            if (spawner is TreeSpawner or SheepSpawner or FlowerSpawner or ZombieSpawner)
                TrainingEnvSpace.EnsureDescendantOfEnv(spawner.transform, transform);
        }
    }

    void BindCampfires()
    {
        var jacks = GetComponentsInChildren<AgentGoToHouseDiscrete>(true);
        for (int i = 0; i < jacks.Length; i++)
        {
            if (jacks[i] != null)
                jacks[i].EnsureCampfireLocalBinding();
        }
    }
}
