using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The bring-up probe: what the player did when, and what a frame costs. After butcher-vita's compat/Diag.cs (same
// author, MIT), cut down to what this port's bring-up measures.
// Always on: T2D_BOOT lines with the seconds since T2DCompat started (PlayerLog.Seconds) at T2DCompat's start, each
// scene load and the first five frames, with the free memory; on the PS Vita that is main memory (LPDDR), video memory
// (CDRAM) and physically contiguous memory, from UnityEngine.PSVita.Diagnostics, read by reflection since T2DCompat is
// built for Windows too (asset_rules.PLATFORM_LINK_PRESERVE keeps it).
// Flags (Flags.cs):
//   -t2d-heartbeat <s>               every s seconds a T2D_HEARTBEAT line: frames, frame time (mean, p50, p90, worst),
//                                    managed heap, free memory, and per frame the sprites SpriteBatch drew, its runs
//                                    (draws), the runs that changed texture (page switches), and the off-screen draws
//   -t2d-shot <s> <path>             a screenshot s seconds after the start
//   -t2d-shots <s> <prefix> [after]  a screenshot every s seconds from after seconds on, <prefix>-<seconds>.png
//   -t2d-quit-after <s>              quit s seconds after the start
// A screenshot is the frame read back at its end and saved as a PNG (on the PS Vita 960x544, about 0.6 s of stall,
// since ScreenCapture writes nothing there); use a path in ux0:data/T2DNoFate.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public class Probe : MonoBehaviour
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly KeyValuePair<string, MethodInfo>[] FreeMemory = FindFreeMemory();

        static KeyValuePair<string, MethodInfo>[] FindFreeMemory()
        {
            try
            {
                Type diagnostics = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("UnityEngine.PSVita.Diagnostics", false)).FirstOrDefault(t => t != null);
                if (diagnostics == null) return new KeyValuePair<string, MethodInfo>[0];
                return new[] { "LPDDR", "CDRAM", "PHYSCONT" }
                    .Select(n => new KeyValuePair<string, MethodInfo>("free" + n, diagnostics.GetMethod("GetFreeMemory" + n, BindingFlags.Static | BindingFlags.Public)))
                    .Where(p => p.Value != null).ToArray();
            }
            catch (Exception)
            {
                return new KeyValuePair<string, MethodInfo>[0];
            }
        }

        // " heapMB=.. freeLPDDRMB=.. freeCDRAMMB=.. freePHYSCONTMB=..".
        public static string Memory()
        {
            var line = new StringBuilder();
            line.Append(" heapMB=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1", Inv));
            foreach (KeyValuePair<string, MethodInfo> free in FreeMemory)
            {
                try
                {
                    line.Append(' ').Append(free.Key).Append("MB=").Append((Convert.ToInt64(free.Value.Invoke(null, null)) / 1048576.0).ToString("F1", Inv));
                }
                catch (Exception)
                {
                }
            }
            return line.ToString();
        }

        static Probe probe;
        static float heartbeat, quitAfter = -1f, shotAt = -1f, shotsEvery = -1f, shotsAfter;
        static string shotPath, shotsPrefix;
        static bool shotTaken;
        double lastBeat, lastShots = -1e9;
        int bootFrames;
        readonly List<float> deltas = new List<float>();
        long sprites, runs, switches, offscreen;
        int maxSprites, maxRuns;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            PlayerLog.Install();  // first, whichever order the player runs the two in
            Debug.Log("T2D_BOOT compat " + Stamp());
            SceneManager.sceneLoaded += (scene, mode) => Debug.Log("T2D_BOOT scene " + scene.name + " (" + mode + ") " + Stamp());
            Application.quitting += () => Debug.Log("T2D_QUIT at " + PlayerLog.Seconds.ToString("F2", Inv) + " s, frame " + Time.frameCount);
            heartbeat = Flags.Number("-t2d-heartbeat", 1, 0f);
            quitAfter = Flags.Number("-t2d-quit-after", 1, -1f);
            shotAt = Flags.Number("-t2d-shot", 1, -1f);
            shotPath = Flags.Word("-t2d-shot", 2);
            shotsEvery = Flags.Number("-t2d-shots", 1, -1f);
            shotsPrefix = Flags.Word("-t2d-shots", 2);
            shotsAfter = Flags.Number("-t2d-shots", 3, 0f);
            var host = new GameObject("T2DProbe");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            probe = host.AddComponent<Probe>();
        }

        static string Stamp()
        {
            return "at " + PlayerLog.Seconds.ToString("F2", Inv) + " s (player " + Time.realtimeSinceStartup.ToString("F2", Inv) + " s), frame " +
                   Time.frameCount + Memory();
        }

        void Update()
        {
            double now = PlayerLog.Seconds;
            if (bootFrames < 5)
            {
                bootFrames++;
                Debug.Log("T2D_BOOT frame " + bootFrames + " " + Stamp());
                lastBeat = now;
            }
            else if (heartbeat > 0f)
            {
                deltas.Add(Time.unscaledDeltaTime * 1000f);
                sprites += SpriteBatch.LastSprites;
                runs += SpriteBatch.LastRuns;
                switches += SpriteBatch.LastPageSwitches;
                offscreen += SpriteBatch.LastOffscreen;
                maxSprites = Math.Max(maxSprites, SpriteBatch.LastSprites);
                maxRuns = Math.Max(maxRuns, SpriteBatch.LastRuns);
                if (now - lastBeat >= heartbeat) Beat(now);
            }
            if (quitAfter > 0f && now >= quitAfter)
            {
                quitAfter = -1f;
                Debug.Log("T2D_PROBE quit after " + now.ToString("F1", Inv) + " s");
                PlayerLog.Flush();
                Application.Quit();
            }
            if (!shotTaken && shotAt >= 0f && shotPath != null && now >= shotAt)
            {
                shotTaken = true;
                Shot(shotPath);
            }
            if (shotsEvery > 0f && shotsPrefix != null && now >= shotsAfter && now - lastShots >= shotsEvery)
            {
                lastShots = now;
                Shot(shotsPrefix + "-" + ((int)now).ToString(Inv) + ".png");
            }
        }

        void Beat(double now)
        {
            int frames = deltas.Count;
            var line = new StringBuilder("T2D_HEARTBEAT t=");
            line.Append(now.ToString("F1", Inv)).Append(" frames=").Append(frames);
            if (frames > 0)
            {
                double sum = 0;
                float worst = 0f;
                foreach (float d in deltas)
                {
                    sum += d;
                    worst = Math.Max(worst, d);
                }
                deltas.Sort();
                line.Append(" fps=").Append((frames / (now - lastBeat)).ToString("F1", Inv));
                line.Append(" ms=").Append((sum / frames).ToString("F1", Inv));
                line.Append(" p50=").Append(deltas[frames / 2].ToString("F1", Inv));
                line.Append(" p90=").Append(deltas[Math.Min(frames - 1, frames * 9 / 10)].ToString("F1", Inv));
                line.Append(" worst=").Append(worst.ToString("F1", Inv));
                line.Append(" sprites=").Append((sprites / (double)frames).ToString("F0", Inv)).Append("/max").Append(maxSprites);
                line.Append(" runs=").Append((runs / (double)frames).ToString("F1", Inv)).Append("/max").Append(maxRuns);
                line.Append(" pageSwitches=").Append((switches / (double)frames).ToString("F1", Inv));
                line.Append(" offscreen=").Append((offscreen / (double)frames).ToString("F1", Inv));
            }
            line.Append(" gc=").Append(GC.CollectionCount(0));
            line.Append(Memory());
            Debug.Log(line.ToString());
            deltas.Clear();
            sprites = runs = switches = offscreen = 0;
            maxSprites = maxRuns = 0;
            lastBeat = now;
        }

        static void Shot(string path)
        {
            if (probe != null) probe.StartCoroutine(SaveFrame(path));
        }

        static IEnumerator SaveFrame(string path)
        {
            yield return new WaitForEndOfFrame();
            var picture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            try
            {
                picture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                picture.Apply(false);
                System.IO.File.WriteAllBytes(path, picture.EncodeToPNG());
                Debug.Log("T2D_PROBE screenshot " + path);
            }
            catch (Exception exception)
            {
                Debug.Log("T2D_PROBE screenshot " + path + " failed: " + exception.Message);
            }
            Destroy(picture);
        }
    }
}
