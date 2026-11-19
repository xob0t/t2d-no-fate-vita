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
//   -t2d-stalls <ms>                 a frame longer than ms (default 1000; 0: off) logs T2D_STALL lines from a thread
//                                    of the probe's, every 250 ms while it lasts: the free memory and, in a T2D_TRACE=1
//                                    build, the game methods the main thread entered last and how many since the frame
//                                    began; then how long the frame took
//   -t2d-peek <path>[,<path>...]     static fields by reflection, Namespace.Type.field, then .field or [index] (into
//                                    an array or a haxe Array's __a) as often as wanted; T2D_PEEK lines on the first
//                                    frame and whenever a value changes
//   -t2d-invoke <call>[,<call>...]   at the second heartbeat (or 10 s), calls by reflection and logs T2D_INVOKE lines with
//                                    the result or the exception: <path to an object or a type>::<method>[::<arg>...],
//                                    each arg an int, @<peek path>, or a string
//   -t2d-atlas-sync                  the atlas prefetches with Resources.Load, not LoadAsync (Atlas.PrefetchAsync)
// A screenshot is the frame read back at its end and saved as a PNG (on the PS Vita 960x544, about 0.6 s of stall,
// since ScreenCapture writes nothing there); use a path in ux0:data/T2DNoFate.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public class Probe : MonoBehaviour
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // " heapMB=.. freeLPDDRMB=.. freeCDRAMMB=.. freePHYSCONTMB=..".
        public static string Memory()
        {
            var line = new StringBuilder();
            line.Append(" heapMB=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1", Inv));
            for (int kind = 0; kind < FreeMemory.Names.Length; kind++)
            {
                long free = FreeMemory.Bytes(kind);
                if (free >= 0) line.Append(" free").Append(FreeMemory.Names[kind]).Append("MB=").Append((free / 1048576.0).ToString("F1", Inv));
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
            stallMs = (int)Flags.Number("-t2d-stalls", 1, 1000f);
            string peek = Flags.Value("-t2d-peek");
            if (peek != null) peeks = peek.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            string invoke = Flags.Value("-t2d-invoke");
            if (invoke != null) invokes = invoke.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            Atlas.PrefetchAsync = !Flags.Has("-t2d-atlas-sync");
            Trace.MarkMain();
            tickMs = Now();
            if (stallMs > 0) new System.Threading.Thread(WatchStalls) { IsBackground = true, Name = "T2DStalls" }.Start();
            var host = new GameObject("T2DProbe");
            DontDestroyOnLoad(host);
            probe = host.AddComponent<Probe>();
        }

        static string Stamp()
        {
            return "at " + PlayerLog.Seconds.ToString("F2", Inv) + " s (player " + Time.realtimeSinceStartup.ToString("F2", Inv) + " s), frame " +
                   Time.frameCount + Memory();
        }

        void Awake() { Debug.Log("T2D_PROBE awake " + Stamp()); }
        void Start() { Debug.Log("T2D_PROBE start " + Stamp()); }

        int tickedFrame = -1;
        static int ticks, errors;

        void Update()
        {
            Tick("update");
        }

        // From Update, and from SpriteBatch.Flush (the game's OnRenderObject) as a fallback: once a frame.
        public static void FromRender()
        {
            if (probe != null) probe.Tick("render");
        }

        void Tick(string from)
        {
            if (tickedFrame == Time.frameCount) return;
            tickedFrame = Time.frameCount;
            int now = Now();
            if (stalled == tickNumber)
                Debug.Log("T2D_STALL frame " + tickNumber + " ended after " + (now - tickMs) + " ms" + Memory());
            tickTraced = Trace.MainCount;
            tickMs = now;
            tickNumber++;
            if (peeks != null) Peek();
            if (invokes != null && PlayerLog.Seconds >= invokeAt) Invoke();
            if (ticks++ == 0) Debug.Log("T2D_PROBE first tick from " + from + " " + Stamp());
            try
            {
                Step();
            }
            catch (Exception e)
            {
                if (errors++ < 5) Debug.Log("T2D_PROBE error " + e);
            }
        }

        // ---- stalls -----------------------------------------------------------------------------------------------

        static int stallMs;
        static volatile int tickMs, tickNumber, stalled = -1;
        static long tickTraced;

        static int Now() { return (int)(PlayerLog.Seconds * 1000.0); }

        // The probe's own thread: while the main thread hasn't ticked for stallMs, every 250 ms a line on where it is.
        static void WatchStalls()
        {
            int next = 0;
            while (true)
            {
                System.Threading.Thread.Sleep(50);
                int frame = tickNumber, since = Now() - tickMs;
                if (since < stallMs) continue;
                bool first = stalled != frame;
                if (first)
                {
                    stalled = frame;
                    next = since;
                }
                if (since < next) continue;
                next = since + 250;
                var line = new StringBuilder("T2D_STALL frame ").Append(frame).Append(" +").Append(since).Append(" ms");
                line.Append(Memory());
                if (Trace.On)
                {
                    line.Append(" traced=").Append(Trace.MainCount - tickTraced).Append(", last entered:\n");
                    line.Append(Trace.MainTrail(first ? 12 : 3).TrimEnd());
                }
                Debug.Log(line.ToString());
            }
        }

        // ---- peeks ------------------------------------------------------------------------------------------------

        static string[] peeks, peeked;

        static void Peek()
        {
            if (peeked == null) peeked = new string[peeks.Length];
            for (int i = 0; i < peeks.Length; i++)
            {
                string value;
                try
                {
                    value = Describe(Resolve(peeks[i]));
                }
                catch (Exception e)
                {
                    value = "(" + e.GetType().Name + ": " + e.Message + ")";
                }
                if (value == peeked[i]) continue;
                peeked[i] = value;
                Debug.Log("T2D_PEEK " + peeks[i] + " = " + value + " (frame " + Time.frameCount + ")");
            }
        }

        static string[] invokes;
        static double invokeAt = 10.0;

        static void Invoke()
        {
            string[] calls = invokes;
            invokes = null;
            foreach (string call in calls)
            {
                string result;
                try
                {
                    string[] parts = call.Split(new[] { "::" }, StringSplitOptions.None);
                    Type type = FindType(parts[0]);
                    object target = type == null ? Resolve(parts[0]) : null;
                    if (type == null && target == null) throw new ArgumentException(parts[0] + " is null");
                    Type owner = type ?? target.GetType();
                    var args = new object[parts.Length - 2];
                    for (int i = 0; i < args.Length; i++)
                    {
                        string a = parts[i + 2];
                        int n;
                        if (a.StartsWith("@")) args[i] = Resolve(a.Substring(1));
                        else if (int.TryParse(a, NumberStyles.Integer, Inv, out n)) args[i] = n;
                        else args[i] = a;
                    }
                    MethodInfo method = owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                        .FirstOrDefault(mi => mi.Name == parts[1] && mi.GetParameters().Length == args.Length);
                    if (method == null) throw new ArgumentException("no " + parts[1] + " with " + args.Length + " parameters in " + owner.FullName);
                    result = Describe(method.Invoke(method.IsStatic ? null : target, args));
                }
                catch (Exception e)
                {
                    Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                    result = "threw " + inner.GetType().Name + ": " + inner.Message + (Trace.On ? " after:\n" + Trace.Trail(12).TrimEnd() : "");
                }
                Debug.Log("T2D_INVOKE " + call + " = " + result);
            }
        }

        static readonly Dictionary<string, Type> types = new Dictionary<string, Type>();

        static Type FindType(string name)
        {
            Type type;
            if (!types.TryGetValue(name, out type))
            {
                type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
                types[name] = type;
            }
            return type;
        }

        static object Resolve(string path)
        {
            string[] parts = path.Split('.');
            Type type = null;
            int at = 0;
            for (int n = parts.Length - 1; n > 0 && type == null; n--)
            {
                type = FindType(string.Join(".", parts, 0, n));
                at = n;
            }
            if (type == null) throw new ArgumentException("no type in " + path);
            object value = null;
            bool first = true;
            for (int k = at; k < parts.Length; k++)
            {
                string part = parts[k];
                var indexes = new List<int>();
                int bracket = part.IndexOf('[');
                if (bracket >= 0)
                {
                    foreach (string index in part.Substring(bracket + 1).TrimEnd(']').Split(new[] { "][" }, StringSplitOptions.None))
                        indexes.Add(int.Parse(index, Inv));
                    part = part.Substring(0, bracket);
                }
                if (part.Length > 0)
                {
                    if (!first && value == null) return null;
                    Type owner = first ? type : value.GetType();
                    FieldInfo field = owner.GetField(part, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                    if (field == null) throw new ArgumentException("no field " + part + " in " + owner.FullName);
                    value = field.GetValue(field.IsStatic ? null : value);
                    first = false;
                }
                foreach (int index in indexes)
                {
                    if (value == null) return null;
                    var array = value as Array;
                    if (array == null)
                    {
                        FieldInfo items = value.GetType().GetField("__a");
                        array = items == null ? null : items.GetValue(value) as Array;
                    }
                    if (array == null) throw new ArgumentException(part + " is no array");
                    if (index < 0 || index >= array.Length) return "(index " + index + " past " + array.Length + ")";
                    value = array.GetValue(index);
                }
            }
            return value;
        }

        static string Describe(object value)
        {
            if (value == null) return "null";
            var text = value as string;
            if (text != null) return "\"" + (text.Length > 40 ? text.Substring(0, 40) + "..." : text) + "\" (" + text.Length + " chars)";
            var array = value as Array;
            if (array != null) return value.GetType().Name + " length " + array.Length;
            if (value is IFormattable) return ((IFormattable)value).ToString(null, Inv);
            if (value is bool) return value.ToString();
            return value.GetType().FullName;
        }

        void Step()
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
            line.Append(Atlas.Summary());
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
