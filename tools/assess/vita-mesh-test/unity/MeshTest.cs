using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

// The PS Vita mesh test for Terminator 2D: NO FATE's sprite meshes (tools/assess/vita-mesh-test).
// The game (BitmapData.CreateMultiMeshFrames) makes 4 Meshes per sprite frame: new Mesh(), MarkDynamic(), vertices
// (4 Vector3), uv (4 Vector2), triangles (6), RecalculateNormals(); no colors, no UploadMeshData. Screen sprites are
// drawn with Graphics.DrawMesh(mesh, TRS, material, 0, renderCamera, 0, mpb, ...) into a 398x224 ARGB32 camera
// target that the main camera blits to the screen; off-screen BitmapData targets use SetPass + DrawMeshNow.
//
// One launch runs the steps in ux0:data/t2dmesh/args.txt (renamed to args.used.txt as it is read), separated by ';':
//   create <flags> <N,N,...> [touch] [keep]
//        make meshes up to each N (stops early when memory runs low) and log memory and time at each N.
//        flags: any of d (MarkDynamic), n (RecalculateNormals), u (UploadMeshData(true)); "game" = dn.
//        touch: draw every mesh once at the end (forces any lazy GPU upload) and log memory again.
//        keep: leave the meshes resident for the next steps (free drops them).
//   free                              destroy kept meshes
//   draw <method> <K,K,...> [run=R]   frame time with K sprites per frame; method: drawmesh (the game's screen
//                                     path), drawnow (the game's off-screen path), batch (one dynamic mesh, one
//                                     submesh per texture run), instanced (DrawMeshInstanced), spritebatch (the
//                                     port's T2DCompat.SpriteBatch: Target, Source and Draw per sprite as the
//                                     rewritten unityDraw calls them, one Flush per frame), drawmeshsb (drawmesh with
//                                     the game's exact quads, from SpriteBatch.FrameQuad). The texture changes
//                                     every R sprites (default 16) among 4 atlases. still: the sprites don't move,
//                                     so shot-spritebatch-still.png and shot-drawmeshsb-still.png can be compared.
//   idle <s>                          log memory and frame time for s seconds
// Writes ux0:data/t2dmesh/log.txt (lines "T2D ..."), screenshots shot-<method>.png there, then quits.
public class MeshTest : MonoBehaviour
{
    const string Folder = "ux0:data/t2dmesh";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    const long MarginLP = 40L << 20, MarginCD = 4L << 20, MarginPC = 4L << 20;
    const int Width = 398, Height = 224, Atlas = 1024, Atlases = 4;

    static Func<int> freeLP, freeCD, freePC;
    StreamWriter logFile;

    Camera mainCam, renderCam;
    RenderTexture camRT;
    Material spriteMat;
    MaterialPropertyBlock mpb;
    int texId, uvRectId;
    Texture2D[] atlases;

    struct Mem
    {
        public long LP, CD, PC, UnityAlloc, UnityReserved, Gc;
        public override string ToString()
        {
            return string.Format(Inv, "freeLP={0:F1} freeCD={1:F1} freePC={2:F1} unityAlloc={3:F1} unityReserved={4:F1} gc={5:F1} MB",
                LP / 1048576.0, CD / 1048576.0, PC / 1048576.0, UnityAlloc / 1048576.0, UnityReserved / 1048576.0, Gc / 1048576.0);
        }
    }

    static Mem Read(bool gcExact = false)
    {
        return new Mem
        {
            LP = freeLP == null ? -1 : freeLP(), CD = freeCD == null ? -1 : freeCD(), PC = freePC == null ? -1 : freePC(),
            UnityAlloc = Profiler.GetTotalAllocatedMemoryLong(), UnityReserved = Profiler.GetTotalReservedMemoryLong(),
            Gc = GC.GetTotalMemory(gcExact),
        };
    }

    static string Mb(long bytes) { return (bytes / 1048576.0).ToString("F1", Inv); }

    void Log(string line)
    {
        line = "T2D " + (Clock.ElapsedMilliseconds / 1000.0).ToString("F2", Inv) + " " + line;
        Debug.Log(line);
        if (logFile != null) { logFile.WriteLine(line); logFile.Flush(); }
    }

    [DllImport("ButcherClock")] static extern int ButcherClockSetArm(int mhz);
    [DllImport("ButcherClock")] static extern int ButcherClockGetArm();
    [DllImport("ButcherClock")] static extern int ButcherClockSetGpu(int mhz);
    [DllImport("ButcherClock")] static extern int ButcherClockGetGpu();

    static Func<int> Reader(Type type, string name)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public);
        return method == null ? null : (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), method);
    }

    void Awake()
    {
        Type diagnostics = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEngine.PSVita.Diagnostics", false)).FirstOrDefault(t => t != null);
        if (diagnostics != null)
        {
            freeLP = Reader(diagnostics, "GetFreeMemoryLPDDR");
            freeCD = Reader(diagnostics, "GetFreeMemoryCDRAM");
            freePC = Reader(diagnostics, "GetFreeMemoryPHYSCONT");
        }
        Mem first = Read();
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
            " graphicsMemoryMB=" + SystemInfo.graphicsMemorySize + " gfx=" + SystemInfo.graphicsDeviceType + " shaderLevel=" + SystemInfo.graphicsShaderLevel +
            " instancing=" + SystemInfo.supportsInstancing + " multithreaded=" + SystemInfo.graphicsMultiThreaded + " screen=" + Screen.width + "x" + Screen.height);
        try
        {
            int armSet = ButcherClockSetArm(444), gpuSet = ButcherClockSetGpu(222);
            Log("CLOCK set arm=" + armSet + " gpu=" + gpuSet + " now arm=" + ButcherClockGetArm() + " gpu=" + ButcherClockGetGpu() + " MHz");
        }
        catch (Exception e) { Log("CLOCK not set (" + e.GetType().Name + ": " + e.Message + "); the player's defaults"); }

        // The game's cameras: a render camera (depth -1) into a 398x224 ARGB32 point target, and the main camera,
        // which draws nothing itself and blits that target to the screen in OnRenderImage.
        mainCam = GetComponent<Camera>();
        mainCam.clearFlags = CameraClearFlags.SolidColor;
        mainCam.backgroundColor = Color.black;
        mainCam.cullingMask = 0;
        mainCam.depth = 0;
        camRT = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        camRT.Create();
        camRT.filterMode = FilterMode.Point;
        var rc = new GameObject("RenderCamera");
        renderCam = rc.AddComponent<Camera>();
        renderCam.depth = -1;
        renderCam.orthographic = true;
        renderCam.orthographicSize = Height / 2f;
        renderCam.nearClipPlane = 0.01f;
        renderCam.farClipPlane = 1100f;
        renderCam.transform.position = Vector3.zero;
        renderCam.clearFlags = CameraClearFlags.SolidColor;
        renderCam.backgroundColor = new Color32(40, 40, 60, 255);
        renderCam.cullingMask = 1;
        renderCam.targetTexture = camRT;

        spriteMat = Resources.Load<Material>("sprite");
        mpb = new MaterialPropertyBlock();
        texId = Shader.PropertyToID("_MainTex");
        uvRectId = Shader.PropertyToID("_UVRect");
        Log("MATERIAL " + (spriteMat == null ? "missing" : spriteMat.shader.name + " supported=" + spriteMat.shader.isSupported + " instancing=" + spriteMat.enableInstancing));
    }

    void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        Graphics.Blit(camRT, dest);
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
            string[] words = step.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Log("STEP " + step);
            if (words[0] == "create") yield return Create(words[1], Ints(words[2]), words.Contains("touch"), words.Contains("keep"));
            else if (words[0] == "free") yield return Free();
            else if (words[0] == "draw")
            {
                int run = 16;
                foreach (string w in words.Skip(3)) if (w.StartsWith("run=")) run = int.Parse(w.Substring(4), Inv);
                still = words.Skip(3).Contains("still");
                yield return Draw(words[1], Ints(words[2]), run);
            }
            else if (words[0] == "idle") yield return Idle(float.Parse(words[1], Inv));
            else Log("STEP unknown " + step);
        }
        Log("DONE " + Read());
        if (logFile != null) logFile.Close();
        logFile = null;
        yield return new WaitForSecondsRealtime(0.5f);
        Application.Quit();
    }

    static int[] Ints(string list) { return list.Split(',').Select(s => int.Parse(s.Trim(), Inv)).ToArray(); }

    IEnumerator Settle()
    {
        GC.Collect();
        yield return Resources.UnloadUnusedAssets();
        GC.Collect();
        for (int i = 0; i < 3; i++) yield return null;
    }

    // ---------------------------------------------------------------- creation, the game's way

    static readonly int[] Tris = { 0, 1, 2, 2, 1, 3 };
    static readonly Vector3[] SetVert = new Vector3[4];
    static readonly Vector2[] SetUv = new Vector2[4];

    // A synthetic sprite frame: trimmed size, offset in the untrimmed frame, UV rect in a 1024 atlas.
    struct Frame { public int W, H, Ox, Oy, Sw, Sh; public float U, V, Uw, Vh; }

    static Frame MakeFrame(System.Random r)
    {
        var f = new Frame();
        f.W = r.Next(8, 41); f.H = r.Next(8, 41);
        f.Ox = r.Next(0, 4); f.Oy = r.Next(0, 4);
        f.Sw = f.W + f.Ox + r.Next(0, 4); f.Sh = f.H + f.Oy + r.Next(0, 4);
        f.U = r.Next(0, Atlas - f.W) / (float)Atlas; f.V = r.Next(0, Atlas - f.H) / (float)Atlas;
        f.Uw = f.W / (float)Atlas; f.Vh = f.H / (float)Atlas;
        return f;
    }

    // The game's exact quad for a frame and flip, from the port's SpriteBatch.FrameQuad (checked against the recovered
    // CreateMultiMeshFrames on the PC), as a Mesh made the game's way.
    static Mesh MakeGameMesh(Frame f, int flip)
    {
        var mesh = new Mesh();
        mesh.MarkDynamic();
        T2DCompat.SpriteBatch.FrameQuad(f.W, f.H, f.Ox, f.Oy, f.Sw, f.Sh, f.U, f.V, f.Uw, f.Vh, flip, SetVert, SetUv);
        mesh.vertices = SetVert;
        mesh.uv = SetUv;
        mesh.triangles = Tris;
        mesh.RecalculateNormals();
        return mesh;
    }

    // CreateMultiMeshFrames' four meshes: 0 plain, 1 hflip, 2 vflip, 3 both (UVs mirrored as the game does).
    static Mesh MakeMesh(Frame f, int flip, bool dyn, bool normals, bool upload)
    {
        var mesh = new Mesh();
        if (dyn) mesh.MarkDynamic();
        float hw = f.Sw * 0.5f, hh = f.Sh * 0.5f;
        float l = f.Ox - hw, r = f.W + f.Ox - hw, t = hh - f.Oy, b = hh - (f.H + f.Oy);
        SetVert[0] = new Vector3(r, t); SetVert[1] = new Vector3(r, b); SetVert[2] = new Vector3(l, t); SetVert[3] = new Vector3(l, b);
        float u0 = f.U, u1 = f.U + f.Uw, v0 = f.V, v1 = f.V + f.Vh;
        if ((flip & 1) != 0) { float s = u0; u0 = u1; u1 = s; }
        if ((flip & 2) != 0) { float s = v0; v0 = v1; v1 = s; }
        SetUv[0] = new Vector2(u1, v1); SetUv[1] = new Vector2(u1, v0); SetUv[2] = new Vector2(u0, v1); SetUv[3] = new Vector2(u0, v0);
        mesh.vertices = SetVert;
        mesh.uv = SetUv;
        mesh.triangles = Tris;
        if (normals) mesh.RecalculateNormals();
        if (upload) mesh.UploadMeshData(true);
        return mesh;
    }

    readonly List<Mesh> kept = new List<Mesh>();

    IEnumerator Create(string flags, int[] targets, bool touch, bool keep)
    {
        if (flags == "game") flags = "dn";
        bool dyn = flags.Contains("d"), normals = flags.Contains("n"), upload = flags.Contains("u");
        yield return Settle();
        Mem start = Read(true);
        Log("CREATE " + flags + " start " + start);
        yield return FrameTimes("CREATE_IDLE " + flags + " n=0", 60);
        var meshes = new List<Mesh>(targets.Max() + 8);
        var rnd = new System.Random(1234);
        Frame frame = default(Frame);
        var create = new System.Diagnostics.Stopwatch();
        var wall = System.Diagnostics.Stopwatch.StartNew();
        string stop = null;
        Mem now = start;
        foreach (int target in targets)
        {
            while (meshes.Count < target && stop == null)
            {
                int chunk = Math.Min(target - meshes.Count, 2000);
                create.Start();
                for (int i = 0; i < chunk; i++)
                {
                    int k = meshes.Count;
                    if ((k & 3) == 0) frame = MakeFrame(rnd);
                    meshes.Add(MakeMesh(frame, k & 3, dyn, normals, upload));
                    if ((k & 511) == 511)
                    {
                        create.Stop();
                        Mem m = Read();
                        if (m.LP < MarginLP || m.CD < MarginCD || m.PC < MarginPC)
                        {
                            stop = string.Format(Inv, "margin at n={0}: {1}", meshes.Count, m);
                            break;
                        }
                        create.Start();
                    }
                }
                create.Stop();
                yield return null;
            }
            yield return null;
            yield return null;
            GC.Collect();
            now = Read(true);
            int n = meshes.Count;
            Log(string.Format(Inv, "CREATE {0} n={1} createMs={2} usPerMesh={3:F1} wallMs={4} used LP={5} CD={6} PC={7} unityAlloc={8} unityReserved={9} gc={10} MB " +
                "perMesh LP={11:F0} CD={12:F0} PC={13:F0} unityAlloc={14:F0} gc={15:F0} B | {16}",
                flags, n, create.ElapsedMilliseconds, create.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, n), wall.ElapsedMilliseconds,
                Mb(start.LP - now.LP), Mb(start.CD - now.CD), Mb(start.PC - now.PC), Mb(now.UnityAlloc - start.UnityAlloc),
                Mb(now.UnityReserved - start.UnityReserved), Mb(now.Gc - start.Gc),
                (start.LP - now.LP) / (double)n, (start.CD - now.CD) / (double)n, (start.PC - now.PC) / (double)n,
                (now.UnityAlloc - start.UnityAlloc) / (double)n, (now.Gc - start.Gc) / (double)n, now));
            if (stop != null) break;
        }
        if (stop != null) Log("CREATE " + flags + " stopped: " + stop);
        if (meshes.Count > 0)
        {
            long one = Profiler.GetRuntimeMemorySizeLong(meshes[0]);
            Log("CREATE " + flags + " runtimeSize(one mesh)=" + one + " B (0 outside development players) readable=" + meshes[0].isReadable +
                " vertexCount=" + meshes[0].vertexCount + " normals=" + (meshes[0].isReadable ? meshes[0].normals.Length.ToString() : "?"));
        }
        yield return FrameTimes("CREATE_IDLE " + flags + " n=" + meshes.Count, 60);

        if (touch && meshes.Count > 0)
        {
            Mem before = Read(true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            mpb.Clear();
            mpb.SetTexture(texId, Texture2D.whiteTexture);
            var tiny = Matrix4x4.TRS(new Vector3(0, 0, 500), Quaternion.identity, new Vector3(0.01f, 0.01f, 1));
            for (int i = 0; i < meshes.Count; i += 4000)
            {
                for (int j = i; j < Math.Min(meshes.Count, i + 4000); j++)
                    Graphics.DrawMesh(meshes[j], tiny, spriteMat, 0, renderCam, 0, mpb, ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
                yield return null;
            }
            yield return null;
            yield return null;
            Mem after = Read(true);
            Log(string.Format(Inv, "TOUCH {0} n={1} wallMs={2} more LP={3} CD={4} PC={5} unityAlloc={6} MB; total perMesh LP={7:F0} CD={8:F0} B | {9}",
                flags, meshes.Count, sw.ElapsedMilliseconds, Mb(before.LP - after.LP), Mb(before.CD - after.CD), Mb(before.PC - after.PC),
                Mb(after.UnityAlloc - before.UnityAlloc), (start.LP - after.LP) / (double)meshes.Count, (start.CD - after.CD) / (double)meshes.Count, after));
            yield return FrameTimes("TOUCH_IDLE " + flags + " n=" + meshes.Count, 60);
        }

        if (keep)
        {
            kept.AddRange(meshes);
            Log("CREATE " + flags + " kept " + meshes.Count);
            yield break;
        }
        yield return Release(meshes, flags, start);
    }

    IEnumerator Release(List<Mesh> meshes, string what, Mem start)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (Mesh m in meshes) Destroy(m);
        long destroyMs = sw.ElapsedMilliseconds;
        int n = meshes.Count;
        meshes.Clear();
        yield return Settle();
        Mem after = Read(true);
        Log(string.Format(Inv, "FREED {0} n={1} destroyMs={2} wallMs={3} still used LP={4} CD={5} PC={6} MB | {7}",
            what, n, destroyMs, sw.ElapsedMilliseconds, Mb(start.LP - after.LP), Mb(start.CD - after.CD), Mb(start.PC - after.PC), after));
    }

    IEnumerator Free()
    {
        Mem m = Read();
        yield return Release(new List<Mesh>(kept), "kept", m);
        kept.Clear();
    }

    IEnumerator FrameTimes(string label, int frames)
    {
        int vsync = QualitySettings.vSyncCount, target = Application.targetFrameRate;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 1000;
        for (int i = 0; i < 10; i++) yield return null;
        var times = new List<float>();
        for (int i = 0; i < frames; i++) { yield return null; times.Add(Time.unscaledDeltaTime * 1000f); }
        times.Sort();
        Log(string.Format(Inv, "{0} frameMs mean={1:F2} p50={2:F2} p90={3:F2}", label, times.Average(), times[times.Count / 2], times[times.Count * 9 / 10]));
        QualitySettings.vSyncCount = vsync;
        Application.targetFrameRate = target;
    }

    IEnumerator Idle(float seconds)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until)
        {
            Log("IDLE " + Read());
            yield return FrameTimes("IDLE", 30);
            yield return new WaitForSecondsRealtime(0.5f);
        }
    }

    // ---------------------------------------------------------------- drawing

    struct Sprite { public Frame F; public int Flip, Tex; public Vector2 Pos, Vel; }

    // A sprite's source as the rewritten unityDraw hands it to SpriteBatch.Source: the BitmapData's frame arrays
    // (one frame here), its sheet cell and size.
    class Source { public int[] Page, Frw, Frh, Ofx, Ofy; public double[] Uvx, Uvy, Uvw, Uvh; }

    Source[] sources;
    bool still;

    string method = "";
    int count;
    Sprite[] sprites;
    Mesh[] pool;
    Mesh batchMesh, quad;
    Vector3[] bVerts;
    Vector2[] bUvs;
    List<int[]> runs = new List<int[]>(); // {firstSprite, count, tex}
    Matrix4x4[] instMatrices = new Matrix4x4[1023];
    Vector4[] instUv = new Vector4[1023];
    readonly System.Diagnostics.Stopwatch submit = new System.Diagnostics.Stopwatch();

    Texture2D MakeAtlas(int k)
    {
        var tex = new Texture2D(Atlas, Atlas, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Repeat;  // as the game's pages: two of the four flips sample at uv - 1
        var px = new Color32[Atlas * Atlas];
        var tint = new[] { new Color32(255, 80, 80, 255), new Color32(80, 255, 80, 255), new Color32(80, 120, 255, 255), new Color32(255, 230, 60, 255) }[k];
        for (int y = 0; y < Atlas; y++)
            for (int x = 0; x < Atlas; x++)
            {
                bool cell = ((x >> 3) + (y >> 3) & 1) == 0;
                byte a = (byte)((x & 7) == 0 || (y & 7) == 0 ? 0 : 255);
                px[y * Atlas + x] = cell ? new Color32(tint.r, tint.g, tint.b, a) : new Color32((byte)(tint.r / 2), (byte)(tint.g / 2), (byte)(tint.b / 2), a);
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
    }

    IEnumerator Draw(string how, int[] ks, int run)
    {
        if (how == "instanced" && !SystemInfo.supportsInstancing)
        {
            Log("DRAW instanced skipped: SystemInfo.supportsInstancing=False");
            yield break;
        }
        yield return Settle();
        Mem start = Read(true);
        if (atlases == null) atlases = Enumerable.Range(0, Atlases).Select(MakeAtlas).ToArray();
        int max = ks.Max();
        var rnd = new System.Random(99);
        sprites = new Sprite[max];
        pool = how == "drawmesh" || how == "drawnow" || how == "drawmeshsb" ? new Mesh[max * 4] : null;
        sources = how == "spritebatch" ? new Source[max] : null;
        T2DCompat.Atlas.Pages = atlases;
        for (int i = 0; i < max; i++)
        {
            Frame f = MakeFrame(rnd);
            sprites[i] = new Sprite
            {
                F = f, Flip = rnd.Next(0, 4), Tex = (i / Math.Max(1, run)) % Atlases,
                Pos = new Vector2(rnd.Next(-Width / 2, Width / 2), rnd.Next(-Height / 2, Height / 2)),
                Vel = new Vector2((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1),
            };
            if (pool != null) for (int k = 0; k < 4; k++) pool[i * 4 + k] = how == "drawmeshsb" ? MakeGameMesh(f, k) : MakeMesh(f, k, true, true, false);
            if (sources != null)
                sources[i] = new Source
                {
                    Page = new[] { sprites[i].Tex }, Frw = new[] { f.W }, Frh = new[] { f.H }, Ofx = new[] { f.Ox }, Ofy = new[] { f.Oy },
                    Uvx = new double[] { f.U }, Uvy = new double[] { f.V }, Uvw = new double[] { f.Uw }, Uvh = new double[] { f.Vh },
                };
        }
        if (how == "instanced")
        {
            quad = new Mesh();
            quad.vertices = new[] { new Vector3(1, 1), new Vector3(1, 0), new Vector3(0, 1), new Vector3(0, 0) };
            quad.uv = new[] { new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 1), new Vector2(0, 0) };
            quad.triangles = Tris;
            quad.UploadMeshData(true);
        }
        Log("DRAW " + how + " setup sprites=" + max + " run=" + run + (still ? " still" : "") + " " + Read());
        int vsync = QualitySettings.vSyncCount, target = Application.targetFrameRate;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 1000;
        foreach (int k in new[] { 0 }.Concat(ks))
        {
            Setup(how, k);
            for (int i = 0; i < 30; i++) yield return null;
            var frames = new List<float>();
            var submits = new List<float>();
            for (int i = 0; i < 180; i++)
            {
                yield return null;
                frames.Add(Time.unscaledDeltaTime * 1000f);
                submits.Add((float)submit.Elapsed.TotalMilliseconds);
            }
            frames.Sort();
            submits.Sort();
            Log(string.Format(Inv, "DRAW {0} k={1} run={2} draws={3} frameMs mean={4:F2} p50={5:F2} p90={6:F2} submitMs mean={7:F2} p50={8:F2} p90={9:F2}",
                how, k, run, Draws(how, k), frames.Average(), frames[frames.Count / 2], frames[frames.Count * 9 / 10],
                submits.Average(), submits[submits.Count / 2], submits[submits.Count * 9 / 10]));
            if (k == 1000) yield return SaveFrame(Path.Combine(Folder, "shot-" + how + (still ? "-still" : "") + (run != 16 ? "-run" + run : "") + ".png"));
        }
        method = "";
        count = 0;
        renderCam.clearFlags = CameraClearFlags.SolidColor;
        QualitySettings.vSyncCount = vsync;
        Application.targetFrameRate = target;
        yield return null;
        if (pool != null) foreach (Mesh m in pool) Destroy(m);
        if (batchMesh != null) Destroy(batchMesh);
        if (quad != null) Destroy(quad);
        pool = null; batchMesh = null; quad = null; sprites = null; sources = null;
        yield return Settle();
        Log("DRAW " + how + " released " + Read() + " (start " + start + ")");
    }

    string Draws(string how, int k)
    {
        if (how == "batch") return runs.Count.ToString(Inv);
        if (how == "spritebatch") return (k == 0 ? 0 : T2DCompat.SpriteBatch.LastRuns).ToString(Inv);
        if (how == "instanced") return runs.Sum(r => (r[1] + 1022) / 1023).ToString(Inv);
        return k.ToString(Inv);
    }

    void Setup(string how, int k)
    {
        method = how;
        count = k;
        runs.Clear();
        for (int i = 0; i < k; i++)
        {
            if (runs.Count == 0 || runs[runs.Count - 1][2] != sprites[i].Tex) runs.Add(new[] { i, 0, sprites[i].Tex });
            runs[runs.Count - 1][1]++;
        }
        renderCam.clearFlags = how == "drawnow" ? CameraClearFlags.Nothing : CameraClearFlags.SolidColor;
        if (how == "batch")
        {
            if (batchMesh != null) Destroy(batchMesh);
            batchMesh = new Mesh();
            batchMesh.MarkDynamic();
            bVerts = new Vector3[Math.Max(4, k * 4)];
            bUvs = new Vector2[bVerts.Length];
            batchMesh.vertices = bVerts;
            batchMesh.uv = bUvs;
            batchMesh.subMeshCount = Math.Max(1, runs.Count);
            for (int r = 0; r < runs.Count; r++)
            {
                var tris = new int[runs[r][1] * 6];
                for (int q = 0; q < runs[r][1]; q++)
                {
                    int v = (runs[r][0] + q) * 4;
                    tris[q * 6] = v; tris[q * 6 + 1] = v + 1; tris[q * 6 + 2] = v + 2;
                    tris[q * 6 + 3] = v + 2; tris[q * 6 + 4] = v + 1; tris[q * 6 + 5] = v + 3;
                }
                batchMesh.SetTriangles(tris, r, false);
            }
        }
    }

    void Update()
    {
        submit.Reset();
        if (count == 0 || method == "") return;
        submit.Start();
        for (int i = 0; i < count && !still; i++)
        {
            Vector2 p = sprites[i].Pos + sprites[i].Vel;
            if (p.x < -Width / 2) p.x += Width; else if (p.x > Width / 2) p.x -= Width;
            if (p.y < -Height / 2) p.y += Height; else if (p.y > Height / 2) p.y -= Height;
            sprites[i].Pos = p;
        }
        if (method == "drawmesh" || method == "drawmeshsb") DrawMeshPath();
        else if (method == "spritebatch") SpriteBatchPath();
        else if (method == "drawnow") DrawNowPath();
        else if (method == "batch") BatchPath();
        else if (method == "instanced") InstancedPath();
        submit.Stop();
    }

    // BitmapData.unityDraw, screen target: the MPB is cleared and given the atlas when it changes; z steps by 0.25.
    void DrawMeshPath()
    {
        int texUsed = -1;
        double zorder = 999.0;
        for (int i = 0; i < count; i++)
        {
            Sprite s = sprites[i];
            if (s.Tex != texUsed)
            {
                mpb.Clear();
                mpb.SetTexture(texId, atlases[s.Tex]);
                spriteMat.mainTexture = atlases[s.Tex];
                texUsed = s.Tex;
            }
            Matrix4x4 m = Matrix4x4.TRS(new Vector3(s.Pos.x, s.Pos.y, (float)zorder), Quaternion.identity, Vector3.one);
            Graphics.DrawMesh(pool[i * 4 + s.Flip], m, spriteMat, 0, renderCam, 0, mpb, ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
            zorder -= 0.25;
        }
    }

    // The port's path: per sprite, what the rewritten BitmapData.unityDraw calls (R8), then one Flush (R9). x and y are
    // the game's (top-left origin, y down), chosen so the quad lands where DrawMeshPath's TRS puts the same mesh.
    void SpriteBatchPath()
    {
        double zorder = 999.0;
        for (int i = 0; i < count; i++)
        {
            Sprite s = sprites[i];
            Source src = sources[i];
            Frame f = s.F;
            double hw = f.Sw * 0.5, hh = f.Sh * 0.5;
            T2DCompat.SpriteBatch.Target(true, null, Width * 0.5, Height * 0.5);
            T2DCompat.SpriteBatch.Source(null, src.Page, src.Uvx, src.Uvy, src.Uvw, src.Uvh, src.Frw, src.Frh, src.Ofx, src.Ofy,
                                         f.Sw, f.Sh, hw, hh, f.Sw, f.Sh);
            double x = s.Pos.x + (Width * 0.5 - hw), y = hh + Height * 0.5 - f.Sh - s.Pos.y;
            zorder = T2DCompat.SpriteBatch.Draw(x, y, 0, 1, 1, -1, false, (s.Flip & 1) != 0, (s.Flip & 2) != 0, -1, -1, 0.0, 1.0, 1.0,
                                                spriteMat, spriteMat, zorder);
        }
        T2DCompat.SpriteBatch.Flush(renderCam);
    }

    // BitmapData.unityDraw, off-screen target: RenderTexture.active, LoadPixelMatrix, SetPass on change, DrawMeshNow.
    void DrawNowPath()
    {
        RenderTexture active = RenderTexture.active;
        RenderTexture.active = camRT;
        GL.Clear(false, true, new Color32(40, 40, 60, 255));
        GL.PushMatrix();
        GL.LoadPixelMatrix(-Width / 2f, Width / 2f, -Height / 2f, Height / 2f);
        int texUsed = -1;
        for (int i = 0; i < count; i++)
        {
            Sprite s = sprites[i];
            if (s.Tex != texUsed)
            {
                spriteMat.mainTexture = atlases[s.Tex];
                spriteMat.SetPass(0);
                texUsed = s.Tex;
            }
            Graphics.DrawMeshNow(pool[i * 4 + s.Flip], new Vector3(s.Pos.x, s.Pos.y), Quaternion.identity);
        }
        GL.PopMatrix();
        RenderTexture.active = active;
    }

    static readonly Bounds CanvasBounds = new Bounds(new Vector3(0, 0, 500), new Vector3(Width * 2, Height * 2, 10));

    // The alternative: one dynamic mesh rebuilt each frame, one submesh (one draw) per run of the same atlas.
    void BatchPath()
    {
        for (int i = 0; i < count; i++)
        {
            Sprite s = sprites[i];
            Frame f = s.F;
            float hw = f.Sw * 0.5f, hh = f.Sh * 0.5f;
            float l = s.Pos.x + f.Ox - hw, r = s.Pos.x + f.W + f.Ox - hw, t = s.Pos.y + hh - f.Oy, b = s.Pos.y + hh - (f.H + f.Oy);
            int v = i * 4;
            bVerts[v] = new Vector3(r, t, 500); bVerts[v + 1] = new Vector3(r, b, 500); bVerts[v + 2] = new Vector3(l, t, 500); bVerts[v + 3] = new Vector3(l, b, 500);
            float u0 = f.U, u1 = f.U + f.Uw, v0 = f.V, v1 = f.V + f.Vh;
            if ((s.Flip & 1) != 0) { float x = u0; u0 = u1; u1 = x; }
            if ((s.Flip & 2) != 0) { float x = v0; v0 = v1; v1 = x; }
            bUvs[v] = new Vector2(u1, v1); bUvs[v + 1] = new Vector2(u1, v0); bUvs[v + 2] = new Vector2(u0, v1); bUvs[v + 3] = new Vector2(u0, v0);
        }
        batchMesh.vertices = bVerts;
        batchMesh.uv = bUvs;
        // The submeshes' bounds were taken from the zeroed vertices at setup (camera-culled): the whole canvas instead.
        batchMesh.bounds = CanvasBounds;
        for (int r = 0; r < runs.Count; r++)
        {
            mpb.Clear();
            mpb.SetTexture(texId, atlases[runs[r][2]]);
            Graphics.DrawMesh(batchMesh, Matrix4x4.identity, spriteMat, 0, renderCam, r, mpb, ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
        }
    }

    // The other alternative: a unit quad drawn instanced, the UV rect per instance.
    void InstancedPath()
    {
        foreach (int[] r in runs)
        {
            for (int first = r[0]; first < r[0] + r[1]; first += 1023)
            {
                int n = Math.Min(1023, r[0] + r[1] - first);
                for (int j = 0; j < n; j++)
                {
                    Sprite s = sprites[first + j];
                    Frame f = s.F;
                    float x = s.Pos.x + f.Ox - f.Sw * 0.5f, y = s.Pos.y + f.Sh * 0.5f - (f.H + f.Oy);
                    instMatrices[j] = Matrix4x4.TRS(new Vector3(x, y, 500), Quaternion.identity, new Vector3(f.W, f.H, 1));
                    instUv[j] = new Vector4(f.U, f.V, f.Uw, f.Vh);
                }
                mpb.Clear();
                mpb.SetTexture(texId, atlases[r[2]]);
                mpb.SetVectorArray(uvRectId, instUv);
                Graphics.DrawMeshInstanced(quad, 0, spriteMat, instMatrices, n, mpb, ShadowCastingMode.Off, false, 0, renderCam, LightProbeUsage.Off);
            }
        }
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
