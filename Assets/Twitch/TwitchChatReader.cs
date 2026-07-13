using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Читает Twitch-чат (IRC over SSL) и парсит команды вида #add_tree=5, #up=3, #forward=5, #zombie=2.
/// </summary>
public sealed class TwitchChatReader : MonoBehaviour
{
    const string IrcHost = "irc.chat.twitch.tv";
    const int IrcSslPort = 6697;
    const string AnonymousPass = "SCHMOOPIIE";

    [Header("Channel")]
    [SerializeField] private string channel = "mysticggx";
    [SerializeField] private bool connectOnPlay = true;
    [SerializeField] private bool loadConfigFromStreamingAssets = true;
    [SerializeField] private int connectRetries = 3;
    [SerializeField] private float retryDelaySeconds = 2f;

    [Header("Auth (optional)")]
    [Tooltip("Если true — вход justinfan (только чтение чата, без токена).")]
    [SerializeField] private bool useAnonymousLogin = true;
    [SerializeField] private string nickname = "";
    [SerializeField] private string oauthToken = "";

    [Header("Logging")]
    [SerializeField] private bool logAllChatMessages;
    [SerializeField] private bool logCommands = true;

    static TwitchChatReader _instance;

    readonly ConcurrentQueue<string> _incomingLines = new ConcurrentQueue<string>();
    readonly object _writeLock = new object();

    CancellationTokenSource _cts;
    TcpClient _tcp;
    SslStream _sslStream;
    StreamWriter _writer;
    StreamReader _reader;
    bool _connected;

    public static event Action<TwitchChatCommand> OnCommand;
    public static event Action<string, string> OnChatMessage;

    public static TwitchChatReader Instance => _instance;

    /// <summary>F в Game view — вкл/выкл выполнение команд чата.</summary>
    public static bool CommandsEnabled { get; set; } = true;

    [SerializeField] private KeyCode toggleCommandsKey = KeyCode.F;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.ShouldRunPresentationOnlyServices())
            return;

        if (_instance != null)
            return;

        if (FindObjectOfType<TwitchChatReader>() != null)
            return;

        var go = new GameObject(nameof(TwitchChatReader));
        DontDestroyOnLoad(go);
        go.AddComponent<TwitchChatReader>();
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

        if (loadConfigFromStreamingAssets)
            TryLoadConfig();
    }

    void Start()
    {
#if UNITY_EDITOR
        if (!connectOnPlay || !IsTruthyEnv(System.Environment.GetEnvironmentVariable("FOREST_TWITCH_CONNECT")))
            return;
#else
        if (!connectOnPlay)
            return;
#endif
        _ = ConnectAsync();
    }

    static bool IsTruthyEnv(string value) =>
        value == "1" || string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);

    void Update()
    {
        if (toggleCommandsKey != KeyCode.None && Input.GetKeyDown(toggleCommandsKey))
        {
            CommandsEnabled = !CommandsEnabled;
            Debug.Log($"[TwitchChat] Обработка команд: {(CommandsEnabled ? "ВКЛ" : "ВЫКЛ")} (клавиша {toggleCommandsKey})");
        }

        while (_incomingLines.TryDequeue(out string line))
            HandleIrcLine(line);
    }

    void OnDestroy()
    {
        Disconnect();
        if (_instance == this)
            _instance = null;
    }

    void OnApplicationQuit()
    {
        Disconnect();
    }

    public async Task ConnectAsync()
    {
        if (_connected)
            return;

        await DisconnectAsync();

        _cts = new CancellationTokenSource();
        string chan = NormalizeChannel(channel);
        string nick = ResolveNickname();
        string pass = ResolvePassword();

        for (int attempt = 1; attempt <= connectRetries; attempt++)
        {
            try
            {
                await ConnectTcpSslAsync(chan, nick, pass, _cts.Token);
                _connected = true;
                Debug.Log($"[TwitchChat] Подключились к #{chan} как {nick} ({IrcHost}:{IrcSslPort})");
                return;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TwitchChat] Попытка {attempt}/{connectRetries}: {ex.Message}");
                await DisconnectAsync();
                if (attempt < connectRetries && retryDelaySeconds > 0f)
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), _cts.Token);
            }
        }

        Debug.LogError(
            $"[TwitchChat] Не удалось подключиться к Twitch IRC. Проверьте интернет, VPN и доступ к {IrcHost}.");
    }

    async Task ConnectTcpSslAsync(string chan, string nick, string pass, CancellationToken token)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(IrcHost, IrcSslPort);

        _sslStream = new SslStream(_tcp.GetStream(), false);
        await _sslStream.AuthenticateAsClientAsync(
            IrcHost,
            null,
            SslProtocols.Tls12,
            false);

        _writer = new StreamWriter(_sslStream, new UTF8Encoding(false))
        {
            NewLine = "\r\n",
            AutoFlush = true
        };
        _reader = new StreamReader(_sslStream, Encoding.UTF8, false);

        SendLine("CAP REQ :twitch.tv/tags twitch.tv/commands");
        SendLine($"PASS {pass}");
        SendLine($"NICK {nick}");
        SendLine($"JOIN #{chan}");

        _ = Task.Run(() => ReadLoop(token), token);
    }

    void ReadLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _reader != null)
            {
                string line = _reader.ReadLine();
                if (line == null)
                    break;
                _incomingLines.Enqueue(line);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                Debug.LogWarning($"[TwitchChat] Соединение разорвано: {ex.Message}");
        }
        finally
        {
            _connected = false;
        }
    }

    void SendLine(string line)
    {
        if (_writer == null)
            return;

        lock (_writeLock)
        {
            _writer.WriteLine(line);
        }
    }

    public Task DisconnectAsync()
    {
        Disconnect();
        return Task.CompletedTask;
    }

    void Disconnect()
    {
        _connected = false;

        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        try { _reader?.Dispose(); } catch { /* ignore */ }
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _sslStream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Close(); } catch { /* ignore */ }

        _reader = null;
        _writer = null;
        _sslStream = null;
        _tcp = null;
    }

    void HandleIrcLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (line.StartsWith("PING", StringComparison.Ordinal))
        {
            SendLine("PONG " + line.Substring(4).Trim());
            return;
        }

        int privmsgIndex = line.IndexOf(" PRIVMSG ", StringComparison.Ordinal);
        if (privmsgIndex < 0)
            return;

        string tags = line.StartsWith("@", StringComparison.Ordinal)
            ? line.Substring(0, privmsgIndex)
            : string.Empty;

        string displayName = ExtractTag(tags, "display-name");
        string username = ExtractUsername(line, privmsgIndex, displayName);
        string message = ExtractTrailingMessage(line);
        if (string.IsNullOrEmpty(message))
            return;

        if (logAllChatMessages)
            Debug.Log($"[TwitchChat] <{displayName}> {message}");

        OnChatMessage?.Invoke(username, message);
        TryParseCommandsInMessage(username, displayName, message);
    }

    void TryParseCommandsInMessage(string username, string displayName, string message)
    {
        TryParseShowMetrics(username, displayName, message);

        int searchFrom = 0;
        while (searchFrom < message.Length)
        {
            int hash = message.IndexOf('#', searchFrom);
            if (hash < 0)
                break;

            if (!TryParseCommandToken(username, displayName, message, hash, out TwitchChatCommand cmd, out int tokenEnd))
            {
                searchFrom = hash + 1;
                continue;
            }

            DispatchCommand(cmd);
            searchFrom = tokenEnd;
        }
    }

    static void TryParseShowMetrics(string username, string displayName, string message)
    {
        int idx = message.IndexOf("#show", System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return;

        int pos = idx + 5;
        while (pos < message.Length && char.IsWhiteSpace(message[pos]))
            pos++;

        if (pos + 7 > message.Length)
            return;

        if (!message.Substring(pos, 7).Equals("metrics", System.StringComparison.OrdinalIgnoreCase))
            return;

        if (pos + 7 < message.Length && !char.IsWhiteSpace(message[pos + 7]))
            return;

        var cmd = new TwitchChatCommand(username, displayName, "#show metrics", "show_metrics", 0);
        if (_instance != null)
            _instance.DispatchCommand(cmd);
    }

    void DispatchCommand(TwitchChatCommand cmd)
    {
        if (!CommandsEnabled)
        {
            if (logCommands)
                Debug.Log($"[TwitchChat] команда проигнорирована (F=выкл): {cmd}");
            return;
        }

        if (logCommands)
            Debug.Log($"[TwitchChat] КОМАНДА: {cmd}");

        OnCommand?.Invoke(cmd);
    }

    static bool TryParseCommandToken(string username, string displayName, string message, int hashIndex,
        out TwitchChatCommand cmd, out int tokenEnd)
    {
        cmd = default;
        tokenEnd = hashIndex + 1;

        int start = hashIndex + 1;
        int end = start;
        while (end < message.Length && !char.IsWhiteSpace(message[end]))
            end++;

        tokenEnd = end;
        string token = message.Substring(start, end - start);
        int eq = token.IndexOf('=');

        string name;
        int value;
        if (eq < 0)
        {
            name = token.Trim().ToLowerInvariant();
            if (!IsCommandWithDefaultValue(name))
                return false;
            value = 1;
        }
        else
        {
            if (eq == 0)
                return false;

            name = token.Substring(0, eq).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name))
                return false;

            if (!int.TryParse(token.Substring(eq + 1).Trim(), out value))
                return false;
        }

        string raw = message.Substring(hashIndex, end - hashIndex);
        cmd = new TwitchChatCommand(username, displayName, raw, name, value);
        return true;
    }

    static bool IsCommandWithDefaultValue(string name)
    {
        switch (name)
        {
            case "add_tree":
            case "add_sheep":
            case "up":
            case "forward":
            case "zombie":
            case "clone_jack":
            case "size":
            case "speed_up":
            case "reset":
                return true;
            default:
                return false;
        }
    }

    static string ExtractTrailingMessage(string line)
    {
        int idx = line.LastIndexOf(" :", StringComparison.Ordinal);
        if (idx < 0 || idx + 2 >= line.Length)
            return string.Empty;
        return line.Substring(idx + 2);
    }

    static string ExtractUsername(string line, int privmsgIndex, string displayName)
    {
        if (!string.IsNullOrEmpty(displayName))
            return displayName;

        int nickStart = line.LastIndexOf(':', privmsgIndex - 1);
        if (nickStart < 0)
            return "unknown";

        string prefix = line.Substring(nickStart + 1, privmsgIndex - nickStart - 1);
        int bang = prefix.IndexOf('!');
        return bang > 0 ? prefix.Substring(0, bang) : prefix;
    }

    static string ExtractTag(string tags, string key)
    {
        if (string.IsNullOrEmpty(tags) || !tags.StartsWith("@", StringComparison.Ordinal))
            return string.Empty;

        string search = key + "=";
        int idx = tags.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0)
            return string.Empty;

        int start = idx + search.Length;
        int end = tags.IndexOf(';', start);
        if (end < 0)
            end = tags.Length;

        return tags.Substring(start, end - start).Replace("\\s", " ").Replace("\\:", ";");
    }

    void TryLoadConfig()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "twitch_chat.json");
        if (!File.Exists(path))
            return;

        try
        {
            var json = JsonUtility.FromJson<TwitchChatConfigJson>(File.ReadAllText(path));
            if (!string.IsNullOrWhiteSpace(json.channel))
                channel = json.channel;
            useAnonymousLogin = json.useAnonymousLogin;
            if (!string.IsNullOrWhiteSpace(json.nickname))
                nickname = json.nickname;
            if (!string.IsNullOrWhiteSpace(json.oauthToken))
                oauthToken = json.oauthToken;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TwitchChat] Не прочитан twitch_chat.json: {ex.Message}");
        }
    }

    string ResolveNickname()
    {
        if (!useAnonymousLogin && !string.IsNullOrWhiteSpace(nickname))
            return nickname.Trim();

        int suffix = UnityEngine.Random.Range(1000, 99999);
        return $"justinfan{suffix}";
    }

    string ResolvePassword()
    {
        if (!useAnonymousLogin && !string.IsNullOrWhiteSpace(oauthToken))
        {
            string token = oauthToken.Trim();
            return token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) ? token : "oauth:" + token;
        }

        return AnonymousPass;
    }

    static string NormalizeChannel(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "mysticggx";
        return raw.Trim().TrimStart('#').ToLowerInvariant();
    }

    [Serializable]
    class TwitchChatConfigJson
    {
        public string channel;
        public bool useAnonymousLogin = true;
        public string nickname;
        public string oauthToken;
    }
}
