using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Последовательность GoalWater1 → GoalWater2 → GoalWater3 для shaping опции «вода».
/// Кубы скрыты визуально; для RayMiddleWater — тег Water и layer Water (bit 13).
/// Пройденный чекпоинт убирается с water-ray (layer Ignore Raycast), пока агент не сбросит путь
/// (повторный вход в опцию воды / ResetAgent).
/// </summary>
[DisallowMultipleComponent]
public sealed class WaterGoalPath : MonoBehaviour
{
    static readonly string[] GoalNames = { "GoalWater1", "GoalWater2", "GoalWater3" };
    const string WaterTag = "Water";
    // RayMiddleWater.m_RayLayerMask = 8192 → layer 13 (второй "Water" в TagManager).
    const int WaterRayLayer = 13;
    const int HiddenFromWaterRayLayer = 2; // Ignore Raycast

    [SerializeField] private float approachRewardScale = 0.3f;
    [SerializeField] private float reachReward = 10f;
    [SerializeField] private bool loopAfterComplete = false;

    Transform[] _goals = new Transform[GoalNames.Length];
    Collider[] _goalColliders = new Collider[GoalNames.Length];
    Bounds[] _goalBounds = new Bounds[GoalNames.Length];
    readonly Dictionary<int, AgentProgress> _progressByAgent = new Dictionary<int, AgentProgress>();
    bool _initialized;

    sealed class AgentProgress
    {
        public int CurrentIndex;
        public float PrevDist = -1f;
    }

    void Awake()
    {
        if (!TrainingEnvSpace.IsEnvRootTransform(transform))
            return;

        Initialize();
    }

    void OnEnable()
    {
        // UnmuteEnvPresentation снова включает Renderer — прячем GoalWater после фокуса Env.
        if (_initialized)
            HideAllGoalVisuals();
    }

    public static void HideGoalsInEnv(Transform envRoot)
    {
        if (envRoot == null)
            return;

        var path = envRoot.GetComponent<WaterGoalPath>();
        if (path == null)
            path = envRoot.GetComponentInChildren<WaterGoalPath>(true);
        path?.Initialize();
        path?.HideAllGoalVisuals();

        // На случай если компонент ещё не создан — прячем по имени.
        var all = envRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null)
                continue;
            if (t.name.StartsWith("GoalWater", StringComparison.Ordinal))
                HideGoalVisuals(t);
        }
    }

    public static WaterGoalPath Get(Transform agent)
    {
        var root = TrainingEnvSpace.FindRoot(agent);
        if (root == null)
            return null;

        var path = root.GetComponent<WaterGoalPath>();
        if (path == null)
            path = root.gameObject.AddComponent<WaterGoalPath>();

        path.Initialize();
        return path.HasAnyGoal() ? path : null;
    }

    public bool HasAnyGoal()
    {
        Initialize();
        for (int i = 0; i < _goals.Length; i++)
        {
            if (_goals[i] != null)
                return true;
        }

        return false;
    }

    public void ResetSequence()
    {
        _progressByAgent.Clear();
        RefreshGoalRayVisibility();
    }

    public void ResetAgent(Transform agent)
    {
        if (agent == null)
            return;

        _progressByAgent.Remove(agent.GetInstanceID());
        // Снова с нуля: текущий агент начинает с GoalWater1 (index 0).
        GetProgress(agent);
        RefreshGoalRayVisibility();
    }

    AgentProgress GetProgress(Transform agent)
    {
        int id = agent.GetInstanceID();
        if (!_progressByAgent.TryGetValue(id, out var progress))
        {
            progress = new AgentProgress();
            _progressByAgent[id] = progress;
        }

        return progress;
    }

    public bool ProcessStep(Transform agent, Action<float> addReward)
    {
        if (agent == null || addReward == null || !HasAnyGoal())
            return false;

        AgentProgress progress = GetProgress(agent);
        AdvancePastMissingGoals(progress);
        if (progress.CurrentIndex >= _goals.Length)
        {
            if (loopAfterComplete)
            {
                ResetAgent(agent);
                progress = GetProgress(agent);
            }
            else
                return false;
        }

        Transform goal = _goals[progress.CurrentIndex];
        if (goal == null)
            return false;

        Collider goalCol = _goalColliders[progress.CurrentIndex];
        Bounds goalBounds = BuildGoalBounds(goal);
        _goalBounds[progress.CurrentIndex] = goalBounds;
        if (IsInsideGoal(agent.position, goalCol, goal, goalBounds))
        {
            addReward(reachReward);
            progress.CurrentIndex++;
            progress.PrevDist = -1f;
            AdvancePastMissingGoals(progress);
            RefreshGoalRayVisibility();

            if (progress.CurrentIndex >= _goals.Length && loopAfterComplete)
                ResetAgent(agent);

            return true;
        }

        float currDist = GetDistanceToGoal(agent.position, goalCol, goal, goalBounds);
        if (progress.PrevDist > 0f)
            addReward((progress.PrevDist - currDist) * approachRewardScale);

        progress.PrevDist = currDist;
        return true;
    }

    public bool HasCompletedPath(Transform agent)
    {
        if (agent == null || !HasAnyGoal())
            return false;

        AgentProgress progress = GetProgress(agent);
        AdvancePastMissingGoals(progress);
        return progress.CurrentIndex >= _goals.Length;
    }

    /// <summary>
    /// Для Streaming Survival: текущий GoalWater чекпоинт; при подходе — следующий.
    /// false = путь пройден (можно идти к WaterSource).
    /// centerOnly: arrive by XZ distance to goal pivot (ignore huge training trigger volumes).
    /// </summary>
    public bool TryGetFollowerWaypoint(Transform agent, float arriveDist, out Vector3 worldPos)
    {
        return TryGetFollowerWaypoint(agent, arriveDist, out worldPos, centerOnly: false);
    }

    public bool TryGetFollowerWaypoint(
        Transform agent, float arriveDist, out Vector3 worldPos, bool centerOnly)
    {
        worldPos = default;
        if (agent == null || !HasAnyGoal())
            return false;

        AgentProgress progress = GetProgress(agent);
        AdvancePastMissingGoals(progress);
        if (progress.CurrentIndex >= _goals.Length)
            return false;

        Transform goal = _goals[progress.CurrentIndex];
        if (goal == null)
            return false;

        Collider goalCol = _goalColliders[progress.CurrentIndex];
        Bounds goalBounds = _goalBounds[progress.CurrentIndex].size.sqrMagnitude > 0.01f
            ? _goalBounds[progress.CurrentIndex]
            : BuildGoalBounds(goal);

        bool arrived;
        if (centerOnly)
        {
            // SS followers must visit GoalWater1 center before GoalWater2.
            // Training GoalWater triggers are often huge → ClosestPoint would skip GW1.
            Vector3 a = agent.position;
            Vector3 g = goal.position;
            float dx = a.x - g.x;
            float dz = a.z - g.z;
            arrived = (dx * dx + dz * dz) <= arriveDist * arriveDist;
        }
        else
        {
            arrived = IsInsideGoal(agent.position, goalCol, goal, goalBounds)
                || GetDistanceToGoal(agent.position, goalCol, goal, goalBounds) <= arriveDist;
        }

        if (arrived)
        {
            progress.CurrentIndex++;
            progress.PrevDist = -1f;
            AdvancePastMissingGoals(progress);
            RefreshGoalRayVisibility();
            if (progress.CurrentIndex >= _goals.Length)
                return false;
            goal = _goals[progress.CurrentIndex];
            if (goal == null)
                return false;
        }

        worldPos = goal.position;
        return true;
    }

    void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        for (int i = 0; i < GoalNames.Length; i++)
        {
            _goals[i] = FindGoal(transform, GoalNames[i]);
            if (_goals[i] == null)
                continue;

            EnsureGoalCollider(_goals[i]);
            EnsureWaterTagAndRayLayer(_goals[i], visibleToWaterRays: true);
            HideGoalVisuals(_goals[i]);
            _goalColliders[i] = _goals[i].GetComponentInChildren<Collider>(true);
            _goalBounds[i] = BuildGoalBounds(_goals[i]);
        }
    }

    /// <summary>
    /// Goal i виден RayMiddleWater, пока есть агент с CurrentIndex &lt;= i
    /// (пройденные чекпоинты скрыты с water-ray; при ResetAgent снова все с нуля).
    /// </summary>
    void RefreshGoalRayVisibility()
    {
        Initialize();

        for (int goalIndex = 0; goalIndex < _goals.Length; goalIndex++)
        {
            var goal = _goals[goalIndex];
            if (goal == null)
                continue;

            bool needed = false;
            foreach (var kv in _progressByAgent)
            {
                if (kv.Value != null && kv.Value.CurrentIndex <= goalIndex)
                {
                    needed = true;
                    break;
                }
            }

            // Никто на пути — держим все чекпоинты видимыми для rays (готовность к воде).
            if (_progressByAgent.Count == 0)
                needed = true;

            EnsureWaterTagAndRayLayer(goal, visibleToWaterRays: needed);
        }
    }

    static void EnsureWaterTagAndRayLayer(Transform goal, bool visibleToWaterRays)
    {
        if (goal == null)
            return;

        if (!goal.CompareTag(WaterTag))
        {
            try
            {
                goal.tag = WaterTag;
            }
            catch (UnityException)
            {
                Debug.LogWarning($"[WaterGoalPath] тег '{WaterTag}' не найден в Tag Manager");
            }
        }

        int layer = visibleToWaterRays ? WaterRayLayer : HiddenFromWaterRayLayer;
        if (goal.gameObject.layer != layer)
            goal.gameObject.layer = layer;
    }

    void HideAllGoalVisuals()
    {
        for (int i = 0; i < _goals.Length; i++)
        {
            if (_goals[i] != null)
                HideGoalVisuals(_goals[i]);
        }
    }

    static Bounds BuildGoalBounds(Transform goal)
    {
        var colliders = goal.GetComponentsInChildren<Collider>(true);
        if (colliders.Length == 0)
            return new Bounds(goal.position, Vector3.one * 5f);

        Bounds bounds = colliders[0].bounds;
        for (int i = 1; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
                bounds.Encapsulate(colliders[i].bounds);
        }

        return bounds;
    }

    void AdvancePastMissingGoals(AgentProgress progress)
    {
        while (progress.CurrentIndex < _goals.Length && _goals[progress.CurrentIndex] == null)
            progress.CurrentIndex++;
    }

    static Transform FindGoal(Transform envRoot, string goalName)
    {
        if (envRoot == null)
            return null;

        if (envRoot.name == goalName)
            return envRoot;

        var direct = envRoot.Find(goalName);
        if (direct != null)
            return direct;

        var all = envRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].name == goalName)
                return all[i];
        }

        return null;
    }

    static void HideGoalVisuals(Transform goal)
    {
        if (!Application.isPlaying)
            return;

        var renderers = goal.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
                renderers[i].enabled = false;
        }
    }

    static void EnsureGoalCollider(Transform goal)
    {
        var colliders = goal.GetComponentsInChildren<Collider>(true);
        if (colliders.Length == 0)
        {
            var box = goal.gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = Vector3.one * 5f;
            return;
        }

        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
                colliders[i].isTrigger = true;
        }
    }

    static bool IsInsideGoal(Vector3 agentPos, Collider goalCol, Transform goal, Bounds goalBounds)
    {
        // Агент ходит по земле; GoalWater может быть выше pivot — считаем попадание по XZ.
        if (IsInsideGoalHorizontally(agentPos, goalBounds))
            return true;

        if (goalCol != null && goalCol.enabled)
        {
            Vector3 closest = goalCol.ClosestPoint(agentPos);
            closest.y = 0f;
            agentPos.y = 0f;
            float slack = GetReachSlack(goalBounds);
            return Vector3.Distance(agentPos, closest) < slack;
        }

        Vector3 goalPos = goal.position;
        goalPos.y = 0f;
        agentPos.y = 0f;
        return Vector3.Distance(agentPos, goalPos) < 2f;
    }

    static bool IsInsideGoalHorizontally(Vector3 agentPos, Bounds goalBounds)
    {
        if (goalBounds.size.sqrMagnitude < 1e-4f)
            return false;

        return agentPos.x >= goalBounds.min.x && agentPos.x <= goalBounds.max.x
            && agentPos.z >= goalBounds.min.z && agentPos.z <= goalBounds.max.z;
    }

    static float GetReachSlack(Bounds goalBounds)
    {
        float horizontal = Mathf.Max(goalBounds.extents.x, goalBounds.extents.z);
        return Mathf.Max(0.6f, horizontal * 0.55f);
    }

    static float GetDistanceToGoal(Vector3 agentPos, Collider goalCol, Transform goal, Bounds goalBounds)
    {
        agentPos.y = 0f;
        if (IsInsideGoalHorizontally(agentPos, goalBounds))
            return 0f;

        if (goalCol != null && goalCol.enabled)
        {
            Vector3 closest = goalCol.ClosestPoint(agentPos);
            closest.y = 0f;
            return Vector3.Distance(agentPos, closest);
        }

        Vector3 goalPos = goal.position;
        goalPos.y = 0f;
        return Vector3.Distance(agentPos, goalPos);
    }
}
