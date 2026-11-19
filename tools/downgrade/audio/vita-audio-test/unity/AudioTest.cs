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

// The PS Vita audio test for Terminator 2D: NO FATE (tools/downgrade/audio/vita-audio-test).
// One launch runs the steps in ux0:data/t2daudio/args.txt (renamed to args.used.txt as it is read), separated by ';':
//   vol <v>              AudioListener volume (default 0.2)
//   load                 Resources.Load every clip of the "game" group in the game's order, as AssetsEmbed.staticInit
//                        does, imported with audio_rules.py's settings: time and main memory per kind
//   streamcost <group>   load the group's 20 streamed copies (pre: preloadAudioData on, np: off), then play each alone:
//                        memory per loaded clip, Play()'s time on the main thread, time to the first samples
//   play <s> <v> <secs>  s streamed music clips looping at once plus v voices of in-memory sound effects, each
//                        restarted with PlayOneShot when it ends: frame times, Play() times, memory, and the
//                        throughput of three low-priority spinning threads (against "play 0 0")
//   probe                each probe clip (one sound in several formats) alone: its fields, Play() time, whether it plays
//   idle <s>             log memory for s seconds
// Writes ux0:data/t2daudio/log.txt (lines "T2D ..."), then quits.
public class AudioTest : MonoBehaviour
{
    const string Folder = "ux0:data/t2daudio";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    static Func<int> freeLP, freeCD, freePC;

    class Entry { public string Group, Path, Kind; public AudioClip Clip; }
    readonly List<Entry> entries = new List<Entry>();
    StreamWriter logFile;
    readonly System.Random random = new System.Random(1);

    struct Mem
    {
        public long LP, CD, PC, UnityAlloc, MonoUsed;
        public override string ToString()
        {
            return string.Format(Inv, "freeLP={0:F1} freeCD={1:F1} freePC={2:F1} unityAlloc={3:F1} monoUsed={4:F1} MB",
                LP / 1048576.0, CD / 1048576.0, PC / 1048576.0, UnityAlloc / 1048576.0, MonoUsed / 1048576.0);
        }
    }

    static Mem Read()
    {
        return new Mem
        {
            LP = freeLP == null ? -1 : freeLP(), CD = freeCD == null ? -1 : freeCD(), PC = freePC == null ? -1 : freePC(),
            UnityAlloc = Profiler.GetTotalAllocatedMemoryLong(), MonoUsed = Profiler.GetMonoUsedSizeLong(),
        };
    }

    static string Mb(long bytes) { return (bytes / 1048576.0).ToString("F2", Inv); }
    static string F(double v) { return v.ToString("F2", Inv); }

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
            " cpus=" + SystemInfo.processorCount + " " + SystemInfo.processorType);
        try
        {
            int armSet = ButcherClockSetArm(444), gpuSet = ButcherClockSetGpu(222);
            Log("CLOCK set arm=" + armSet + " gpu=" + gpuSet + " now arm=" + ButcherClockGetArm() + " gpu=" + ButcherClockGetGpu() + " MHz");
        }
        catch (Exception e) { Log("CLOCK not set (" + e.GetType().Name + ": " + e.Message + "); the player's defaults"); }

        // The game's AudioManager: DSP buffer 1024, 32 real voices, 2048 virtual.
        AudioConfiguration config = AudioSettings.GetConfiguration();
        Log(string.Format(Inv, "AUDIOCONFIG before rate={0} buffer={1} real={2} virtual={3} speakers={4}", config.sampleRate, config.dspBufferSize,
            config.numRealVoices, config.numVirtualVoices, config.speakerMode));
        config.dspBufferSize = 1024; config.numRealVoices = 32; config.numVirtualVoices = 2048;
        bool reset = AudioSettings.Reset(config);
        config = AudioSettings.GetConfiguration();
        Log(string.Format(Inv, "AUDIOCONFIG after reset={5} rate={0} buffer={1} real={2} virtual={3} speakers={4} outputRate={6}", config.sampleRate,
            config.dspBufferSize, config.numRealVoices, config.numVirtualVoices, config.speakerMode, reset, AudioSettings.outputSampleRate));
        AudioListener.volume = 0.2f;
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 1;

        TextAsset list = Resources.Load<TextAsset>("cliplist");
        foreach (string line in list.text.Split('\n'))
        {
            string[] f = line.Trim().Split('\t');
            if (f.Length >= 3) entries.Add(new Entry { Group = f[0], Path = f[1], Kind = f[2] });
        }
        Resources.UnloadAsset(list);
        Log("CLIPLIST " + entries.Count + " entries: " + string.Join(", ", entries.GroupBy(e => e.Group).Select(g => g.Key + "=" + g.Count()).ToArray()));
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
        if (args.Length == 0) args = "idle 3";
        Log("ARGS " + args);
        foreach (string step in args.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            string[] w = step.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Log("STEP " + step);
            if (w[0] == "vol") AudioListener.volume = float.Parse(w[1], Inv);
            else if (w[0] == "load") yield return LoadGame();
            else if (w[0] == "streamcost") yield return StreamCost(w[1]);
            else if (w[0] == "play") yield return Play(int.Parse(w[1]), int.Parse(w[2]), float.Parse(w[3], Inv));
            else if (w[0] == "probe") yield return Probe();
            else if (w[0] == "idle")
            {
                float until = Time.realtimeSinceStartup + float.Parse(w[1], Inv);
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

    IEnumerator Settle()
    {
        GC.Collect();
        for (int i = 0; i < 3; i++) yield return null;
    }

    // As AssetsEmbed.staticInit: every clip, one Resources.Load after another, in one frame.
    IEnumerator LoadGame()
    {
        yield return Settle();
        Mem start = Read();
        var ms = new Dictionary<string, double>();
        var lp = new Dictionary<string, long>();
        var count = new Dictionary<string, int>();
        double worst = 0; string worstPath = "";
        int failed = 0, exists = 0;
        double existsMs = 0;
        var all = System.Diagnostics.Stopwatch.StartNew();
        foreach (Entry e in entries.Where(x => x.Group == "game"))
        {
            long before = freeLP == null ? 0 : freeLP();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Sound.createSoundFromFile first looks for a loose WAV under dataPath/Resources (not music).
            if (!e.Path.StartsWith("audio/music/"))
            {
                var x = System.Diagnostics.Stopwatch.StartNew();
                if (File.Exists(Application.dataPath + "/Resources/" + e.Path + ".wav")) exists++;
                existsMs += x.Elapsed.TotalMilliseconds;
            }
            e.Clip = Resources.Load<AudioClip>(e.Path);
            double t = sw.Elapsed.TotalMilliseconds;
            long used = freeLP == null ? 0 : before - freeLP();
            if (e.Clip == null) { failed++; Log("LOAD failed " + e.Path); }
            string k = e.Clip != null && e.Clip.loadType == AudioClipLoadType.Streaming ? e.Kind + "/stream" : e.Kind + "/memory";
            if (!ms.ContainsKey(k)) { ms[k] = 0; lp[k] = 0; count[k] = 0; }
            ms[k] += t; lp[k] += used; count[k]++;
            if (t > worst) { worst = t; worstPath = e.Path; }
        }
        double total = all.Elapsed.TotalMilliseconds;
        yield return null;
        yield return null;
        Mem end = Read();
        foreach (string k in ms.Keys.OrderBy(x => x))
            Log(string.Format(Inv, "LOAD_KIND {0} clips={1} ms={2:F0} usedLP={3} MB perClipKB={4:F1}", k, count[k], ms[k], Mb(lp[k]), lp[k] / 1024.0 / count[k]));
        Log(string.Format(Inv, "LOAD_RESULT clips={0} failed={1} totalMs={2:F0} worstMs={3:F1} ({4}) usedLP={5} usedCD={6} usedPC={7} MB unityAlloc+={8} | {9}",
            count.Values.Sum(), failed, total, worst, worstPath, Mb(start.LP - end.LP), Mb(start.CD - end.CD), Mb(start.PC - end.PC),
            Mb(end.UnityAlloc - start.UnityAlloc), end));
        Log(string.Format(Inv, "LOAD_FILE_EXISTS found={0} ms={1:F0} dataPath={2}", exists, existsMs, Application.dataPath));
        var states = entries.Where(x => x.Group == "game" && x.Clip != null).GroupBy(x => x.Clip.loadType + "/" + x.Clip.loadState).Select(g => g.Key + "=" + g.Count());
        Log("LOAD_STATES " + string.Join(" ", states.ToArray()));
    }

    // The memory of loaded streamed clips, with and without preloadAudioData, and what playing one costs.
    IEnumerator StreamCost(string group)
    {
        yield return Settle();
        List<Entry> list = entries.Where(x => x.Group == group).ToList();
        Mem start = Read();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (Entry e in list) e.Clip = Resources.Load<AudioClip>(e.Path);
        double loadMs = sw.Elapsed.TotalMilliseconds;
        yield return null;
        yield return null;
        Mem loaded = Read();
        Log(string.Format(Inv, "STREAMCOST {0} loaded={1} loadMs={2:F1} usedLP={3} MB perClipKB={4:F1} states={5} | {6}", group, list.Count, loadMs,
            Mb(start.LP - loaded.LP), (start.LP - loaded.LP) / 1024.0 / list.Count,
            string.Join(",", list.GroupBy(e => e.Clip == null ? "null" : e.Clip.loadType + "/" + e.Clip.loadState + "/preload=" + e.Clip.preloadAudioData).Select(g => g.Key + "=" + g.Count()).ToArray()), loaded));
        var source = gameObject.AddComponent<AudioSource>();
        var plays = new List<double>();
        var firsts = new List<double>();
        for (int i = 0; i < list.Count && i < 8; i++)
        {
            source.clip = list[i].Clip;
            source.loop = true;
            var p = System.Diagnostics.Stopwatch.StartNew();
            source.Play();
            plays.Add(p.Elapsed.TotalMilliseconds);
            float started = Time.realtimeSinceStartup;
            while (source.timeSamples == 0 && Time.realtimeSinceStartup - started < 2f) yield return null;
            firsts.Add((Time.realtimeSinceStartup - started) * 1000.0);
            Mem playing = Read();
            yield return new WaitForSecondsRealtime(0.3f);
            source.Stop();
            Log(string.Format(Inv, "STREAMCOST_PLAY {0} {1} playMs={2:F1} firstSamplesMs={3:F0} playingLP={4} MB over loaded", group, list[i].Path, plays[i], firsts[i], Mb(loaded.LP - playing.LP)));
            yield return null;
        }
        Destroy(source);
        foreach (Entry e in list) { if (e.Clip != null) Resources.UnloadAsset(e.Clip); e.Clip = null; }
        yield return Settle();
        Log(string.Format(Inv, "STREAMCOST_RESULT {0} playMs mean={1:F1} max={2:F1} firstSamplesMs mean={3:F0} | unloaded {4}", group, plays.Average(), plays.Max(), firsts.Average(), Read()));
    }

    // CPU: three low-priority threads count; whatever the audio threads take shows as fewer counts.
    volatile bool spinning;
    long[] spins = new long[3];

    void StartSpin()
    {
        spinning = true;
        for (int i = 0; i < spins.Length; i++)
        {
            int n = i;
            spins[n] = 0;
            var t = new Thread(() => { long c = 0; while (spinning) { c++; if ((c & 0xFFFF) == 0) Interlocked.Exchange(ref spins[n], c); } Interlocked.Exchange(ref spins[n], c); })
            { IsBackground = true, Priority = System.Threading.ThreadPriority.Lowest };
            t.Start();
        }
    }

    long StopSpin()
    {
        spinning = false;
        Thread.Sleep(5);
        return spins.Sum();
    }

    static readonly Dictionary<string, double> SpinBaseline = new Dictionary<string, double>();

    IEnumerator Play(int streams, int voices, float seconds)
    {
        yield return Settle();
        List<AudioClip> music = entries.Where(e => e.Group == "game" && e.Clip != null && e.Clip.loadType == AudioClipLoadType.Streaming)
            .OrderByDescending(e => e.Clip.length).Select(e => e.Clip).ToList();
        List<AudioClip> sfx = entries.Where(e => e.Group == "game" && e.Clip != null && e.Clip.loadType != AudioClipLoadType.Streaming && (e.Kind == "sfx" || e.Kind == "voice"))
            .Select(e => e.Clip).ToList();
        if (streams > music.Count || (voices > 0 && sfx.Count == 0)) { Log("PLAY needs load first (streamed " + music.Count + ", sfx " + sfx.Count + ")"); yield break; }
        Mem start = Read();
        var plays = new List<double>();
        var streamSources = new List<AudioSource>();
        for (int i = 0; i < streams; i++)
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.clip = music[i]; s.loop = true; s.volume = 0.5f;
            var p = System.Diagnostics.Stopwatch.StartNew();
            s.Play();
            plays.Add(p.Elapsed.TotalMilliseconds);
            streamSources.Add(s);
        }
        double streamPlayMs = plays.Sum();
        var voiceSources = new List<AudioSource>();
        for (int i = 0; i < voices; i++) { var s = gameObject.AddComponent<AudioSource>(); s.panStereo = (float)(random.NextDouble() * 2 - 1); voiceSources.Add(s); }
        var sfxPlays = new List<double>();
        for (int i = 0; i < 20; i++) yield return null; // the streams start
        StartSpin();
        var frames = new List<float>();
        float until = Time.realtimeSinceStartup + seconds;
        long minLP = long.MaxValue;
        int oneShots = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Time.realtimeSinceStartup < until)
        {
            foreach (AudioSource s in voiceSources)
            {
                if (s.isPlaying) continue;
                var p = System.Diagnostics.Stopwatch.StartNew();
                s.PlayOneShot(sfx[random.Next(sfx.Count)], 0.6f);
                sfxPlays.Add(p.Elapsed.TotalMilliseconds);
                oneShots++;
            }
            yield return null;
            frames.Add(Time.unscaledDeltaTime * 1000f);
            if (freeLP != null) minLP = Math.Min(minLP, freeLP());
        }
        double elapsed = sw.Elapsed.TotalSeconds;
        long spun = StopSpin();
        double rate = spun / elapsed;
        Mem end = Read();
        int playingStreams = streamSources.Count(s => s.isPlaying && s.timeSamples > 0);
        foreach (AudioSource s in streamSources) { s.Stop(); Destroy(s); }
        foreach (AudioSource s in voiceSources) { s.Stop(); Destroy(s); }
        frames.Sort();
        if (!SpinBaseline.ContainsKey("base") && streams == 0 && voices == 0) SpinBaseline["base"] = rate;
        double baseRate = SpinBaseline.ContainsKey("base") ? SpinBaseline["base"] : rate;
        Log(string.Format(Inv, "PLAY streams={0} voices={1} secs={2:F1} streamsPlaying={3} streamPlayMs sum={4:F1} max={5:F1} oneShots={6} oneShotMs mean={7:F2} max={8:F2} " +
            "frameMs mean={9:F2} p50={10:F2} p99={11:F2} max={12:F2} over20={13} over34={14} frames={15} spin={16:F0}/s ({17:F1}% of baseline) usedLP={18} peakLP={19} MB",
            streams, voices, elapsed, playingStreams, streamPlayMs, plays.Count == 0 ? 0 : plays.Max(), oneShots,
            sfxPlays.Count == 0 ? 0 : sfxPlays.Average(), sfxPlays.Count == 0 ? 0 : sfxPlays.Max(),
            frames.Average(), frames[frames.Count / 2], frames[Math.Min(frames.Count - 1, frames.Count * 99 / 100)], frames[frames.Count - 1],
            frames.Count(f => f > 20f), frames.Count(f => f > 34f), frames.Count, rate, 100.0 * rate / baseRate,
            Mb(start.LP - end.LP), minLP == long.MaxValue ? "?" : Mb(start.LP - minLP)));
        yield return Settle();
    }

    // One sound in several formats: what the PS Vita player got and whether it plays.
    IEnumerator Probe()
    {
        yield return Settle();
        var source = gameObject.AddComponent<AudioSource>();
        foreach (Entry e in entries.Where(x => x.Group == "probe"))
        {
            Mem before = Read();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AudioClip clip = Resources.Load<AudioClip>(e.Path);
            double loadMs = sw.Elapsed.TotalMilliseconds;
            Mem loaded = Read();
            if (clip == null) { Log("PROBE " + e.Path + " failed to load"); continue; }
            source.clip = clip;
            source.loop = false;
            var p = System.Diagnostics.Stopwatch.StartNew();
            source.Play();
            double playMs = p.Elapsed.TotalMilliseconds;
            yield return new WaitForSecondsRealtime(1.2f);
            int at = source.timeSamples;
            bool playing = source.isPlaying;
            source.Stop();
            Log(string.Format(Inv, "PROBE {0} load={1} state={2} {3}Hz {4}ch samples={5} length={6:F2}s loadMs={7:F1} usedLP={8} MB playMs={9:F1} playing={10} timeSamples={11}",
                e.Path, clip.loadType, clip.loadState, clip.frequency, clip.channels, clip.samples, clip.length, loadMs, Mb(before.LP - loaded.LP), playMs, playing, at));
            source.clip = null;
            Resources.UnloadAsset(clip);
            yield return null;
        }
        Destroy(source);
    }
}
