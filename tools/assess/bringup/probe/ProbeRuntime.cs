// The probe player's own monitor, loaded before the game's scene (Assets/Probe in the probe project).
//  - Keeps the run quiet and harmless: AudioListener volume 0, cursor unlocked and unconfined, the window
//    windowed (Windows' cursor clip is global, so a player on a hidden desktop could trap the mouse).
//  - Logs a PROBE line every second (frame, fps, active scene, root objects, cameras, renderers), and the
//    first occurrence of each distinct exception message as PROBE_EXC with a running count.
//  - -probe-shots <dir>: a screenshot every -probe-shot-every seconds (default 5) into <dir>.
//  - -probe-quit <seconds>: quit after that long.
//  - Caps the frame rate at 60 (vSyncCount 0, targetFrameRate 60): vsync doesn't pace a window on a
//    hidden desktop, and the game's logic counts frames.
// No game content: it only looks at the engine's state.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class T2DProbe : MonoBehaviour
{
    static readonly Dictionary<string, int> exceptions = new Dictionary<string, int>();
    string shots;
    float shotEvery = 5, nextShot = 1, nextLog = 1, quitAt = -1;
    int shotIndex, lastFrame;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        var go = new GameObject("T2DProbe");
        DontDestroyOnLoad(go);
        go.AddComponent<T2DProbe>();
        Application.logMessageReceived += OnLog;
        Debug.Log("PROBE start " + Application.unityVersion + " args=" + string.Join(" ", Environment.GetCommandLineArgs()));
    }

    static void OnLog(string message, string stack, LogType type)
    {
        if (type != LogType.Exception && type != LogType.Error) return;
        string key = type + ": " + (message.Length > 200 ? message.Substring(0, 200) : message);
        int n;
        exceptions.TryGetValue(key, out n);
        exceptions[key] = n + 1;
        if (n == 0) Console.WriteLine("PROBE_EXC first t=" + Time.realtimeSinceStartup.ToString("F1") + " frame=" + Time.frameCount + " " + key);
    }

    static string Arg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    void Awake()
    {
        shots = Arg("-probe-shots");
        string every = Arg("-probe-shot-every");
        if (every != null) shotEvery = float.Parse(every, System.Globalization.CultureInfo.InvariantCulture);
        string quit = Arg("-probe-quit");
        if (quit != null) quitAt = float.Parse(quit, System.Globalization.CultureInfo.InvariantCulture);
        if (shots != null) System.IO.Directory.CreateDirectory(shots);
        Quiet();
    }

    static void Quiet()
    {
        AudioListener.volume = 0;
        if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
        if (Application.targetFrameRate != 60) Application.targetFrameRate = 60;
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        if (Screen.fullScreen) Screen.fullScreen = false;
    }

    void Update()
    {
        Quiet();
        float t = Time.realtimeSinceStartup;
        if (t >= nextLog)
        {
            nextLog = t + 1;
            Scene scene = SceneManager.GetActiveScene();
            Debug.Log("PROBE t=" + t.ToString("F1") + " frame=" + Time.frameCount + " fps=" + (Time.frameCount - lastFrame) +
                      " scene=" + scene.name + " roots=" + scene.rootCount + " cams=" + Camera.allCamerasCount +
                      " renderers=" + FindObjectsOfType<Renderer>().Length + " screen=" + Screen.width + "x" + Screen.height +
                      " exceptions=" + Total());
            lastFrame = Time.frameCount;
        }
        if (shots != null && t >= nextShot)
        {
            nextShot = t + shotEvery;
            string path = System.IO.Path.Combine(shots, string.Format("shot-{0:D3}-t{1:F0}.png", shotIndex++, t));
            ScreenCapture.CaptureScreenshot(path);
        }
        if (quitAt > 0 && t >= quitAt)
        {
            Debug.Log("PROBE quit t=" + t.ToString("F1") + " frame=" + Time.frameCount);
            foreach (var e in exceptions) Debug.Log("PROBE_EXC_SUMMARY " + e.Value + "x " + e.Key);
            quitAt = -1;
            Application.Quit();
        }
    }

    static int Total()
    {
        int n = 0;
        foreach (var v in exceptions.Values) n += v;
        return n;
    }
}
