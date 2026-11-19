using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

// The shader test for Terminator 2D: NO FATE's three shaders as written for 2018.2 (tools/downgrade/shaders).
// Renders each line of Resources/cases.txt (check.py writes it) into a render texture of the case's size and
// saves it as <case>.png: in the editor (ShaderTestBuild.RenderD3D11, Direct3D 11) into the folder it is given,
// on the PS Vita into ux0:data/t2dsh. On the PS Vita it then times the shaders (see Perf) and quits.
// Every line it logs starts with "T2DSH"; the PS Vita's log is ux0:data/t2dsh/log.txt.
public class ShaderTest : MonoBehaviour
{
    public const string VitaFolder = "ux0:data/t2dsh";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static StreamWriter logFile;

    // One line of cases.txt: name shader fast input filter width height clear(4) color(4) coloff(4) outputsize(2).
    public class Case
    {
        public string Name, Shader, Input, Filter;
        public bool Fast;
        public int Width, Height;
        public Color Clear;
        public Vector4 Color, Coloff, OutputSize;

        public static List<Case> Load()
        {
            var text = Resources.Load<TextAsset>("cases").text;
            var cases = new List<Case>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] f = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                Func<int, float> n = i => float.Parse(f[i], Inv);
                cases.Add(new Case
                {
                    Name = f[0], Shader = f[1], Fast = f[2] == "1", Input = f[3], Filter = f[4],
                    Width = int.Parse(f[5], Inv), Height = int.Parse(f[6], Inv),
                    Clear = new Color(n(7), n(8), n(9), n(10)),
                    Color = new Vector4(n(11), n(12), n(13), n(14)),
                    Coloff = new Vector4(n(15), n(16), n(17), n(18)),
                    OutputSize = new Vector4(n(19), n(20), 0, 0),
                });
            }
            return cases;
        }
    }

    public static void Log(string line)
    {
        line = "T2DSH " + (Clock.ElapsedMilliseconds / 1000.0).ToString("F2", Inv) + " " + line;
        Debug.Log(line);
        if (logFile != null) { logFile.WriteLine(line); logFile.Flush(); }
    }

    static readonly Dictionary<string, Texture2D> inputs = new Dictionary<string, Texture2D>();

    public static Texture2D Input(string name, string filter)
    {
        string key = name + "/" + filter;
        Texture2D tex;
        if (inputs.TryGetValue(key, out tex)) return tex;
        tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        tex.LoadImage(Resources.Load<TextAsset>("inputs/" + name).bytes);
        tex.filterMode = filter == "bilinear" ? FilterMode.Bilinear : FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.anisoLevel = 0;
        inputs[key] = tex;
        return tex;
    }

    public static Material MaterialFor(Case c)
    {
        var shader = UnityEngine.Shader.Find(c.Shader);
        if (shader == null) throw new Exception("no shader " + c.Shader);
        var m = new Material(shader);
        m.SetColor("_Color", new Color(c.Color.x, c.Color.y, c.Color.z, c.Color.w));
        m.SetVector("_Coloff", c.Coloff);
        m.SetVector("_OutputSize", c.OutputSize);
        return m;
    }

    // A quad over the whole target, UV 0..1, clockwise as Unity's front faces are (the originals cull back faces).
    public static void Quad(Material m, Texture tex, bool fast)
    {
        if (fast) UnityEngine.Shader.EnableKeyword("T2D_CRT_FAST"); else UnityEngine.Shader.DisableKeyword("T2D_CRT_FAST");
        m.mainTexture = tex;
        m.SetPass(0);
        GL.PushMatrix();
        GL.LoadOrtho();
        GL.Begin(GL.QUADS);
        GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0);
        GL.TexCoord2(0, 1); GL.Vertex3(0, 1, 0);
        GL.TexCoord2(1, 1); GL.Vertex3(1, 1, 0);
        GL.TexCoord2(1, 0); GL.Vertex3(1, 0, 0);
        GL.End();
        GL.PopMatrix();
    }

    // Renders a case and returns its PNG.
    public static byte[] Render(Case c)
    {
        var rt = new RenderTexture(c.Width, c.Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        rt.filterMode = FilterMode.Point;
        rt.Create();
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, c.Clear);
        var m = MaterialFor(c);
        Quad(m, Input(c.Input, c.Filter), c.Fast);
        var read = new Texture2D(c.Width, c.Height, TextureFormat.RGBA32, false, false);
        read.ReadPixels(new Rect(0, 0, c.Width, c.Height), 0, 0, false);
        read.Apply(false);
        RenderTexture.active = previous;
        byte[] png = read.EncodeToPNG();
        UnityEngine.Object.DestroyImmediate(read);
        UnityEngine.Object.DestroyImmediate(m);
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);
        return png;
    }

    public static void RenderAll(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (Case c in Case.Load())
        {
            File.WriteAllBytes(Path.Combine(folder, c.Name + ".png"), Render(c));
            Log("CASE " + c.Name + " " + c.Width + "x" + c.Height);
        }
    }

    // The PS Vita app.

    [DllImport("ButcherClock")] static extern int ButcherClockSetArm(int mhz);
    [DllImport("ButcherClock")] static extern int ButcherClockGetArm();
    [DllImport("ButcherClock")] static extern int ButcherClockSetGpu(int mhz);
    [DllImport("ButcherClock")] static extern int ButcherClockGetGpu();

    void Awake()
    {
        try { Directory.CreateDirectory(VitaFolder); } catch (Exception) { }
        try { logFile = new StreamWriter(new FileStream(Path.Combine(VitaFolder, "log.txt"), FileMode.Create, FileAccess.Write, FileShare.Read)); }
        catch (Exception e) { Debug.Log("T2DSH no log file: " + e.Message); }
        Log("START platform=" + Application.platform + " unity=" + Application.unityVersion + " gfx=" + SystemInfo.graphicsDeviceType +
            " screen=" + Screen.width + "x" + Screen.height);
        try
        {
            int arm = ButcherClockSetArm(444), gpu = ButcherClockSetGpu(222);
            Log("CLOCK set arm=" + arm + " gpu=" + gpu + " now arm=" + ButcherClockGetArm() + " gpu=" + ButcherClockGetGpu() + " MHz");
        }
        catch (Exception e) { Log("CLOCK not set (" + e.GetType().Name + "); the player's defaults"); }
    }

    IEnumerator Start()
    {
        yield return null;
        foreach (Case c in Case.Load())
        {
            byte[] png = Render(c);
            File.WriteAllBytes(Path.Combine(VitaFolder, c.Name + ".png"), png);
            Log("CASE " + c.Name + " " + c.Width + "x" + c.Height);
            yield return null;
        }
        yield return Perf();
        Log("DONE");
        if (logFile != null) logFile.Close();
        logFile = null;
        yield return new WaitForSecondsRealtime(0.5f);
        Application.Quit();
    }

    // GPU time per full-screen pass at 960x544: frames with 0, 2 and 6 passes, each into its own render texture
    // (one scene each, so the GPU's hidden surface removal can't drop a pass), vsync off.
    // The difference per pass is the pass's cost. "normal" is Unlit/NormalShader drawing the 398x224 source
    // scaled up (as the game's CRT option draws its 398x896 frame and crt_layer: two such passes).
    string perfShader = null;
    bool perfFast;
    int perfPasses;
    Texture perfSource;
    Material perfMaterial;
    RenderTexture[] perfTargets;

    IEnumerator Perf()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 1000;
        perfTargets = Enumerable.Range(0, 6).Select(_ =>
        {
            var rt = new RenderTexture(960, 544, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            return rt;
        }).ToArray();
        perfSource = Input("pixelart", "point");
        var runs = new[] { new { Name = "crt", Fast = false }, new { Name = "crt_fast", Fast = true }, new { Name = "normal", Fast = false } };
        foreach (var run in runs)
        {
            var c = new Case
            {
                Shader = run.Name == "normal" ? "Unlit/NormalShader" : "Unlit/CRTShader", Fast = run.Fast,
                Color = Vector4.one, OutputSize = new Vector4(960, 544, 0, 0),
            };
            perfMaterial = MaterialFor(c);
            perfShader = run.Name;
            perfFast = run.Fast;
            var means = new Dictionary<int, float>();
            foreach (int passes in new[] { 0, 2, 6, 0 })
            {
                perfPasses = passes;
                for (int i = 0; i < 30; i++) yield return null;
                var times = new List<float>();
                for (int i = 0; i < 240; i++) { yield return null; times.Add(Time.unscaledDeltaTime * 1000f); }
                times.Sort();
                float mean = times.Average();
                means[passes] = mean;
                Log(string.Format(Inv, "PERF {0} passes={1} meanMs={2:F3} p10Ms={3:F3} p50Ms={4:F3} p90Ms={5:F3}",
                    run.Name, passes, mean, times[times.Count / 10], times[times.Count / 2], times[times.Count * 9 / 10]));
            }
            Log(string.Format(Inv, "PERFPASS {0} msPerPass={1:F3}", run.Name, (means[6] - means[2]) / 4f));
            Destroy(perfMaterial);
        }
        perfShader = null;
    }

    void OnPostRender()
    {
        if (perfShader == null) return;
        var previous = RenderTexture.active;
        for (int i = 0; i < perfPasses; i++)
        {
            RenderTexture.active = perfTargets[i];
            Quad(perfMaterial, perfSource, perfFast);
        }
        RenderTexture.active = previous;
    }
}
