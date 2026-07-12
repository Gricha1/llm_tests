using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Последовательность GoalWater1 → GoalWater2 → GoalWater3 для shaping опции «вода».
/// Кубы скрываются во время Play, коллайдеры остаются для проверки достижения.
/// </summary>
[DisallowMultipleComponent]
public sealed class WaterGoalPath : MonoBehaviour
{
    static readonly string[] GoalNames = { "GoalWater1", "GoalWater2", "GoalWater3" };

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
    }

    public void ResetAgent(Transform agent)
    {
        if (agent == null)
            return;

        _progressByAgent.Remove(agent.GetInstanceID());
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
                ResetAgent(agent);
            else
                return false;

            progress = GetProgress(agent);
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
            HideGoalVisuals(_goals[i]);
            _goalColliders[i] = _goals[i].GetComponentInChildren<Collider>(true);
            _goalBounds[i] = BuildGoalBounds(_goals[i]);
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

        if (goalCol != null)
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

        if (goalCol != null)
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
