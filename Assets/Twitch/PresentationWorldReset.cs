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

        treeSpawner?.ResetTrees();
        sheepSpawner?.ResetSheep();

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
            int alive = trees.AliveCount;
            int target = trees.TargetCount;
            if (alive < Mathf.Max(1, Mathf.RoundToInt(target * MinFillRatio)))
            {
                Debug.LogWarning(
                    $"[PresentationWorldReset] деревьев мало ({alive}/{target}), повторный спавн");
                trees.ResetTrees();
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
