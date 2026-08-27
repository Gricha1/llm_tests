using UnityEngine;

/// <summary>
/// У Wolf_1 нет клипов. Крутим только 4 лапы (rotation) + короткий lunge при ударе.
/// Не трогаем Bone/Bone.020 (спина/хвост) и никогда не двигаем localPosition костей.
/// </summary>
public sealed class StreamingSurvivalWolfAnim : MonoBehaviour
{
    const string DeployMarker = "wolf_legs_rot_only_v3_attack";

    const float IdleSwingDeg = 4f;
    const float RunSwingDeg = 26f;
    const float IdleHz = 1.2f;
    const float RunHz = 2.6f;
    const float RootBobIdle = 0.015f;
    const float RootBobRun = 0.04f;
    const float AttackDuration = 0.42f;
    const float AttackLungeDeg = 18f;
    const float AttackForwardM = 0.12f;

    static readonly string[] LegNames =
    {
        "Bone.012",
        "Bone.016",
        "Bone.006",
        "Bone.009",
    };

    static readonly float[] LegPhase = { 0f, Mathf.PI, Mathf.PI, 0f };
    static readonly float[] LegSide = { -1f, 1f, -1f, 1f };

    Transform _root;
    Vector3 _rootRestPos;
    Quaternion _rootRestRot;

    readonly Transform[] _legs = new Transform[4];
    readonly Quaternion[] _legRest = new Quaternion[4];
    float _phase;
    float _move01;
    bool _bound;
    float _attackUntil;
    float _attackT;

    public void SetMoveAmount(float amount01)
    {
        _move01 = Mathf.Clamp01(amount01);
    }

    public void PlayAttack(float duration = AttackDuration)
    {
        _attackUntil = Time.realtimeSinceStartup + Mathf.Max(0.15f, duration);
        _attackT = 0f;
    }

    public bool IsAttacking => Time.realtimeSinceStartup < _attackUntil;

    public void Bind(Transform wolfRoot)
    {
        _bound = false;
        _attackUntil = 0f;
        for (int i = 0; i < 4; i++)
        {
            _legs[i] = null;
            _legRest[i] = Quaternion.identity;
        }

        _root = wolfRoot != null ? wolfRoot : transform;
        _rootRestPos = _root.localPosition;
        _rootRestRot = _root.localRotation;
        _phase = 0f;

        var armature = FindDeep(_root, "Wolf_Armature");
        if (armature == null)
            armature = _root;

        int found = 0;
        for (int i = 0; i < 4; i++)
        {
            var bone = FindDirectChild(armature, LegNames[i]);
            if (bone == null)
                continue;
            _legs[i] = bone;
            _legRest[i] = bone.localRotation;
            found++;
        }

        if (found < 4)
        {
            found = 0;
            for (int i = 0; i < armature.childCount && found < 4; i++)
            {
                var c = armature.GetChild(i);
                if (c == null) continue;
                if (c.name == "Bone" || c.name == "Bone.020") continue;
                if (c.name.IndexOf("end", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                _legs[found] = c;
                _legRest[found] = c.localRotation;
                found++;
            }
            for (int i = found; i < 4; i++)
                _legs[i] = null;
        }

        _bound = _root != null;
        if (_bound)
            Debug.Log($"[SSPos] {DeployMarker} legs={found}");
    }

    void LateUpdate()
    {
        if (!_bound || _root == null) return;

        float move = _move01;
        float hz = Mathf.Lerp(IdleHz, RunHz, move);
        float dt = Time.timeScale > 0.01f ? Time.deltaTime : Time.unscaledDeltaTime;
        _phase += dt * hz * Mathf.PI * 2f;
        if (_phase > 1000f) _phase -= 1000f;

        float bobAmp = Mathf.Lerp(RootBobIdle, RootBobRun, move);
        float bob = Mathf.Sin(_phase) * bobAmp;
        Vector3 pos = _rootRestPos + new Vector3(0f, bob, 0f);
        Quaternion rot = _rootRestRot;

        if (IsAttacking)
        {
            float dur = Mathf.Max(0.15f, AttackDuration);
            _attackT += dt / dur;
            float envelope = Mathf.Sin(Mathf.Clamp01(_attackT) * Mathf.PI);
            pos += _rootRestRot * new Vector3(0f, 0f, AttackForwardM * envelope);
            rot = _rootRestRot * Quaternion.Euler(AttackLungeDeg * envelope, 0f, 0f);
        }

        _root.localPosition = pos;
        _root.localRotation = rot;

        float swing = Mathf.Lerp(IdleSwingDeg, RunSwingDeg, move);
        if (IsAttacking)
            swing = Mathf.Lerp(swing, 38f, 0.85f);
        for (int i = 0; i < 4; i++)
        {
            var bone = _legs[i];
            if (bone == null) continue;
            float s = Mathf.Sin(_phase + LegPhase[i]);
            var delta = Quaternion.Euler(s * swing, 0f, s * swing * 0.12f * LegSide[i]);
            bone.localRotation = _legRest[i] * delta;
        }
    }

    static Transform FindDirectChild(Transform parent, string exactName)
    {
        if (parent == null) return null;
        for (int i = 0; i < parent.childCount; i++)
        {
            var c = parent.GetChild(i);
            if (c != null && c.name == exactName)
                return c;
        }
        return null;
    }

    static Transform FindDeep(Transform root, string exactName)
    {
        if (root == null) return null;
        if (root.name == exactName) return root;
        var all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].name == exactName)
                return all[i];
        }
        return null;
    }
}
