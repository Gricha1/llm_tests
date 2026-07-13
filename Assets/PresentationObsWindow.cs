using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;

/// <summary>
/// SingleEnvByPort: уникальное имя окна для OBS (w0 PRESENTATION, w1–w11 train).
/// </summary>
public sealed class PresentationObsWindow : MonoBehaviour
{
    const float RenameIntervalSec = 2f;
    const int MaxAttempts = 60;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!TrainingEnvSpace.IsSingleEnvByPortMode || TrainingEnvSpace.SingleEnvWorkerCopyIndex < 0)
            return;

        var go = new GameObject(nameof(PresentationObsWindow));
        DontDestroyOnLoad(go);
        go.AddComponent<PresentationObsWindow>();
    }

    void Start()
    {
        StartCoroutine(RenameLoop());
    }

    static string BuildTitle()
    {
        int worker = TrainingEnvSpace.SingleEnvWorkerCopyIndex;
        if (TrainingEnvSpace.IsPresentationWorkerProcess)
            return $"forest_survival w{worker} PRESENTATION";
        return $"forest_survival w{worker} train";
    }

    IEnumerator RenameLoop()
    {
        string title = BuildTitle();
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TrySetProcessWindowTitle(title))
            {
                UnityEngine.Debug.Log(
                    $"[PresentationObsWindow] Окно → «{title}» (попытка {attempt + 1}).");
            }

            yield return new WaitForSecondsRealtime(RenameIntervalSec);
        }

        UnityEngine.Debug.LogWarning(
            $"[PresentationObsWindow] Остановили переименование «{title}» после {MaxAttempts} попыток.");
    }

    static bool TrySetProcessWindowTitle(string title)
    {
#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
        if (LinuxX11WindowTitle.TrySetForCurrentProcess(title))
            return true;
        if (TrySetWithWmctrl(title))
            return true;
        if (TrySetWithXprop(title))
            return true;
#endif
        return TrySetWithXdotool(title);
    }

#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
    static bool TrySetWithWmctrl(string title)
    {
        try
        {
            int pid = Process.GetCurrentProcess().Id;
            using var list = Process.Start(new ProcessStartInfo
            {
                FileName = "wmctrl",
                Arguments = "-lp",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (list == null)
                return false;

            string output = list.StandardOutput.ReadToEnd();
            list.WaitForExit(2000);
            if (list.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return false;

            bool any = false;
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                    continue;
                if (!int.TryParse(parts[2], out int linePid) || linePid != pid)
                    continue;

                string wid = parts[0];
                RunQuiet("wmctrl", $"-ir \"{wid}\" -T \"{title}\"");
                RunQuiet("wmctrl", $"-ir \"{wid}\" -N \"{title}\"");
                any = true;
            }

            return any;
        }
        catch
        {
            return false;
        }
    }

    static bool TrySetWithXprop(string title)
    {
        try
        {
            int pid = Process.GetCurrentProcess().Id;
            using var search = Process.Start(new ProcessStartInfo
            {
                FileName = "xdotool",
                Arguments = $"search --pid {pid}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (search == null)
                return false;

            string output = search.StandardOutput.ReadToEnd();
            search.WaitForExit(2000);
            if (search.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return false;

            bool any = false;
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!long.TryParse(lines[i].Trim(), out long windowId))
                    continue;

                string wid = $"0x{windowId:x}";
                RunQuiet("xprop", $"-id {wid} -f _NET_WM_NAME 8u -set _NET_WM_NAME \"{title}\"");
                RunQuiet("xprop", $"-id {wid} WM_NAME \"{title}\"");
                any = true;
            }

            return any;
        }
        catch
        {
            return false;
        }
    }
#endif

    static bool TrySetWithXdotool(string title)
    {
        try
        {
            int pid = Process.GetCurrentProcess().Id;
            using var search = Process.Start(new ProcessStartInfo
            {
                FileName = "xdotool",
                Arguments = $"search --pid {pid}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (search == null)
                return false;

            string output = search.StandardOutput.ReadToEnd();
            search.WaitForExit(2000);
            if (search.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return false;

            bool any = false;
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!long.TryParse(lines[i].Trim(), out long windowId))
                    continue;

                RunQuiet("xdotool", $"set_window --name \"{title}\" {windowId}");
                any = true;
            }

            return any;
        }
        catch
        {
            return false;
        }
    }

    static void RunQuiet(string fileName, string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
            });
            proc?.WaitForExit(2000);
        }
        catch
        {
            // ignore
        }
    }
}
