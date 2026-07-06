#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class EvalAgentCamerasMenu
{
    [MenuItem("Forest Survival/Cameras: добавить Eval камеры Jack + Lily (FP/TP)")]
    public static void AddEvalCamerasToJackAndLily()
    {
        int created = 0;
        created += EnsureForRoot("Jack");
        created += EnsureForRoot("Lily");

        for (int i = 0; i < SceneManager.sceneCount; i++)
            EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneAt(i));

        Debug.Log($"EvalAgentCamerasMenu: created/updated camera objects: {created}");
    }

    private static int EnsureForRoot(string rootName)
    {
        var root = GameObject.Find(rootName);
        if (root == null)
        {
            Debug.LogWarning($"EvalAgentCamerasMenu: root '{rootName}' not found in current scene.");
            return 0;
        }

        int delta = 0;
        delta += EnsureFp(root.transform, rootName);
        delta += EnsureTp(root.transform, rootName);
        return delta;
    }

    private static int EnsureFp(Transform root, string rootName)
    {
        string camName = $"{rootName}_EvalCam_FP";
        var t = root.Find(camName);
        if (t == null)
        {
            var go = new GameObject(camName);
            Undo.RegisterCreatedObjectUndo(go, "Create FP eval camera");
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(0f, 1.55f, 0.15f);
            go.transform.localRotation = Quaternion.identity;
            go.AddComponent<Camera>();
            deltaDefaults(go.GetComponent<Camera>(), depth: 5);
            go.AddComponent<AudioListener>().enabled = false;
            return 1;
        }
        else
        {
            var cam = t.GetComponent<Camera>() ?? Undo.AddComponent<Camera>(t.gameObject);
            deltaDefaults(cam, depth: 5);
            var al = t.GetComponent<AudioListener>() ?? Undo.AddComponent<AudioListener>(t.gameObject);
            al.enabled = false;
            return 0;
        }
    }

    private static int EnsureTp(Transform root, string rootName)
    {
        string camName = $"{rootName}_EvalCam_TP";
        var t = root.Find(camName);
        if (t == null)
        {
            var go = new GameObject(camName);
            Undo.RegisterCreatedObjectUndo(go, "Create TP eval camera");
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(0f, 2.6f, -3.2f);
            go.transform.localRotation = Quaternion.identity;
            go.AddComponent<Camera>();
            deltaDefaults(go.GetComponent<Camera>(), depth: 4);
            var follow = go.AddComponent<FollowTargetCamera>();
            follow.Target = root;
            go.AddComponent<AudioListener>().enabled = false;
            return 1;
        }
        else
        {
            var cam = t.GetComponent<Camera>() ?? Undo.AddComponent<Camera>(t.gameObject);
            deltaDefaults(cam, depth: 4);
            var follow = t.GetComponent<FollowTargetCamera>() ?? Undo.AddComponent<FollowTargetCamera>(t.gameObject);
            follow.Target = root;
            var al = t.GetComponent<AudioListener>() ?? Undo.AddComponent<AudioListener>(t.gameObject);
            al.enabled = false;
            return 0;
        }
    }

    private static void deltaDefaults(Camera cam, int depth)
    {
        cam.enabled = true;
        cam.depth = depth;
        cam.fieldOfView = 60f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 200f;
        cam.clearFlags = CameraClearFlags.Skybox;
    }
}
#endif

