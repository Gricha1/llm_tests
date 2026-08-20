using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Периодический снимок presentation/stream Env: деревья, овцы, герои, зомби, спавнеры.
/// Пишет в results/stream_world_snapshot.log — чтобы ловить пропажи без догадок.
/// Плюс results/stream_world_live.json для UI (карта + KPI).
/// </summary>
public sealed class PresentationWorldSnapshotLogger : MonoBehaviour
{
    const string FileName = "stream_world_snapshot.log";
    const string LiveJsonName = "stream_world_live.json";
    const float IntervalSeconds = 10f;
    const float CountDropWarnFraction = 0.5f;

    static PresentationWorldSnapshotLogger _instance;
    static string _logPath;
    static string _liveJsonPath;
    static readonly object _ioLock = new object();

    float _nextSnapshotUnscaled;
    int _prevTrees = -1;
    int _prevSheep = -1;
    int _prevZombies = -1;

    // Счётчики текущего эпизода (между reset_spawners).
    bool _epActive;
    int _epWaterGained;
    int _epWoodGained;
    int _epSheepKilled;
    int _epTreesChopped;
    int _prevJackWater = -1, _prevLilyWater = -1, _prevGeorgeWater = -1;
    int _prevJackWood = -1, _prevGeorgeWood = -1;
    int _prevTreeN = -1, _prevSheepN = -1;

    // Последний завершённый раунд (для UI).
    static int _lastRoundWater;
    static int _lastRoundWood;
    static int _lastRoundSheep;
    static int _lastRoundTrees;
    static string _lastRoundAt = "";
    static int _episodeIndex; // 1-based текущий эпизод с момента старта процесса
    static int _lastRoundEpisode;

    // Статичные ориентиры карты (дом / озеро / забор / камни) — кэш на процесс.
    static Landmarks _landmarksCache;
    static bool _landmarksReady;

    struct Landmarks
    {
        public Vector2 house;
        public bool hasHouse;
        public List<RectXZ> lakes;
        public List<Vector2> stones;
        public List<RectXZ> fences;
    }

    struct RectXZ
    {
        public float x, z, sx, sz;
        public RectXZ(float x, float z, float sx, float sz)
        {
            this.x = x; this.z = z; this.sx = sx; this.sz = sz;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!ShouldEnable())
            return;
        if (_instance != null)
            return;

        var go = new GameObject(nameof(PresentationWorldSnapshotLogger));
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<PresentationWorldSnapshotLogger>();
    }

    static bool ShouldEnable()
    {
        if (TrainingEnvSpace.IsHeadlessTrainWorkerProcess)
            return false;
        return TrainingEnvSpace.ShouldRunPresentationOnlyServices()
            || TrainingEnvSpace.IsStreamOnlyMode
            || TrainingEnvSpace.IsValidateOrInferenceMode;
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
        _nextSnapshotUnscaled = Time.unscaledTime + 2f;
        LogEvent("logger_start", $"path={ResolveLogPath()}");
    }

    void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    void Update()
    {
        if (Time.unscaledTime < _nextSnapshotUnscaled)
            return;
        _nextSnapshotUnscaled = Time.unscaledTime + IntervalSeconds;
        WriteSnapshot("tick");
    }

    /// <summary>Короткая запись без полного снимка (watchdog / refill).</summary>
    public static void Note(string kind, string detail = null)
    {
        if (!ShouldEnable() && _instance == null)
            return;

        var line = new StringBuilder(192);
        line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        line.Append(" [NOTE] ").Append(kind ?? "note");
        if (!string.IsNullOrEmpty(detail))
            line.Append(' ').Append(detail);
        AppendLine(line.ToString());
    }

    /// <summary>Событие (reset / twitch / episode) + сразу полный снимок.</summary>
    public static void LogEvent(string kind, string detail = null)
    {
        if (!ShouldEnable() && _instance == null)
            return;

        var line = new StringBuilder(256);
        line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        line.Append(" [EVENT] ").Append(kind ?? "event");
        if (!string.IsNullOrEmpty(detail))
            line.Append(' ').Append(detail);
        AppendLine(line.ToString());
        WriteSnapshot(kind ?? "event");
    }

    static void WriteSnapshot(string reason)
    {
        var env = TrainingEnvSpace.ActiveViewEnvRoot ?? TrainingEnvSpace.PresentationRoot;
        if (env == null)
        {
            AppendLine($"{Stamp()} [SNAP:{reason}] env=null");
            WriteLiveJson(null);
            return;
        }

        bool isReset = !string.IsNullOrEmpty(reason) && reason.IndexOf("reset", StringComparison.OrdinalIgnoreCase) >= 0;
        if (isReset && _instance != null)
            _instance.FinalizeEpisodeRound();

        // ep= пишем как номер ТЕКУЩЕГО эпизода (после Begin на reset / первом tick).
        int epForHeader = Mathf.Max(1, _episodeIndex);
        if (_instance != null)
        {
            if (isReset || !_instance._epActive)
                epForHeader = Mathf.Max(0, _episodeIndex) + 1;
        }

        var sb = new StringBuilder(2048);
        sb.Append(Stamp());
        sb.Append(" [SNAP:").Append(reason).Append("] env=").Append(env.name);
        sb.Append(" ep=").Append(epForHeader);
        sb.Append(" t=").Append(Time.time.ToString("0.0"));
        sb.Append(" ut=").Append(Time.unscaledTime.ToString("0.0"));

        var treeSpawner = env.GetComponentInChildren<TreeSpawner>(true);
        var sheepSpawner = env.GetComponentInChildren<SheepSpawner>(true);
        var flowerSpawner = env.GetComponentInChildren<FlowerSpawner>(true);
        var zombieSpawners = env.GetComponentsInChildren<ZombieSpawner>(true);

        AppendSpawner(sb, "treeSpawner", treeSpawner != null ? treeSpawner.transform : null);
        AppendSpawner(sb, "sheepSpawner", sheepSpawner != null ? sheepSpawner.transform : null);
        AppendSpawner(sb, "flowerSpawner", flowerSpawner != null ? flowerSpawner.transform : null);
        if (zombieSpawners != null)
        {
            for (int i = 0; i < zombieSpawners.Length; i++)
            {
                var zs = zombieSpawners[i];
                if (zs == null)
                    continue;
                AppendSpawner(sb, "zombieSpawner:" + zs.name, zs.transform);
            }
        }

        var jack = TrainingEnvSpace.FindPresentationPrimaryJack()
            ?? TrainingEnvSpace.FindPrimaryJackInEnv(env);
        var lily = TrainingEnvSpace.FindPresentationLily();
        var george = TrainingEnvSpace.FindPresentationGeorge();

        AppendAgent(sb, "Jack", jack != null ? jack.transform : null, jack);
        AppendAgent(sb, "Lily", lily != null ? lily.transform : null, null, lily);
        AppendAgent(sb, "George", george != null ? george.transform : null, george);

        var trees = env.GetComponentsInChildren<ChoppableTree>(true);
        var treePts = new List<Vector2>(32);
        int treeN = 0;
        sb.Append("\n  trees=");
        if (trees != null)
        {
            for (int i = 0; i < trees.Length; i++)
            {
                var t = trees[i];
                if (t == null || !t.gameObject.activeSelf)
                    continue;
                if (treeN == 0)
                    sb.Append('[');
                else
                    sb.Append(' ');
                AppendXz(sb, t.transform.position);
                treePts.Add(new Vector2(t.transform.position.x, t.transform.position.z));
                treeN++;
            }
        }
        if (treeN == 0)
            sb.Append("[]");
        else
            sb.Append(']');
        sb.Append(" n=").Append(treeN);
        int treeTarget = treeSpawner != null ? treeSpawner.TargetCount : 0;
        if (treeSpawner != null)
            sb.Append('/').Append(treeTarget).Append("(spawnerAlive=")
                .Append(treeSpawner.AliveCount).Append(')');

        var sheep = env.GetComponentsInChildren<SheepWander>(true);
        var sheepPts = new List<Vector2>(16);
        int sheepN = 0;
        sb.Append("\n  sheep=");
        if (sheep != null)
        {
            for (int i = 0; i < sheep.Length; i++)
            {
                var s = sheep[i];
                if (s == null || !s.gameObject.activeSelf)
                    continue;
                if (sheepN == 0)
                    sb.Append('[');
                else
                    sb.Append(' ');
                AppendXz(sb, s.transform.position);
                sheepPts.Add(new Vector2(s.transform.position.x, s.transform.position.z));
                sheepN++;
            }
        }
        if (sheepN == 0)
            sb.Append("[]");
        else
            sb.Append(']');
        sb.Append(" n=").Append(sheepN);
        int sheepTarget = sheepSpawner != null ? sheepSpawner.TargetCount : 0;
        if (sheepSpawner != null)
            sb.Append('/').Append(sheepTarget).Append("(spawnerAlive=")
                .Append(sheepSpawner.AliveCount).Append(')');

        var zombies = env.GetComponentsInChildren<ZombieHealth>(true);
        var zombiePts = new List<Vector2>(16);
        int zN = 0;
        sb.Append("\n  zombies=");
        if (zombies != null)
        {
            for (int i = 0; i < zombies.Length; i++)
            {
                var z = zombies[i];
                if (z == null || !z.gameObject.activeSelf)
                    continue;
                if (zN == 0)
                    sb.Append('[');
                else
                    sb.Append(' ');
                AppendXz(sb, z.transform.position);
                sb.Append(" hp=").Append(z.Hp);
                zombiePts.Add(new Vector2(z.transform.position.x, z.transform.position.z));
                zN++;
            }
        }
        if (zN == 0)
            sb.Append("[]");
        else
            sb.Append(']');
        sb.Append(" n=").Append(zN);

        string hud = RewardDisplay.DescribeHudHealth();
        sb.Append("\n  hud_reward ").Append(hud);
        if (CountHudRole(hud, "jack") > 1 || CountHudRole(hud, "lily") > 1
            || CountHudRole(hud, "george") > 1)
        {
            AppendLine($"{Stamp()} [WARN] hud_reward_duplicate {hud}");
            RewardDisplay.DeduplicateAll();
        }

        var landmarks = EnsureLandmarks(env);
        AppendLandmarks(sb, landmarks);

        AppendLine(sb.ToString());

        int jackWater = jack != null ? jack.water : -1;
        int jackWood = jack != null ? jack.wood : -1;
        int lilyWater = lily != null ? lily.WaterCount : -1;
        int georgeWater = george != null ? george.water : -1;
        int georgeWood = george != null ? george.wood : -1;

        if (_instance != null)
        {
            _instance.MaybeWarnCountDrop("trees", ref _instance._prevTrees, treeN);
            _instance.MaybeWarnCountDrop("sheep", ref _instance._prevSheep, sheepN);
            _instance.MaybeWarnCountDrop("zombies", ref _instance._prevZombies, zN);
            if (isReset)
                _instance.BeginEpisodeRound(jackWater, jackWood, lilyWater, georgeWater, georgeWood, treeN, sheepN);
            else
                _instance.AccumulateEpisode(jackWater, jackWood, lilyWater, georgeWater, georgeWood, treeN, sheepN);
        }

        var live = new LiveSnapshot
        {
            reason = reason ?? "tick",
            env = env.name,
            t = Time.time,
            ut = Time.unscaledTime,
            trees_n = treeN,
            trees_target = treeTarget,
            sheep_n = sheepN,
            sheep_target = sheepTarget,
            zombies_n = zN,
            trees = treePts,
            sheep = sheepPts,
            zombies = zombiePts,
            landmarks = landmarks,
            jack = MakeAgentLive("Jack", jack != null ? jack.transform : null, jackWater, jackWood, jack != null ? jack.hp : -1, jack != null && jack.IsInDeathState),
            lily = MakeAgentLive("Lily", lily != null ? lily.transform : null, lilyWater, -1, lily != null ? lily.Hp : -1, lily != null && lily.IsInDeathState),
            george = MakeAgentLive("George", george != null ? george.transform : null, georgeWater, georgeWood, george != null ? george.hp : -1, george != null && george.IsInDeathState),
        };
        WriteLiveJson(live);
    }

    struct LiveAgent
    {
        public string name;
        public bool ok;
        public float x, z, y;
        public int hp, water, wood;
        public bool dead;
    }

    struct LiveSnapshot
    {
        public string reason, env;
        public float t, ut;
        public int trees_n, trees_target, sheep_n, sheep_target, zombies_n;
        public List<Vector2> trees, sheep, zombies;
        public Landmarks landmarks;
        public LiveAgent jack, lily, george;
    }

    static LiveAgent MakeAgentLive(string name, Transform t, int water, int wood, int hp, bool dead)
    {
        var a = new LiveAgent { name = name, ok = t != null, water = water, wood = wood, hp = hp, dead = dead };
        if (t != null)
        {
            a.x = t.position.x;
            a.z = t.position.z;
            a.y = t.position.y;
        }
        return a;
    }

    void FinalizeEpisodeRound()
    {
        if (!_epActive)
            return;
        _lastRoundWater = _epWaterGained;
        _lastRoundWood = _epWoodGained;
        _lastRoundSheep = _epSheepKilled;
        _lastRoundTrees = _epTreesChopped;
        _lastRoundAt = Stamp();
        _lastRoundEpisode = Mathf.Max(1, _episodeIndex);
        _epActive = false;
    }

    void BeginEpisodeRound(int jW, int jWood, int lW, int gW, int gWood, int trees, int sheep)
    {
        _episodeIndex = Mathf.Max(0, _episodeIndex) + 1;
        _epActive = true;
        _epWaterGained = 0;
        _epWoodGained = 0;
        _epSheepKilled = 0;
        _epTreesChopped = 0;
        _prevJackWater = jW;
        _prevLilyWater = lW;
        _prevGeorgeWater = gW;
        _prevJackWood = jWood;
        _prevGeorgeWood = gWood;
        _prevTreeN = trees;
        _prevSheepN = sheep;
    }

    void AccumulateEpisode(int jW, int jWood, int lW, int gW, int gWood, int trees, int sheep)
    {
        if (!_epActive)
        {
            BeginEpisodeRound(jW, jWood, lW, gW, gWood, trees, sheep);
            return;
        }
        Gain(ref _epWaterGained, ref _prevJackWater, jW);
        Gain(ref _epWaterGained, ref _prevLilyWater, lW);
        Gain(ref _epWaterGained, ref _prevGeorgeWater, gW);
        Gain(ref _epWoodGained, ref _prevJackWood, jWood);
        Gain(ref _epWoodGained, ref _prevGeorgeWood, gWood);
        if (_prevSheepN >= 0 && sheep >= 0 && sheep < _prevSheepN)
            _epSheepKilled += (_prevSheepN - sheep);
        if (_prevTreeN >= 0 && trees >= 0 && trees < _prevTreeN)
            _epTreesChopped += (_prevTreeN - trees);
        _prevSheepN = sheep;
        _prevTreeN = trees;
    }

    static void Gain(ref int acc, ref int prev, int now)
    {
        if (now < 0)
            return;
        if (prev >= 0 && now > prev)
            acc += (now - prev);
        prev = now;
    }

    static void WriteLiveJson(LiveSnapshot? snapOpt)
    {
        try
        {
            string path = ResolveLiveJsonPath();
            if (string.IsNullOrEmpty(path))
                return;

            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"ts\":\"").Append(EscapeJson(Stamp())).Append('"');
            sb.Append(",\"ok\":").Append(snapOpt.HasValue ? "true" : "false");
            if (_instance != null)
            {
                sb.Append(",\"episode\":{");
                sb.Append("\"index\":").Append(Mathf.Max(1, _episodeIndex));
                sb.Append(",\"water\":").Append(_instance._epWaterGained);
                sb.Append(",\"wood\":").Append(_instance._epWoodGained);
                sb.Append(",\"sheep_killed\":").Append(_instance._epSheepKilled);
                sb.Append(",\"trees_chopped\":").Append(_instance._epTreesChopped);
                sb.Append('}');
            }
            sb.Append(",\"episode_index\":").Append(Mathf.Max(1, _episodeIndex));
            sb.Append(",\"last_round\":{");
            sb.Append("\"episode\":").Append(_lastRoundEpisode);
            sb.Append(",\"water\":").Append(_lastRoundWater);
            sb.Append(",\"wood\":").Append(_lastRoundWood);
            sb.Append(",\"sheep_killed\":").Append(_lastRoundSheep);
            sb.Append(",\"trees_chopped\":").Append(_lastRoundTrees);
            sb.Append(",\"at\":\"").Append(EscapeJson(_lastRoundAt)).Append('"');
            sb.Append('}');

            if (snapOpt.HasValue)
            {
                var s = snapOpt.Value;
                sb.Append(",\"reason\":\"").Append(EscapeJson(s.reason)).Append('"');
                sb.Append(",\"env\":\"").Append(EscapeJson(s.env)).Append('"');
                sb.Append(",\"t\":").Append(s.t.ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append(",\"trees_n\":").Append(s.trees_n);
                sb.Append(",\"trees_target\":").Append(s.trees_target);
                sb.Append(",\"sheep_n\":").Append(s.sheep_n);
                sb.Append(",\"sheep_target\":").Append(s.sheep_target);
                sb.Append(",\"zombies_n\":").Append(s.zombies_n);
                AppendPtsJson(sb, "trees", s.trees);
                AppendPtsJson(sb, "sheep", s.sheep);
                AppendPtsJson(sb, "zombies", s.zombies);
                AppendLandmarksJson(sb, s.landmarks);
                AppendAgentJson(sb, "jack", s.jack);
                AppendAgentJson(sb, "lily", s.lily);
                AppendAgentJson(sb, "george", s.george);
            }
            sb.Append('}');

            lock (_ioLock)
            {
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PresentationWorldSnapshotLogger] live json failed: {e.Message}");
        }
    }

    static void AppendPtsJson(StringBuilder sb, string key, List<Vector2> pts)
    {
        sb.Append(",\"").Append(key).Append("\":[");
        if (pts != null)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"x\":").Append(pts[i].x.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(",\"z\":").Append(pts[i].y.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append('}');
            }
        }
        sb.Append(']');
    }

    static Landmarks EnsureLandmarks(Transform env)
    {
        bool needRefresh = !_landmarksReady
            || !_landmarksCache.hasHouse
            || _landmarksCache.fences == null
            || _landmarksCache.fences.Count == 0;
        if (!needRefresh)
            return _landmarksCache;
        _landmarksCache = CollectLandmarks(env);
        _landmarksReady = _landmarksCache.hasHouse
            || (_landmarksCache.lakes != null && _landmarksCache.lakes.Count > 0)
            || (_landmarksCache.stones != null && _landmarksCache.stones.Count > 0)
            || (_landmarksCache.fences != null && _landmarksCache.fences.Count > 0);
        return _landmarksCache;
    }

    static Landmarks CollectLandmarks(Transform env)
    {
        var lm = new Landmarks
        {
            lakes = new List<RectXZ>(4),
            stones = new List<Vector2>(24),
            fences = new List<RectXZ>(128),
        };

        // Дом
        Transform home = null;
        if (env != null)
        {
            home = env.Find("HomeSpot");
            if (home == null)
            {
                foreach (var t in env.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == "HomeSpot")
                    {
                        home = t;
                        break;
                    }
                }
            }
        }
        if (home == null)
        {
            var go = GameObject.Find("HomeSpot");
            if (go != null) home = go.transform;
        }
        if (home != null)
        {
            lm.house = new Vector2(home.position.x, home.position.z);
            lm.hasHouse = true;
        }

        // Озеро: WaterSource + GoalWater3 (северное озеро)
        var sources = env != null
            ? env.GetComponentsInChildren<WaterSource>(true)
            : FindObjectsByType<WaterSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (sources != null)
        {
            for (int i = 0; i < sources.Length; i++)
            {
                var ws = sources[i];
                if (ws == null) continue;
                Vector3 p = ws.transform.position;
                if (p.z < 24f) continue; // южные маркеры воды пропускаем
                ws.EnsureCollider();
                var col = ws.GetComponent<Collider>();
                float sx = 6f, sz = 6f;
                if (col != null)
                {
                    Bounds b = col.bounds;
                    p = b.center;
                    sx = Mathf.Max(2f, b.size.x);
                    sz = Mathf.Max(2f, b.size.z);
                }
                lm.lakes.Add(new RectXZ(p.x, p.z, sx, sz));
            }
        }
        if (lm.lakes.Count == 0)
        {
            var g3 = GameObject.Find("GoalWater3");
            if (g3 != null)
            {
                Vector3 p = g3.transform.position;
                var col = g3.GetComponent<Collider>();
                float sx = 8f, sz = 6f;
                if (col != null)
                {
                    Bounds b = col.bounds;
                    p = b.center;
                    sx = Mathf.Max(4f, b.size.x);
                    sz = Mathf.Max(4f, b.size.z);
                }
                lm.lakes.Add(new RectXZ(p.x, p.z, sx, sz));
            }
        }

        // Камни
        Transform[] all = env != null
            ? env.GetComponentsInChildren<Transform>(true)
            : FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (all != null)
        {
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null || t.parent == null) continue;
                string n = t.name;
                string low = n.ToLowerInvariant();
                if (!(low.Contains("stone") || low.StartsWith("rock") || low.Contains("rock_")))
                    continue;
                if (low.Contains("wall") || low.Contains("sword") || low.Contains("door"))
                    continue;
                Vector3 p = t.position;
                if (p.z < 2f || p.z > 40f || Mathf.Abs(p.x) > 45f) continue;
                lm.stones.Add(new Vector2(p.x, p.z));
                if (lm.stones.Count >= 40) break;
            }
        }

        // Забор: активные коллайдеры fence/gate/door_/tall-thin
        Collider[] cols = env != null
            ? env.GetComponentsInChildren<Collider>(false)
            : FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (cols != null)
        {
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null || !c.enabled || c is TerrainCollider) continue;
                if (!c.gameObject.activeInHierarchy) continue;
                if (c.GetComponentInParent<CharacterController>() != null) continue;
                string n = c.gameObject.name.ToLowerInvariant();
                string pn = c.transform.parent != null
                    ? c.transform.parent.name.ToLowerInvariant()
                    : "";
                bool named = n.Contains("fence") || pn.Contains("fence")
                    || n.Contains("gate") || pn.Contains("gate")
                    || n.Contains("woodfence") || pn.Contains("woodfence")
                    || n.StartsWith("door_") || pn.StartsWith("door_")
                    || n.Contains("railing") || pn.Contains("railing");
                Vector3 size = c.bounds.size;
                bool lookLikeFoliage = n.Contains("tree") || pn.Contains("tree")
                    || n.Contains("bush") || pn.Contains("bush")
                    || n.Contains("rock") || pn.Contains("rock")
                    || n.Contains("stone") || pn.Contains("stone")
                    || n.Contains("flower") || n.Contains("grass")
                    || n.Contains("log") || n.Contains("stump")
                    || n.Contains("sheep") || n.Contains("zombie")
                    || n.Contains("water") || n.Contains("lake");
                bool tallThin = !lookLikeFoliage
                    && size.y > 1.0f && size.x < 5.5f && size.z < 5.5f
                    && size.y > Mathf.Max(size.x, size.z) * 0.85f;
                if (!named && !tallThin) continue;
                if (size.x > 12f && size.z > 12f && size.y < 3.5f) continue;
                var rend = c.GetComponent<Renderer>() ?? c.GetComponentInChildren<Renderer>(false);
                if (rend == null || !rend.enabled || !rend.gameObject.activeInHierarchy)
                    continue;
                Vector3 p = c.bounds.center;
                Bounds rb = rend.bounds;
                float sx = size.x, sz = size.z;
                if (rb.size.x > 0.01f && rb.size.z > 0.01f)
                {
                    sx = rb.size.x;
                    sz = rb.size.z;
                    p = new Vector3(rb.center.x, p.y, rb.center.z);
                }
                if (p.x < -18f || p.x > 35f || p.z < -2f || p.z > 45f) continue;
                lm.fences.Add(new RectXZ(p.x, p.z, Mathf.Max(sx, 0.35f), Mathf.Max(sz, 0.35f)));
                if (lm.fences.Count >= 160) break;
            }
        }

        return lm;
    }

    static void AppendLandmarks(StringBuilder sb, Landmarks lm)
    {
        sb.Append("\n  landmarks");
        if (lm.hasHouse)
        {
            sb.Append(" house=");
            AppendXz(sb, new Vector3(lm.house.x, 0f, lm.house.y));
        }
        sb.Append(" lakes=");
        AppendRectsLog(sb, lm.lakes);
        sb.Append(" stones=");
        if (lm.stones == null || lm.stones.Count == 0)
            sb.Append("[]");
        else
        {
            sb.Append('[');
            for (int i = 0; i < lm.stones.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append('(').Append(lm.stones[i].x.ToString("0.00", CultureInfo.InvariantCulture));
                sb.Append(',').Append(lm.stones[i].y.ToString("0.00", CultureInfo.InvariantCulture));
                sb.Append(')');
            }
            sb.Append(']');
        }
        sb.Append(" n_stones=").Append(lm.stones != null ? lm.stones.Count : 0);
        sb.Append(" fences=");
        AppendRectsLog(sb, lm.fences);
        sb.Append(" n_fences=").Append(lm.fences != null ? lm.fences.Count : 0);
    }

    static void AppendRectsLog(StringBuilder sb, List<RectXZ> rects)
    {
        if (rects == null || rects.Count == 0)
        {
            sb.Append("[]");
            return;
        }
        sb.Append('[');
        for (int i = 0; i < rects.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            var r = rects[i];
            sb.Append('(').Append(r.x.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(',').Append(r.z.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(",sx=").Append(r.sx.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(",sz=").Append(r.sz.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(')');
        }
        sb.Append(']');
    }

    static void AppendLandmarksJson(StringBuilder sb, Landmarks lm)
    {
        sb.Append(",\"landmarks\":{");
        if (lm.hasHouse)
        {
            sb.Append("\"house\":{\"x\":").Append(lm.house.x.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(",\"z\":").Append(lm.house.y.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append('}');
        }
        else
            sb.Append("\"house\":null");
        sb.Append(",\"lakes\":[");
        if (lm.lakes != null)
        {
            for (int i = 0; i < lm.lakes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = lm.lakes[i];
                sb.Append("{\"x\":").Append(r.x.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(",\"z\":").Append(r.z.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(",\"sx\":").Append(r.sx.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(",\"sz\":").Append(r.sz.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append('}');
            }
        }
        sb.Append("],\"stones\":[");
        if (lm.stones != null)
        {
            for (int i = 0; i < lm.stones.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"x\":").Append(lm.stones[i].x.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(",\"z\":").Append(lm.stones[i].y.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append('}');
            }
        }
        sb.Append("],\"fences\":[");
        if (lm.fences != null)
        {
            for (int i = 0; i < lm.fences.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = lm.fences[i];
                sb.Append("{\"x\":").Append(r.x.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(",\"z\":").Append(r.z.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(",\"sx\":").Append(r.sx.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append(",\"sz\":").Append(r.sz.ToString("0.##", CultureInfo.InvariantCulture));
                sb.Append('}');
            }
        }
        sb.Append("]}");
    }

    static void AppendAgentJson(StringBuilder sb, string key, LiveAgent a)
    {
        sb.Append(",\"").Append(key).Append("\":{");
        sb.Append("\"name\":\"").Append(EscapeJson(a.name)).Append('"');
        sb.Append(",\"ok\":").Append(a.ok ? "true" : "false");
        if (a.ok)
        {
            sb.Append(",\"x\":").Append(a.x.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(",\"z\":").Append(a.z.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(",\"y\":").Append(a.y.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(",\"hp\":").Append(a.hp);
            sb.Append(",\"water\":").Append(a.water);
            sb.Append(",\"wood\":").Append(a.wood);
            sb.Append(",\"dead\":").Append(a.dead ? "true" : "false");
        }
        sb.Append('}');
    }

    static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    void MaybeWarnCountDrop(string label, ref int prev, int now)
    {
        if (prev > 0 && now < prev * CountDropWarnFraction)
        {
            AppendLine(
                $"{Stamp()} [WARN] {label} count drop {prev}->{now} " +
                $"(env={(TrainingEnvSpace.ActiveViewEnvRoot ?? TrainingEnvSpace.PresentationRoot)?.name})");
        }
        prev = now;
    }

    static int CountHudRole(string hud, string role)
    {
        string key = role + "=";
        int idx = hud.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0)
            return 0;
        idx += key.Length;
        int end = idx;
        while (end < hud.Length && char.IsDigit(hud[end]))
            end++;
        if (end <= idx)
            return 0;
        return int.TryParse(hud.Substring(idx, end - idx), out int n) ? n : 0;
    }

    static void AppendAgent(
        StringBuilder sb,
        string name,
        Transform t,
        AgentGoToHouseDiscrete jackOrGeorge = null,
        LilyScript lily = null)
    {
        sb.Append('\n').Append("  ").Append(name).Append('=');
        if (t == null)
        {
            sb.Append("null");
            return;
        }

        AppendXz(sb, t.position);
        sb.Append(" y=").Append(t.position.y.ToString("0.00"));
        if (jackOrGeorge != null)
        {
            sb.Append(" hp=").Append(jackOrGeorge.hp);
            sb.Append(" sat=").Append(jackOrGeorge.satiety);
            sb.Append(" heat=").Append(jackOrGeorge.heat);
            sb.Append(" water=").Append(jackOrGeorge.water);
            sb.Append(" wood=").Append(jackOrGeorge.wood);
            if (jackOrGeorge.IsInDeathState)
                sb.Append(" DEAD");
        }
        else if (lily != null)
        {
            sb.Append(" hp=").Append(lily.Hp);
            sb.Append(" sat=").Append(lily.Satiety);
            sb.Append(" heat=").Append(lily.Heat);
            sb.Append(" water=").Append(lily.WaterCount);
            if (lily.IsInDeathState)
                sb.Append(" DEAD");
        }
    }

    static void AppendSpawner(StringBuilder sb, string label, Transform t)
    {
        sb.Append('\n').Append("  ").Append(label).Append('=');
        if (t == null)
        {
            sb.Append("null");
            return;
        }

        AppendXz(sb, t.position);
        sb.Append(" active=").Append(t.gameObject.activeInHierarchy ? 1 : 0);
    }

    static void AppendXz(StringBuilder sb, Vector3 p)
    {
        sb.Append('(').Append(p.x.ToString("0.00")).Append(',').Append(p.z.ToString("0.00")).Append(')');
    }

    static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

    static void AppendLine(string line)
    {
        try
        {
            string path = ResolveLogPath();
            if (string.IsNullOrEmpty(path))
                return;

            lock (_ioLock)
            {
                File.AppendAllText(path, line + "\n", Encoding.UTF8);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PresentationWorldSnapshotLogger] write failed: {e.Message}");
        }
    }

    static string ResolveLogPath()
    {
        if (!string.IsNullOrEmpty(_logPath))
            return _logPath;

        string dir = ResolveResultsDir();
        if (string.IsNullOrEmpty(dir))
        {
            try
            {
                dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "results"));
            }
            catch
            {
                dir = Application.persistentDataPath;
            }
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // ignore
        }

        _logPath = Path.Combine(dir, FileName);
        return _logPath;
    }

    static string ResolveLiveJsonPath()
    {
        if (!string.IsNullOrEmpty(_liveJsonPath))
            return _liveJsonPath;
        string log = ResolveLogPath();
        if (string.IsNullOrEmpty(log))
            return null;
        string dir = Path.GetDirectoryName(log);
        _liveJsonPath = Path.Combine(dir ?? "", LiveJsonName);
        return _liveJsonPath;
    }

    static string ResolveResultsDir()
    {
        var env = Environment.GetEnvironmentVariable("FOREST_RESULTS_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "-forestResultsDir" || args[i] == "--forest-results-dir")
                && i + 1 < args.Length
                && !string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1].Trim();
        }

        return null;
    }
}
