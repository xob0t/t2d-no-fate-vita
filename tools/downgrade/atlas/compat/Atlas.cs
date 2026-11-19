// The PS Vita port's texture pages: the game's twelve 4096x4096 packs, repacked at build time
// (tools/downgrade/atlas/repack.py) into 1024x1024 pages grouped by scene, and loaded per scene instead of
// all at start (docs/textures.md).
//
// The rewrite rules (atlas/rewriter/AtlasPatches.cs) send the game here:
//   R1  AssetsPack.staticInit5..9 take the frame table from Tex, Uvx, Uvy, Uvw, Uvh; imagetexnumber = PageCount()
//   R2  BBSplash.update's pack loads go to SplashStep (nothing to load there now)
//   R3  AssetsEmbed.staticInit's splash pack load goes to Enter("app:BBSplash")
//   R4  BitmapData.loadTexturePacks(n) becomes Preload(n)
//   R5  BitmapData.unityDraw and unityDrawClippedWidth read Get(page) instead of BitmapData.texpack[page]
//   R6  Transition.getApp's result goes through EnterApp; GameMap.loadFile starts with EnterMap,
//       Game.loadScriptByLevelID with EnterLevel
//
// Resources/atlas, written by repack.py:
//   frames.bytes  'T2DF', u32 format (1), u32 rows, u32 pages; per page u16 width, u16 height;
//                 per row u32 frames, then per frame u16 page, u16 x, u16 y (from the bottom), u16 w, u16 h
//   scenes.bytes  'T2DS', u32 format (1), u32 scenes; per scene string name, u16 n, u16 pages[n];
//                 u32 keys; per key string key, u16 n, u16 scenes[n]; u16 n, string level apps[n];
//                 u16 n, u16 sticky pages[n]. Strings: u16 byte count, UTF-8.
//   pNNN.png      the pages, imported as RGBA 4444, point filtered, Repeat wrapped (the game's flipped meshes
//                 sample at uv - 1), no mipmaps, not readable;
//   or pNNN.bytes (repack.py --raw) RGBA 4444 texels, rows bottom first, made into a texture with
//                 LoadRawTextureData and Apply(false, true), which frees the CPU copy.
//
// Residency: the pages of the current app's scene and of the current level's scene. An Enter unloads the pages
// the new scenes don't need before it loads the ones they do, so the peak stays at one scene. A draw from a page
// that isn't resident (a frame the static analysis missed) loads it at once and logs the miss; such pages go at
// the next Enter. Sticky pages (other languages' fonts) stay once loaded.
//
// Memory: Resources.Load of a page takes a millisecond; the PS Vita player reads its texels at the page's first draw,
// about 2.6 s for 32 MB (tools/assess/vita-texture-test), into video memory (CDRAM) while it has room and into main
// memory after. So a scene's pages cost nothing at the Enter and freeze its first frame. Two things follow:
// - The budget follows free video memory (BudgetBytes): a page that wouldn't fit beside the pages loaded but not drawn
//   yet first unloads pages no current scene wants, least recently used first. Pages the scenes want load anyway, and
//   go to main memory, which is slower to sample.
// - Ballast: the player puts a page in CDRAM while it has room, whatever the budget says, and with CDRAM full its
//   render thread stalls for good on its own next allocation (Cyberdyne Factory's 108 MB left 0.5 MB and froze the
//   level's first frame). So while pages wait for their first draw, a render texture of BallastBytes holds CDRAM;
//   the pages that don't fit beside it go to main memory, and its release gives the player its headroom back.
// - Prefetch: a mission briefing (PrefetchApps) loads the pages of the level it briefs, which its start() names
//   through Game.loadScriptByLevelID (EnterLevel, outside a level), and SpriteBatch.Flush draws one of them a frame,
//   invisibly (NextWarm), so their texels are read while the briefing shows rather than on the level's first frame.
//
// C# 5, as T2DCompat is compiled by the .NET Framework's csc.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace T2DCompat
{
    public static class Atlas
    {
        const string Folder = "atlas/";
        const int Format = 1;

        // The budget where the player doesn't report free video memory (Windows): over this many bytes of pages
        // resident, a load first unloads pages no current scene needs, least recently used first.
        public static long FixedBudgetBytes = 150L * 1024 * 1024;
        // Video memory left to the rest of the player: render textures, the sprite batch's meshes, the GPU's own.
        public static long CdramReserveBytes = 6L * 1024 * 1024;
        // The ballast: a 2048x1024 ARGB32 render texture, held while pages wait for their first draw (HoldBallast).
        public static long BallastBytes = 8L * 1024 * 1024;
        // Apps whose start prefetches the level named just before (a briefing's constructor loads its level's script),
        // and the apps between them and the level that keep what was prefetched (story mode's controls tutorial).
        public static readonly string[] PrefetchApps = { "MissionBriefing" };
        public static readonly string[] PrefetchKeep = { "LevelControls" };
        // Prefetch with Resources.LoadAsync (the probe's -t2d-atlas-sync turns it off). The loading thread then reads
        // the texels, and the first draw only uploads them: Future War 1's 19 pages took the briefing 7.8 s with no frame
        // over 0.4 s, against a 6.7 s freeze with Resources.Load.
        public static bool PrefetchAsync = true;
        // How fast the player reads a page's texels at its first draw (2.6 s for 32 MB in the texture test): the
        // prefetch draws the next page only after the last one's time is up. The render thread reads the page, and
        // the main thread waits for it, so drawing them all at once froze the briefing for 6.3 s; paced, it slows.
        public static float WarmBytesPerSecond = 14f * 1024 * 1024;
        // Log every miss (true) or only the first per page and scene (false).
        public static bool LogAllMisses = false;

        static bool loaded;
        static int[] pageW, pageH;
        static int[][] tex;
        static double[][] uvx, uvy, uvw, uvh;
        static string[] sceneNames;
        static int[][] scenePages;
        static Dictionary<string, int[]> keys;
        static HashSet<string> levelApps;
        static bool[] sticky;

        static Texture2D[] pages;
        static bool[] made;         // made from a raw TextAsset (destroyed), not loaded as a Texture2D (unloaded)
        static bool[] drawn;        // drawn since it was loaded: its texels are in memory
        static int[] lastUse;
        static int useClock;
        static long residentBytes;
        static long pendingBytes;   // resident pages not drawn yet: their memory is taken at the first draw
        static long releasingBytes; // drawn pages unloaded, until ReleaseUnloaded gives their memory back

        static string appKey = "";
        static int[] appScenes = new int[0];
        static int[] levelScenes = new int[0];
        static int[] prefetchScenes = new int[0];
        static string nextLevel;    // the level a menu or a briefing named last, outside a level
        static string prefetchKey;
        static float prefetchStart, warmNextAt;
        static int prefetchPages, prefetchAsyncPages;
        static readonly Queue<int> warmQueue = new Queue<int>();
        static readonly Dictionary<int, ResourceRequest> asyncLoads = new Dictionary<int, ResourceRequest>();
        static long asyncBytes;
        static bool[] wanted;
        static string sceneLabel = "(start)";

        static int misses;
        static readonly HashSet<long> missLogged = new HashSet<long>();
        static readonly Dictionary<int, int> missesByPage = new Dictionary<int, int>();

        // ---- tables (R1) -------------------------------------------------------------------------

        public static int[][] Tex() { Load(); return tex; }
        public static double[][] Uvx() { Load(); return uvx; }
        public static double[][] Uvy() { Load(); return uvy; }
        public static double[][] Uvw() { Load(); return uvw; }
        public static double[][] Uvh() { Load(); return uvh; }
        public static int PageCount() { Load(); return pageW.Length; }

        // ---- pages (R4, R5) ----------------------------------------------------------------------

        // The page, loading it now if it isn't resident.
        public static Texture2D Get(int page)
        {
            if (Trace.On) Trace.Enter("Atlas.Get");
            Load();
            if (page < 0 || page >= pages.Length) return null;
            Texture2D t = pages[page];
            if (t == null)
            {
                Miss(page);
                t = LoadPage(page);
            }
            lastUse[page] = ++useClock;
            if (!drawn[page]) Drawn(page);
            return t;
        }

        static void Drawn(int page)
        {
            drawn[page] = true;
            pendingBytes -= PageBytes(page);
        }

        // loadTexturePacks(n): the game's own preloads, by pack number; a page now, harmless.
        public static void Preload(int page)
        {
            Load();
            if (page >= 0 && page < pages.Length && pages[page] == null) LoadPage(page);
        }

        // BBSplash.update loaded a pack per frame while the splash showed; the pages load per scene now.
        public static void SplashStep(int pack)
        {
        }

        public static bool IsResident(int page)
        {
            Load();
            return page >= 0 && page < pages.Length && pages[page] != null;
        }

        public static long ResidentBytes { get { return residentBytes; } }
        public static long PendingBytes { get { return pendingBytes; } }

        // The bytes of pages that fit in video memory now: those resident and drawn, those unloaded but not released
        // yet, and what is free beyond CdramReserveBytes. FixedBudgetBytes where the player doesn't say.
        public static long BudgetBytes
        {
            get
            {
                long free = FreeMemory.Bytes(FreeMemory.Cdram);
                if (free < 0) return FixedBudgetBytes;
                return residentBytes - pendingBytes + releasingBytes + free - CdramReserveBytes;
            }
        }

        // " pages=.. pagesMB=.. pendingMB=.. budgetMB=..", for the probe's heartbeat.
        public static string Summary()
        {
            if (!loaded) return "";
            int n = 0;
            foreach (Texture2D t in pages) if (t != null) n++;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, " pages={0} pagesMB={1:0.0} pendingMB={2:0.0} budgetMB={3:0.0}",
                n, residentBytes / 1048576.0, pendingBytes / 1048576.0, BudgetBytes / 1048576.0);
        }
        public static int Misses { get { return misses; } }

        // ---- scenes (R3, R6) ---------------------------------------------------------------------

        // After Transition.getApp: the App's class names the scene. During an App named in level_apps
        // (Game), the level and map hooks choose the pages; an App without a scene of its own keeps them.
        public static void EnterApp(object app)
        {
            if (app == null) return;
            if (AppEntered != null) AppEntered(app.GetType().Name);
            Enter("app:" + app.GetType().Name);
        }

        // Each App the game enters, by class name, before its start() (the probe's -t2d-set).
        public static Action<string> AppEntered;

        // Game.loadScriptByLevelID: also called by menus for level previews, so it acts only during a level app.
        // Outside one it names the level a briefing (PrefetchApps) that starts next prefetches.
        public static void EnterLevel(int levelID)
        {
            Load();
            if (!levelApps.Contains(appKey))
            {
                string key = "level:" + levelID;
                int[] level;
                // MissionBriefing.start names its level after the app's Enter: prefetch now. A constructor that names
                // it before the Enter leaves it in nextLevel for the Enter.
                if (Array.IndexOf(PrefetchApps, appKey) >= 0 && key != prefetchKey && keys.TryGetValue(key, out level))
                {
                    prefetchScenes = level;
                    prefetchKey = key;
                    Switch("prefetch " + key);
                    Prefetch();
                    return;
                }
                nextLevel = key;
                return;
            }
            SetLevel("level:" + levelID);
        }

        // GameMap.loadFile(map, level): maps are loaded to be played (levels, demos, a cutscene's backdrop).
        public static void EnterMap(int map, int levelID)
        {
            Load();
            SetLevel("map:" + map);
        }

        // A key as scenes.bytes has it: "app:<Class>", "level:<id>", "map:<id>".
        public static void Enter(string key)
        {
            Load();
            if (key.StartsWith("app:"))
            {
                string name = key.Substring(4);
                int[] scenes;
                bool known = keys.TryGetValue(key, out scenes);
                appKey = name;
                string named = nextLevel;
                nextLevel = null;
                if (levelApps.Contains(name))
                {
                    // Game: the level hooks that follow pick the pages; keep what is resident until then.
                    appScenes = new int[0];
                    return;
                }
                if (!known)
                {
                    Debug.Log("T2D atlas: no scene for " + key + "; pages stay as they are");
                    return;
                }
                appScenes = scenes;
                levelScenes = new int[0];
                int[] level;
                if (Array.IndexOf(PrefetchApps, name) >= 0 && named != null && keys.TryGetValue(named, out level))
                {
                    prefetchScenes = level;
                    prefetchKey = named;
                }
                else if (Array.IndexOf(PrefetchKeep, name) < 0)
                {
                    prefetchScenes = new int[0];
                    prefetchKey = null;
                }
                Switch(key);
                if (prefetchScenes.Length > 0 && prefetchKey == named) Prefetch();
                return;
            }
            SetLevel(key);
        }

        static void SetLevel(string key)
        {
            int[] scenes;
            if (!keys.TryGetValue(key, out scenes))
            {
                Debug.Log("T2D atlas: no scene for " + key + "; pages stay as they are");
                return;
            }
            // In the level app the level is chosen, and the prefetch has done its work (a map of the tutorial's
            // keeps it).
            bool prefetched = prefetchScenes.Length > 0 && levelApps.Contains(appKey);
            if (prefetched)
            {
                prefetchScenes = new int[0];
                prefetchKey = null;
            }
            if (SameScenes(scenes, levelScenes) && !prefetched) return;
            levelScenes = scenes;
            Switch(key);
        }

        static bool SameScenes(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static void Switch(string key)
        {
            float start = Time.realtimeSinceStartup;
            for (int i = 0; i < wanted.Length; i++) wanted[i] = false;
            var label = new StringBuilder();
            foreach (int[] set in new[] { appScenes, levelScenes, prefetchScenes })
                foreach (int s in set)
                {
                    foreach (int p in scenePages[s]) wanted[p] = true;
                    if (label.Length > 0) label.Append(set == prefetchScenes ? " + prefetch " : " + ");
                    label.Append(sceneNames[s]);
                }
            sceneLabel = label.Length > 0 ? label.ToString() : key;
            int unloaded = 0, added = 0;
            for (int p = 0; p < pages.Length; p++)
                if (pages[p] != null && !wanted[p] && !sticky[p])
                {
                    UnloadPage(p);
                    unloaded++;
                }
            bool async = PrefetchAsync;
            for (int p = 0; p < pages.Length; p++)
                if (wanted[p] && pages[p] == null && !asyncLoads.ContainsKey(p))
                {
                    if (async && !InScenes(p, appScenes) && !InScenes(p, levelScenes))
                    {
                        Evict(PageBytes(p));
                        LoadAsync(p);
                    }
                    else LoadPage(p);
                    added++;
                }
            Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "T2D atlas: {0} -> {1}: -{2} +{3} pages, {4:0.0} MB resident ({5:0.0} not drawn yet), budget {6:0.0} MB, {7:0} ms",
                key, sceneLabel, unloaded, added, residentBytes / 1048576.0, pendingBytes / 1048576.0, BudgetBytes / 1048576.0,
                (Time.realtimeSinceStartup - start) * 1000));
        }

        static bool InScenes(int page, int[] scenes)
        {
            foreach (int s in scenes)
                if (Array.IndexOf(scenePages[s], page) >= 0) return true;
            return false;
        }

        // ---- prefetch ----------------------------------------------------------------------------------------------

        // After the briefing's Switch: every page of the level not drawn yet waits for its invisible draw.
        static void Prefetch()
        {
            prefetchStart = Time.realtimeSinceStartup;
            prefetchPages = prefetchAsyncPages = 0;
            warmQueue.Clear();
            var order = new List<int>();
            foreach (int s in prefetchScenes)
                foreach (int p in scenePages[s])
                    if (((pages[p] != null && !drawn[p]) || asyncLoads.ContainsKey(p)) && !order.Contains(p)) order.Add(p);
            // Small pages first, so the briefing slows a little at a time before the big layers stop it once each.
            order.Sort((a, b) => PageBytes(a) != PageBytes(b) ? PageBytes(a).CompareTo(PageBytes(b)) : a.CompareTo(b));
            foreach (int p in order)
            {
                warmQueue.Enqueue(p);
                prefetchPages++;
                if (asyncLoads.ContainsKey(p)) prefetchAsyncPages++;
            }
            Debug.Log("T2D atlas: prefetching " + prefetchPages + " pages of " + prefetchKey + (prefetchAsyncPages > 0 ? ", " + prefetchAsyncPages + " with LoadAsync" : ""));
        }

        static void LoadAsync(int page)
        {
            HoldBallast();
            ResourceRequest request = Resources.LoadAsync<Texture2D>(Folder + PageName(page));
            if (request == null)
            {
                LoadPage(page);
                return;
            }
            asyncLoads[page] = request;
            asyncBytes += PageBytes(page);
        }

        // From SpriteBatch.Flush, once a frame: the next prefetched page that hasn't been drawn, for an invisible draw
        // that makes the player read its texels now, or null.
        public static Texture2D NextWarm()
        {
            if (!loaded || Time.realtimeSinceStartup < warmNextAt) return null;
            while (warmQueue.Count > 0)
            {
                int p = warmQueue.Peek();
                ResourceRequest request;
                if (asyncLoads.TryGetValue(p, out request))
                {
                    if (!request.isDone) return null;
                    asyncLoads.Remove(p);
                    asyncBytes -= PageBytes(p);
                    var t = request.asset as Texture2D;
                    if (t == null || pages[p] != null)
                    {
                        if (pages[p] == null && wanted[p]) LoadPage(p);
                    }
                    else Adopt(p, t);
                }
                warmQueue.Dequeue();
                if (pages[p] == null || drawn[p]) continue;
                Drawn(p);
                lastUse[p] = ++useClock;
                warmNextAt = Time.realtimeSinceStartup + PageBytes(p) / WarmBytesPerSecond;
                if (warmQueue.Count == 0)
                    Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "T2D atlas: prefetched {0}: {1} pages drawn in {2:0.00} s, {3:0.0} MB resident, budget {4:0.0} MB",
                        prefetchKey ?? "(done)", prefetchPages, Time.realtimeSinceStartup - prefetchStart, residentBytes / 1048576.0, BudgetBytes / 1048576.0));
                return pages[p];
            }
            return null;
        }

        // ---- loading -----------------------------------------------------------------------------

        static Texture2D LoadPage(int page)
        {
            Evict(PageBytes(page));
            HoldBallast();
            Texture2D t = Resources.Load<Texture2D>(Folder + PageName(page));
            made[page] = false;
            if (t == null)
            {
                // repack.py --raw: the page as RGBA 4444 texels in a TextAsset.
                TextAsset raw = Resources.Load<TextAsset>(Folder + PageName(page));
                if (raw == null)
                {
                    Debug.LogError("T2D atlas: page " + PageName(page) + " is not in Resources/" + Folder);
                    return null;
                }
                t = new Texture2D(pageW[page], pageH[page], TextureFormat.RGBA4444, false);
                t.name = PageName(page);
                t.LoadRawTextureData(raw.bytes);
                t.filterMode = FilterMode.Point;
                t.wrapMode = TextureWrapMode.Repeat;
                t.Apply(false, true);
                Resources.UnloadAsset(raw);
                made[page] = true;
            }
            Adopt(page, t);
            return t;
        }

        static void Adopt(int page, Texture2D t)
        {
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Repeat;
            pages[page] = t;
            residentBytes += PageBytes(page);
            drawn[page] = false;
            pendingBytes += PageBytes(page);
            lastUse[page] = ++useClock;
        }

        // A page leaves the table at once, but its texture is released only two SpriteBatch flushes later
        // (ReleaseUnloaded): screen sprites queued earlier in the frame still draw with it when the camera renders,
        // and a destroyed texture there makes MaterialPropertyBlock.SetTexture throw, which lost the whole frame's
        // sprites on a scene switch, or when an eviction made room mid-frame.
        static void UnloadPage(int page)
        {
            Texture2D t = pages[page];
            pages[page] = null;
            residentBytes -= PageBytes(page);
            if (!drawn[page]) pendingBytes -= PageBytes(page);
            else releasingBytes += PageBytes(page);
            if (t == null) return;
            unloaded.Add(new Unloaded { Page = page, Texture = t, Made = made[page], Drawn = drawn[page] });
            drawn[page] = false;
        }

        struct Unloaded
        {
            public int Page;
            public Texture2D Texture;
            public bool Made;  // made from raw texels (Destroy), else a Resources asset (UnloadAsset)
            public bool Drawn; // its memory comes back when it is released
        }

        static List<Unloaded> unloaded = new List<Unloaded>();
        static List<Unloaded> unloadedBefore = new List<Unloaded>();

        // From SpriteBatch.Flush, before it draws: releases the textures of the pages unloaded before the last flush,
        // unless the page was loaded again since (Resources.Load gives back the same texture while it isn't unloaded).
        public static void ReleaseUnloaded()
        {
            StepBallast();
            foreach (Unloaded u in unloadedBefore)
            {
                if (u.Drawn) releasingBytes -= PageBytes(u.Page);
                if ((object)pages[u.Page] == (object)u.Texture) continue;
                if (u.Made) UnityEngine.Object.Destroy(u.Texture);
                else Resources.UnloadAsset(u.Texture);
            }
            unloadedBefore.Clear();
            List<Unloaded> swap = unloadedBefore;
            unloadedBefore = unloaded;
            unloaded = swap;
        }

        // Whether `need` more bytes of pages fit: in free video memory beyond the reserve, with the pages loaded but not
        // drawn yet (whose texels arrive at their first draw) and the pages unloaded but not released yet (whose memory
        // comes back then); or under FixedBudgetBytes where the player doesn't say.
        static bool Fits(long need)
        {
            long free = FreeMemory.Bytes(FreeMemory.Cdram);
            if (free < 0) return residentBytes + need <= FixedBudgetBytes;
            return pendingBytes + asyncBytes + need <= free + releasingBytes - CdramReserveBytes;
        }

        static int spills;

        // Unloads pages no current scene wants, least recently used first, until `need` more bytes fit. If they still
        // don't, the page goes to main memory.
        static void Evict(long need)
        {
            while (!Fits(need))
            {
                int victim = -1;
                for (int p = 0; p < pages.Length; p++)
                    if (pages[p] != null && !wanted[p] && !sticky[p] && (victim < 0 || lastUse[p] < lastUse[victim]))
                        victim = p;
                if (victim < 0)
                {
                    if (spills++ < 20)
                        Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "T2D atlas: video memory full in {0}: a {1:0.0} MB page goes to main memory ({2:0.0} MB of pages resident, {3:0.0} MB not drawn yet,{4})",
                            sceneLabel, need / 1048576.0, residentBytes / 1048576.0, pendingBytes / 1048576.0, FreeLine()));
                    return;
                }
                UnloadPage(victim);
            }
        }

        // ---- ballast -----------------------------------------------------------------------------

        static RenderTexture ballast;
        static int ballastIdle, ballastLogs;
        static long ballastFreeBefore = -1;

        // Before a page that waits for its first draw: CDRAM's headroom goes to a render texture, so that the page
        // goes to main memory if it doesn't fit beside it. Only with room for it and the reserve.
        static void HoldBallast()
        {
            ballastIdle = 0;
            if (ballast != null) return;
            long free = FreeMemory.Bytes(FreeMemory.Cdram);
            if (free < 0 || free < BallastBytes + CdramReserveBytes) return;
            var rt = new RenderTexture(2048, 1024, 0, RenderTextureFormat.ARGB32);
            rt.name = "T2D atlas ballast";
            if (!rt.Create())
            {
                UnityEngine.Object.Destroy(rt);
                return;
            }
            ballast = rt;
            ballastFreeBefore = free;
        }

        // Each flush: the ballast goes two flushes after the last page was drawn (the render thread reads a page at
        // the draw), or at once if CDRAM is down to half the reserve beside it outside a prefetch.
        static void StepBallast()
        {
            if (ballast == null) return;
            long free = FreeMemory.Bytes(FreeMemory.Cdram);
            if (ballastFreeBefore >= 0)
            {
                if (ballastLogs++ < 20)
                    Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "T2D atlas: ballast held in {0}: CDRAM {1:0.0} MB free before, {2:0.0} MB now", sceneLabel,
                        ballastFreeBefore / 1048576.0, free / 1048576.0));
                ballastFreeBefore = -1;
            }
            // Low outside a prefetch: pages that wait for a draw the scene may never make; the player's headroom first.
            // In a prefetch the ballast stays, or the pages still to come would take what it gave back.
            bool prefetching = asyncLoads.Count > 0 || warmQueue.Count > 0;
            bool waiting = pendingBytes > 0 || prefetching;
            bool low = free >= 0 && free < CdramReserveBytes / 2 && !prefetching;
            if (waiting && !low)
            {
                ballastIdle = 0;
                return;
            }
            if (!low && ++ballastIdle < 2) return;
            ballast.Release();
            UnityEngine.Object.Destroy(ballast);
            ballast = null;
            if (ballastLogs++ < 20)
                Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "T2D atlas: ballast released in {0}{1}: CDRAM {2:0.0} MB free, {3:0.0} MB of pages not drawn yet",
                    sceneLabel, low ? " (CDRAM low)" : "", free / 1048576.0, pendingBytes / 1048576.0));
        }

        static string FreeLine()
        {
            var line = new StringBuilder();
            for (int kind = 0; kind < 2; kind++)
            {
                long free = FreeMemory.Bytes(kind);
                if (free >= 0) line.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, " {0} {1:0.0} MB free", FreeMemory.Names[kind], free / 1048576.0));
            }
            return line.ToString();
        }

        static void Miss(int page)
        {
            misses++;
            int n;
            missesByPage.TryGetValue(page, out n);
            missesByPage[page] = n + 1;
            long id = ((long)sceneLabel.GetHashCode() << 32) | (uint)page;
            if (LogAllMisses || missLogged.Add(id))
                Debug.Log(string.Format("T2D atlas: miss, page {0} in {1} ({2} misses in all)", PageName(page), sceneLabel, misses));
        }

        // A summary of the misses so far, by page: for the scene data (scene_sets.py) when it is redone.
        public static string MissReport()
        {
            var b = new StringBuilder("T2D atlas misses: " + misses);
            foreach (KeyValuePair<int, int> kv in missesByPage) b.Append(' ').Append(PageName(kv.Key)).Append('x').Append(kv.Value);
            return b.ToString();
        }

        static long PageBytes(int page) { return (long)pageW[page] * pageH[page] * 2; }

        static string PageName(int page) { return "p" + page.ToString("000"); }

        // ---- the data ----------------------------------------------------------------------------

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            ReadFrames(Bytes("frames"));
            ReadScenes(Bytes("scenes"));
            pages = new Texture2D[pageW.Length];
            made = new bool[pageW.Length];
            drawn = new bool[pageW.Length];
            lastUse = new int[pageW.Length];
            wanted = new bool[pageW.Length];
            Debug.Log("T2D atlas: " + pageW.Length + " pages, " + tex.Length + " images, " + sceneNames.Length + " scenes");
        }

        static byte[] Bytes(string name)
        {
            TextAsset asset = Resources.Load<TextAsset>(Folder + name);
            if (asset == null) throw new InvalidOperationException("T2D atlas: Resources/" + Folder + name + ".bytes is missing");
            byte[] data = asset.bytes;
            Resources.UnloadAsset(asset);
            return data;
        }

        sealed class Reader
        {
            readonly byte[] data;
            int pos;
            public Reader(byte[] data, string magic)
            {
                this.data = data;
                if (data.Length < 8 || Encoding.ASCII.GetString(data, 0, 4) != magic)
                    throw new InvalidOperationException("T2D atlas: not a " + magic + " file");
                pos = 4;
                if (U32() != Format) throw new InvalidOperationException("T2D atlas: " + magic + " is another format");
            }
            public int U16() { int v = data[pos] | data[pos + 1] << 8; pos += 2; return v; }
            public int U32() { int v = data[pos] | data[pos + 1] << 8 | data[pos + 2] << 16 | data[pos + 3] << 24; pos += 4; return v; }
            public string Str() { int n = U16(); string s = Encoding.UTF8.GetString(data, pos, n); pos += n; return s; }
            public bool End { get { return pos == data.Length; } }
        }

        static void ReadFrames(byte[] data)
        {
            var r = new Reader(data, "T2DF");
            int rows = r.U32(), count = r.U32();
            pageW = new int[count];
            pageH = new int[count];
            for (int p = 0; p < count; p++) { pageW[p] = r.U16(); pageH[p] = r.U16(); }
            tex = new int[rows][];
            uvx = new double[rows][];
            uvy = new double[rows][];
            uvw = new double[rows][];
            uvh = new double[rows][];
            for (int i = 0; i < rows; i++)
            {
                int n = r.U32();
                tex[i] = new int[n];
                uvx[i] = new double[n];
                uvy[i] = new double[n];
                uvw[i] = new double[n];
                uvh[i] = new double[n];
                for (int j = 0; j < n; j++)
                {
                    int p = r.U16();
                    double w = pageW[p], h = pageH[p];
                    tex[i][j] = p;
                    uvx[i][j] = r.U16() / w;
                    uvy[i][j] = r.U16() / h;
                    uvw[i][j] = r.U16() / w;
                    uvh[i][j] = r.U16() / h;
                }
            }
            if (!r.End) throw new InvalidOperationException("T2D atlas: frames.bytes has trailing data");
        }

        static void ReadScenes(byte[] data)
        {
            var r = new Reader(data, "T2DS");
            int count = r.U32();
            sceneNames = new string[count];
            scenePages = new int[count][];
            for (int s = 0; s < count; s++)
            {
                sceneNames[s] = r.Str();
                scenePages[s] = new int[r.U16()];
                for (int k = 0; k < scenePages[s].Length; k++) scenePages[s][k] = r.U16();
            }
            keys = new Dictionary<string, int[]>();
            int nkeys = r.U32();
            for (int k = 0; k < nkeys; k++)
            {
                string key = r.Str();
                var scenes = new int[r.U16()];
                for (int i = 0; i < scenes.Length; i++) scenes[i] = r.U16();
                keys[key] = scenes;
            }
            levelApps = new HashSet<string>();
            for (int n = r.U16(), i = 0; i < n; i++) levelApps.Add(r.Str());
            sticky = new bool[pageW.Length];
            for (int n = r.U16(), i = 0; i < n; i++) sticky[r.U16()] = true;
            if (!r.End) throw new InvalidOperationException("T2D atlas: scenes.bytes has trailing data");
        }
    }
}
