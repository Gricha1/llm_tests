using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Unity → stream_bot: учёт воды/еды/овец/костров для #stats.</summary>
public static class StreamingSurvivalStatsReporter
{
    const string DefaultUrl = "http://127.0.0.1:8765/stats/event";

    static string BotUrl =>
        (Environment.GetEnvironmentVariable("FOREST_BOT_HTTP_URL") ?? DefaultUrl).Trim();

    public static void Report(string username, string stat, int amount = 1)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(stat) || amount <= 0)
            return;
        Runner.Ensure().StartCoroutine(Post(username.Trim(), stat.Trim(), amount));
    }

    static IEnumerator Post(string username, string stat, int amount)
    {
        var payload = $"{{\"username\":\"{Escape(username)}\",\"stat\":\"{Escape(stat)}\",\"amount\":{amount}}}";
        var body = Encoding.UTF8.GetBytes(payload);
        using var req = new UnityWebRequest(BotUrl, "POST");
        req.uploadHandler = new UploadHandlerRaw(body);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 3;
        yield return req.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
        if (req.result != UnityWebRequest.Result.Success)
#else
        if (req.isNetworkError || req.isHttpError)
#endif
            Debug.LogWarning($"[SSStats] POST fail stat={stat} user={username}: {req.error}");
    }

    static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    sealed class Runner : MonoBehaviour
    {
        static Runner _instance;

        public static Runner Ensure()
        {
            if (_instance != null)
                return _instance;
            var go = new GameObject(nameof(StreamingSurvivalStatsReporter));
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<Runner>();
            return _instance;
        }
    }
}
