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
//                 sample at uv - 1), no mipmaps, not readable.
//
// Residency: the pages of the current app's scene and of the current level's scene. An Enter unloads the pages
// the new scenes don't need before it loads the ones they do, so the peak stays at one scene. A draw from a page
// that isn't resident (a frame the static analysis missed) loads it at once and logs the miss; such pages go at
// the next Enter. Sticky pages (other languages' fonts) stay once loaded.
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

        // Over this many bytes of pages resident, a miss first unloads pages no current scene needs, least
        // recently used first.
        public static long BudgetBytes = 150L * 1024 * 1024;
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
        static int[] lastUse;
        static int useClock;
        static long residentBytes;

        static string appKey = "";
        static int[] appScenes = new int[0];
        static int[] levelScenes = new int[0];
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
            Load();
            if (page < 0 || page >= pages.Length) return null;
            Texture2D t = pages[page];
            if (t == null)
            {
                Miss(page);
                t = LoadPage(page);
            }
            lastUse[page] = ++useClock;
            return t;
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
        public static int Misses { get { return misses; } }

        // ---- scenes (R3, R6) ---------------------------------------------------------------------

        // After Transition.getApp: the App's class names the scene. During an App named in level_apps
        // (Game), the level and map hooks choose the pages; an App without a scene of its own keeps them.
        public static void EnterApp(object app)
        {
            if (app != null) Enter("app:" + app.GetType().Name);
        }

        // Game.loadScriptByLevelID: also called by menus for level previews, so it acts only during a level app.
        public static void EnterLevel(int levelID)
        {
            Load();
            if (!levelApps.Contains(appKey)) return;
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
                Switch(key);
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
            if (SameScenes(scenes, levelScenes)) return;
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
            foreach (int[] set in new[] { appScenes, levelScenes })
                foreach (int s in set)
                {
                    foreach (int p in scenePages[s]) wanted[p] = true;
                    if (label.Length > 0) label.Append(" + ");
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
            for (int p = 0; p < pages.Length; p++)
                if (wanted[p] && pages[p] == null)
                {
                    LoadPage(p);
                    added++;
                }
            Debug.Log(string.Format("T2D atlas: {0} -> {1}: -{2} +{3} pages, {4:0.0} MB resident, {5:0} ms",
                key, sceneLabel, unloaded, added, residentBytes / 1048576.0, (Time.realtimeSinceStartup - start) * 1000));
        }

        // ---- loading -----------------------------------------------------------------------------

        static Texture2D LoadPage(int page)
        {
            if (residentBytes + PageBytes(page) > BudgetBytes) Evict(PageBytes(page));
            Texture2D t = Resources.Load<Texture2D>(Folder + PageName(page));
            if (t == null)
            {
                Debug.LogError("T2D atlas: page " + PageName(page) + " is not in Resources/" + Folder);
                return null;
            }
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Repeat;
            pages[page] = t;
            residentBytes += PageBytes(page);
            lastUse[page] = ++useClock;
            return t;
        }

        static void UnloadPage(int page)
        {
            Texture2D t = pages[page];
            pages[page] = null;
            residentBytes -= PageBytes(page);
            if (t != null) Resources.UnloadAsset(t);
        }

        // Unloads pages no current scene wants, least recently used first, until `need` more bytes fit.
        static void Evict(long need)
        {
            while (residentBytes + need > BudgetBytes)
            {
                int victim = -1;
                for (int p = 0; p < pages.Length; p++)
                    if (pages[p] != null && !wanted[p] && !sticky[p] && (victim < 0 || lastUse[p] < lastUse[victim]))
                        victim = p;
                if (victim < 0) return;
                UnloadPage(victim);
            }
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
