using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// UDP JSON от Python stream_bot (stream_command / poll_state / stream_event).
/// Команды выполняются только после маппинга на известные TwitchChatGameBridge-хендлеры.
/// </summary>
public sealed class StreamBotUdpReceiver : MonoBehaviour
{
    [SerializeField] private int listenPort = 5055;
    [SerializeField] private bool logPackets = true;

    static StreamBotUdpReceiver _instance;

    readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
    UdpClient _udp;
    Thread _thread;
    volatile bool _running;
    string _lastPollTitle = "";
    string _lastEvent = "";

    public static string LastPollTitle => _instance != null ? _instance._lastPollTitle : "";
    public static string LastEvent => _instance != null ? _instance._lastEvent : "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.ShouldRunPresentationOnlyServices())
            return;
        if (_instance != null)
            return;
        if (FindFirstObjectByType<StreamBotUdpReceiver>() != null)
            return;

        var go = new GameObject(nameof(StreamBotUdpReceiver));
        DontDestroyOnLoad(go);
        go.AddComponent<StreamBotUdpReceiver>();
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        string envPort = System.Environment.GetEnvironmentVariable("FOREST_STREAM_BOT_PORT");
        if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int p) && p > 0)
            listenPort = p;
    }

    void OnEnable()
    {
        StartListener();
    }

    void OnDisable()
    {
        StopListener();
    }

    void Update()
    {
        while (_queue.TryDequeue(out string json))
            HandleJson(json);
    }

    void StartListener()
    {
        if (_running)
            return;
        try
        {
            _udp = new UdpClient(listenPort);
            _running = true;
            _thread = new Thread(ListenLoop) { IsBackground = true, Name = "StreamBotUdp" };
            _thread.Start();
            Debug.Log($"[StreamBotUdp] listening UDP :{listenPort}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[StreamBotUdp] bind failed :{listenPort} — {e.Message}");
            _running = false;
        }
    }

    void StopListener()
    {
        _running = false;
        try { _udp?.Close(); } catch { /* ignore */ }
        _udp = null;
        if (_thread != null && _thread.IsAlive)
            _thread.Join(500);
        _thread = null;
    }

    void ListenLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running && _udp != null)
        {
            try
            {
                byte[] data = _udp.Receive(ref remote);
                string text = Encoding.UTF8.GetString(data);
                if (!string.IsNullOrEmpty(text))
                    _queue.Enqueue(text);
            }
            catch (SocketException)
            {
                if (!_running)
                    break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StreamBotUdp] recv: {e.Message}");
            }
        }
    }

    void HandleJson(string json)
    {
        try
        {
            var msg = JsonUtility.FromJson<StreamBotEnvelope>(json);
            if (msg == null || string.IsNullOrEmpty(msg.type))
            {
                // JsonUtility не парсит произвольные option-массивы — fallback MiniJSON-like manual
                HandleLoose(json);
                return;
            }

            switch (msg.type)
            {
                case "stream_command":
                    ApplyCommand(msg.command, msg.value, msg.user, msg.source, msg.message);
                    break;
                case "stream_event":
                    _lastEvent = msg.message ?? "";
                    if (logPackets)
                        Debug.Log($"[StreamBotUdp] event [{msg.severity}] {msg.message}");
                    break;
                case "poll_state":
                    HandleLoose(json);
                    break;
                default:
                    HandleLoose(json);
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[StreamBotUdp] parse: {e.Message}");
        }
    }

    void HandleLoose(string json)
    {
        string type = ExtractString(json, "type");
        if (type == "stream_command")
        {
            ApplyCommand(
                ExtractString(json, "command"),
                ExtractFloat(json, "value"),
                ExtractString(json, "user"),
                ExtractString(json, "source"),
                ExtractString(json, "message"));
            return;
        }
        if (type == "poll_state")
        {
            _lastPollTitle = ExtractString(json, "title");
            if (logPackets)
                Debug.Log($"[StreamBotUdp] poll_state title={_lastPollTitle} left={ExtractFloat(json, "seconds_left")}");
            return;
        }
        if (type == "stream_event")
        {
            _lastEvent = ExtractString(json, "message");
            if (logPackets)
                Debug.Log($"[StreamBotUdp] event {_lastEvent}");
        }
    }

    void ApplyCommand(string command, float value, string user, string source, string message)
    {
        if (string.IsNullOrEmpty(command))
            return;
        if (logPackets)
            Debug.Log($"[StreamBotUdp] cmd={command} value={value} user={user} source={source} msg={message}");

        // Маппинг whitelist stream_bot → существующие Twitch-хендлеры / локальные эффекты.
        switch (command)
        {
            case "add_zombie":
                TwitchChatGameBridge.SimulateCommand("add_zombie", Mathf.Clamp(Mathf.RoundToInt(value), 1, 10));
                break;
            case "food_rain":
                TwitchChatGameBridge.SimulateCommand("add_sheep", 3);
                TwitchChatGameBridge.SimulateCommand("add_tree", 2);
                break;
            case "night":
                DayNightCycle.ForceNightForSeconds(Mathf.Clamp(value, 10f, 120f));
                break;
            case "chaos":
                TwitchChatGameBridge.SimulateCommand("add_zombie", Mathf.Clamp(Mathf.RoundToInt(value / 10f) + 2, 2, 8));
                DayNightCycle.ForceNightForSeconds(Mathf.Clamp(value, 10f, 60f));
                break;
            case "reset":
            case "reset_vote":
                TwitchChatGameBridge.SimulateCommand("reset", 0);
                break;
            case "tree_reward":
                if (value >= 0)
                    TwitchChatGameBridge.SimulateCommand("add_tree", Mathf.Clamp(Mathf.RoundToInt(value), 0, 5));
                break;
            case "zombie_speed":
                // Нет отдельного API скорости зомби — логируем событие.
                _lastEvent = $"zombie_speed={value}";
                break;
            case "heal_agent":
                // Плейсхолдер: лёгкий buff через speed_up как «помощь»
                TwitchChatGameBridge.SimulateCommand("speed_up", 1);
                break;
            case "do_nothing":
                break;
            default:
                Debug.LogWarning($"[StreamBotUdp] unknown command (ignored): {command}");
                break;
        }
    }

    static string ExtractString(string json, string key)
    {
        string pattern = $"\"{key}\"";
        int i = json.IndexOf(pattern, StringComparison.Ordinal);
        if (i < 0)
            return "";
        int colon = json.IndexOf(':', i + pattern.Length);
        if (colon < 0)
            return "";
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0)
            return "";
        // number?
        char next = SkipWs(json, colon + 1);
        if (next != '"')
            return ExtractNumberToken(json, colon + 1);
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0)
            return "";
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    static float ExtractFloat(string json, string key)
    {
        string pattern = $"\"{key}\"";
        int i = json.IndexOf(pattern, StringComparison.Ordinal);
        if (i < 0)
            return 0f;
        int colon = json.IndexOf(':', i + pattern.Length);
        if (colon < 0)
            return 0f;
        string token = ExtractNumberToken(json, colon + 1);
        return float.TryParse(token, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }

    static string ExtractNumberToken(string json, int start)
    {
        int i = start;
        while (i < json.Length && char.IsWhiteSpace(json[i]))
            i++;
        int j = i;
        while (j < json.Length && "0123456789.-+eE".IndexOf(json[j]) >= 0)
            j++;
        return json.Substring(i, Mathf.Max(0, j - i));
    }

    static char SkipWs(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
            i++;
        return i < s.Length ? s[i] : '\0';
    }

    [Serializable]
    class StreamBotEnvelope
    {
        public string type;
        public string command;
        public float value;
        public string user;
        public string source;
        public string message;
        public string severity;
        public long timestamp;
        public string title;
        public float seconds_left;
    }
}
