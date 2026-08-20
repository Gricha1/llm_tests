using UnityEngine;

/// <summary>
/// Костёр у дома в presentation: включает сценовый Fire (URP), не CreatePrimitive.
/// </summary>
public static class StreamingSurvivalCampfireVfx
{
    const string FireName = "Fire";
    const string MarkerName = "SS_Campfire";

    public static bool IsLit()
    {
        var fire = FindPresentationFire();
        return fire != null && fire.activeSelf;
    }

    public static bool TryLight(out GameObject fireGo)
    {
        fireGo = FindPresentationFire();
        if (fireGo == null)
            return false;

        fireGo.SetActive(true);
        foreach (var renderer in fireGo.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = true;
        foreach (var ps in fireGo.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (ps != null && !ps.isPlaying)
                ps.Play(true);
        }

        if (fireGo.GetComponent<CampfireLoopAudio>() == null)
            fireGo.AddComponent<CampfireLoopAudio>();
        else
            fireGo.GetComponent<CampfireLoopAudio>().enabled = true;

        var marker = fireGo.transform.Find(MarkerName);
        if (marker == null)
        {
            var tag = new GameObject(MarkerName);
            tag.transform.SetParent(fireGo.transform, false);
            tag.transform.localPosition = Vector3.zero;
        }

        return true;
    }

    public static void Extinguish()
    {
        var fire = FindPresentationFire();
        if (fire == null)
            return;
        fire.SetActive(false);
        var audio = fire.GetComponent<CampfireLoopAudio>();
        if (audio != null)
            audio.enabled = false;
    }

    static GameObject FindPresentationFire()
    {
        var root = TrainingEnvSpace.PresentationRoot;
        if (root == null)
            return null;

        Transform fire = root.Find(FireName);
        if (fire == null)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == FireName)
                {
                    fire = t;
                    break;
                }
            }
        }

        return fire != null ? fire.gameObject : null;
    }
}
