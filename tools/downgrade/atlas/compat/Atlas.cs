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
// - Ahead: an App that is always followed by the same one (AheadApps: the boot's splash, logo, attract, menu, and the
//   menu's briefing) loads and warms the next one's pages the same way, so the next App's first frames find them drawn.
// - Every load but a miss's and the game's own preloads is a Resources.LoadAsync, and every page a scene wants that
//   isn't drawn yet gets its invisible draw (WarmAll), paced: the loading thread reads the texels, a first draw only
//   uploads them (about 21 ms a MB). Get waits for a page still loading. Background loads (prefetch, ahead) start
//   PrefetchDelayMs after a Switch, PrefetchInFlight at a time, so a synchronous load on the main thread (the
//   briefing's music, a sound) waits for few of them.
// - A page over WarmBigBytes (the 16 and 32 MB layers; 0.7 s to upload a 32 MB one) isn't warmed while an App shows:
//   it is warmed in the frames just after the next App or level Enter (CoverMs), which Transition.update makes while
//   its fade covers the screen, with whatever else is still waiting.
// - Slow frames (SlowFrameMs) are logged with what the atlas did in them and the frame before.
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
        // Scenes whose pages load at their first draw, not at the Enter. The four attract demos were one scene of 33
        // pages, 190 MB, when the usage analysis merged them, and loading them all froze the demo's first frames for 17
        // to 19 s; now each demo's map is a scene of its own (build_scenes.py's DEMOS), which the menu loads ahead
        // (IdleTicks), and none is lazy.
        public static readonly string[] LazyScenes = { };
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

        // The switches below are bools and ints so that the probe's -t2d-set can turn each off for a comparison run.
        // Apps always followed by the same App, "<from>><to>": the next one's pages load and warm while the first shows.
        public static readonly string[] AheadApps = { "BBSplash>ReefLogo", "ReefLogo>Attract", "Attract>MainMenu", "MainMenu>MissionBriefing" };
        // Apps followed by another only sometimes, "<from>><to>?<bool field of the from-App>&<Namespace.Type.static bool>":
        // the next one's pages load ahead when both are true at AheadDelayMs. A story-mode briefing goes on to the
        // controls tutorial when Game.showTutorials is on and its level shows it (MissionBriefing.start sets
        // forceControlsExplanation, after the Enter); the tutorial's Enter loaded its 10 pages and warmed them under the
        // cover, a 1.0 to 1.6 s frame on every story level.
        public static readonly string[] AheadIfApps = { "MissionBriefing>LevelControls?forceControlsExplanation&com.bitmapbureau.terminator.game.Game.showTutorials" };
        public static bool Ahead = true;
        // Apps between a level and its restart that keep the level's pages: the continue screen after a death. It
        // unloaded them, and the restart loaded the level's whole scene again, its first frames 1.5 to 6 s.
        public static readonly string[] LevelKeep = { "ContinueScreen" };
        // "<App>.<BitmapData field>": the pages of that image go first in the App's background loads. The briefing
        // draws its level's portrait (a page of the level's scene) once its screen has turned on, about 3 s in, and
        // waiting for that page there took 350 to 430 ms when the prefetch hadn't reached it.
        public static readonly string[] FirstPages = { "MissionBriefing.previewImage" };
        // Under the cover, wait for a page still loading to warm it (true), or warm only those loaded (false). A
        // page drawn before its LoadAsync finishes has its texels read at the draw (about 100 ms a MB), so waiting
        // for the scene's pages there made the controls tutorial's first frames 1.3 s.
        public static bool CoverWaits = false;
        // A Switch queues the scenes' own pages first among the background loads, PrefetchInFlight at a time, and Get
        // waits for those it draws (true); or it starts them all at once (false). Waiting for one request completes
        // every request started before it, and their texels were then read in one frame: a briefing skipped after
        // 4 s left the Factory's 26 pages to its level's second frame, 5.8 s.
        public static bool SwitchQueues = true;
        // A page a miss loaded this many times stays loaded like a sticky page (0: never), at most StickyMissMax of
        // them. The scene data lacks two pages that nearly every level, menu and cutscene draws (p076, p083 in Steam's
        // build 24040403): each scene loaded them again at its first draw, a 200 ms frame, the continue screen's
        // restart of the level included.
        public static int StickyMisses = 2;
        public static int StickyMissMax = 6;
        public static int AheadDelayMs = 1000;
        // A Switch loads the scenes' pages with Resources.LoadAsync too (Get waits for one still loading), not Resources.Load.
        public static bool AsyncSwitch = true;
        // Every wanted page not drawn yet is warmed, not only the prefetch's; the first warm WarmDelayMs after a Switch.
        public static bool WarmAll = true;
        public static int WarmDelayMs = 500;
        // Background loads (prefetch, ahead) in flight at once (0: all at once), from PrefetchDelayMs after their Switch.
        public static int PrefetchInFlight = 2;
        public static int PrefetchDelayMs = 500;
        // Pages over this many bytes are warmed only in the CoverMs after an App or level Enter (0: like the others).
        public static int WarmBigBytes = 8 * 1024 * 1024;
        public static int CoverMs = 300;
        // Under the cover, at most this many bytes of pages warmed (and waited for) in one flush, the first page
        // whatever its size; the rest go on at the usual pace (0: no limit). A briefing skipped early left a level's
        // whole scene to one flush.
        public static int CoverWarmBytes = 16 * 1024 * 1024;
        // A frame longer than this logs what the atlas did in it (0: off).
        public static int SlowFrameMs = 100;

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
        static int[] aheadScenes = new int[0];
        static string aheadKey;     // the next App's key, until AheadDelayMs is up
        static string aheadIf;      // its condition (AheadIfApps), or null
        static object currentApp;   // the App EnterApp had last
        static bool levelAppEntered; // the level App was entered and no Switch came since: its app scenes went
        static float aheadAt;
        static string prefetchKey;
        static float prefetchStart, warmNextAt, warmStartAt, coverUntil;
        static int prefetchPages, prefetchAsyncPages;
        static readonly HashSet<int> prefetchLeft = new HashSet<int>();
        static readonly List<int> warmList = new List<int>();     // wanted pages not drawn yet, smallest first
        static readonly List<int> bgQueue = new List<int>();      // background loads not started yet
        static float bgStartAt;
        static readonly Dictionary<int, ResourceRequest> asyncLoads = new Dictionary<int, ResourceRequest>();
        static readonly List<int> doneLoads = new List<int>();
        static readonly Dictionary<int, float> asyncStarted = new Dictionary<int, float>();
        static long asyncBytes;
        static bool[] viaAsync;     // loaded with LoadAsync, for the slow-frame log
        static bool[] wanted, eager;
        static bool[] missing;      // not in the build: logged once, its draws skipped
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
        public static int PageCount() { Load(); return realPages; }

        // ---- pages (R4, R5) ----------------------------------------------------------------------

        // The page, loading it now if it isn't resident.
        public static Texture2D Get(int page)
        {
            if (Trace.On) Trace.Enter("Atlas.Get");
            Load();
            if (page < 0 || page >= pages.Length) return null;
            if (page < realPages && tileFirst[page] >= 0)
            {
                // SpriteBatch draws a tiled page's frames from its tiles (Tiles); a caller that doesn't gets the first.
                if (tiledGets++ == 0) Debug.LogWarning("T2D atlas: Get of " + PageName(page) + ", which is drawn from tiles");
                page = tileFirst[page];
            }
            Texture2D t = pages[page];
            if (t == null)
            {
                // A page no current scene wants, drawn anyway, is wanted until the next Switch, as the scenes' own pages:
                // an eviction made it load again on its next draw, every few frames with CDRAM full (the Factory's p007,
                // p043, p065). Before WaitFor, so that a load still in flight for it is kept, not unloaded as it completes.
                bool demanded = !wanted[page];
                wanted[page] = true;
                t = WaitFor(page);
                if (t == null)
                {
                    Miss(page);
                    t = LoadPage(page);
                }
                else if (demanded) Miss(page);
            }
            // A page missing from the build: nothing was added to count off, and the draw is skipped (SpriteBatch).
            if (t == null) return null;
            lastUse[page] = ++useClock;
            if (!drawn[page])
            {
                Drawn(page);
                Note("draw " + PageName(page) + " " + Mb(page) + (viaAsync[page] ? " async" : " sync"));
            }
            return t;
        }

        static void Drawn(int page)
        {
            drawn[page] = true;
            pendingBytes -= PageBytes(page);
            PrefetchDrawn(page);
        }

        // A page of the prefetch is in video memory: drawn, or adopted again while its drawn texture waited for release.
        static void PrefetchDrawn(int page)
        {
            if (prefetchLeft.Remove(page) && prefetchLeft.Count == 0 && prefetchKey != null)
                Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "T2D atlas: prefetched {0}: {1} pages drawn in {2:0.00} s, {3:0.0} MB resident, budget {4:0.0} MB",
                    prefetchKey, prefetchPages, Time.realtimeSinceStartup - prefetchStart, residentBytes / 1048576.0, BudgetBytes / 1048576.0));
        }

        // A page Get needs that is loading in the background or waits to: finished now (the main thread waits for the
        // loading thread), or null if it isn't one of those.
        static Texture2D WaitFor(int page)
        {
            ResourceRequest request;
            bool queued = bgQueue.Remove(page);
            if (!asyncLoads.TryGetValue(page, out request))
            {
                if (!queued) return null;
                Evict(PageBytes(page));
                LoadAsync(page);
                if (!asyncLoads.TryGetValue(page, out request)) return pages[page];
            }
            float start = Time.realtimeSinceStartup;
            Complete(page, request);
            waits++;
            Note("wait " + PageName(page) + " " + Mb(page) + " " + Ms(start));
            return pages[page];
        }

        static int waits, tiledGets, switches;

        // loadTexturePacks(n): the game's own preloads, by pack number; a page now, harmless.
        public static void Preload(int page)
        {
            Load();
            int cols, rows, first = Tiles(page, out cols, out rows);
            if (first >= 0)
            {
                for (int k = 0; k < cols * rows; k++) Preload(first + k);
                return;
            }
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
            currentApp = app;
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
                // Transition.getApp makes the App while the fade covers the screen: big pages warm now.
                coverUntil = Time.realtimeSinceStartup + CoverMs / 1000f;
                aheadScenes = new int[0];
                aheadKey = null;
                aheadIf = null;
                fadeAhead = null;
                // The fade that made this App is over: Transition.update enters the main menu with dir 0 and no fade
                // in after any App but the attract, and HoldFade would not see the fade end.
                fadeTo = -1;
                holdSince = -1;
                firstSet.Clear();
                firstPending = true;
                if (Ahead)
                {
                    foreach (string pair in AheadApps)
                        if (pair.StartsWith(name + ">", StringComparison.Ordinal))
                        {
                            aheadKey = "app:" + pair.Substring(name.Length + 1);
                            aheadAt = Time.realtimeSinceStartup + AheadDelayMs / 1000f;
                        }
                    foreach (string pair in AheadIfApps)
                        if (aheadKey == null && pair.StartsWith(name + ">", StringComparison.Ordinal))
                        {
                            int q = pair.IndexOf('?');
                            aheadKey = "app:" + pair.Substring(name.Length + 1, q - name.Length - 1);
                            aheadIf = pair.Substring(q + 1);
                            aheadAt = Time.realtimeSinceStartup + AheadDelayMs / 1000f;
                        }
                }
                if (levelApps.Contains(name))
                {
                    // Game: the level hooks that follow pick the pages; keep what is resident until then, and let the
                    // first of them switch even to the same level (a restart from the continue screen), so that the
                    // last App's pages go.
                    appScenes = new int[0];
                    levelAppEntered = true;
                    return;
                }
                if (!known)
                {
                    Debug.Log("T2D atlas: no scene for " + key + "; pages stay as they are");
                    return;
                }
                appScenes = scenes;
                if (Array.IndexOf(LevelKeep, name) < 0) levelScenes = new int[0];
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
                prefetchLeft.Clear();
                // The level's start, still under the transition's fade: what the briefing left waits no longer.
                coverUntil = Time.realtimeSinceStartup + CoverMs / 1000f;
            }
            if (SameScenes(scenes, levelScenes) && !prefetched && !levelAppEntered) return;
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
            levelAppEntered = false;
            for (int i = 0; i < wanted.Length; i++) wanted[i] = eager[i] = false;
            var label = new StringBuilder();
            foreach (int[] set in new[] { appScenes, levelScenes, prefetchScenes, aheadScenes })
                foreach (int s in set)
                {
                    bool lazy = Array.IndexOf(LazyScenes, sceneNames[s]) >= 0;
                    foreach (int p in scenePages[s])
                    {
                        wanted[p] = true;
                        if (!lazy) eager[p] = true;
                    }
                    // The next App's scene by number only: drive.py's waits (until app:MainMenu, until MissionBriefing)
                    // read the log for the App's name, and must not find it before the App starts.
                    if (set == aheadScenes)
                    {
                        label.Append(" + ahead scene ").Append(s);
                        continue;
                    }
                    if (label.Length > 0) label.Append(set == prefetchScenes ? " + prefetch " : " + ");
                    label.Append(sceneNames[s]);
                }
            sceneLabel = label.Length > 0 ? label.ToString() : key;
            int unloaded = 0, added = 0, background = 0;
            for (int p = 0; p < pages.Length; p++)
                if (pages[p] != null && !wanted[p] && !sticky[p])
                {
                    UnloadPage(p);
                    unloaded++;
                }
            bgQueue.RemoveAll(p => !wanted[p]);
            bool async = PrefetchAsync;
            // The scenes' own pages now; the prefetch's and the next App's in the background, smallest first.
            var later = new List<int>();
            var ownQueue = new List<int>();
            for (int p = 0; p < pages.Length; p++)
                if (eager[p] && pages[p] == null && !asyncLoads.ContainsKey(p) && !bgQueue.Contains(p))
                {
                    bool own = InScenes(p, appScenes) || InScenes(p, levelScenes);
                    if (async && !own) later.Add(p);
                    // Not the first (the splash's, at boot): its LoadAsync pages integrated while the clips loaded, in
                    // one 0.7 to 2 s frame; Resources.Load costs nothing until a page is drawn.
                    else if (async && AsyncSwitch && switches > 0)
                    {
                        if (SwitchQueues) ownQueue.Add(p);
                        else
                        {
                            Evict(PageBytes(p));
                            LoadAsync(p);
                        }
                    }
                    else LoadPage(p);
                    added++;
                }
            // A level's Enter: what the briefing hadn't started yet starts now, first in the queue (SwitchQueues).
            for (int i = bgQueue.Count - 1; i >= 0; i--)
                if (InScenes(bgQueue[i], levelScenes) || InScenes(bgQueue[i], appScenes))
                {
                    int p = bgQueue[i];
                    bgQueue.RemoveAt(i);
                    if (SwitchQueues) ownQueue.Add(p);
                    else
                    {
                        Evict(PageBytes(p));
                        LoadAsync(p);
                    }
                }
            ownQueue.Sort(BySize);
            bgQueue.InsertRange(0, ownQueue);
            later.Sort(BySize);
            // The next App's pages before a prefetch's still waiting: that App comes first (the controls tutorial
            // before its level).
            if (key == "ahead")
            {
                int at = 0;
                while (at < bgQueue.Count && firstSet.Contains(bgQueue[at])) at++;
                bgQueue.InsertRange(at, later);
            }
            else bgQueue.AddRange(later);
            background = later.Count;
            if (later.Count > 0) bgStartAt = Time.realtimeSinceStartup + (PrefetchInFlight > 0 ? PrefetchDelayMs / 1000f : 0f);
            if (ownQueue.Count > 0) bgStartAt = Time.realtimeSinceStartup;
            RebuildWarm();
            switches++;
            Note("switch " + key + " " + Ms(start));
            Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "T2D atlas: {0} -> {1}: -{2} +{3} pages{8}, {4:0.0} MB resident ({5:0.0} not drawn yet), budget {6:0.0} MB, {7:0} ms",
                key, sceneLabel, unloaded, added, residentBytes / 1048576.0, pendingBytes / 1048576.0, BudgetBytes / 1048576.0,
                (Time.realtimeSinceStartup - start) * 1000, background > 0 ? " (" + background + " in the background)" : ""));
        }

        static int BySize(int a, int b)
        {
            return PageBytes(a) != PageBytes(b) ? PageBytes(a).CompareTo(PageBytes(b)) : a.CompareTo(b);
        }

        // The pages to warm: those wanted and not drawn yet, resident or on their way, smallest first; all of them, or
        // (without WarmAll) the prefetch's and the next App's. The first warm waits WarmDelayMs, for the scene's own draws.
        static void RebuildWarm()
        {
            warmList.Clear();
            for (int p = 0; p < pages.Length; p++)
            {
                if (!wanted[p] || (pages[p] != null && drawn[p])) continue;
                if (pages[p] == null && !asyncLoads.ContainsKey(p) && !bgQueue.Contains(p)) continue;
                if (!WarmAll && !InScenes(p, prefetchScenes) && !InScenes(p, aheadScenes)) continue;
                warmList.Add(p);
            }
            warmList.Sort(BySize);
            warmStartAt = Time.realtimeSinceStartup + WarmDelayMs / 1000f;
        }

        static bool InScenes(int page, int[] scenes)
        {
            foreach (int s in scenes)
                if (Array.IndexOf(scenePages[s], page) >= 0) return true;
            return false;
        }

        // ---- prefetch ----------------------------------------------------------------------------------------------

        // After the briefing's Switch: the level's pages not drawn yet, for the log line when the last is drawn.
        static void Prefetch()
        {
            prefetchStart = Time.realtimeSinceStartup;
            prefetchPages = prefetchAsyncPages = 0;
            prefetchLeft.Clear();
            foreach (int s in prefetchScenes)
                foreach (int p in scenePages[s])
                    if (((pages[p] != null && !drawn[p]) || asyncLoads.ContainsKey(p) || bgQueue.Contains(p)) && prefetchLeft.Add(p))
                    {
                        prefetchPages++;
                        if (pages[p] == null) prefetchAsyncPages++;
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
            asyncStarted[page] = Time.realtimeSinceStartup;
            asyncBytes += PageBytes(page);
        }

        // A LoadAsync done (or waited for): the page joins the table, or goes again if no scene wants it any more.
        static void Complete(int page, ResourceRequest request)
        {
            asyncLoads.Remove(page);
            asyncBytes -= PageBytes(page);
            float start = Time.realtimeSinceStartup;
            var t = request.asset as Texture2D;
            float issued;
            if (asyncStarted.TryGetValue(page, out issued)) asyncStarted.Remove(page);
            Note("done " + PageName(page) + " " + Mb(page) + " after " + Ms(issued) + " (" + Ms(start) + ")");
            if (pages[page] != null) return;
            if (t == null)
            {
                if (wanted[page]) LoadPage(page);
                return;
            }
            Adopt(page, t);
            viaAsync[page] = true;
            // Through UnloadPage, so that its release waits two flushes like any other's.
            if (!wanted[page] && !sticky[page]) UnloadPage(page);
        }

        // From SpriteBatch.Flush, once a frame before the warm draws: finishes the loads that are done, starts the
        // background loads that may start, and the next App's pages once AheadDelayMs is up.
        static void Pump()
        {
            float now = Time.realtimeSinceStartup;
            if (asyncLoads.Count > 0)
            {
                doneLoads.Clear();
                foreach (KeyValuePair<int, ResourceRequest> kv in asyncLoads) if (kv.Value.isDone) doneLoads.Add(kv.Key);
                foreach (int p in doneLoads) Complete(p, asyncLoads[p]);
            }
            if (aheadKey != null && Ahead && now >= aheadAt)
            {
                int[] scenes;
                string key = aheadKey;
                aheadKey = null;
                if ((aheadIf == null || AheadHolds(aheadIf)) && keys.TryGetValue(key, out scenes))
                {
                    aheadScenes = scenes;
                    fadeAhead = key;
                    Switch("ahead");
                }
            }
            if (firstPending) PromoteFirst();
            IdleDemo(now);
            IssueBackground();
            if (!Busy && Idle != null) Idle();
        }

        // An AheadIfApps condition, "<bool field of the current App>&<Namespace.Type.static bool>", read by reflection:
        // T2DCompat can't name the game's types. A field that isn't there counts as false, once logged.
        static bool AheadHolds(string condition)
        {
            if (currentApp == null) return false;
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            string[] parts = condition.Split('&');
            Type appType = currentApp.GetType();
            System.Reflection.FieldInfo own = appType.GetField(parts[0], any | System.Reflection.BindingFlags.Instance);
            int dot = parts[1].LastIndexOf('.');
            Type type = appType.Assembly.GetType(parts[1].Substring(0, dot));
            System.Reflection.FieldInfo stat = type == null ? null : type.GetField(parts[1].Substring(dot + 1), any | System.Reflection.BindingFlags.Static);
            if (own == null || stat == null || own.FieldType != typeof(bool) || stat.FieldType != typeof(bool))
            {
                if (aheadIfLogs++ == 0) Debug.LogWarning("T2D atlas: no bool fields for the ahead condition " + condition);
                return false;
            }
            bool holds = (bool)own.GetValue(currentApp) && (bool)stat.GetValue(null);
            Debug.Log("T2D atlas: ahead condition " + condition + ": " + holds);
            return holds;
        }

        static int aheadIfLogs;

        // The first flush after an App's start(): the pages of its FirstPages image go to the front of the background
        // loads, which are smallest first otherwise. BitmapData.texpack_tex is the image's page per frame (R1's table).
        static void PromoteFirst()
        {
            firstPending = false;
            if (currentApp == null) return;
            const System.Reflection.BindingFlags own = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            string name = currentApp.GetType().Name;
            foreach (string spec in FirstPages)
            {
                int dot = spec.IndexOf('.');
                if (spec.Substring(0, dot) != name) continue;
                System.Reflection.FieldInfo field = currentApp.GetType().GetField(spec.Substring(dot + 1), own);
                object image = field == null ? null : field.GetValue(currentApp);
                System.Reflection.FieldInfo texField = image == null ? null : image.GetType().GetField("texpack_tex", own);
                int[] frames = texField == null ? null : texField.GetValue(image) as int[];
                if (frames == null)
                {
                    if (firstLogs++ < 5) Debug.Log("T2D atlas: no image in " + spec);
                    continue;
                }
                var first = new List<int>();
                foreach (int real in frames)
                {
                    int cols, rows, tile = Tiles(real, out cols, out rows);
                    for (int k = 0; k < (tile < 0 ? 1 : cols * rows); k++)
                    {
                        int p = tile < 0 ? real : tile + k;
                        if (p >= 0 && p < pages.Length && bgQueue.Remove(p)) first.Add(p);
                        if (p >= 0 && p < pages.Length) firstSet.Add(p);
                    }
                }
                bgQueue.InsertRange(0, first);
                if (firstLogs++ < 20) Debug.Log("T2D atlas: " + spec + ": " + first.Count + " of its pages first");
            }
        }

        static int firstLogs;
        static bool firstPending;
        static readonly HashSet<int> firstSet = new HashSet<int>();

        // Pages loading or waiting to, for SoundLoads, which starts no clip load meanwhile; Idle each flush they aren't
        // (Probe.Install joins the two).
        public static Action Idle;
        public static bool Busy { get { return loaded && (asyncLoads.Count > 0 || bgQueue.Count > 0); } }

        static void IssueBackground()
        {
            if (bgQueue.Count == 0 || Time.realtimeSinceStartup < bgStartAt) return;
            while (bgQueue.Count > 0 && (PrefetchInFlight <= 0 || asyncLoads.Count < PrefetchInFlight))
            {
                int p = bgQueue[0];
                bgQueue.RemoveAt(0);
                if (!wanted[p] || pages[p] != null || asyncLoads.ContainsKey(p)) continue;
                Evict(PageBytes(p));
                LoadAsync(p);
            }
        }

        // From SpriteBatch.Flush, once a frame, and again while it returns a page: the next page that is wanted and
        // not drawn yet, for an invisible draw that makes the player upload its texels now, or null. Paced by
        // WarmBytesPerSecond; pages over WarmBigBytes only in the CoverMs after an Enter, unpaced, as the screen is covered.
        public static Texture2D NextWarm()
        {
            if (!loaded || warmList.Count == 0) return null;
            float now = Time.realtimeSinceStartup;
            bool cover = now < coverUntil;
            bool paced = now < warmNextAt || now < warmStartAt;
            if (!cover && paced) return null;
            for (int i = 0; i < warmList.Count; i++)
            {
                int p = warmList[i];
                if (!wanted[p] || (pages[p] != null && drawn[p]) || (pages[p] == null && !asyncLoads.ContainsKey(p) && !bgQueue.Contains(p)))
                {
                    warmList.RemoveAt(i--);
                    continue;
                }
                // Under the cover, the scenes' own pages: all of them, big ones too, waited for if still loading.
                // A prefetch's or the next App's pages keep their pace.
                bool covered = cover && (InScenes(p, appScenes) || InScenes(p, levelScenes));
                if (covered && CoverWarmBytes > 0 && coverWarmed > 0 && coverWarmed + PageBytes(p) > CoverWarmBytes) covered = false;
                if (!covered && (paced || (WarmBigBytes > 0 && PageBytes(p) > WarmBigBytes))) continue;
                // A page Resources.Load gave is read at its first draw (about 80 ms a MB): only under the cover.
                if (!covered && pages[p] != null && !viaAsync[p]) continue;
                if (pages[p] == null && (!covered || !CoverWaits || WaitFor(p) == null)) continue;
                warmList.RemoveAt(i);
                if (covered) coverWarmed += PageBytes(p);
                Drawn(p);
                lastUse[p] = ++useClock;
                if (!covered) warmNextAt = now + PageBytes(p) / WarmBytesPerSecond;
                Note("warm " + PageName(p) + " " + Mb(p) + (covered ? " covered" : ""));
                return pages[p];
            }
            return null;
        }

        // ---- loading -----------------------------------------------------------------------------

        static Texture2D LoadPage(int page)
        {
            if (missing[page]) return null;
            Evict(PageBytes(page));
            HoldBallast();
            float start = Time.realtimeSinceStartup;
            Texture2D t = Resources.Load<Texture2D>(Folder + PageName(page));
            Note("load " + PageName(page) + " " + Mb(page) + " " + Ms(start));
            made[page] = false;
            viaAsync[page] = false;
            if (t == null)
            {
                // repack.py --raw: the page as RGBA 4444 texels in a TextAsset.
                TextAsset raw = Resources.Load<TextAsset>(Folder + PageName(page));
                if (raw == null)
                {
                    // Once: a draw of it would try again every frame.
                    Debug.LogError("T2D atlas: page " + PageName(page) + " is not in Resources/" + Folder + "; its draws are skipped");
                    missing[page] = true;
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
            // Pages wrap (the flipped meshes read at uv - 1); tiles clamp, since SpriteBatch moves those UVs into the
            // tile (SubmitTiled).
            t.wrapMode = page >= realPages ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            pages[page] = t;
            // Resources.Load gives back a texture unloaded but not released yet: its pending release goes, so that an
            // unload before the next flush waits its own two flushes rather than releasing it under this frame's draws.
            // One that was drawn stays drawn: its texels are in video memory already. Counted as pending again (before
            // Nov 2026), its size was reserved a second time for an upload that never comes, and an unload before its
            // next draw then counted no release for memory that does come back.
            bool wasDrawn = Unrelease(unloaded, t) | Unrelease(unloadedBefore, t);
            residentBytes += PageBytes(page);
            drawn[page] = wasDrawn;
            if (!wasDrawn) pendingBytes += PageBytes(page);
            else PrefetchDrawn(page);
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

        // Cancels t's pending release; whether it had been drawn.
        static bool Unrelease(List<Unloaded> list, Texture2D t)
        {
            bool wasDrawn = false;
            for (int i = list.Count - 1; i >= 0; i--)
                if ((object)list[i].Texture == (object)t)
                {
                    if (list[i].Drawn) releasingBytes -= PageBytes(list[i].Page);
                    wasDrawn |= list[i].Drawn;
                    list.RemoveAt(i);
                }
            return wasDrawn;
        }

        // From SpriteBatch.Flush, before it draws: releases the textures of the pages unloaded before the last flush,
        // unless the page was loaded again since (Resources.Load gives back the same texture while it isn't unloaded).
        public static void ReleaseUnloaded()
        {
            if (!loaded) return;
            SlowFrame();
            StepBallast();
            coverWarmed = 0;
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
            // After the swap, so that what Pump unloads waits two flushes like the rest.
            Pump();
        }

        // ---- slow frames -------------------------------------------------------------------------

        static readonly StringBuilder noteNow = new StringBuilder();
        static string noteBefore = "";
        static float lastFlushAt = -1;
        static int slowLogs;

        // What the atlas did since the last flush: switches, loads, waits, first draws, warm draws.
        static void Note(string what)
        {
            if (SlowFrameMs <= 0) return;
            if (noteNow.Length < 600) noteNow.Append(noteNow.Length > 0 ? ", " : "").Append(what);
        }

        // Each flush: a frame longer than SlowFrameMs since the last one is logged with what the atlas did in it and
        // in the frame before (whose draws the render thread may have been working through), and the free memory.
        static void SlowFrame()
        {
            float now = Time.realtimeSinceStartup;
            float ms = lastFlushAt < 0 ? 0 : (now - lastFlushAt) * 1000f;
            lastFlushAt = now;
            if (SlowFrameMs > 0 && ms > SlowFrameMs && slowLogs++ < 200)
                Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "T2D atlas: slow frame {0:0} ms in {1}: [{2}] before: [{3}];{4} pages {5:0.0} MB, {6:0.0} not drawn yet, {7} loading, {8} waiting",
                    ms, sceneLabel, noteNow, noteBefore, FreeLine(), residentBytes / 1048576.0, pendingBytes / 1048576.0, asyncLoads.Count, bgQueue.Count));
            noteBefore = noteNow.ToString();
            noteNow.Length = 0;
        }

        static string Ms(float since)
        {
            return ((Time.realtimeSinceStartup - since) * 1000f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        }

        static string Mb(int page)
        {
            return (PageBytes(page) / 1048576.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";
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
        static long coverWarmed;    // bytes warmed under the cover in this flush (NextWarm)

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
            bool prefetching = asyncLoads.Count > 0 || bgQueue.Count > 0 || warmList.Count > 0;
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
            if (missLogged.Add(id) || LogAllMisses)
                Debug.Log(string.Format("T2D atlas: miss, page {0} in {1} ({2} misses in all)", PageName(page), sceneLabel, misses));
            if (sticky[page] || StickyMisses <= 0 || missesByPage[page] < StickyMisses || stickyByMiss >= StickyMissMax) return;
            sticky[page] = true;
            stickyByMiss++;
            Debug.Log("T2D atlas: " + PageName(page) + " missed " + missesByPage[page] + " times, stays loaded from now on");
        }

        static int stickyByMiss;

        // A summary of the misses so far, by page: for the scene data (scene_sets.py) when it is redone.
        public static string MissReport()
        {
            var b = new StringBuilder("T2D atlas misses: " + misses);
            foreach (KeyValuePair<int, int> kv in missesByPage) b.Append(' ').Append(PageName(kv.Key)).Append('x').Append(kv.Value);
            return b.ToString();
        }

        static long PageBytes(int page) { return (long)pageW[page] * pageH[page] * 2; }

        static string PageName(int page) { return page >= realPages && realPages > 0 ? tileNames[page - realPages] : "p" + page.ToString("000"); }

        // ---- the fade (R6) -----------------------------------------------------------------------
        //
        // Transition.update starts with HoldFade(dir, progress, speed, nextApp) and does nothing in a frame where it
        // returns true. A fade out runs Transition.update once a frame until its progress passes 1, the screen covered;
        // that update makes the next App. HoldFade keeps the fade at the covered screen while the next App's pages
        // still load, so that their texels are read by the loading thread, the game running on at 60 fps under the
        // cover, rather than at the next App's first draws, a freeze: a briefing skipped 4 to 6 s in left Cyberdyne
        // Factory's first frames 4 to 6 s (runs skipA to skipC). When the pages are ready it changes nothing.
        // At the fade's start, the next App's pages start loading ahead, so the fade's own frames cover some of the load.

        // "<Transition app id>:<App class>", from Transition.getApp: the Apps whose pages a fade into them loads first.
        public static readonly string[] FadeApps = { "4:MainMenu", "8:LevelSelect", "15:MissionBriefing", "16:ContinueScreen",
            "19:Credits", "22:MusicPlayer", "23:HighscoreTable", "25:InfiniteModeMenu", "26:BossRushMenu", "33:LevelControls",
            "35:Database", "37:Attract" };
        // Transition.CLASS_GAME: the level, whose pages are the briefing's prefetch.
        public static int FadeGameApp = 5;
        public static bool HoldFades = true;
        // The longest a fade waits; then the App starts with what is there.
        public static int HoldMaxMs = 10000;

        static int fadeTo = -1;         // the App the fade going on goes to; -1 outside a fade out
        static string fadeAhead;        // the key of the scenes aheadScenes holds ("app:<Class>", "map:<id>")
        static float holdSince = -1;
        static bool holdGaveUp;
        static int holdLogs;

        public static bool HoldFade(int dir, double progress, double speed, int nextApp)
        {
            if (!loaded || !HoldFades) return false;
            if (dir != 1)
            {
                fadeTo = -1;
                holdGaveUp = false;
                return EndHold();
            }
            if (fadeTo != nextApp)
            {
                fadeTo = nextApp;
                holdGaveUp = false;
                FadeStarts(nextApp);
            }
            // Transition.update ends the fade once progress passes 1, as it computes it.
            if (!(1.0 < (double)dir * speed + progress) || holdGaveUp) return false;
            long bytes;
            int left = NotReady(nextApp, out bytes);
            if (left == 0) return EndHold();
            float now = Time.realtimeSinceStartup;
            if (holdSince < 0)
            {
                holdSince = now;
                if (holdLogs++ < 50)
                    Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "T2D atlas: the fade into {0} waits for {1} pages, {2:0.0} MB, in {3}", FadeKey(nextApp) ?? "app " + nextApp, left,
                        bytes / 1048576.0, sceneLabel));
            }
            if ((now - holdSince) * 1000f < HoldMaxMs) return true;
            Debug.LogWarning("T2D atlas: the fade gave up waiting after " + Ms(holdSince) + ", " + left + " pages left");
            holdGaveUp = true;
            return EndHold();
        }

        static bool EndHold()
        {
            if (holdSince >= 0 && holdLogs < 100)
            {
                holdLogs++;
                Debug.Log("T2D atlas: the fade waited " + Ms(holdSince));
            }
            holdSince = -1;
            return false;
        }

        // The scenes' key of the App a fade goes to: "app:<Class>" for one of FadeApps, the next demo's map for DemoApp.
        static string FadeKey(int app)
        {
            if (app == DemoApp) return NextDemoKey();
            string prefix = app.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
            foreach (string pair in FadeApps)
                if (pair.StartsWith(prefix, StringComparison.Ordinal)) return "app:" + pair.Substring(prefix.Length);
            return null;
        }

        // A fade out into an App of FadeApps or the demo: its pages join the wanted ones and go to the front of the
        // background loads, without a Switch, which would drop the pages the App that shows still draws (a miss's).
        static void FadeStarts(int app)
        {
            string key = FadeKey(app);
            int[] scenes;
            if (key == null || !Ahead || key == "app:" + appKey || key == fadeAhead || !keys.TryGetValue(key, out scenes)) return;
            aheadKey = null;    // a pending ahead load for another App would take this one's place
            aheadScenes = scenes;
            fadeAhead = key;
            var add = new List<int>();
            foreach (int s in scenes)
                foreach (int p in scenePages[s])
                {
                    wanted[p] = eager[p] = true;
                    if (pages[p] != null && drawn[p]) continue;
                    if (!warmList.Contains(p)) warmList.Add(p);
                    if (pages[p] != null || asyncLoads.ContainsKey(p) || missing[p]) continue;
                    bgQueue.Remove(p);
                    if (!add.Contains(p)) add.Add(p);
                }
            add.Sort(BySize);
            bgQueue.InsertRange(0, add);
            warmList.Sort(BySize);
            bgStartAt = Mathf.Min(bgStartAt, Time.realtimeSinceStartup);
            Debug.Log("T2D atlas: " + (fadeTo == app ? "the fade into " : "ahead, ") + key + ": " + add.Count + " of its pages load first, in " + sceneLabel);
        }

        // Transition.CLASS_DEMO: the menu's idle timeout fades into the attract demo (Game.mode 8), whose Demo script
        // picks the next of four maps in its constructor from Demo.demoCharacter: "<demoCharacter now>:<the map it
        // plays next>" (6 -> 4, John, map 9; 4 -> 1, Sarah, map 10; 1 -> 0, T-800, map 12; any other -> 6, T-1000, map 11).
        public static int DemoApp = 40;
        public static string DemoCharacterField = "com.bitmapbureau.terminator.scripts.Demo.demoCharacter";
        public static readonly string[] NextDemoMaps = { "6:9", "4:10", "1:12", "*:11" };
        // The menu's idle ticks left (MainMenu.timeOut, 1200 from the last press, counted down at 60 a second while the
        // title or the top menu shows) below which the next demo's pages load ahead (0: not ahead, only in its fade).
        // The first demo, Sarah's in the Factory, froze its first frames 6.9 to 7.4 s with its pages loaded at their draws.
        public static string IdleField = "MainMenu.timeOut";
        public static int IdleTicks = 900;
        static float idleCheckAt;
        static int idleLogs;

        static void IdleDemo(float now)
        {
            if (IdleTicks <= 0 || !Ahead || fadeTo >= 0 || currentApp == null || now < idleCheckAt) return;
            idleCheckAt = now + 0.5f;
            int dot = IdleField.IndexOf('.');
            if (currentApp.GetType().Name != IdleField.Substring(0, dot)) return;
            System.Reflection.FieldInfo field = currentApp.GetType().GetField(IdleField.Substring(dot + 1),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(int))
            {
                if (idleLogs++ == 0) Debug.LogWarning("T2D atlas: no int field " + IdleField);
                return;
            }
            int left = (int)field.GetValue(currentApp);
            if (left > 0 && left < IdleTicks) FadeStarts(DemoApp);
        }

        static string NextDemoKey()
        {
            if (currentApp == null) return null;
            int dot = DemoCharacterField.LastIndexOf('.');
            Type type = currentApp.GetType().Assembly.GetType(DemoCharacterField.Substring(0, dot));
            System.Reflection.FieldInfo field = type == null ? null : type.GetField(DemoCharacterField.Substring(dot + 1),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (field == null || field.FieldType != typeof(int))
            {
                if (idleLogs++ == 0) Debug.LogWarning("T2D atlas: no static int field " + DemoCharacterField);
                return null;
            }
            string now = ((int)field.GetValue(null)).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
            foreach (string pair in NextDemoMaps)
                if (pair.StartsWith(now, StringComparison.Ordinal) || pair.StartsWith("*:", StringComparison.Ordinal))
                    return "map:" + pair.Substring(pair.IndexOf(':') + 1);
            return null;
        }

        // The pages of the fade's next App not ready yet, those that are on their way: the prefetch's for the level,
        // the ahead set for an App of FadeApps. A page that isn't loading (one Resources.Load gave, or missing) counts
        // as ready, and so does one over WarmBigBytes, warmed only under the cover.
        static int NotReady(int app, out long bytes)
        {
            bytes = 0;
            int n = 0;
            if (app == FadeGameApp)
            {
                if (prefetchKey == null) return 0;
                foreach (int p in prefetchLeft) Count(p, ref n, ref bytes);
                return n;
            }
            string key = FadeKey(app);
            if (key == null || key != fadeAhead) return 0;
            foreach (int s in aheadScenes)
                foreach (int p in scenePages[s]) Count(p, ref n, ref bytes);
            return n;
        }

        static void Count(int p, ref int n, ref long bytes)
        {
            if (!wanted[p] || (pages[p] != null && drawn[p])) return;
            if (WarmBigBytes > 0 && PageBytes(p) > WarmBigBytes) return;
            bool coming = asyncLoads.ContainsKey(p) || bgQueue.Contains(p) || (pages[p] != null && viaAsync[p]);
            if (!coming) return;
            n++;
            bytes += PageBytes(p);
        }

        // ---- the data ----------------------------------------------------------------------------

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            ReadFrames(Bytes("frames"));
            realPages = pageW.Length;
            ReadScenes(Bytes("scenes"));
            ReadTiles();
            pages = new Texture2D[pageW.Length];
            made = new bool[pageW.Length];
            drawn = new bool[pageW.Length];
            viaAsync = new bool[pageW.Length];
            lastUse = new int[pageW.Length];
            wanted = new bool[pageW.Length];
            eager = new bool[pageW.Length];
            missing = new bool[pageW.Length];
            Debug.Log("T2D atlas: " + realPages + " pages" + (pageW.Length > realPages ? " (" + (pageW.Length - realPages) + " tiles of the big ones)" : "") +
                ", " + tex.Length + " images, " + sceneNames.Length + " scenes");
        }

        // ---- tiles -------------------------------------------------------------------------------

        // A page larger than the tile size (tiles.bytes: u16) is in the build as tiles of at most that size, pNNN_K,
        // K row by row from the bottom (atlas/step.py): the player integrates a 4096x4096 page in one 0.7 s frame, a
        // 1024x1024 one in a few ms. Each tile is a page of its own here, numbered after the real ones, and a scene
        // wants a big page's tiles; SpriteBatch draws a frame of a big page across its tiles (Tiles).
        static int realPages;
        static int[] tileFirst, tileCols, tileRows;
        static string[] tileNames;

        static void ReadTiles()
        {
            TextAsset asset = Resources.Load<TextAsset>(Folder + "tiles");
            tileFirst = new int[realPages];
            tileCols = new int[realPages];
            tileRows = new int[realPages];
            for (int p = 0; p < realPages; p++) tileFirst[p] = -1;
            if (asset == null) return;
            byte[] data = asset.bytes;
            Resources.UnloadAsset(asset);
            int size = data[0] | data[1] << 8;
            var w = new List<int>(pageW);
            var h = new List<int>(pageH);
            var names = new List<string>();
            for (int p = 0; p < realPages; p++)
            {
                if (pageW[p] <= size && pageH[p] <= size) continue;
                int tw = Math.Min(pageW[p], size), th = Math.Min(pageH[p], size);
                tileCols[p] = pageW[p] / tw;
                tileRows[p] = pageH[p] / th;
                tileFirst[p] = w.Count;
                for (int k = 0; k < tileCols[p] * tileRows[p]; k++)
                {
                    w.Add(tw);
                    h.Add(th);
                    names.Add(PageName(p) + "_" + k);
                }
            }
            pageW = w.ToArray();
            pageH = h.ToArray();
            tileNames = names.ToArray();
            // A scene wants a big page's tiles instead of the page; a sticky page's tiles stay as it would.
            for (int s = 0; s < scenePages.Length; s++)
            {
                var list = new List<int>();
                foreach (int p in scenePages[s])
                    if (tileFirst[p] < 0) list.Add(p);
                    else for (int k = 0; k < tileCols[p] * tileRows[p]; k++) list.Add(tileFirst[p] + k);
                scenePages[s] = list.ToArray();
            }
            var stickyAll = new bool[pageW.Length];
            for (int p = 0; p < realPages; p++)
            {
                if (!sticky[p]) continue;
                if (tileFirst[p] < 0) stickyAll[p] = true;
                else for (int k = 0; k < tileCols[p] * tileRows[p]; k++) stickyAll[tileFirst[p] + k] = true;
            }
            sticky = stickyAll;
        }

        // The first tile of a page drawn from tiles (cols x rows of them, row by row from the bottom), or -1.
        public static int Tiles(int page, out int cols, out int rows)
        {
            Load();
            if (page < 0 || page >= realPages || tileFirst[page] < 0) { cols = rows = 1; return -1; }
            cols = tileCols[page];
            rows = tileRows[page];
            return tileFirst[page];
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
