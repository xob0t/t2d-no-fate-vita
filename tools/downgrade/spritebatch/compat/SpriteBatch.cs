// The PS Vita port's sprite renderer: draws the game's BitmapData frames as quads collected into one dynamic mesh
// per frame, one draw per run of sprites that share a material, a texture and a colour transform, instead of one
// Mesh per frame and flip and one Graphics.DrawMesh per sprite (docs/drawing.md, docs/mesh-test.md).
//
// The rewrite rules (spritebatch/rewriter/SpriteBatchPatches.cs) send the game here:
//   R7  BitmapData.CreateMultiMeshFrames returns an array of nulls: no meshes are built for sprite sheets
//   R8  BitmapData.unityDraw and unityDrawClippedWidth: their bodies pass the target's and the source's fields to
//       Target and Source, a ColorTransform's colours to Colors, and the call's arguments to Draw or DrawClipped,
//       which return the new BitmapData.zorder; then BitmapData.matused and texused become unknown
//   R9  UnityMain.OnRenderObject: Flush(UnityMain.m_RenderCamera) after Main.draw()
//
// A quad is what the game's mesh for that frame and flip held: the frame's trimmed rect placed in the sheet's cell
// (origin at the cell's centre, y up), the corners in the order 0 = right top, 1 = right bottom, 2 = left top,
// 3 = left bottom, drawn as triangles 0 1 2 and 2 1 3. A horizontal flip mirrors the rect's x about the cell's centre
// and takes its UVs from the rect's other side, one texture width to the left (uv - 1, read through the texture's
// Repeat wrap); a vertical flip does the same in y. The quad is placed, rotated and scaled on the CPU as the game's
// Matrix4x4.TRS would place the mesh.
//
// Screen draws (the 398x224 BitmapData) are queued and drawn at Flush, in the order they came. Each draw keeps the
// z the game gave it (BitmapData.zorder, which falls by 0.25 per draw); a run's mesh draw is placed at its first
// sprite's z, so Unity's back-to-front sort of the transparent queue keeps runs and the game's other screen draws
// (fillRect, colorTransform), which take z from the same counter, in order. A run ends when the material, the
// texture, the colour transform or the z sequence changes, the last when another draw took a z in between.
// Draws into a render texture happen at once, as the game's did, as two triangles with GL.
//
// C# 5, as T2DCompat is compiled by the .NET Framework's csc.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace T2DCompat
{
    public static class SpriteBatch
    {
        // Quads per mesh, so that its vertices fit 16-bit indices. A frame with more sprites takes another mesh.
        public const int MaxQuads = 16384;

        // BitmapData's alignment bits.
        const int AlignRight = 2, AlignBottom = 4, AlignHCentre = 16, AlignVCentre = 32;

        // ---- the next draw's target, source and colours (R8 sets them before each Draw) ---------------------------

        static bool tScreen;
        static RenderTexture tTex;
        static double tHalfW, tHalfH;

        static RenderTexture sTex;
        static int[] sPage, sFrw, sFrh, sOfx, sOfy;
        static double[] sUvx, sUvy, sUvw, sUvh;
        static int sSfw, sSfh, sWidth, sHeight;
        static double sHalfW, sHalfH;

        static Color ctMul;
        static Vector4 ctOff;

        // The BitmapData drawn into: screen, its render texture, width_half, height_half.
        public static void Target(bool screen, RenderTexture tex, double halfW, double halfH)
        {
            tScreen = screen;
            tTex = tex;
            tHalfW = halfW;
            tHalfH = halfH;
        }

        // The BitmapData drawn: its render texture, texpack_tex, texpack_uvx/uvy/uvw/uvh, texpack_frw/frh/ofx/ofy,
        // texpack_sfw/sfh, mesh_half_width/height, width, height.
        public static void Source(RenderTexture tex, int[] page, double[] uvx, double[] uvy, double[] uvw, double[] uvh,
                                  int[] frw, int[] frh, int[] ofx, int[] ofy, int sfw, int sfh,
                                  double halfW, double halfH, int width, int height)
        {
            sTex = tex;
            sPage = page;
            sUvx = uvx; sUvy = uvy; sUvw = uvw; sUvh = uvh;
            sFrw = frw; sFrh = frh; sOfx = ofx; sOfy = ofy;
            sSfw = sfw; sSfh = sfh;
            sHalfW = halfW; sHalfH = halfH;
            sWidth = width; sHeight = height;
        }

        // A ColorTransform's colmul and coloff, for the next draw that has one.
        public static void Colors(Color mul, Vector4 off)
        {
            ctMul = mul;
            ctOff = off;
        }

        // ---- geometry: pure functions, checked against the game's meshes on the PC --------------------------------

        // The quad of frame rect (fw x fh at ox, oy in a sfw x sfh cell; y down from the cell's top) with texture rect
        // (ux, uy, uw, uh), for flip 0 (none), 1 (horizontal), 2 (vertical) or 3 (both), into v[0..3] and t[0..3].
        public static void FrameQuad(int fw, int fh, int ox, int oy, int sfw, int sfh,
                                     double ux, double uy, double uw, double uh, int flip, Vector3[] v, Vector2[] t)
        {
            double cx = sfw * 0.5, cy = sfh * 0.5;
            double left = ox - cx, right = (fw + ox) - cx;
            double top = cy - oy, bottom = -((fh + oy) - cy);
            bool h = (flip & 1) != 0, vf = (flip & 2) != 0;
            float x0 = (float)(h ? -left : right), x2 = (float)(h ? -right : left);
            float y0 = (float)(vf ? -bottom : top), y1 = (float)(vf ? -top : bottom);
            float u0 = (float)(h ? ux - 1.0 : uw + ux), u2 = (float)(h ? uw + ux - 1.0 : ux);
            float v0 = (float)(vf ? uy - 1.0 : uh + uy), v1 = (float)(vf ? uh + uy - 1.0 : uy);
            Corners(v, t, x0, x2, y0, y1, u0, u2, v0, v1);
        }

        // unityDrawClippedWidth's quad: the frame unflipped, cut at frameWidth from the cell's left edge.
        // False when nothing of the frame is left of frameWidth. clipped says whether it was cut.
        public static bool ClippedQuad(int fw, int fh, int ox, int oy, int sfw, int sfh,
                                       double ux, double uy, double uw, double uh, double frameWidth,
                                       Vector3[] v, Vector2[] t, out bool clipped)
        {
            clipped = false;
            if (frameWidth <= ox) return false;
            double cx = sfw * 0.5, cy = sfh * 0.5;
            float top = (float)(cy - oy), bottom = (float)(0.0 - ((fh + oy) - cy)), x2 = (float)(ox - cx);
            if ((double)(fw + ox) <= frameWidth)
            {
                Corners(v, t, (float)((fw + ox) - cx), x2, top, bottom, (float)(ux + uw), (float)ux, (float)(uh + uy), (float)uy);
                return true;
            }
            clipped = true;
            double kept = frameWidth - ox;
            Corners(v, t, (float)((ox + kept) - cx), x2, top, bottom, (float)(uw * (kept / fw) + ux), (float)ux, (float)(uh + uy), (float)uy);
            return true;
        }

        static void Corners(Vector3[] v, Vector2[] t, float x0, float x2, float y0, float y1, float u0, float u2, float v0, float v1)
        {
            v[0] = new Vector3(x0, y0, 0f);
            v[1] = new Vector3(x0, y1, 0f);
            v[2] = new Vector3(x2, y0, 0f);
            v[3] = new Vector3(x2, y1, 0f);
            t[0] = new Vector2(u0, v0);
            t[1] = new Vector2(u0, v1);
            t[2] = new Vector2(u2, v0);
            t[3] = new Vector2(u2, v1);
        }

        // A placement: Matrix4x4.TRS(position, Quaternion.AngleAxis(angle, Vector3.forward), (sx, sy, 1)), as Unity
        // builds it, reduced to the terms a quad at z = 0 needs.
        public struct Place
        {
            public float M00, M01, M10, M11, X, Y, Z;
            public bool Plain;  // no rotation or scale: the corners only move

            public static Place Make(float x, float y, float z, double rotation, float sx, float sy)
            {
                var p = new Place { X = x, Y = y, Z = z, M00 = sx, M11 = sy };
                if (rotation != 0.0)
                {
                    // AngleAxis: (0, 0, sin(a/2), cos(a/2)); the matrix of a rotation about z, columns scaled.
                    float angle = (float)(0.0 - 180.0 / Math.PI * rotation);
                    float half = angle * Mathf.Deg2Rad * 0.5f;
                    float qz = (float)Math.Sin(half), qw = (float)Math.Cos(half);
                    float z2 = qz * 2f, zz = qz * z2, wz = qw * z2;
                    p.M00 = (1f - zz) * sx;
                    p.M10 = wz * sx;
                    p.M01 = -wz * sy;
                    p.M11 = (1f - zz) * sy;
                }
                p.Plain = rotation == 0.0 && sx == 1f && sy == 1f;
                return p;
            }

            public Vector3 Apply(Vector3 c)
            {
                if (Plain) return new Vector3(c.x + X, c.y + Y, Z);
                return new Vector3(M00 * c.x + M01 * c.y + X, M10 * c.x + M11 * c.y + Y, Z);
            }
        }

        // Where unityDraw puts a source's cell: x, y are the call's, already aligned; h the cell height it aligned by.
        public static void Position(double x, double y, double h, double hscale, double vscale,
                                    double targetHalfW, double targetHalfH, double sourceHalfW, double sourceHalfH,
                                    out double px, out double py)
        {
            if (hscale == 1.0 && vscale == 1.0)
            {
                px = x - (targetHalfW - sourceHalfW);
                py = sourceHalfH + targetHalfH - h - y;
            }
            else
            {
                px = x - (targetHalfW - sourceHalfW * hscale);
                py = sourceHalfH * vscale + targetHalfH - h * vscale - y;
            }
        }

        // The game's alignment: by the cell's width and height, times the scale.
        public static void Align(int align, double w, double h, double hscale, double vscale, ref double x, ref double y)
        {
            if (align == -1) return;
            if ((align & AlignHCentre) != 0) x = x - w * 0.5 * hscale;
            else if ((align & AlignRight) != 0) x = x - w * hscale;
            if ((align & AlignVCentre) != 0) y = y - h * 0.5 * vscale;
            else if ((align & AlignBottom) != 0) y = y - h * vscale;
        }

        // ---- the draws ------------------------------------------------------------------------------------------

        // The placed quad of a unityDraw call, from the Source and Target set before: corners in the target's space at
        // depth z, and their UVs, into v and t. Returns the atlas page, -1 for the source's render texture, -2 for
        // nothing to draw (a frame the source doesn't have).
        public static int Layout(double x, double y, int frame, int hframes, int vframes, int align, bool hflip, bool vflip,
                                 int width, int height, double rotation, double hscale, double vscale, float z,
                                 Vector3[] v, Vector2[] t)
        {
            int flip = (hflip ? 1 : 0) + (vflip ? 2 : 0), page;
            if ((object)sTex == null)
            {
                if (sPage == null || frame < 0 || frame >= sPage.Length) return -2;
                page = sPage[frame];
                FrameQuad(sFrw[frame], sFrh[frame], sOfx[frame], sOfy[frame], sSfw, sSfh,
                          sUvx[frame], sUvy[frame], sUvw[frame], sUvh[frame], flip, v, t);
            }
            else
            {
                // A render texture's BitmapData has one cell, its own size (BitmapData.CreateMultiMesh).
                if (frame != 0) { Once("SpriteBatch: frame " + frame + " of a render texture, which has only frame 0"); return -2; }
                page = -1;
                FrameQuad(sWidth, sHeight, 0, 0, sWidth, sHeight, 0.0, 0.0,
                          sWidth / (double)sTex.width, sHeight / (double)sTex.height, flip, v, t);
            }
            double w = width != -1 ? width : sWidth / hframes;
            double h = height != -1 ? height : sHeight / vframes;
            Align(align, w, h, hscale, vscale, ref x, ref y);
            double px, py;
            Position(x, y, h, hscale, vscale, tHalfW, tHalfH, sHalfW, sHalfH, out px, out py);
            Place place = Place.Make((float)px, (float)py, z, rotation, (float)hscale, (float)vscale);
            for (int k = 0; k < 4; k++) v[k] = place.Apply(v[k]);
            return page;
        }

        // unityDrawClippedWidth's placed quad: the frame unflipped, unscaled, cut at frameWidth. Returns as Layout.
        public static int LayoutClipped(double x, double y, int frame, int hframes, int vframes, int align, double frameWidth,
                                        float z, Vector3[] v, Vector2[] t)
        {
            if (sOfx == null || sPage == null || frame < 0 || frame >= sPage.Length) return -2;
            bool clipped;
            if (!ClippedQuad(sFrw[frame], sFrh[frame], sOfx[frame], sOfy[frame], sSfw, sSfh,
                             sUvx[frame], sUvy[frame], sUvw[frame], sUvh[frame], frameWidth, v, t, out clipped))
                return -2;
            double w = clipped ? frameWidth : (double)(sWidth / hframes);
            double h = sHeight / vframes;
            Align(align, w, h, 1.0, 1.0, ref x, ref y);
            double px, py;
            Position(x, y, h, 1.0, 1.0, tHalfW, tHalfH, sHalfW, sHalfH, out px, out py);
            Place place = Place.Make((float)px, (float)py, z, 0.0, 1f, 1f);
            for (int k = 0; k < 4; k++) v[k] = place.Apply(v[k]);
            return sPage[frame];
        }

        static readonly Vector3[] quadV = new Vector3[4];
        static readonly Vector2[] quadT = new Vector2[4];

        // unityDraw. Returns zorder, less 0.25 when a screen sprite was queued.
        public static double Draw(double x, double y, int frame, int hframes, int vframes, int align, bool colored,
                                  bool hflip, bool vflip, int width, int height, double rotation, double hscale, double vscale,
                                  Material normal, Material colorT, double zorder)
        {
            if (Trace.On) Trace.Enter("SpriteBatch.Draw");
            int page = Layout(x, y, frame, hframes, vframes, align, hflip, vflip, width, height, rotation, hscale, vscale,
                              tScreen ? (float)zorder : 0f, quadV, quadT);
            if ((object)colorT != null) warmMaterial = colorT;
            return Submit(page, colored ? colorT : normal, colored, zorder);
        }

        // unityDrawClippedWidth. Returns zorder as Draw does.
        public static double DrawClipped(double x, double y, int frame, int hframes, int vframes, int align, bool colored,
                                         double frameWidth, Material normal, Material colorT, double zorder)
        {
            if (Trace.On) Trace.Enter("SpriteBatch.DrawClipped");
            int page = LayoutClipped(x, y, frame, hframes, vframes, align, frameWidth, tScreen ? (float)zorder : 0f, quadV, quadT);
            return Submit(page, colored ? colorT : normal, colored, zorder);
        }

        static double Submit(int page, Material material, bool colored, double zorder)
        {
            if (Trace.On) Trace.Enter("SpriteBatch.Submit");
            if (page == -2) return zorder;
            Texture tex = page == -1 ? (Texture)sTex : Atlas.Get(page);
            if (!tScreen)
            {
                DrawNow(material, tex, colored);
                return zorder;
            }
            Queue(material, tex, colored, zorder);
            return zorder - 0.25;
        }

        // ---- the screen queue -----------------------------------------------------------------------------------

        struct Run
        {
            public int Segment, First, Quads;
            public Material Material;
            public Texture Texture;
            public bool Colored;
            public Color Mul;
            public Vector4 Off;
            public float Z;
        }

        // A mesh's worth of quads. The arrays grow by doubling and go to the mesh whole (2018.2 has no ranged
        // SetVertices); the corners past Quads are left over from earlier frames, and no index refers to them.
        class Segment
        {
            public Vector3[] Vertices = new Vector3[256];
            public Vector2[] Uvs = new Vector2[256];
            public int Quads;
        }

        static Run[] runs = new Run[256];
        static int runCount;
        static readonly List<Segment> segments = new List<Segment>();
        static int segmentCount;
        static double lastZ;

        static void Queue(Material material, Texture tex, bool colored, double zorder)
        {
            if (Trace.On) Trace.Enter("SpriteBatch.Queue");
            Segment segment = segmentCount > 0 ? segments[segmentCount - 1] : null;
            if (segment == null || segment.Quads == MaxQuads)
            {
                if (segmentCount == segments.Count) segments.Add(new Segment());
                segment = segments[segmentCount++];
                segment.Quads = 0;
            }
            int last = runCount - 1;
            if (last >= 0 && zorder == lastZ - 0.25 && runs[last].Segment == segmentCount - 1 &&
                (object)runs[last].Material == (object)material && (object)runs[last].Texture == (object)tex &&
                runs[last].Colored == colored && (!colored || Same(ref runs[last].Mul, ref ctMul) && Same(ref runs[last].Off, ref ctOff)))
                runs[last].Quads++;
            else
            {
                if (last >= 0 && (object)runs[last].Texture != (object)tex) pageSwitches++;
                if (runCount == runs.Length) Array.Resize(ref runs, runs.Length * 2);
                runs[runCount++] = new Run
                {
                    Segment = segmentCount - 1, First = segment.Quads, Quads = 1, Material = material, Texture = tex,
                    Colored = colored, Mul = ctMul, Off = ctOff, Z = quadV[0].z,
                };
            }
            lastZ = zorder;
            float z0 = runs[runCount - 1].Z;
            int at = segment.Quads * 4;
            if (at + 4 > segment.Vertices.Length)
            {
                Array.Resize(ref segment.Vertices, segment.Vertices.Length * 2);
                Array.Resize(ref segment.Uvs, segment.Uvs.Length * 2);
            }
            Vector3[] v = segment.Vertices;
            Vector2[] t = segment.Uvs;
            Vector3[] q = quadV;
            // z relative to the run's draw, at z0
            v[at] = new Vector3(q[0].x, q[0].y, q[0].z - z0);
            v[at + 1] = new Vector3(q[1].x, q[1].y, q[1].z - z0);
            v[at + 2] = new Vector3(q[2].x, q[2].y, q[2].z - z0);
            v[at + 3] = new Vector3(q[3].x, q[3].y, q[3].z - z0);
            t[at] = quadT[0];
            t[at + 1] = quadT[1];
            t[at + 2] = quadT[2];
            t[at + 3] = quadT[3];
            segment.Quads++;
        }

        // Exact, unlike Color's and Vector4's == (which allow a small distance).
        static bool Same(ref Color a, ref Color b) { return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a; }
        static bool Same(ref Vector4 a, ref Vector4 b) { return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w; }

        // Two sets of meshes, used on alternate frames, so a mesh the render thread may still read is not rewritten.
        static readonly List<Mesh>[] meshes = { new List<Mesh>(), new List<Mesh>() };
        static int parity;
        static readonly List<int> indices = new List<int>(6 * 1024);
        static MaterialPropertyBlock block;
        static int idMainTex, idColor, idColoff;
        // The meshes' bounds: any quad on or near the screen, centred on z = 0 so that a run sorts by its own z.
        static readonly Bounds everywhere = new Bounds(Vector3.zero, new Vector3(1e5f, 1e5f, 0.5f * MaxQuads + 2f));

        // Sprites, runs (draws) and meshes of the last Flush; of its runs, those whose texture differs from the run
        // before (page switches); and the off-screen draws (into render textures) since the Flush before it.
        public static int LastSprites, LastRuns, LastMeshes, LastPageSwitches, LastOffscreen;
        static int pageSwitches, offscreen;

        // Draws the queued screen sprites with the camera they were drawn for, and empties the queue.
        public static void Flush(Camera camera)
        {
            if (Trace.On) Trace.Enter("SpriteBatch.Flush");
            Probe.FromRender();
            Atlas.ReleaseUnloaded();
            int sprites = 0;
            if (runCount > 0)
            {
                Init();
                parity ^= 1;
                List<Mesh> pool = meshes[parity];
                int r = 0;
                for (int s = 0; s < segmentCount; s++)
                {
                    Segment segment = segments[s];
                    sprites += segment.Quads;
                    if (s == pool.Count)
                    {
                        var made = new Mesh();
                        made.name = "SpriteBatch";
                        made.MarkDynamic();
                        pool.Add(made);
                    }
                    Mesh mesh = pool[s];
                    int first = r;
                    while (r < runCount && runs[r].Segment == s) r++;
                    if (SameLayout(parity, s, first, r, segment.Vertices.Length))
                    {
                        // The same runs as this mesh had two frames ago: the indices stand, only the corners move.
                        mesh.vertices = segment.Vertices;
                        mesh.uv = segment.Uvs;
                    }
                    else
                    {
                        mesh.Clear(true);
                        mesh.vertices = segment.Vertices;
                        mesh.uv = segment.Uvs;
                        mesh.subMeshCount = r - first;
                        for (int k = first; k < r; k++)
                        {
                            indices.Clear();
                            int end = (runs[k].First + runs[k].Quads) * 4;
                            for (int v = runs[k].First * 4; v < end; v += 4)
                            {
                                indices.Add(v); indices.Add(v + 1); indices.Add(v + 2);
                                indices.Add(v + 2); indices.Add(v + 1); indices.Add(v + 3);
                            }
                            mesh.SetTriangles(indices, k - first, false);
                        }
                    }
                    mesh.bounds = everywhere;
                    for (int k = first; k < r; k++)
                    {
                        Run run = runs[k];
                        block.Clear();
                        if ((object)run.Texture != null) block.SetTexture(idMainTex, run.Texture);
                        if (run.Colored)
                        {
                            block.SetColor(idColor, run.Mul);
                            block.SetVector(idColoff, run.Off);
                        }
                        Matrix4x4 at = Matrix4x4.identity;
                        at.m23 = run.Z;
                        Graphics.DrawMesh(mesh, at, run.Material, 0, camera, k - first, block,
                                          ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
                        runs[k].Material = null;
                        runs[k].Texture = null;
                    }
                }
            }
            Warm(camera);
            LastSprites = sprites;
            LastRuns = runCount;
            LastMeshes = segmentCount;
            LastPageSwitches = pageSwitches;
            LastOffscreen = offscreen;
            pageSwitches = 0;
            offscreen = 0;
            runCount = 0;
            segmentCount = 0;
        }

        // ---- prefetch ---------------------------------------------------------------------------------------------

        // The game's ColorTransform material, from its draws.
        static Material warmMaterial;
        static Mesh warmMesh;
        public static int Warms;

        // A page the atlas prefetched (Atlas.NextWarm), drawn as one texel with the ColorTransform material at colour 0
        // and offset 0: its alpha is 0, so the blend leaves the screen as it was, but the player reads the page's
        // texels for it now rather than at the page's first real draw.
        static void Warm(Camera camera)
        {
            if ((object)warmMaterial == null || (object)camera == null) return;
            Texture2D page = Atlas.NextWarm();
            if ((object)page == null) return;
            Init();
            if (warmMesh == null)
            {
                warmMesh = new Mesh();
                warmMesh.name = "SpriteBatch warm";
                warmMesh.vertices = new[] { new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, 0f, 0f) };
                warmMesh.uv = new[] { new Vector2(0f, 0.0005f), new Vector2(0f, 0f), new Vector2(0.0005f, 0.0005f), new Vector2(0.0005f, 0f) };
                warmMesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
                warmMesh.bounds = everywhere;
            }
            block.Clear();
            block.SetTexture(idMainTex, page);
            block.SetColor(idColor, new Color(0f, 0f, 0f, 0f));
            block.SetVector(idColoff, Vector4.zero);
            Graphics.DrawMesh(warmMesh, Matrix4x4.identity, warmMaterial, 0, camera, 0, block,
                              ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
            Warms++;
        }

        // Per mesh of each set, the quads of its runs when its indices were last written.
        static readonly List<List<int>>[] layouts = { new List<List<int>>(), new List<List<int>>() };

        // Whether mesh s of set p has runs first..end-1's sizes and the vertex count given; if not, records them.
        static bool SameLayout(int p, int s, int first, int end, int vertices)
        {
            List<List<int>> sets = layouts[p];
            while (sets.Count <= s) sets.Add(new List<int>());
            List<int> had = sets[s];
            bool same = had.Count == end - first + 1 && had[0] == vertices;
            for (int k = first; same && k < end; k++) same = had[k - first + 1] == runs[k].Quads;
            if (same) return true;
            had.Clear();
            had.Add(vertices);
            for (int k = first; k < end; k++) had.Add(runs[k].Quads);
            return false;
        }

        static void Init()
        {
            if (block != null) return;
            block = new MaterialPropertyBlock();
            idMainTex = Shader.PropertyToID("_MainTex");
            idColor = Shader.PropertyToID("_Color");
            idColoff = Shader.PropertyToID("_Coloff");
        }

        // ---- draws into a render texture ------------------------------------------------------------------------

        static Material normalFrom, normalCopy, colorFrom, colorCopy;

        // As the game's off-screen draw: the target active, a pixel matrix about its centre, the material's pass, the
        // quad. The game's materials are left as they are (their state is the game's own cache); copies take the
        // texture and colours.
        static void DrawNow(Material material, Texture tex, bool colored)
        {
            if (Trace.On) Trace.Enter("SpriteBatch.DrawNow");
            offscreen++;
            Init();
            Material pass = colored ? Copy(material, ref colorFrom, ref colorCopy) : Copy(material, ref normalFrom, ref normalCopy);
            if (pass == null) return;
            pass.mainTexture = tex;
            if (colored)
            {
                pass.SetColor(idColor, ctMul);
                pass.SetVector(idColoff, ctOff);
            }
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = tTex;
            GL.PushMatrix();
            GL.LoadPixelMatrix((float)(0.0 - tHalfW), (float)tHalfW, (float)(0.0 - tHalfH), (float)tHalfH);
            if (pass.SetPass(0))
            {
                GL.Begin(GL.TRIANGLES);
                Corner(0); Corner(1); Corner(2);
                Corner(2); Corner(1); Corner(3);
                GL.End();
            }
            GL.PopMatrix();
            RenderTexture.active = active;
        }

        static void Corner(int k)
        {
            GL.TexCoord2(quadT[k].x, quadT[k].y);
            GL.Vertex3(quadV[k].x, quadV[k].y, quadV[k].z);
        }

        static Material Copy(Material from, ref Material source, ref Material copy)
        {
            if (Trace.On) Trace.Enter("SpriteBatch.Copy");
            if ((object)from == null) return null;
            if ((object)source != (object)from || (object)copy == null)
            {
                if ((object)copy != null) UnityEngine.Object.Destroy(copy);
                copy = new Material(from);
                copy.name = from.name + " (SpriteBatch)";
                source = from;
            }
            return copy;
        }

        static readonly HashSet<string> told = new HashSet<string>();

        static void Once(string message)
        {
            if (told.Add(message)) Debug.LogWarning(message);
        }
    }
}
