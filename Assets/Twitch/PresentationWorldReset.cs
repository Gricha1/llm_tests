using UnityEngine;

/// <summary>Сброс деревьев, овец и зомби в presentation Env (Twitch #reset и OnEpisodeBegin).</summary>
public static class PresentationWorldReset
{
    const float DedupeSeconds = 0.25f;
    const float MinFillRatio = 0.5f;

    static int _lastEnvId;
    static float _lastResetTime;

    public static void ResetSpawners(Transform envRoot, bool force = false)
    {
        if (envRoot == null)
            return;

        int envId = envRoot.GetInstanceID();
        float now = Time.unscaledTime;
        if (!force && _lastEnvId == envId && now - _lastResetTime < DedupeSeconds)
            return;

        _lastEnvId = envId;
        _lastResetTime = now;

        var treeSpawner = envRoot.GetComponentInChildren<TreeSpawner>(true);
        var sheepSpawner = envRoot.GetComponentInChildren<SheepSpawner>(true);
        var flowerSpawner = envRoot.GetComponentInChildren<FlowerSpawner>(true);

        treeSpawner?.ResetTrees();
        sheepSpawner?.ResetSheep();
        flowerSpawner?.ResetFlowers();

        var envConfig = envRoot.GetComponent<EnvTrainingConfig>();
        if (envConfig == null || envConfig.ResolveJackMode() != JackTrainingMode.ZombieOnly)
        {
            foreach (var zombieSpawner in envRoot.GetComponentsInChildren<ZombieSpawner>(true))
            {
                if (zombieSpawner != null)
                    zombieSpawner.ResetForNewEpisode();
            }
        }

        VerifyAndRetry(treeSpawner, sheepSpawner);
    }

    static void VerifyAndRetry(TreeSpawner trees, SheepSpawner sheep)
    {
        if (trees != null)
        {
            int target = trees.TargetCount;
            int need = Mathf.Max(1, Mathf.RoundToInt(target * MinFillRatio));
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int alive = trees.AliveCount;
                if (alive >= need)
                    break;

                Debug.LogWarning(
                    $"[PresentationWorldReset] деревьев мало ({alive}/{target}), " +
                    $"повторный спавн {attempt + 1}/3");
                trees.ResetTrees();
            }

            if (trees.AliveCount < need)
            {
                Debug.LogError(
                    $"[PresentationWorldReset] деревья так и не восстановились " +
                    $"({trees.AliveCount}/{target})");
            }
        }

        if (sheep != null)
        {
            int alive = sheep.AliveCount;
            int target = sheep.TargetCount;
            if (alive < Mathf.Max(1, Mathf.RoundToInt(target * MinFillRatio)))
            {
                Debug.LogWarning(
                    $"[PresentationWorldReset] овец мало ({alive}/{target}), повторный спавн");
                sheep.ResetSheep();
            }
        }
    }

    public static string DescribeState(Transform envRoot)
    {
        if (envRoot == null)
            return "env=null";

        var trees = envRoot.GetComponentInChildren<TreeSpawner>(true);
        var sheep = envRoot.GetComponentInChildren<SheepSpawner>(true);
        var jack = TrainingEnvSpace.FindPresentationPrimaryJack();

        int treeN = trees != null ? trees.AliveCount : -1;
        int treeT = trees != null ? trees.TargetCount : -1;
        int sheepN = sheep != null ? sheep.AliveCount : -1;
        int sheepT = sheep != null ? sheep.TargetCount : -1;
        string jackState = jack != null
            ? $"hp={jack.hp} wood={jack.wood} satiety={jack.satiety} heat={jack.heat}"
            : "нет";

        return $"деревья {treeN}/{treeT}, овцы {sheepN}/{sheepT}, Jack ({jackState})";
    }
}
