using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Profiling;

// The PS Vita texture test for Terminator 2D: NO FATE's atlases (tools/assess/vita-texture-test).
// One launch runs the steps in ux0:data/t2dtex/args.txt (renamed to args.used.txt as it is read), separated by ';':
//   single <v,v,...>   load each variant alone, draw it, screenshot it, unload it: load time, memory, peak
//   fill <v>           load the variant, then more atlases of its format until memory runs low (png: the game's
//                      12 packs in its order; others: copies made at run time with the same format and size)
//   draw <v,v,...>     frame time with 0, 1, 4 and 16 full-screen layers of the variant, vsync off
//   idle <s>           log memory for s seconds
// Writes ux0:data/t2dtex/log.txt (lines "T2D ..."), screenshots shot-<variant>.png there, then quits.
public class TexTest : MonoBehaviour
{
    const string Folder = "ux0:data/t2dtex";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    // Free memory readers of UnityEngine.PSVita.Diagnostics (the PS Vita player only; link.xml keeps them).
    static Func<int> freeLP, freeCD, freePC;

    // Variants: the resource and what its texture should be.
    class Variant
    {
        public string Name, Path; public int Size; public TextureFormat Format; public bool Png;
    }
    static readonly Variant[] Variants =
    {
        new Variant { Name = "png", Path = "texturepack/TexturePack0", Size = 4096, Format = TextureFormat.ARGB32, Png = true },
        new Variant { Name = "rgba32_4096", Path = "tex/tp0_rgba32_4096", Size = 4096, Format = TextureFormat.RGBA32 },
        new Variant { Name = "rgba32_2048", Path = "tex/tp0_rgba32_2048", Size = 2048, Format = TextureFormat.RGBA32 },
        new Variant { Name = "rgba4444_4096", Path = "tex/tp0_rgba4444_4096", Size = 4096, Format = TextureFormat.RGBA4444 },
        new Variant { Name = "dxt5_4096", Path = "tex/tp0_dxt5_4096", Size = 4096, Format = TextureFormat.DXT5 },
        new Variant { Name = "dxt5_2048", Path = "tex/tp0_dxt5_2048", Size = 2048, Format = TextureFormat.DXT5 },
        new Variant { Name = "pvrtc4_4096", Path = "tex/tp0_pvrtc4_4096", Size = 4096, Format = TextureFormat.PVRTC_RGBA4 },
        new Variant { Name = "pvrtc2_4096", Path = "tex/tp0_pvrtc2_4096", Size = 4096, Format = TextureFormat.PVRTC_RGBA2 },
    };
    // The game's load order (AssetsEmbed.staticInit loads pack 1 for the logo, BBSplash the others).
    static readonly int[] GameOrder = { 1, 0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    // Margins left free when filling (bytes).
    const long MarginLP = 40L << 20, MarginPC = 8L << 20;
    const int FillCap = 128;

    StreamWriter logFile;
    Texture shown;
    int layers = 1;
    string label = "";
    readonly List<Texture> resident = new List<Texture>();
    Material blit;

    struct Mem
    {
        public long LP, CD, PC, UnityAlloc, UnityReserved, MonoUsed, MonoHeap;
        public override string ToString()
        {
            return string.Format(Inv, "freeLP={0:F1} freeCD={1:F1} freePC={2:F1} MB unityAlloc={3:F1} unityReserved={4:F1} monoUsed={5:F1} monoHeap={6:F1} MB",
                LP / 1048576.0, CD / 1048576.0, PC / 1048576.0, UnityAlloc / 1048576.0, UnityReserved / 1048576.0, MonoUsed / 1048576.0, MonoHeap / 1048576.0);
        }
    }

    static Mem Read()
    {
        return new Mem
        {
            LP = freeLP == null ? -1 : freeLP(), CD = freeCD == null ? -1 : freeCD(), PC = freePC == null ? -1 : freePC(),
            UnityAlloc = Profiler.GetTotalAllocatedMemoryLong(), UnityReserved = Profiler.GetTotalReservedMemoryLong(),
            MonoUsed = Profiler.GetMonoUsedSizeLong(), MonoHeap = Profiler.GetMonoHeapSizeLong(),
        };
    }

    static string Mb(long bytes) { return (bytes / 1048576.0).ToString("F1", Inv); }

    void Log(string line)
    {
        line = "T2D " + (Clock.ElapsedMilliseconds / 1000.0).ToString("F2", Inv) + " " + line;
        Debug.Log(line);
        if (logFile != null) { logFile.WriteLine(line); logFile.Flush(); }
    }

    // The memory sampler: the lowest free memory while a load runs (a thread, since the load is one call).
    volatile bool sampling;
    long minLP, minCD, minPC;
    Thread sampler;
    bool samplerBroken;

    void StartSampling(Mem now)
    {
        minLP = now.LP; minCD = now.CD; minPC = now.PC;
        if (samplerBroken || freeLP == null) return;
        sampling = true;
        sampler = new Thread(() =>
        {
            try
            {
                while (sampling)
                {
                    long lp = freeLP(), cd = freeCD(), pc = freePC();
                    if (lp < minLP) minLP = lp;
                    if (cd < minCD) minCD = cd;
                    if (pc < minPC) minPC = pc;
                    Thread.Sleep(1);
                }
            }
            catch (Exception e) { samplerBroken = true; Debug.Log("T2D sampler failed: " + e.Message); }
        }) { IsBackground = true };
        sampler.Start();
    }

    void StopSampling(Mem now)
    {
        sampling = false;
        if (sampler != null) sampler.Join();
        sampler = null;
        if (now.LP < minLP) minLP = now.LP;
        if (now.CD < minCD) minCD = now.CD;
        if (now.PC < minPC) minPC = now.PC;
    }

    [DllImport("ButcherClock")] static extern int ButcherClockSetArm(int mhz);
    [DllImport("ButcherClock")] static extern int ButcherClockGetArm();
    [DllImport("ButcherClock")] static extern int ButcherClockSetGpu(int mhz);
    [DllImport("ButcherClock")] static extern int ButcherClockGetGpu();

    void Awake()
    {
        Mem first;
        Type diagnostics = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEngine.PSVita.Diagnostics", false)).FirstOrDefault(t => t != null);
        if (diagnostics != null)
        {
            freeLP = Reader(diagnostics, "GetFreeMemoryLPDDR");
            freeCD = Reader(diagnostics, "GetFreeMemoryCDRAM");
            freePC = Reader(diagnostics, "GetFreeMemoryPHYSCONT");
        }
        first = Read();
        try { Directory.CreateDirectory(Folder); } catch (Exception) { }
        try
        {
            string path = Path.Combine(Folder, "log.txt");
            if (File.Exists(path)) { string prev = Path.Combine(Folder, "log.prev.txt"); if (File.Exists(prev)) File.Delete(prev); File.Move(path, prev); }
            logFile = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
        }
        catch (Exception e) { Debug.Log("T2D no log file: " + e.Message); }
        Log("START awake " + first);
        Log("SYSINFO platform=" + Application.platform + " unity=" + Application.unityVersion + " systemMemoryMB=" + SystemInfo.systemMemorySize +
            " graphicsMemoryMB=" + SystemInfo.graphicsMemorySize + " gfx=" + SystemInfo.graphicsDeviceType + " maxTex=" + SystemInfo.maxTextureSize +
            " copyTexture=" + SystemInfo.copyTextureSupport + " npot=" + SystemInfo.npotSupport + " screen=" + Screen.width + "x" + Screen.height);
        foreach (TextureFormat f in new[] { TextureFormat.RGBA32, TextureFormat.ARGB32, TextureFormat.RGBA4444, TextureFormat.ARGB4444, TextureFormat.RGB565,
            TextureFormat.DXT1, TextureFormat.DXT5, TextureFormat.PVRTC_RGBA4, TextureFormat.PVRTC_RGBA2, TextureFormat.PVRTC_RGB4, TextureFormat.ETC_RGB4,
            TextureFormat.ETC2_RGBA8, TextureFormat.ASTC_RGBA_4x4, TextureFormat.Alpha8, TextureFormat.R8, TextureFormat.BC7, TextureFormat.DXT5Crunched })
        {
            bool ok; try { ok = SystemInfo.SupportsTextureFormat(f); } catch (Exception) { ok = false; }
            Log("FORMAT " + f + " supported=" + ok);
        }
        try
        {
            int armSet = ButcherClockSetArm(444), gpuSet = ButcherClockSetGpu(222);
            Log("CLOCK set arm=" + armSet + " gpu=" + gpuSet + " now arm=" + ButcherClockGetArm() + " gpu=" + ButcherClockGetGpu() + " MHz");
        }
        catch (Exception e) { Log("CLOCK not set (" + e.GetType().Name + ": " + e.Message + "); the player's defaults"); }
        Camera cam = GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color32(40, 40, 60, 255);
        blit = Resources.Load<Material>("blit"); // Sprites/Default (TexTestBuild makes it)
        if (blit == null) blit = new Material(Shader.Find("Sprites/Default"));
    }

    static Func<int> Reader(Type type, string name)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public);
        return method == null ? null : (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), method);
    }

    IEnumerator Start()
    {
        yield return null;
        Log("FIRSTFRAME " + Read());
        string args = "";
        string argsPath = Path.Combine(Folder, "args.txt");
        try
        {
            if (File.Exists(argsPath))
            {
                args = File.ReadAllText(argsPath).Trim();
                string used = Path.Combine(Folder, "args.used.txt");
                if (File.Exists(used)) File.Delete(used);
                File.Move(argsPath, used);
            }
        }
        catch (Exception e) { Log("ARGS unreadable: " + e.Message); }
        if (args.Length == 0) args = "idle 5";
        Log("ARGS " + args);
        foreach (string step in args.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            string[] words = step.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string what = words.Length > 1 ? words[1] : "";
            Log("STEP " + step);
            if (words[0] == "single") foreach (string v in what.Split(',')) yield return Single(Find(v));
            else if (words[0] == "fill") yield return Fill(Find(what));
            else if (words[0] == "draw") foreach (string v in what.Split(',')) yield return Draw(Find(v));
            else if (words[0] == "idle")
            {
                float until = Time.realtimeSinceStartup + float.Parse(what, Inv);
                while (Time.realtimeSinceStartup < until) { Log("IDLE " + Read()); yield return new WaitForSecondsRealtime(1f); }
            }
            else Log("STEP unknown " + step);
        }
        Log("DONE " + Read());
        if (logFile != null) logFile.Close();
        logFile = null;
        yield return new WaitForSecondsRealtime(0.5f);
        Application.Quit();
    }

    static Variant Find(string name)
    {
        Variant v = Variants.FirstOrDefault(x => x.Name == name.Trim());
        if (v == null) throw new ArgumentException("no variant " + name);
        return v;
    }

    IEnumerator Settle()
    {
        System.GC.Collect();
        yield return Resources.UnloadUnusedAssets();
        System.GC.Collect();
        for (int i = 0; i < 3; i++) yield return null;
    }

    // Loads a variant, timing the parts. Returns the texture and what to unload with it.
    class Loaded { public Texture2D Tex; public TextAsset Asset; public string Times; public long Ms; }

    static Loaded Load(Variant v, int pack)
    {
        var l = new Loaded();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (v.Png)
        {
            // BitmapData.loadTexturePacks, as the game does it.
            Texture2D t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            t.filterMode = FilterMode.Point;
            TextAsset asset = Resources.Load<TextAsset>("texturepack/TexturePack" + pack);
            long loadMs = sw.ElapsedMilliseconds;
            byte[] bytes = asset.bytes;
            long bytesMs = sw.ElapsedMilliseconds;
            bool ok = t.LoadImage(bytes, true);
            long decodeMs = sw.ElapsedMilliseconds;
            l.Tex = ok ? t : null; l.Asset = asset;
            l.Times = string.Format(Inv, "resourcesLoad={0} bytes={1} loadImage={2} ok={3} pngBytes={4}", loadMs, bytesMs - loadMs, decodeMs - bytesMs, ok, bytes.Length);
        }
        else
        {
            l.Tex = Resources.Load<Texture2D>(v.Path);
            l.Times = "resourcesLoad=" + sw.ElapsedMilliseconds;
        }
        l.Ms = sw.ElapsedMilliseconds;
        return l;
    }

    IEnumerator Single(Variant v)
    {
        yield return Settle();
        Mem before = Read();
        StartSampling(before);
        Loaded l = Load(v, 0);
        Mem loaded = Read();
        StopSampling(loaded);
        if (l.Tex == null) { Log("SINGLE " + v.Name + " FAILED to load " + l.Times); yield break; }
        Show(l.Tex, v.Name, 1);
        float frameStart = Time.realtimeSinceStartup;
        yield return null; // drawn in this frame
        yield return null;
        float firstFrames = Time.realtimeSinceStartup - frameStart;
        for (int i = 0; i < 10; i++) yield return null;
        Mem drawn = Read();
        string shot = Path.Combine(Folder, "shot-" + v.Name + ".png");
        yield return SaveFrame(shot);
        Log(string.Format(Inv, "SINGLE {0} tex={1}x{2} {3} mips={4} filter={5} loadMs={6} ({7}) twoFramesMs={8:F0} " +
            "usedLP={9} usedCD={10} usedPC={11} peakLP={12} peakCD={13} peakPC={14} MB sampler={15}",
            v.Name, l.Tex.width, l.Tex.height, l.Tex.format, l.Tex.mipmapCount, l.Tex.filterMode, l.Ms, l.Times, firstFrames * 1000,
            Mb(before.LP - drawn.LP), Mb(before.CD - drawn.CD), Mb(before.PC - drawn.PC),
            Mb(before.LP - minLP), Mb(before.CD - minCD), Mb(before.PC - minPC), samplerBroken ? "broken" : (freeLP == null ? "none" : "ok")));
        Log("SINGLE_MEM " + v.Name + " before " + before);
        Log("SINGLE_MEM " + v.Name + " loaded " + loaded);
        Log("SINGLE_MEM " + v.Name + " drawn " + drawn);
        if (l.Asset != null)
        {
            Resources.UnloadAsset(l.Asset);
            yield return Settle();
            Mem noAsset = Read();
            Log("SINGLE_MEM " + v.Name + " textAssetUnloaded " + noAsset + " (the game keeps it: Resources.Load caches it)");
        }
        Show(null, "", 0);
        yield return null;
        if (v.Png) Destroy(l.Tex); else Resources.UnloadAsset(l.Tex);
        yield return Settle();
        Mem after = Read();
        Log(string.Format(Inv, "SINGLE_FREED {0} back LP={1} CD={2} PC={3} MB of the before " + after, v.Name,
            Mb(after.LP - drawn.LP), Mb(after.CD - drawn.CD), Mb(after.PC - drawn.PC)));
    }

    IEnumerator Fill(Variant v)
    {
        yield return Settle();
        Mem start = Read();
        Log("FILL " + v.Name + " start " + start);
        var made = new List<Texture2D>();
        var assets = new List<TextAsset>();
        Texture2D source = null;
        Mem prev = start;
        long stepLP = 0, stepCD = 0, stepPC = 0, peakLP = 0, peakCD = 0, peakPC = 0;
        string stop = "cap";
        for (int n = 0; n < (v.Png ? GameOrder.Length : FillCap); n++)
        {
            // Would the next one fit? Take the largest step and peak seen so far.
            long needLP = Math.Max(stepLP, peakLP), needCD = Math.Max(stepCD, peakCD), needPC = Math.Max(stepPC, peakPC);
            // Video memory (CDRAM) may run out: the player then puts textures in main memory, so only main memory stops it.
            if (n > 0 && (prev.LP - needLP < MarginLP || prev.PC - needPC < MarginPC))
            {
                stop = string.Format(Inv, "margin: next needs LP={0} CD={1} PC={2} MB", Mb(needLP), Mb(needCD), Mb(needPC));
                break;
            }
            StartSampling(prev);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Texture2D t;
            string how;
            if (v.Png)
            {
                Loaded l = Load(v, GameOrder[n]);
                t = l.Tex; assets.Add(l.Asset); how = "pack" + GameOrder[n] + " " + l.Times;
            }
            else if (n == 0)
            {
                Loaded l = Load(v, 0);
                t = source = l.Tex; how = "asset " + l.Times;
            }
            else
            {
                t = new Texture2D(source.width, source.height, source.format, false);
                t.filterMode = FilterMode.Point;
                t.Apply(false, true);
                how = "new+Apply";
                if (SystemInfo.copyTextureSupport != UnityEngine.Rendering.CopyTextureSupport.None)
                {
                    try { Graphics.CopyTexture(source, t); how += "+CopyTexture"; }
                    catch (Exception e) { how += " copyFailed:" + e.Message; }
                }
            }
            long ms = sw.ElapsedMilliseconds;
            Mem afterLoad = Read();
            StopSampling(afterLoad);
            if (t == null) { stop = "load failed"; break; }
            made.Add(t);
            resident.Add(t);
            yield return null; // drawn (small) once
            yield return null;
            Mem now = Read();
            stepLP = Math.Max(stepLP, prev.LP - now.LP); stepCD = Math.Max(stepCD, prev.CD - now.CD); stepPC = Math.Max(stepPC, prev.PC - now.PC);
            peakLP = Math.Max(peakLP, prev.LP - minLP); peakCD = Math.Max(peakCD, prev.CD - minCD); peakPC = Math.Max(peakPC, prev.PC - minPC);
            Log(string.Format(Inv, "FILL {0} n={1} {2} {3}ms step LP={4} CD={5} PC={6} peak LP={7} CD={8} PC={9} MB | " + now,
                v.Name, n + 1, how, ms, Mb(prev.LP - now.LP), Mb(prev.CD - now.CD), Mb(prev.PC - now.PC),
                Mb(prev.LP - minLP), Mb(prev.CD - minCD), Mb(prev.PC - minPC)));
            prev = now;
        }
        Log(string.Format(Inv, "FILL_RESULT {0} count={1} stop={2} used LP={3} CD={4} PC={5} MB end " + prev,
            v.Name, made.Count, stop, Mb(start.LP - prev.LP), Mb(start.CD - prev.CD), Mb(start.PC - prev.PC)));
        resident.Clear();
        yield return null;
        foreach (Texture2D t in made) { if (t == source && !v.Png) Resources.UnloadAsset(t); else Destroy(t); }
        foreach (TextAsset a in assets) if (a != null) Resources.UnloadAsset(a);
        yield return Settle();
        Log("FILL " + v.Name + " released " + Read());
    }

    IEnumerator Draw(Variant v)
    {
        yield return Settle();
        Loaded l = Load(v, 0);
        int vsync = QualitySettings.vSyncCount;
        int target = Application.targetFrameRate;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 1000;
        foreach (int n in new[] { 0, 1, 4, 16 })
        {
            Show(n == 0 ? null : l.Tex, v.Name, n);
            for (int i = 0; i < 30; i++) yield return null;
            var times = new List<float>();
            for (int i = 0; i < 180; i++) { yield return null; times.Add(Time.unscaledDeltaTime * 1000f); }
            times.Sort();
            Log(string.Format(Inv, "DRAW {0} layers={1} meanMs={2:F2} p50Ms={3:F2} p90Ms={4:F2}", v.Name, n, times.Average(), times[times.Count / 2], times[times.Count * 9 / 10]));
        }
        Show(null, "", 0);
        QualitySettings.vSyncCount = vsync;
        Application.targetFrameRate = target;
        yield return null;
        if (v.Png) { Destroy(l.Tex); Resources.UnloadAsset(l.Asset); } else Resources.UnloadAsset(l.Tex);
    }

    void Show(Texture t, string name, int n) { shown = t; label = name; layers = n; }

    // The quality view at the game's scale: atlas pixels to the 398x224 canvas, the canvas to 960x544 (x2.43).
    // Left, a level crop of TexturePack0 (240x224 at 2680,950); right, sprites (155x224 at 3941,1150).
    static readonly Rect LevelCrop = new Rect(2680, 950, 240, 224), SpriteCrop = new Rect(3941, 1150, 155, 224);

    void OnPostRender()
    {
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
        float scale = Screen.height / 224f;
        if (shown != null)
        {
            for (int i = 0; i < layers; i++)
            {
                DrawCrop(shown, LevelCrop, new Rect(0, 0, LevelCrop.width * scale, Screen.height));
                DrawCrop(shown, SpriteCrop, new Rect(LevelCrop.width * scale, 0, Screen.width - LevelCrop.width * scale, Screen.height));
            }
        }
        // Atlases loaded by a fill: each drawn as a small square so it is resident in video memory.
        for (int i = 0; i < resident.Count; i++)
            Graphics.DrawTexture(new Rect(4 + (i % 32) * 28, Screen.height - 32 - (i / 32) * 28, 24, 24), resident[i], blit);
        GL.PopMatrix();
    }

    void DrawCrop(Texture t, Rect atlas, Rect screen)
    {
        // Texture v runs from the bottom; the atlas's y from the top (4096 = the PNG's size at either resolution).
        var uv = new Rect(atlas.x / 4096f, 1f - (atlas.y + atlas.height) / 4096f, atlas.width / 4096f, atlas.height / 4096f);
        Graphics.DrawTexture(screen, t, uv, 0, 0, 0, 0, blit);
    }

    void OnGUI()
    {
        if (label.Length > 0 && layers <= 1) GUI.Label(new Rect(8, 4, 400, 24), label);
    }

    // On the PS Vita ScreenCapture writes nothing: read the frame back and save it (as butcher-vita's Diag does).
    IEnumerator SaveFrame(string path)
    {
        yield return new WaitForEndOfFrame();
        var picture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        try
        {
            picture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
            picture.Apply(false);
            File.WriteAllBytes(path, picture.EncodeToPNG());
        }
        catch (Exception e) { Log("SHOT " + path + " failed: " + e.Message); }
        Destroy(picture);
        yield return null;
    }
}
