using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Локальные координаты первой среды (Env) → мировые для дубликатов Env (1), Env (2)…
/// Звук, HUD и рендер — только у основной среды «Env» (первая копия).
/// </summary>
public static class TrainingEnvSpace
{
    static Transform _presentationRoot;
    static bool _parallelEnvsVisible;

    public static bool ParallelEnvsVisible => _parallelEnvsVisible;

    /// <summary>Показать рендер копий Env (1)… для отладки в Play. Env var FOREST_SHOW_PARALLEL_ENVS=1 или -forestShowParallelEnvs.</summary>
    public static bool IsShowParallelEnvsRequested()
    {
        var env = System.Environment.GetEnvironmentVariable("FOREST_SHOW_PARALLEL_ENVS");
        if (env == "1" || string.Equals(env, "true", System.StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var arg in System.Environment.GetCommandLineArgs())
        {
            if (arg == "-forestShowParallelEnvs" || arg == "--forest-show-parallel-envs")
                return true;
        }

        return false;
    }

    public static void SetParallelEnvsVisible(bool visible)
    {
        _parallelEnvsVisible = visible;
        ApplyParallelEnvPresentation();
    }

    public static void ToggleParallelEnvsVisible()
    {
        SetParallelEnvsVisible(!_parallelEnvsVisible);
    }

    /// <summary>Presentation Env на стриме: Full-режим Jack при нескольких Env в сцене.</summary>
    public static bool IsPresentationOnlyRequested()
    {
        return HasMultipleTrainingEnvs() && PresentationRoot != null;
    }

    public static bool IsMlAgentsTrainingActive()
    {
        if (!Unity.MLAgents.Academy.IsInitialized)
            return false;
        return Unity.MLAgents.Academy.Instance.IsCommunicatorOn;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ConfigureParallelEnvPresentation()
    {
        _presentationRoot = null;
        _parallelEnvsVisible = IsShowParallelEnvsRequested();
        ApplyParallelEnvPresentation();
        EnsureTrainingConfigs();
        EnsureEnvLocalHierarchyComponents();
    }

    static void ApplyParallelEnvPresentation()
    {
        var presentation = PresentationRoot;
        if (presentation == null)
            return;

        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot == presentation)
                continue;

            if (_parallelEnvsVisible)
                UnmuteEnvPresentation(envRoot);
            else
                MuteEnvPresentation(envRoot);
        }
    }

    public static bool IsPresentationEnv(Transform envRoot) =>
        envRoot != null && envRoot == PresentationRoot;

    /// <summary>0 = Env (presentation), 1 = Env (1), 2 = Env (2), …</summary>
    public static int GetEnvCopyIndex(Transform envRoot)
    {
        if (envRoot == null)
            return 0;

        string name = envRoot.name;
        if (name == "Env")
            return 0;

        if (name.StartsWith("Env (") && name.EndsWith(")"))
        {
            string inner = name.Substring(5, name.Length - 6);
            if (int.TryParse(inner, out int n))
                return n;
        }

        return 0;
    }

    static void EnsureTrainingConfigs()
    {
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot.GetComponent<EnvTrainingConfig>() == null)
                envRoot.gameObject.AddComponent<EnvTrainingConfig>();
        }
    }

    public static Transform PresentationRoot
    {
        get
        {
            if (_presentationRoot == null)
                _presentationRoot = ResolvePresentationRoot();
            return _presentationRoot;
        }
    }

    public static bool IsPresentationTransform(Transform t)
    {
        var root = PresentationRoot;
        if (root == null || t == null)
            return true;
        return IsDescendantOf(t, root);
    }

    public static bool ShouldPlayFeedback(Transform source)
    {
        var root = PresentationRoot;
        if (root == null)
            return true;
        if (source == null)
            return false;
        return IsDescendantOf(source, root);
    }

    public static T FindInPresentation<T>() where T : Component
    {
        var root = PresentationRoot;
        if (root == null)
            return Object.FindObjectOfType<T>();

        T inactiveFallback = null;
        foreach (var c in root.GetComponentsInChildren<T>(true))
        {
            if (c == null)
                continue;
            if (c.gameObject.activeInHierarchy)
                return c;
            if (inactiveFallback == null)
                inactiveFallback = c;
        }

        return inactiveFallback;
    }

    public static LilyScript FindPresentationLily()
    {
        var root = PresentationRoot;
        if (root == null)
            return Object.FindObjectOfType<LilyScript>();

        LilyScript hero = null;
        LilyScript anyActive = null;
        foreach (var lily in root.GetComponentsInChildren<LilyScript>(true))
        {
            if (lily == null || !lily.isActiveAndEnabled)
                continue;

            anyActive ??= lily;
            if (lily.gameObject.name.IndexOf("Hero", System.StringComparison.OrdinalIgnoreCase) >= 0)
                hero = lily;
        }

        if (hero != null)
            return hero;
        return anyActive;
    }

    public static bool IsGeorgeAgent(AgentGoToHouseDiscrete agent)
    {
        if (agent == null)
            return false;
        if (agent is GeorgeScript)
            return true;

        var go = agent.gameObject;
        if (go.CompareTag("George"))
            return true;

        return go.name.IndexOf("George", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Живой Jack для HUD и Twitch: оригинал, иначе клон с HP &gt; 0.</summary>
    public static AgentGoToHouseDiscrete FindPresentationJack()
    {
        var root = PresentationRoot;
        AgentGoToHouseDiscrete[] jacks = root != null
            ? root.GetComponentsInChildren<AgentGoToHouseDiscrete>(false)
            : Object.FindObjectsOfType<AgentGoToHouseDiscrete>();

        AgentGoToHouseDiscrete primary = null;
        AgentGoToHouseDiscrete livingClone = null;

        for (int i = 0; i < jacks.Length; i++)
        {
            var jack = jacks[i];
            if (jack == null || IsGeorgeAgent(jack))
                continue;

            if (TwitchEphemeralEffects.IsTwitchClone(jack))
            {
                if (livingClone == null && jack.IsAliveForTwitch)
                    livingClone = jack;
            }
            else if (primary == null)
            {
                primary = jack;
            }
        }

        if (primary != null && primary.IsAliveForTwitch)
            return primary;
        if (livingClone != null)
            return livingClone;
        return primary != null ? primary : FindFirstJackAgent(jacks);
    }

    static AgentGoToHouseDiscrete FindFirstJackAgent(AgentGoToHouseDiscrete[] agents)
    {
        if (agents == null)
            return null;
        for (int i = 0; i < agents.Length; i++)
        {
            if (agents[i] != null && !IsGeorgeAgent(agents[i]))
                return agents[i];
        }

        return null;
    }

    /// <summary>Оригинальный Jack (не Twitch-клон) в presentation Env.</summary>
    public static AgentGoToHouseDiscrete FindPresentationPrimaryJack()
    {
        var root = PresentationRoot;
        if (root == null)
        {
            var all = Object.FindObjectsOfType<AgentGoToHouseDiscrete>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && !TwitchEphemeralEffects.IsTwitchClone(all[i]) && !IsGeorgeAgent(all[i]))
                    return all[i];
            }

            return FindFirstJackAgent(all);
        }

        var jacks = root.GetComponentsInChildren<AgentGoToHouseDiscrete>(false);
        for (int i = 0; i < jacks.Length; i++)
        {
            if (jacks[i] != null && !TwitchEphemeralEffects.IsTwitchClone(jacks[i]) && !IsGeorgeAgent(jacks[i]))
                return jacks[i];
        }

        return FindFirstJackAgent(jacks);
    }

    public static AgentGoToHouseDiscrete FindPresentationGeorge()
    {
        var root = PresentationRoot;
        AgentGoToHouseDiscrete[] agents = root != null
            ? root.GetComponentsInChildren<AgentGoToHouseDiscrete>(false)
            : Object.FindObjectsOfType<AgentGoToHouseDiscrete>();

        AgentGoToHouseDiscrete hero = null;
        AgentGoToHouseDiscrete primary = null;

        for (int i = 0; i < agents.Length; i++)
        {
            var agent = agents[i];
            if (agent == null || !IsGeorgeAgent(agent))
                continue;

            if (agent.gameObject.name == "GeorgeHero" && agent.gameObject.activeInHierarchy)
                hero = agent;
            else if (agent.gameObject.name == "George")
                primary = agent;
            else if (primary == null)
                primary = agent;
        }

        if (hero != null)
            return hero;
        return primary;
    }

    public static Transform FindRoot(Transform from)
    {
        if (from == null)
            return null;

        Transform t = from;
        while (t != null)
        {
            if (IsEnvRootName(t.name))
                return t;
            t = t.parent;
        }

        return null;
    }

    public static bool IsEnvRootName(string objectName)
    {
        return objectName == "Env" || objectName.StartsWith("Env (");
    }

    public static bool IsEnvRootTransform(Transform t)
    {
        return t != null && IsEnvRootName(t.name)
            && (t.parent == null || !IsEnvRootName(t.parent.name));
    }

    public static bool IsDescendantOf(Transform child, Transform ancestor)
    {
        if (child == null || ancestor == null)
            return false;

        Transform t = child;
        while (t != null)
        {
            if (t == ancestor)
                return true;
            t = t.parent;
        }

        return false;
    }

    public static Vector3 LocalToWorld(Transform from, Vector3 localPosition)
    {
        var root = FindRoot(from);
        return root != null ? root.TransformPoint(localPosition) : localPosition;
    }

    public static Quaternion LocalToWorldRotation(Transform from, Vector3 localEuler)
    {
        var root = FindRoot(from);
        if (root == null)
            return Quaternion.Euler(localEuler);

        return root.rotation * Quaternion.Euler(localEuler);
    }

    /// <summary>Локальная позиция anchor внутри envRoot + смещение.</summary>
    public static Vector3 AnchorLocalPosition(Transform envRoot, Transform anchor, Vector3 localOffset)
    {
        if (envRoot == null)
            return localOffset;
        if (anchor == null)
            return localOffset;

        return envRoot.InverseTransformPoint(anchor.position) + localOffset;
    }

    /// <summary>Гарантирует, что объект — потомок envRoot (контекст — любой Transform внутри Env).</summary>
    public static bool EnsureDescendantOfEnv(Transform objectTransform, Transform context, bool preserveWorldPosition = true)
    {
        if (objectTransform == null)
            return false;

        var envRoot = FindRoot(context != null ? context : objectTransform);
        if (envRoot == null)
            return false;

        if (IsDescendantOf(objectTransform, envRoot))
            return true;

        objectTransform.SetParent(envRoot, preserveWorldPosition);
        Debug.LogWarning($"[{objectTransform.name}] перенесён под {envRoot.name} — объект должен жить в локальной иерархии Env.");
        return true;
    }

    static void EnsureEnvLocalHierarchyComponents()
    {
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot == null)
                continue;

            if (envRoot.GetComponent<EnvLocalHierarchy>() == null)
                envRoot.gameObject.AddComponent<EnvLocalHierarchy>();
        }
    }

    static Transform ResolvePresentationRoot()
    {
        Transform fallback = null;
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot.name == "Env")
                return envRoot;
            if (fallback == null)
                fallback = envRoot;
        }

        return fallback;
    }

    static Transform[] FindAllEnvRoots()
    {
        var scene = SceneManager.GetActiveScene();
        if (!scene.IsValid())
            return System.Array.Empty<Transform>();

        var roots = scene.GetRootGameObjects();
        var list = new System.Collections.Generic.List<Transform>(4);
        for (int i = 0; i < roots.Length; i++)
            CollectEnvRoots(roots[i].transform, list);
        return list.ToArray();
    }

    public static bool HasMultipleTrainingEnvs()
    {
        int active = 0;
        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot != null && envRoot.gameObject.activeInHierarchy)
                active++;
        }

        return active > 1;
    }

    public static Transform[] GetAllEnvRoots()
    {
        return FindAllEnvRoots();
    }

    static void CollectEnvRoots(Transform t, System.Collections.Generic.List<Transform> list)
    {
        if (t == null)
            return;

        if (IsEnvRootTransform(t))
            list.Add(t);

        for (int i = 0; i < t.childCount; i++)
            CollectEnvRoots(t.GetChild(i), list);
    }

    static void MuteEnvPresentation(Transform envRoot)
    {
        foreach (var canvas in envRoot.GetComponentsInChildren<Canvas>(true))
            canvas.enabled = false;

        foreach (var listener in envRoot.GetComponentsInChildren<AudioListener>(true))
            listener.enabled = false;

        foreach (var audio in envRoot.GetComponentsInChildren<AudioSource>(true))
            audio.mute = true;

        foreach (var cam in envRoot.GetComponentsInChildren<Camera>(true))
            cam.enabled = false;

        foreach (var light in envRoot.GetComponentsInChildren<Light>(true))
            light.enabled = false;

        foreach (var renderer in envRoot.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;
    }

    static void UnmuteEnvPresentation(Transform envRoot)
    {
        foreach (var canvas in envRoot.GetComponentsInChildren<Canvas>(true))
            canvas.enabled = true;

        foreach (var audio in envRoot.GetComponentsInChildren<AudioSource>(true))
            audio.mute = false;

        foreach (var cam in envRoot.GetComponentsInChildren<Camera>(true))
            cam.enabled = false;

        foreach (var listener in envRoot.GetComponentsInChildren<AudioListener>(true))
            listener.enabled = false;

        foreach (var light in envRoot.GetComponentsInChildren<Light>(true))
            light.enabled = true;

        foreach (var renderer in envRoot.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = true;
    }
}
