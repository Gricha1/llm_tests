using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections;
using Unity.MLAgents;
using UnityEngine;

/// <summary>
/// Optional runtime-only helper for evaluation runs launched from CLI.
/// Enables frame capture (PNG sequence) and auto-quit after N Academy steps.
///
/// Usage (passed via ML-Agents --env-args):
///   --capture-dir <path>         Directory for PNG frames
///   --capture-dir-b <path>       Directory for 2nd camera PNG frames
///   --capture-dir-c <path>       Directory for 3rd camera PNG frames
///   --capture-every <n>          Capture every n rendered frames (default: 1)
///   --capture-width <w>          Camera capture width (default: 1920)
///   --capture-height <h>         Camera capture height (default: 1080)
///   --capture-msaa <n>             RenderTexture MSAA 1/2/4/8 (default: 4; 1 = off)
///   --capture-camera-a <name>    Camera A name (optional)
///   --capture-camera-b <name>    Camera B name (optional)
///   --capture-camera-c <name>    Camera C name (optional)
///   --quit-after-steps <n>       Quit after n Academy steps (default: disabled)
///   --quit-after-episodes <n>    Quit after n completed episodes (0 = disabled; requires agent integration)
///   --quit-after-seconds <s>     Quit after s real-time seconds (works even if Academy doesn't step)
///   --quit-after-capture-frames <n>  Quit after saving n frames per fastest active capture stream (see Update)
///   --quit-delay-seconds <s>     Delay before quitting to allow final frame writes (default: 0.5)
/// </summary>
public sealed class CommandLineEvalCapture : MonoBehaviour
{
    // Для валидации видео важнее качество тайминга, чем скорость wall-time.
    // Time.captureFramerate заставляет Unity шагать время ровно 1/fps за кадр, даже если рендер/запись медленные.
    private int _captureFps = 30;
    private int _quitAfterCaptureFrames = -1;
    private string _captureDir;
    private string _captureDirB;
    private string _captureDirC;
    private int _captureEvery = 1;
    private int _captureWidth = 1920;
    private int _captureHeight = 1080;
    private int _captureMsaa = 4;
    // png = тяжело (мало FPS), jpg = гораздо быстрее (для валидации обычно достаточно).
    private string _captureFormat = "jpg";
    private int _jpgQuality = 85;
    private string _cameraAName;
    private string _cameraBName;
    private string _cameraCName;
    private bool _captureAScreenFinal; // include Overlay HUD by capturing final screen at end of frame
    private long _quitAfterSteps = -1;
    private int _quitAfterEpisodes = -1;
    private float _quitAfterSeconds = -1f;
    private float _quitDelaySeconds = 0.5f;

    private int _frameIndexA;
    private int _frameIndexB;
    private int _frameIndexC;
    private int _renderFrameCounter;
    private long _startStep = -1;
    private bool _quitScheduled;
    private float _startRealtime = -1f;
    private float _lastCaptureProgressLogWall = -1f;
    private Camera _cameraA;
    private Camera _cameraB;
    private Camera _cameraC;
    private Camera _thirdPersonCameraB;
    private Camera _jackOverheadCameraC;
    private Transform _jackTarget;
    private Vector3 _thirdPersonOffset = new Vector3(0f, 2.2f, -4.5f);
    private Vector3 _thirdPersonLookAtOffset = new Vector3(0f, 1.2f, 0f);
    private Vector3 _overheadOffset = new Vector3(0f, 12f, 0f);
    private Vector3 _overheadLookAtOffset = new Vector3(0f, 1.0f, 0f);
    private Texture2D _captureTexture;
    private RenderTexture _captureRt;
    private bool _pendingEndOfFrameCaptureA;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            if (args == null || args.Length == 0) return;

            // Only create the helper if any supported flag is present (order-independent).
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--capture-dir" || a == "--capture-dir-b" || a == "--capture-dir-c"
                    || a == "--quit-after-steps" || a == "--quit-after-capture-frames"
                    || a == "--quit-after-episodes" || a == "--quit-after-seconds"
                    || a == "--capture-every" || a == "--capture-fps")
                {
                    var go = new GameObject(nameof(CommandLineEvalCapture));
                    DontDestroyOnLoad(go);
                    go.AddComponent<CommandLineEvalCapture>();
                    return;
                }
            }
        }
        catch
        {
            // Best-effort: never break a build due to eval utilities.
        }
    }

    private void Awake()
    {
        ParseArgs(Environment.GetCommandLineArgs());

        // Для захвата делаем детерминированный тайминг: 1 кадр = 1/fps секунды игрового времени.
        // Это НЕ "замедляет" видео — оно будет ровно fps, просто генерация может идти долго по wall-time.
        if (!string.IsNullOrWhiteSpace(_captureDir)
            || !string.IsNullOrWhiteSpace(_captureDirB)
            || !string.IsNullOrWhiteSpace(_captureDirC))
        {
            Time.captureFramerate = Mathf.Clamp(_captureFps, 1, 240);
            Time.timeScale = 1f;
        }

        if (!string.IsNullOrWhiteSpace(_captureDir))
        {
            Directory.CreateDirectory(_captureDir);
            try
            {
                File.WriteAllText(Path.Combine(_captureDir, "capture_started.txt"), DateTime.UtcNow.ToString("O"));
            }
            catch
            {
                // ignore
            }
        }

        if (!string.IsNullOrWhiteSpace(_captureDirB))
        {
            Directory.CreateDirectory(_captureDirB);
            try
            {
                File.WriteAllText(Path.Combine(_captureDirB, "capture_started.txt"), DateTime.UtcNow.ToString("O"));
            }
            catch
            {
                // ignore
            }
        }

        if (!string.IsNullOrWhiteSpace(_captureDirC))
        {
            Directory.CreateDirectory(_captureDirC);
            try
            {
                File.WriteAllText(Path.Combine(_captureDirC, "capture_started.txt"), DateTime.UtcNow.ToString("O"));
            }
            catch
            {
                // ignore
            }
        }

        InitCaptureResources();

        if (_quitAfterEpisodes > 0)
        {
            EvalEpisodeTracker.Reset();
        }
    }

    private void Update()
    {
        if (_startRealtime < 0f) _startRealtime = Time.realtimeSinceStartup;

        // If we need a 2nd camera capture, ensure a 3rd-person Jack camera exists.
        if (!string.IsNullOrWhiteSpace(_captureDirB))
            EnsureJackThirdPersonCameraB();
        if (!string.IsNullOrWhiteSpace(_captureDirC))
            EnsureJackOverheadCameraC();

        if (!string.IsNullOrWhiteSpace(_captureDir) || !string.IsNullOrWhiteSpace(_captureDirB) || !string.IsNullOrWhiteSpace(_captureDirC))
        {
            _renderFrameCounter++;
            if (_renderFrameCounter % Math.Max(1, _captureEvery) == 0)
            {
                CaptureFrames();
            }
        }

        // Ограничение по количеству сохранённых кадров: гарантирует длительность видео (frames / fps).
        if (_quitAfterCaptureFrames > 0 && !_quitScheduled)
        {
            int frames = Mathf.Max(_frameIndexA, Mathf.Max(_frameIndexB, _frameIndexC));
            if (frames >= _quitAfterCaptureFrames)
            {
                _quitScheduled = true;
                Invoke(nameof(Quit), Mathf.Max(0f, _quitDelaySeconds));
            }
        }

        // Wall-time progress: game time is paced by captureFramerate, but JPEG+IO can be much slower than real-time.
        if (_quitAfterCaptureFrames > 0 && !_quitScheduled && _startRealtime >= 0f)
        {
            var wall = Time.realtimeSinceStartup - _startRealtime;
            if (_lastCaptureProgressLogWall < 0f || wall - _lastCaptureProgressLogWall >= 120f)
            {
                _lastCaptureProgressLogWall = wall;
                var maxIdx = Mathf.Max(_frameIndexA, Mathf.Max(_frameIndexB, _frameIndexC));
                Debug.Log(
                    $"[CommandLineEvalCapture] capture progress wall_s={wall:F0} max_frames={maxIdx} " +
                    $"(A={_frameIndexA} B={_frameIndexB} C={_frameIndexC}) target_frames={_quitAfterCaptureFrames}");
            }
        }

        if (_quitAfterSteps > 0 && !_quitScheduled)
        {
            var currentStep = Academy.Instance.StepCount;
            if (_startStep < 0) _startStep = currentStep;

            if (currentStep - _startStep >= _quitAfterSteps)
            {
                _quitScheduled = true;
                Invoke(nameof(Quit), Mathf.Max(0f, _quitDelaySeconds));
            }
        }

        if (_quitAfterEpisodes > 0 && !_quitScheduled)
        {
            if (EvalEpisodeTracker.EndedEpisodes >= _quitAfterEpisodes)
            {
                _quitScheduled = true;
                Invoke(nameof(Quit), Mathf.Max(0f, _quitDelaySeconds));
            }
        }

        if (_quitAfterSeconds > 0f && !_quitScheduled)
        {
            if (Time.realtimeSinceStartup - _startRealtime >= _quitAfterSeconds)
            {
                _quitScheduled = true;
                Invoke(nameof(Quit), Mathf.Max(0f, _quitDelaySeconds));
            }
        }
    }

    private void Quit()
    {
        WriteCaptureMetaFiles();
        Application.Quit(0);
    }

    /// <summary>
    /// Пишет wall_seconds и frame_count — bash/ffmpeg могут выставить -framerate = frames/wall,
    /// чтобы длительность mp4 совпадала с реальным временем захвата (без «ускоренного» ролика).
    /// </summary>
    private void WriteCaptureMetaFiles()
    {
        var wall = Mathf.Max(0.001f, Time.realtimeSinceStartup - _startRealtime);
        TryWriteCaptureMeta(_captureDir, _frameIndexA, wall);
        TryWriteCaptureMeta(_captureDirB, _frameIndexB, wall);
        TryWriteCaptureMeta(_captureDirC, _frameIndexC, wall);
    }

    private static void TryWriteCaptureMeta(string dir, int frameCount, float wallSeconds)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            var path = Path.Combine(dir, "capture_meta.txt");
            var w = wallSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(path, $"wall_seconds={w}\nframe_count={frameCount}\n");
        }
        catch
        {
            // ignore
        }
    }

    private void OnDestroy()
    {
        if (_captureRt != null)
        {
            _captureRt.Release();
            Destroy(_captureRt);
        }
        if (_captureTexture != null)
        {
            Destroy(_captureTexture);
        }
    }

    private void InitCaptureResources()
    {
        _captureWidth = Math.Max(64, _captureWidth);
        _captureHeight = Math.Max(64, _captureHeight);
        if (_captureRt != null)
        {
            _captureRt.Release();
            Destroy(_captureRt);
            _captureRt = null;
        }
        if (_captureTexture != null)
        {
            Destroy(_captureTexture);
            _captureTexture = null;
        }

        _captureRt = new RenderTexture(_captureWidth, _captureHeight, 24, RenderTextureFormat.ARGB32);
        var msaa = _captureMsaa >= 8 ? 8 : _captureMsaa >= 4 ? 4 : _captureMsaa >= 2 ? 2 : 1;
        _captureRt.antiAliasing = msaa;
        _captureRt.filterMode = FilterMode.Trilinear;
        _captureRt.Create();
        _captureTexture = new Texture2D(_captureWidth, _captureHeight, TextureFormat.RGB24, false);
        ResolveCameras();
    }

    private void ResolveCameras()
    {
        var cameras = Camera.allCameras
            .Where(c => c != null && c.enabled)
            .OrderBy(c => c.depth)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(_cameraAName))
            _cameraA = cameras.FirstOrDefault(c => c.name == _cameraAName);
        if (!string.IsNullOrWhiteSpace(_cameraBName))
            _cameraB = cameras.FirstOrDefault(c => c.name == _cameraBName);
        if (!string.IsNullOrWhiteSpace(_cameraCName))
            _cameraC = cameras.FirstOrDefault(c => c.name == _cameraCName);

        if (_cameraA == null)
            _cameraA = Camera.main ?? cameras.FirstOrDefault();

        if (_cameraB == null)
            _cameraB = cameras.FirstOrDefault(c => c != _cameraA);

        if (_cameraC == null)
            _cameraC = cameras.FirstOrDefault(c => c != _cameraA && c != _cameraB);
    }

    private void CaptureFrames()
    {
        // Some cameras may spawn after scene load.
        if (_cameraA == null
            || (!string.IsNullOrWhiteSpace(_captureDirB) && _cameraB == null)
            || (!string.IsNullOrWhiteSpace(_captureDirC) && _cameraC == null))
            ResolveCameras();

        if (!string.IsNullOrWhiteSpace(_captureDir))
        {
            if (_captureAScreenFinal)
            {
                // Capture after all rendering (including ScreenSpaceOverlay UI).
                if (!_pendingEndOfFrameCaptureA)
                {
                    _pendingEndOfFrameCaptureA = true;
                    StartCoroutine(CaptureAScreenAtEndOfFrame());
                }
            }
            else
            {
                if (_cameraA != null)
                    CaptureCameraToImage(_cameraA, _captureDir, ref _frameIndexA);
                else
                    CaptureScreenFallback(_captureDir, ref _frameIndexA);
            }
        }

        if (!string.IsNullOrWhiteSpace(_captureDirB))
        {
            if (_cameraB != null)
                CaptureCameraToImage(_cameraB, _captureDirB, ref _frameIndexB);
        }

        if (!string.IsNullOrWhiteSpace(_captureDirC))
        {
            if (_cameraC != null)
                CaptureCameraToImage(_cameraC, _captureDirC, ref _frameIndexC);
        }
    }

    private void EnsureJackThirdPersonCameraB()
    {
        // If a named camera B was requested: never auto-create cameras. Use only what's in the scene.
        if (!string.IsNullOrWhiteSpace(_cameraBName))
        {
            if (_cameraB != null) return;
            ResolveCameras();
            return;
        }

        if (_thirdPersonCameraB == null)
        {
            var go = GameObject.Find("JackThirdPersonCamera");
            if (go == null)
                go = new GameObject("JackThirdPersonCamera");

            _thirdPersonCameraB = go.GetComponent<Camera>();
            if (_thirdPersonCameraB == null)
                _thirdPersonCameraB = go.AddComponent<Camera>();

            // Make sure it's enabled for Render() and doesn't interfere with the main view.
            _thirdPersonCameraB.enabled = true;
            _thirdPersonCameraB.depth = -100; // keep it out of the main stack ordering
            _thirdPersonCameraB.clearFlags = CameraClearFlags.Skybox;

            _cameraB = _thirdPersonCameraB;
        }

        if (_jackTarget == null)
        {
            AgentGoToHouseDiscrete jack;
#if UNITY_2023_1_OR_NEWER
            jack = FindAnyObjectByType<AgentGoToHouseDiscrete>();
#else
            jack = FindFirstObjectByType<AgentGoToHouseDiscrete>();
#endif
            if (jack != null) _jackTarget = jack.transform;
        }

        if (_jackTarget != null)
        {
            // Place camera behind Jack in Jack's local space.
            _thirdPersonCameraB.transform.position = _jackTarget.TransformPoint(_thirdPersonOffset);
            var lookAt = _jackTarget.TransformPoint(_thirdPersonLookAtOffset);
            _thirdPersonCameraB.transform.rotation = Quaternion.LookRotation(lookAt - _thirdPersonCameraB.transform.position, Vector3.up);
        }
    }

    private void EnsureJackOverheadCameraC()
    {
        // If a named camera C was requested: never auto-create cameras. Use only what's in the scene.
        if (!string.IsNullOrWhiteSpace(_cameraCName))
        {
            if (_cameraC != null) return;
            ResolveCameras();
            return;
        }

        if (_jackOverheadCameraC == null)
        {
            var go = GameObject.Find("JackOverheadCamera");
            if (go == null)
                go = new GameObject("JackOverheadCamera");

            _jackOverheadCameraC = go.GetComponent<Camera>();
            if (_jackOverheadCameraC == null)
                _jackOverheadCameraC = go.AddComponent<Camera>();

            _jackOverheadCameraC.enabled = true;
            _jackOverheadCameraC.depth = -101; // keep it out of the main stack ordering
            _jackOverheadCameraC.clearFlags = CameraClearFlags.Skybox;

            _cameraC = _jackOverheadCameraC;
        }

        if (_jackTarget == null)
        {
            AgentGoToHouseDiscrete jack;
#if UNITY_2023_1_OR_NEWER
            jack = FindAnyObjectByType<AgentGoToHouseDiscrete>();
#else
            jack = FindFirstObjectByType<AgentGoToHouseDiscrete>();
#endif
            if (jack != null) _jackTarget = jack.transform;
        }

        if (_jackTarget != null)
        {
            _jackOverheadCameraC.transform.position = _jackTarget.TransformPoint(_overheadOffset);
            var lookAt = _jackTarget.TransformPoint(_overheadLookAtOffset);
            _jackOverheadCameraC.transform.rotation = Quaternion.LookRotation(lookAt - _jackOverheadCameraC.transform.position, Vector3.up);
        }
    }

    private void CaptureCameraToImage(Camera camera, string dir, ref int index)
    {
        var prevTarget = camera.targetTexture;
        var prevActive = RenderTexture.active;

        // Важно: во время рендера конкретной камеры заставляем billboard-иконки смотреть именно в неё,
        // иначе на CamB они будут развёрнуты на CamA.
        BillboardIconCamera.CaptureOverride = camera;
        camera.targetTexture = _captureRt;
        camera.Render();
        RenderTexture.active = _captureRt;
        _captureTexture.ReadPixels(new Rect(0, 0, _captureWidth, _captureHeight), 0, 0);
        _captureTexture.Apply(false, false);
        BillboardIconCamera.CaptureOverride = null;

        camera.targetTexture = prevTarget;
        RenderTexture.active = prevActive;

        byte[] bytes;
        string ext;
        if (string.Equals(_captureFormat, "png", StringComparison.OrdinalIgnoreCase))
        {
            bytes = _captureTexture.EncodeToPNG();
            ext = "png";
        }
        else
        {
            bytes = ImageConversion.EncodeToJPG(_captureTexture, Mathf.Clamp(_jpgQuality, 1, 100));
            ext = "jpg";
        }

        var path = Path.Combine(dir, $"frame_{index:D06}.{ext}");
        File.WriteAllBytes(path, bytes);
        index++;
    }

    private static void CaptureScreenFallback(string dir, ref int index)
    {
        var path = Path.Combine(dir, $"frame_{index:D06}.png");
        ScreenCapture.CaptureScreenshot(path, 1);
        index++;
    }

    private IEnumerator CaptureAScreenAtEndOfFrame()
    {
        yield return new WaitForEndOfFrame();
        try
        {
            // Backbuffer includes Overlay UI. Нельзя ReadPixels(0,0,capW,capH):
            // origin снизу — при Screen выше capture (xvfb 1280x1024 vs 720) срезается верх.
            // Читаем весь экран и масштабируем в capture-размер.
            int sw = Screen.width;
            int sh = Screen.height;
            if (sw >= 2 && sh >= 2 && _captureTexture != null)
            {
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = null;

                if (sw == _captureWidth && sh == _captureHeight)
                {
                    _captureTexture.ReadPixels(new Rect(0, 0, sw, sh), 0, 0);
                    _captureTexture.Apply(false, false);
                }
                else
                {
                    var full = new Texture2D(sw, sh, TextureFormat.RGB24, false);
                    full.ReadPixels(new Rect(0, 0, sw, sh), 0, 0);
                    full.Apply(false, false);
                    var rt = RenderTexture.GetTemporary(_captureWidth, _captureHeight, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(full, rt);
                    RenderTexture.active = rt;
                    _captureTexture.ReadPixels(new Rect(0, 0, _captureWidth, _captureHeight), 0, 0);
                    _captureTexture.Apply(false, false);
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    Destroy(full);
                }

                RenderTexture.active = prev;

                byte[] bytes;
                string ext;
                if (string.Equals(_captureFormat, "png", StringComparison.OrdinalIgnoreCase))
                {
                    bytes = _captureTexture.EncodeToPNG();
                    ext = "png";
                }
                else
                {
                    bytes = ImageConversion.EncodeToJPG(_captureTexture, Mathf.Clamp(_jpgQuality, 1, 100));
                    ext = "jpg";
                }

                var path = Path.Combine(_captureDir, $"frame_{_frameIndexA:D06}.{ext}");
                File.WriteAllBytes(path, bytes);
                _frameIndexA++;
            }
        }
        finally
        {
            _pendingEndOfFrameCaptureA = false;
        }
    }

    private void ParseArgs(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal)) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                dict[key] = args[i + 1];
                i++;
            }
            else
            {
                dict[key] = "true";
            }
        }

        if (dict.TryGetValue("--capture-dir", out var captureDir) && !string.IsNullOrWhiteSpace(captureDir))
        {
            _captureDir = captureDir;
        }

        if (dict.TryGetValue("--capture-dir-b", out var captureDirB) && !string.IsNullOrWhiteSpace(captureDirB))
        {
            _captureDirB = captureDirB;
        }

        if (dict.TryGetValue("--capture-dir-c", out var captureDirC) && !string.IsNullOrWhiteSpace(captureDirC))
        {
            _captureDirC = captureDirC;
        }

        if (dict.TryGetValue("--capture-every", out var captureEveryStr) && int.TryParse(captureEveryStr, out var captureEvery))
        {
            _captureEvery = Mathf.Max(1, captureEvery);
        }

        if (dict.TryGetValue("--capture-width", out var widthStr) && int.TryParse(widthStr, out var width))
        {
            _captureWidth = Math.Max(64, width);
        }

        if (dict.TryGetValue("--capture-height", out var heightStr) && int.TryParse(heightStr, out var height))
        {
            _captureHeight = Math.Max(64, height);
        }

        if (dict.TryGetValue("--capture-msaa", out var msaaStr) && int.TryParse(msaaStr, out var msaa))
        {
            _captureMsaa = Mathf.Clamp(msaa, 1, 8);
        }

        if (dict.TryGetValue("--capture-fps", out var fpsStr) && int.TryParse(fpsStr, out var fps))
        {
            _captureFps = Mathf.Clamp(fps, 1, 240);
        }

        if (dict.TryGetValue("--quit-after-capture-frames", out var qfStr) && int.TryParse(qfStr, out var qf))
        {
            _quitAfterCaptureFrames = qf <= 0 ? -1 : Mathf.Max(1, qf);
        }

        if (dict.TryGetValue("--capture-format", out var fmt) && !string.IsNullOrWhiteSpace(fmt))
        {
            var f = fmt.Trim().ToLowerInvariant();
            _captureFormat = (f == "png") ? "png" : "jpg";
        }

        if (dict.TryGetValue("--capture-jpg-quality", out var qStr) && int.TryParse(qStr, out var q))
        {
            _jpgQuality = Mathf.Clamp(q, 1, 100);
        }

        if (dict.TryGetValue("--capture-camera-a", out var cameraAName) && !string.IsNullOrWhiteSpace(cameraAName))
        {
            _cameraAName = cameraAName;
        }

        if (dict.TryGetValue("--capture-a-source", out var srcA) && !string.IsNullOrWhiteSpace(srcA))
        {
            var s = srcA.Trim().ToLowerInvariant();
            _captureAScreenFinal = s == "screen";
        }

        if (dict.TryGetValue("--capture-camera-b", out var cameraBName) && !string.IsNullOrWhiteSpace(cameraBName))
        {
            _cameraBName = cameraBName;
        }

        if (dict.TryGetValue("--capture-camera-c", out var cameraCName) && !string.IsNullOrWhiteSpace(cameraCName))
        {
            _cameraCName = cameraCName;
        }

        if (dict.TryGetValue("--quit-after-steps", out var quitAfterStepsStr) && long.TryParse(quitAfterStepsStr, out var quitAfterSteps))
        {
            _quitAfterSteps = Math.Max(1, quitAfterSteps);
        }

        if (dict.TryGetValue("--quit-after-episodes", out var quitAfterEpisodesStr) && int.TryParse(quitAfterEpisodesStr, out var quitAfterEpisodes))
        {
            // 0 = выключить выход по числу эпизодов (оставить только quit-after-seconds / steps).
            _quitAfterEpisodes = quitAfterEpisodes <= 0 ? -1 : Mathf.Max(1, quitAfterEpisodes);
        }

        if (dict.TryGetValue("--quit-after-seconds", out var quitAfterSecondsStr) && float.TryParse(quitAfterSecondsStr, out var quitAfterSeconds))
        {
            _quitAfterSeconds = Mathf.Max(0.1f, quitAfterSeconds);
        }

        if (dict.TryGetValue("--quit-delay-seconds", out var quitDelayStr) && float.TryParse(quitDelayStr, out var quitDelay))
        {
            _quitDelaySeconds = Mathf.Max(0f, quitDelay);
        }
    }
}
