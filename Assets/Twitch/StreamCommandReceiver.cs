using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// UDP JSON от Python stream_bot: stream_command / character_behavior / stream_event.
/// </summary>
public class StreamCommandReceiver : MonoBehaviour
{
    [SerializeField] protected int listenPort = 5055;
    [SerializeField] protected bool logPackets = true;

    static StreamCommandReceiver _instance;

    readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
    UdpClient _udp;
    Thread _thread;
    volatile bool _running;
    string _lastPollTitle = "";
    string _lastEvent = "";
    readonly List<string> _recentEvents = new List<string>(24);

    public static string LastPollTitle => _instance != null ? _instance._lastPollTitle : "";
    public static string LastEvent => _instance != null ? _instance._lastEvent : "";
    public static IReadOnlyList<string> RecentEvents =>
        _instance != null ? _instance._recentEvents : Array.Empty<string>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.ShouldRunPresentationOnlyServices())
            return;
        if (_instance != null)
            return;
        if (FindFirstObjectByType<StreamCommandReceiver>() != null)
            return;

        var go = new GameObject(nameof(StreamCommandReceiver));
        DontDestroyOnLoad(go);
        go.AddComponent<StreamCommandReceiver>();
    }

    protected virtual void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        string envPort = Environment.GetEnvironmentVariable("FOREST_STREAM_BOT_PORT");
        if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int p) && p > 0)
            listenPort = p;
    }

    void OnEnable() => StartListener();
    void OnDisable() => StopListener();

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
            _thread = new Thread(ListenLoop) { IsBackground = true, Name = "StreamCmdUdp" };
            _thread.Start();
            Debug.Log($"[StreamCommandReceiver] listening UDP :{listenPort}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[StreamCommandReceiver] bind failed :{listenPort} — {e.Message}");
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
                if (!_running) break;
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception e)
            {
                Debug.LogWarning($"[StreamCommandReceiver] recv: {e.Message}");
            }
        }
    }

    void HandleJson(string json)
    {
        try
        {
            Debug.Log($"[StreamCommandReceiver] JSON: {json}");
            string type = ExtractString(json, "type");
            switch (type)
            {
                case "stream_command":
                    ApplyCommand(
                        ExtractString(json, "command"),
                        ExtractFloat(json, "value"),
                        ExtractString(json, "user"),
                        ExtractString(json, "source"),
                        ExtractString(json, "message"));
                    break;
                case "streaming_survival_user_joined":
                    ApplySsJoin(ExtractString(json, "username"));
                    break;
                case "streaming_survival_user_left":
                    ApplySsLeave(ExtractString(json, "username"));
                    break;
                case "streaming_survival_action":
                    ApplySsAction(json);
                    break;
                case "streaming_survival_users_sync":
                    ApplySsSync(json);
                    break;
                case "streaming_survival_test":
                    ApplySsTest(json);
                    break;
                case "character_behavior":
                case "behavior_program": // legacy
                    if (TrainingEnvSpace.IsStreamingSurvivalMode)
                        break;
                    ApplyCharacterBehavior(json);
                    break;
                case "stream_event":
                    NoteEvent(ExtractString(json, "message"), ExtractString(json, "severity"));
                    break;
                case "poll_state":
                    // голосования отключены в MVP
                    break;
                default:
                    if (logPackets)
                        Debug.LogWarning($"[StreamCommandReceiver] unknown type={type}");
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[StreamCommandReceiver] parse: {e.Message}");
        }
    }

    void ApplySsJoin(string user)
    {
        if (!TrainingEnvSpace.IsStreamingSurvivalMode)
            return;
        var c = StreamingSurvivalController.Instance;
        if (c == null) return;
        var p = c.EnsurePlayer(user);
        // #join always places at follower spawn — even on re-join when the
        // body already exists (otherwise leftover pond/home positions stick).
        if (p != null)
        {
            p.TeleportTo(c.FollowerSpawnWorld, "join_spawn");
            Debug.Log($"[SSJoin] force_spawn_on_join user={user} pos={c.FollowerSpawnWorld}");
        }
        // Default after #join is idle until an explicit #do / action packet.
        c.ApplyAction(user, "idle", "Ждёт у базы", 1, null);
        NoteEvent($"{user} вошёл в Streaming Survival", "info");
    }

    void ApplySsLeave(string user)
    {
        if (!TrainingEnvSpace.IsStreamingSurvivalMode)
            return;
        var c = StreamingSurvivalController.Instance;
        if (c == null) return;
        c.RemovePlayer(user);
        NoteEvent($"{user} вышел из Streaming Survival", "info");
    }

    void ApplySsAction(string json)
    {
        if (!TrainingEnvSpace.IsStreamingSurvivalMode)
            return;
        var c = StreamingSurvivalController.Instance;
        if (c == null) return;
        string user = ExtractString(json, "username");
        string action = ExtractString(json, "action");
        string name = ExtractString(json, "action_name");
        string queue = ExtractString(json, "action_queue");
        int amount = ExtractInt(json, "amount", 1);
        if (amount < 1) amount = 1;
        c.ApplyAction(user, action, name, amount, queue);
        NoteEvent($"{user}: {name}", "info");
    }

    void ApplySsTest(string json)
    {
        if (!TrainingEnvSpace.IsStreamingSurvivalMode)
            return;
        var ctrl = StreamingSurvivalController.Instance;
        if (ctrl == null) return;
        var runner = ctrl.GetComponent<StreamingSurvivalScenarioRunner>();
        if (runner == null)
            runner = ctrl.gameObject.AddComponent<StreamingSurvivalScenarioRunner>();

        string runId = ExtractString(json, "run_id");
        string outputDir = ExtractString(json, "output_dir");
        if (string.IsNullOrEmpty(outputDir))
            outputDir = ExtractString(json, "artifacts_dir");
        runner.ConfigureOutput(runId, outputDir);

        string cmd = ExtractString(json, "cmd");
        if (string.IsNullOrEmpty(cmd))
            cmd = ExtractString(json, "command");
        Debug.Log($"[SSTest] cmd={cmd} run_id={runId}");
        if (cmd == "ping")
        {
            runner.WritePing();
            // Also dump landmarks for trajectory charts (water/home/trees/fences).
            runner.WriteWorldMap();
            return;
        }
        if (cmd == "dump_world_map")
        {
            runner.WriteWorldMap();
            return;
        }
        if (cmd == "world_registry_self_test")
        {
            string result = runner.RunWorldRegistrySelfTest();
            Debug.Log($"[SSTest] WORLD_REGISTRY_JSON {result}");
            return;
        }
        if (cmd == "run_scenario")
        {
            int idx = json.IndexOf("\"scenario\"", System.StringComparison.Ordinal);
            string scJson = json;
            if (idx >= 0)
            {
                int brace = json.IndexOf('{', idx);
                int end = FindBraceEnd(json, brace);
                if (brace >= 0 && end > brace)
                    scJson = json.Substring(brace, end - brace + 1);
            }
            var sc = StreamingSurvivalScenarioRunner.ParseScenarioJson(scJson);
            if (string.IsNullOrEmpty(sc.Id))
                sc.Id = ExtractString(json, "scenario_id");
            runner.RunScenarioAsync(sc);
            return;
        }
        if (cmd == "test_join_idempotent")
        {
            runner.RunJoinIdempotentTest();
            return;
        }
        if (cmd == "test_action_cooldown")
        {
            runner.RunActionCooldownTest();
            return;
        }
        if (cmd == "test_join_default_idle")
        {
            runner.RunJoinDefaultIdleTest();
            return;
        }
        if (cmd == "clear_all_players" || cmd == "reset_session")
        {
            // Live-stress / QA: empty world — no leftover viewers from prior runs.
            ctrl.ClearAllPlayers();
            // Long hold so ROUND_LOSE does not interrupt multi-step water→campfire chains.
            float hold = ExtractFloat(json, "hold_seconds");
            if (hold < 30f)
                hold = 900f;
            ctrl.ResetResourcesForTest(hold);
            if (StreamingSurvivalWorldRegistry.Instance != null)
                StreamingSurvivalWorldRegistry.Instance.Rebuild();
            Debug.Log($"[SSTest] clear_all_players (empty session) hold={hold:F0}s");
            return;
        }
        if (cmd == "reset_player_to_spawn")
        {
            string user = ExtractString(json, "username");
            if (string.IsNullOrEmpty(user) || ctrl == null)
                return;
            // Do NOT EnsurePlayer here — that would spawn someone before #join.
            var p = ctrl.GetPlayer(user);
            if (p == null)
            {
                Debug.Log($"[SSTest] reset_player_to_spawn skip (not in world) user={user}");
                return;
            }
            // Idle + hard reset to follower spawn so leftover pond positions
            // from prior runs do not poison live-stress trajectories.
            p.SetAction("idle", "Ждёт у базы", 1, null);
            p.ControlledTeleport(ctrl.FollowerSpawnWorld, "scenario_setup");
            Debug.Log($"[SSTest] reset_player_to_spawn user={user}");
            return;
        }
        if (cmd == "begin_live_trajectory")
        {
            string attemptId = ExtractString(json, "attempt_id");
            if (string.IsNullOrEmpty(attemptId))
                attemptId = ExtractString(json, "scenario_id");
            string user = ExtractString(json, "username");
            runner.EnsureHooks();
            runner.EnsureOutputReadyPublic();
            var rec = StreamingSurvivalTrajectoryRecorder.Instance;
            if (rec != null)
                rec.BeginLiveTrajectory(attemptId, user);
            return;
        }
        if (cmd == "end_live_trajectory")
        {
            var rec = StreamingSurvivalTrajectoryRecorder.Instance;
            if (rec != null)
                rec.EndLiveTrajectory(
                    string.IsNullOrEmpty(ExtractString(json, "event"))
                        ? "live_end"
                        : ExtractString(json, "event"));
            string attemptId = ExtractString(json, "attempt_id");
            if (string.IsNullOrEmpty(attemptId))
                attemptId = ExtractString(json, "scenario_id");
            if (!string.IsNullOrEmpty(attemptId))
            {
                string path = System.IO.Path.Combine(
                    runner.EffectiveOutputDirPublic(),
                    attemptId + "_result.json");
                System.IO.File.WriteAllText(
                    path,
                    "{\"id\":\"" + attemptId + "\",\"overall\":\"PASS\",\"ok\":true,\"reason\":\"live_end\"}");
            }
            return;
        }
        if (cmd == "reset_resources")
        {
            ctrl.ResetResourcesForTest();
            return;
        }
        Debug.LogWarning($"[SSTest] unknown cmd={cmd}");
    }

    static int ExtractInt(string json, string key, int fallback)
    {
        string s = ExtractString(json, key);
        if (int.TryParse(s, out int v)) return v;
        // "amount":10 without quotes
        string needle = "\"" + key + "\"";
        int i = json.IndexOf(needle, System.StringComparison.Ordinal);
        if (i < 0) return fallback;
        int colon = json.IndexOf(':', i + needle.Length);
        if (colon < 0) return fallback;
        int j = colon + 1;
        while (j < json.Length && (json[j] == ' ' || json[j] == '\t')) j++;
        int k = j;
        while (k < json.Length && ((json[k] >= '0' && json[k] <= '9') || json[k] == '-')) k++;
        if (k > j && int.TryParse(json.Substring(j, k - j), out int n)) return n;
        return fallback;
    }

    void ApplySsSync(string json)
    {
        if (!TrainingEnvSpace.IsStreamingSurvivalMode)
            return;
        var c = StreamingSurvivalController.Instance;
        if (c == null) return;
        // простой разбор массива users по username
        var list = new System.Collections.Generic.List<StreamingSurvivalUserDto>();
        int search = 0;
        while (true)
        {
            int uIdx = json.IndexOf("\"username\"", search, System.StringComparison.Ordinal);
            if (uIdx < 0) break;
            int objStart = json.LastIndexOf('{', uIdx);
            int objEnd = FindBraceEnd(json, objStart);
            if (objStart < 0 || objEnd < 0)
            {
                search = uIdx + 10;
                continue;
            }
            string chunk = json.Substring(objStart, objEnd - objStart + 1);
            list.Add(new StreamingSurvivalUserDto
            {
                username = ExtractString(chunk, "username"),
                action = ExtractString(chunk, "action"),
                action_name = ExtractString(chunk, "action_name"),
            });
            search = objEnd + 1;
        }
        c.SyncUsers(list);
    }

    static int FindBraceEnd(string s, int openIdx)
    {
        if (openIdx < 0 || openIdx >= s.Length || s[openIdx] != '{') return -1;
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    void ApplyCharacterBehavior(string json)
    {
        string user = ExtractString(json, "username");
        if (string.IsNullOrEmpty(user))
            user = ExtractString(json, "agent_name");
        string name = ExtractString(json, "behavior_name");
        string desc = ExtractString(json, "description");
        string species = ExtractString(json, "species");
        if (string.IsNullOrEmpty(species))
            species = "sheep";
        int level = Mathf.Max(1, Mathf.RoundToInt(ExtractFloat(json, "level")));
        if (logPackets)
            Debug.Log($"[StreamCommandReceiver] character_behavior user={user} species={species} name={name}");

        var agent = ViewerSimpleAgent.FindOrSpawn(user);
        if (agent != null)
            agent.ApplyCharacterBehavior(user, species, name, level, json);

        ViewerAgentBehavior.ApplyOrPending(user, name, desc, json);
        NoteEvent($"{user}: {name}", "info");
    }

    void NoteEvent(string message, string severity)
    {
        _lastEvent = message ?? "";
        if (!string.IsNullOrEmpty(_lastEvent))
        {
            _recentEvents.Insert(0, _lastEvent);
            while (_recentEvents.Count > 20)
                _recentEvents.RemoveAt(_recentEvents.Count - 1);
        }
        if (logPackets)
            Debug.Log($"[StreamCommandReceiver] event [{severity}] {_lastEvent}");
    }

    void ApplyCommand(string command, float value, string user, string source, string message)
    {
        if (string.IsNullOrEmpty(command))
            return;
        if (logPackets)
            Debug.Log($"[StreamCommandReceiver] cmd={command} value={value} user={user} source={source}");

        // MVP: только join персонажа. Зомби/night/chaos/reset из чата отключены.
        switch (command)
        {
            case "viewer_join":
                if (TrainingEnvSpace.IsStreamingSurvivalMode)
                {
                    ApplySsJoin(user);
                    break;
                }
                ViewerSimpleAgent.FindOrSpawn(user);
                NoteEvent($"{user} добавил персонажа", "info");
                break;
            case "viewer_delete":
                // публичная команда отключена; оставляем на случай локального теста
                if (ViewerSimpleAgent.Despawn(user))
                    NoteEvent($"{user} удалил персонажа", "info");
                break;
            case "do_nothing":
                break;
            default:
                if (logPackets)
                    Debug.Log($"[StreamCommandReceiver] ignored world cmd in MVP: {command}");
                break;
        }

        if (!string.IsNullOrEmpty(message))
            NoteEvent(message, "info");
    }

    static string ExtractString(string json, string key)
    {
        string pattern = $"\"{key}\"";
        int i = json.IndexOf(pattern, StringComparison.Ordinal);
        if (i < 0) return "";
        int colon = json.IndexOf(':', i + pattern.Length);
        if (colon < 0) return "";
        char next = SkipWs(json, colon + 1);
        if (next != '"')
            return ExtractNumberToken(json, colon + 1);
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return "";
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return "";
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    static float ExtractFloat(string json, string key)
    {
        string pattern = $"\"{key}\"";
        int i = json.IndexOf(pattern, StringComparison.Ordinal);
        if (i < 0) return 0f;
        int colon = json.IndexOf(':', i + pattern.Length);
        if (colon < 0) return 0f;
        string token = ExtractNumberToken(json, colon + 1);
        return float.TryParse(token, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }

    static string ExtractNumberToken(string json, int start)
    {
        int i = start;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        int j = i;
        while (j < json.Length && "0123456789.-+eE".IndexOf(json[j]) >= 0) j++;
        return json.Substring(i, Mathf.Max(0, j - i));
    }

    static char SkipWs(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i < s.Length ? s[i] : '\0';
    }
}
