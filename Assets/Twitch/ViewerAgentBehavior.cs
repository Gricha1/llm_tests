using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// MVP: хранит Character Behavior DSL от stream_bot; движение — ViewerSimpleAgent.
/// </summary>
public sealed class ViewerAgentBehavior : MonoBehaviour
{
    static readonly Dictionary<string, string> PendingByUser = new Dictionary<string, string>();
    static readonly List<ViewerAgentBehavior> Live = new List<ViewerAgentBehavior>();

    [SerializeField] string username = "";
    [SerializeField] string behaviorName = "";
    [SerializeField] string description = "";
    [SerializeField] string rawJson = "";

    TextMesh _label;

    public string Username => username;
    public string BehaviorName => behaviorName;

    void OnEnable()
    {
        if (!Live.Contains(this))
            Live.Add(this);
        TryApplyPending();
    }

    void OnDisable()
    {
        Live.Remove(this);
    }

    public void SetBehaviorProgram(string user, string name, string desc, string json, bool createLabel = true)
    {
        username = user ?? "";
        behaviorName = name ?? "";
        description = desc ?? "";
        rawJson = json ?? "";
        if (createLabel && GetComponent<ViewerSimpleAgent>() == null)
        {
            EnsureLabel();
            if (_label != null)
                _label.text = string.IsNullOrEmpty(behaviorName) ? username : behaviorName;
        }
        Debug.Log($"[ViewerAgentBehavior] set {username}: {behaviorName} — {description}");
    }

    void TryApplyPending()
    {
        if (string.IsNullOrEmpty(username))
            return;
        if (PendingByUser.TryGetValue(username.ToLowerInvariant(), out string json))
        {
            PendingByUser.Remove(username.ToLowerInvariant());
            string name = Extract(json, "behavior_name");
            string desc = Extract(json, "description");
            SetBehaviorProgram(username, name, desc, json);
        }
    }

    void EnsureLabel()
    {
        if (_label != null)
            return;
        var go = new GameObject("BehaviorLabel");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, 2.2f, 0f);
        _label = go.AddComponent<TextMesh>();
        _label.characterSize = 0.08f;
        _label.fontSize = 48;
        _label.anchor = TextAnchor.LowerCenter;
        _label.alignment = TextAlignment.Center;
        _label.color = new Color(0.85f, 0.95f, 1f);
    }

    public static void ApplyOrPending(string user, string name, string desc, string json)
    {
        string key = (user ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
            key = "viewer";

        for (int i = 0; i < Live.Count; i++)
        {
            var v = Live[i];
            if (v != null && string.Equals(v.username, user, System.StringComparison.OrdinalIgnoreCase))
            {
                v.SetBehaviorProgram(user, name, desc, json);
                return;
            }
        }

        // Нет живого маркера — спавним простого персонажа (капсула, без сенсоров).
        var simple = ViewerSimpleAgent.FindOrSpawn(user);
        var store = simple != null ? simple.GetComponent<ViewerAgentBehavior>() : null;
        if (store != null)
        {
            store.SetBehaviorProgram(user, name, desc, json);
            return;
        }

        PendingByUser[key] = json ?? "";
        Debug.Log($"[ViewerAgentBehavior] pending for {user}: {name}");
    }

    static string Extract(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return "";
        string pattern = "\"" + key + "\"";
        int i = json.IndexOf(pattern, System.StringComparison.Ordinal);
        if (i < 0) return "";
        int colon = json.IndexOf(':', i + pattern.Length);
        if (colon < 0) return "";
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return "";
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return "";
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }
}
