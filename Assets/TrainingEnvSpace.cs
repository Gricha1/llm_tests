using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Локальные координаты первой среды (Env) → мировые для дубликатов Env (1), Env (2)…
/// Звук, HUD и рендер — только у основной среды «Env» (первая копия).
/// </summary>
public static class TrainingEnvSpace
{
    static Transform _presentationRoot;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ConfigureParallelEnvPresentation()
    {
        _presentationRoot = null;
        var presentation = PresentationRoot;
        if (presentation == null)
            return;

        foreach (var envRoot in FindAllEnvRoots())
        {
            if (envRoot == presentation)
                continue;
            MuteEnvPresentation(envRoot);
        }

        EnsureTrainingConfigs();
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
            if (envRoot.GetComponent<JackEnvTrainingConfig>() == null)
                envRoot.gameObject.AddComponent<JackEnvTrainingConfig>();
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
        return root.GetComponentInChildren<T>(true);
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
        return FindAllEnvRoots().Length > 1;
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

        // Фоновые Env не нужны на экране — отключаем рендер, чтобы не грузить GPU.
        foreach (var cam in envRoot.GetComponentsInChildren<Camera>(true))
            cam.enabled = false;

        foreach (var light in envRoot.GetComponentsInChildren<Light>(true))
            light.enabled = false;

        foreach (var renderer in envRoot.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;
    }
}
