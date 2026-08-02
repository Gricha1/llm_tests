using System.Collections;
using UnityEngine;

/// <summary>
/// Простой автотест костра без OBS/стрима:
///   Unity.exe ... -batchmode -nographics -forestCampfireTest
/// или Player: forest.exe -batchmode -nographics -forestCampfireTest
/// Лог: CAMPFIRE_TEST PASS|FAIL …
/// </summary>
public sealed class CampfireWarmthSelfTest : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!Requested())
            return;
        var go = new GameObject(nameof(CampfireWarmthSelfTest));
        Object.DontDestroyOnLoad(go);
        go.AddComponent<CampfireWarmthSelfTest>();
    }

    static bool Requested()
    {
        if (System.Environment.GetEnvironmentVariable("FOREST_CAMPFIRE_TEST") == "1")
            return true;
        foreach (var a in System.Environment.GetCommandLineArgs())
        {
            if (a == "-forestCampfireTest" || a == "--forest-campfire-test")
                return true;
        }
        return false;
    }

    IEnumerator Start()
    {
        // Дать Awake/Start агентам.
        yield return new WaitForSecondsRealtime(2f);

        var george = TrainingEnvSpace.FindPresentationGeorge();
        var jack = TrainingEnvSpace.FindPresentationPrimaryJack();
        if (george == null || jack == null)
        {
            Fail($"нет агентов george={(george != null)} jack={(jack != null)}");
            yield break;
        }

        jack.EnsureTrainingCampfireLit(60f);
        Vector3 warmth = jack.GetCampfireWarmthWorldPosition();
        george.transform.position = warmth + new Vector3(0.5f, 0f, 0.5f);

        int heat0 = george.heat;
        // Сбросить тепло, чтобы точно увидеть рост.
        george.heat = 0;

        float t = 0f;
        while (t < 4f)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }

        bool near = george.IsGeorgeNearBurningCampfirePublic();
        int heat1 = george.heat;
        if (!near)
        {
            Fail($"не near костра burn={jack.CampfireBurnSecondsRemaining:0.0} dist check failed heat={heat1}");
            yield break;
        }

        if (heat1 <= 0)
        {
            Fail($"heat не вырос: {heat0}->0->{heat1} burn={jack.CampfireBurnSecondsRemaining:0.0}");
            yield break;
        }

        Pass($"heat 0→{heat1} burn={jack.CampfireBurnSecondsRemaining:0.0} near=1");
    }

    static void Pass(string detail)
    {
        Debug.Log($"CAMPFIRE_TEST PASS {detail}");
        Quit(0);
    }

    static void Fail(string detail)
    {
        Debug.LogError($"CAMPFIRE_TEST FAIL {detail}");
        Quit(2);
    }

    static void Quit(int code)
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(code);
#endif
    }
}
