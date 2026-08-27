#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Собирает игровой ShirtlessZombie (логика как у FatZombie) + AssetBundle для hot-deploy на стрим.
/// </summary>
public static class ShirtlessZombieMigration
{
    const string SourceUrpPrefab =
        "Assets/NewPunch/ShirtlessZombieFree/Prefabs/ShirtlessZombie_FREE_URP.prefab";
    const string PrefabPath = "Assets/Prefabs/ShirtlessZombie.prefab";
    const string ResourcesPrefabPath = "Assets/Resources/ShirtlessZombie.prefab";
    const string ZombieControllerPath =
        "Assets/ResilientLogicGames/ChubyCharacterFree/Animations/Animator Zombie.controller";
    const string FatPrefabPath = "Assets/Prefabs/FatZombie.prefab";
    const string BundleDir = "Assets/StreamingAssets";
    const string BundleAssetName = "ShirtlessZombie";
    const string BundleName = "zombie_skins";

    [MenuItem("Forest Survival/Build ShirtlessZombie prefab + bundle")]
    public static void BuildFromMenu()
    {
        if (!BuildInternal(BuildTarget.StandaloneWindows64, exitOnDone: false))
            Debug.LogError("ShirtlessZombieMigration: failed (see console).");
    }

    /// <summary>Batchmode: -executeMethod ShirtlessZombieMigration.BuildFromCommandLine</summary>
    public static void BuildFromCommandLine()
    {
        string targetArg = GetArg("-bundleTarget");
        BuildTarget target = BuildTarget.StandaloneLinux64;
        if (!string.IsNullOrEmpty(targetArg)
            && Enum.TryParse(targetArg, ignoreCase: true, out BuildTarget parsed))
            target = parsed;

        bool ok = BuildInternal(target, exitOnDone: true);
        EditorApplication.Exit(ok ? 0 : 1);
    }

    static bool BuildInternal(BuildTarget bundleTarget, bool exitOnDone)
    {
        try
        {
            EditorUtility.DisplayProgressBar("ShirtlessZombie", "Creating prefab…", 0.2f);
            var prefab = CreateGamePrefab();
            if (prefab == null)
            {
                Debug.LogError("ShirtlessZombieMigration: CreateGamePrefab failed.");
                return false;
            }

            EditorUtility.DisplayProgressBar("ShirtlessZombie", "Copy to Resources…", 0.45f);
            EnsureFolder("Assets/Resources");
            AssetDatabase.CopyAsset(PrefabPath, ResourcesPrefabPath);
            AssetDatabase.ImportAsset(ResourcesPrefabPath);

            EditorUtility.DisplayProgressBar("ShirtlessZombie", $"AssetBundle ({bundleTarget})…", 0.7f);
            if (!BuildAssetBundle(bundleTarget))
            {
                Debug.LogError("ShirtlessZombieMigration: AssetBundle build failed.");
                return false;
            }

            AssetDatabase.SaveAssets();
            Debug.Log(
                $"ShirtlessZombieMigration: OK. prefab={PrefabPath}, resources={ResourcesPrefabPath}, " +
                $"bundle={BundleDir}/{BundleName} target={bundleTarget}");
            return true;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static GameObject CreateGamePrefab()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourceUrpPrefab);
        if (source == null)
        {
            Debug.LogError($"ShirtlessZombieMigration: missing {SourceUrpPrefab}");
            return null;
        }

        EnsureFolder("Assets/Prefabs");

        var temp = PrefabUtility.InstantiatePrefab(source) as GameObject;
        if (temp == null)
            temp = UnityEngine.Object.Instantiate(source);
        temp.name = "ShirtlessZombie";
        temp.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        // Чуть крупнее дефолта — ближе к FatZombie (1.5), но не толстый.
        temp.transform.localScale = new Vector3(1.25f, 1.25f, 1.25f);

        int zombieLayer = LayerMask.NameToLayer("Zombie");
        if (zombieLayer >= 0)
            ApplyLayerRecursively(temp.transform, zombieLayer);
        temp.tag = "Zombie";

        SetupAnimator(temp);
        EnsureGameplayComponents(temp);

        try
        {
            var saved = PrefabUtility.SaveAsPrefabAsset(temp, PrefabPath);
            AssetDatabase.ImportAsset(PrefabPath);
            return saved;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(temp);
        }
    }

    static void SetupAnimator(GameObject root)
    {
        var anim = root.GetComponent<Animator>() ?? root.GetComponentInChildren<Animator>(true);
        if (anim == null)
            return;

        var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ZombieControllerPath);
        if (controller != null)
            anim.runtimeAnimatorController = controller;
        anim.applyRootMotion = false;
        EditorUtility.SetDirty(anim);
    }

    static void EnsureGameplayComponents(GameObject root)
    {
        // CharacterController сначала — ZombieChase/Attack могут требовать коллайдер.
        var cc = root.GetComponent<CharacterController>();
        if (cc == null)
            cc = root.AddComponent<CharacterController>();
        cc.height = 2f;
        cc.radius = 0.45f;
        cc.center = new Vector3(0f, 1f, 0f);
        cc.skinWidth = 0.08f;
        cc.slopeLimit = 45f;
        cc.stepOffset = 0.3f;

        if (root.GetComponent<CapsuleCollider>() == null)
        {
            var cap = root.AddComponent<CapsuleCollider>();
            cap.height = cc.height;
            cap.radius = cc.radius;
            cap.center = cc.center;
            cap.isTrigger = false;
        }

        // Значения как у FatZombie.prefab, чтобы поведение совпадало.
        var chase = root.GetComponent<ZombieChase>() ?? root.AddComponent<ZombieChase>();
        var soChase = new SerializedObject(chase);
        SetFloat(soChase, "moveSpeed", 1.5f);
        SetFloat(soChase, "rotationSpeed", 120f);
        SetFloat(soChase, "stopDistance", 1.0f);
        soChase.ApplyModifiedPropertiesWithoutUndo();

        var attack = root.GetComponent<ZombieAttack>() ?? root.AddComponent<ZombieAttack>();
        var soAtk = new SerializedObject(attack);
        SetFloat(soAtk, "hitCooldown", 1f);
        SetFloat(soAtk, "damageDelaySeconds", 0.5f);
        SetFloat(soAtk, "damageRadius", 1.55f);
        SetFloat(soAtk, "attackCheckInterval", 0.2f);
        soAtk.ApplyModifiedPropertiesWithoutUndo();

        if (root.GetComponent<ZombieHealth>() == null)
            root.AddComponent<ZombieHealth>();

        // Скопировать tag с Fat если есть.
        var fat = AssetDatabase.LoadAssetAtPath<GameObject>(FatPrefabPath);
        if (fat != null && !string.IsNullOrEmpty(fat.tag) && fat.tag != "Untagged")
            root.tag = fat.tag;
    }

    static void SetFloat(SerializedObject so, string prop, float value)
    {
        var p = so.FindProperty(prop);
        if (p != null)
            p.floatValue = value;
    }

    static bool BuildAssetBundle(BuildTarget target)
    {
        EnsureFolder(BundleDir);

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
            return false;

        string assetPath = PrefabPath;
        var build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = new[] { assetPath },
            addressableNames = new[] { BundleAssetName },
        };

        var manifest = BuildPipeline.BuildAssetBundles(
            BundleDir,
            new[] { build },
            BuildAssetBundleOptions.ForceRebuildAssetBundle
            | BuildAssetBundleOptions.StrictMode,
            target);

        if (manifest == null)
            return false;

        // Убрать мусорный манифест-бандл с именем папки — оставляем zombie_skins (+ .manifest).
        string folderManifest = Path.Combine(BundleDir, "StreamingAssets");
        if (File.Exists(folderManifest))
            AssetDatabase.DeleteAsset("Assets/StreamingAssets/StreamingAssets");
        string folderManifestMeta = folderManifest + ".manifest";
        if (File.Exists(folderManifestMeta))
            File.Delete(folderManifestMeta);

        AssetDatabase.Refresh();
        string bundlePath = Path.Combine(BundleDir, BundleName);
        if (!File.Exists(bundlePath))
        {
            Debug.LogError($"ShirtlessZombieMigration: bundle missing at {bundlePath}");
            return false;
        }

        return true;
    }

    static void EnsureFolder(string assetFolder)
    {
        if (AssetDatabase.IsValidFolder(assetFolder))
            return;
        string parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/');
        string name = Path.GetFileName(assetFolder);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
            return;
        if (!AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }

    static void ApplyLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        foreach (Transform child in root)
            ApplyLayerRecursively(child, layer);
    }

    static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }

        return null;
    }
}
#endif
