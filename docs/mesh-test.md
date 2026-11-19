# Sprite meshes on the PS Vita, Terminator 2D: NO FATE

Measured on the user's PS Vita on 2026-11-19 with tools/assess/vita-mesh-test (Unity 2018.2.19f1, IL2CPP, PC Hosted, title TDMS00001, 109 MB memory expansion, clocks 444/222 MHz).
Synthetic frames and atlases, no game content. MB = 2^20 bytes. Logs: runs/c1 (creation), runs/d1 (draw, dynamic batching off), runs/d2 (draw, dynamic batching on, batch path fixed).

## What the game does (Rewind's BitmapData.cs)

- `CreateMultiMeshFrames(tframes)`: per frame, 4 meshes (plain, hflip, vflip, both): `new Mesh()`, `MarkDynamic()`, `vertices` = 4 Vector3, `uv` = 4 Vector2, `triangles` = `mesh_triangles` (6), `RecalculateNormals()`. No colors, no `UploadMeshData`, no bounds. `CreateMultiMesh` does the same for render-texture and texture BitmapData.
- `unityDraw` and `unityDrawClippedWidth`, screen target (the 398×224 BitmapData, `screen = true`): per sprite, `Matrix4x4.TRS(x, y, zorder)`, the `MaterialPropertyBlock` cleared and given the atlas when material or atlas changes (with a ColorTransform, `ct_material` and `_Color`/`_Coloff` in the MPB), then `Graphics.DrawMesh(meshes[frame*4 + hflip + 2*vflip], matrix, material, 0, UnityMain.m_RenderCamera, 0, mpb, ShadowCastingMode.Off, …, LightProbeUsage.Off)`, `zorder -= 0.25`. `m_RenderCamera` (the depth -1 camera) renders into `cam_rt`, 398×224 ARGB32 point, and the main camera's `OnRenderImage` blits it to the screen (or through the CRT shader).
- Off-screen BitmapData targets: `RenderTexture.active = rentex`, `GL.LoadPixelMatrix`, `material.SetPass(0)` when material or atlas changes, `Graphics.DrawMeshNow(mesh, position, rotation)`.
- Main.draw() runs from `UnityMain.OnRenderObject`.

The test reproduces both paths: the same mesh calls, one MPB, the same TRS and z steps, a depth -1 camera into a 398×224 ARGB32 target blitted in `OnRenderImage`, alpha-blended unlit shader, sprites 8–40 px.

## 1. Memory and time to create N meshes

Game flags (`dn`: MarkDynamic + normals), fresh app, 273 MB free main memory (LPDDR) at the start:

| N | create s | µs/mesh (avg) | main memory used | per mesh | Unity allocator per mesh | GC heap per mesh | CDRAM | free main left |
|---|---|---|---|---|---|---|---|---|
| 10,000 | 3.8 | 380 | 14 MB | 1,468 B | 878 B | 85 B | 0 | 259 MB |
| 50,000 | 45.1 | 902 | 67 MB | 1,405 B | 829 B | 34 B | 0 | 206 MB |
| 100,000 | 98.5 | 985 | 133 MB | 1,395 B | 821 B | 28 B | 0 | 140 MB |
| 150,000 | 152.3 | 1,015 | 200 MB | 1,398 B | 813 B | 27 B | 0 | 73 MB |
| 150,000, each drawn once | +111 s | | 259 MB | 1,811 B | 1,247 B | | 0 | **14 MB** |

A second run (d1, after other tests) took 145 s and 182 MB (1,272 B per mesh) for 150k.

- Video memory (CDRAM) and physically contiguous memory did not move in any run: the player keeps mesh vertex and index buffers in main memory. "GPU memory" here is the +59 MB (≈ 410 B per mesh) that appears the first time each mesh is drawn, when the player makes its GPU buffers.
- The vertex data is a small part: 4 × 32 B + 12 B = 140 B. The rest is per-object overhead (the native Mesh, its buffers' bookkeeping, allocator granularity).
- Idle frame time with 150k meshes resident: 6.42 ms, as with none. The meshes cost memory and startup time, not per-frame time.
- Destroying 150k meshes takes 0.6 s; the `UnloadUnusedAssets` + GC that follows took 40–110 s, and main memory came back only partly (the game never frees them, so this matters only for a port that would).

Variants (each run after the previous variant's meshes were freed, so the start was 224–227 MB free and part of the freed heap was reused; the Unity allocator column is the cleanest comparison):

| Flags | N reached | create s | µs/mesh | main memory from 273 MB baseline | per mesh | Unity allocator per mesh |
|---|---|---|---|---|---|---|
| dn (the game) | 150,000 | 152 | 1,015 | 200 MB | 1,398 B | 813 B |
| dn, drawn once | 150,000 | | | 259 MB | 1,811 B | 1,247 B |
| dnu (+ `UploadMeshData(true)`) | 136,192 (stopped at 40 MB free) | 75 | 549 | 233 MB | ≈1,790 B | 1,017 B |
| n (no MarkDynamic) | 150,000 | 166 | 1,107 | 214 MB | ≈1,500 B | 806 B |
| u (no MarkDynamic, no normals, `UploadMeshData(true)`) | 139,776 (stopped at 40 MB free) | 69 | 493 | 234 MB | ≈1,755 B | 971 B |

- `UploadMeshData(true)` doesn't save memory. It makes the GPU buffers at once, so a mesh costs what a drawn game mesh costs (≈1.8 KB). Dropping the CPU copy (≈140 B) is lost in the overhead.
- A smaller vertex format (no normals) saves ≈46 B per mesh (971 vs 1,017 B), about 3%.
- `MarkDynamic` makes no measurable difference (806 vs 813 B).
- Creation time varies between 0.5 and 1.1 ms per mesh. It is about 0.4 ms for the first 10k and grows with the number of live meshes. 150k is 70–170 s on the console in every variant.

## 2. Draw cost per frame, K sprites into the 398×224 target, scaled to 960×544

Frame time with vsync off (mean of 180 frames; p90 within 1 ms), and in brackets the main thread's submit time. Empty frame (clear, blit to screen): 6.43 ms. The atlas changes every 16 sprites among 4 atlases unless "1 atlas". The player renders on a separate thread (`graphicsMultiThreaded=True`), so frame time can exceed submit time.

| K | game path DrawMesh + MPB | same, dynamic batching on | same, 1 atlas | same, 150k meshes resident | game off-screen path SetPass + DrawMeshNow | batched dynamic mesh (draws) | batched, 1 atlas |
|---|---|---|---|---|---|---|---|
| 200 | 9.16 (2.68) | 9.30 (2.75) | 8.80 (2.46) | | 8.12 (2.77) | 6.72 (0.55), 13 | 6.71 (0.27) |
| 500 | 20.54 (7.20) | 19.69 (6.86) | 19.51 (6.47) | | 11.55 (7.09) | 7.09 (1.35), 32 | 7.05 (0.61) |
| 1000 | 41.43 (14.51) | 44.35 (14.64) | 42.46 (13.25) | 44.36 (14.66) | 19.46 (14.39) | 7.80 (2.63), 63 | 7.84 (1.18) |
| 2000 | 88.22 (30.45) | 93.23 (30.62) | 86.73 (27.65) | 90.26 (30.36) | 36.92 (30.13) | 11.15 (5.61), 125 | 9.09 (2.49) |

- The game's screen path costs 14–15 µs per sprite on the main thread, but its frame time grows faster than linearly: 14 µs per sprite over the empty frame at K=200, 28 at 500, 35 at 1000 and 41 at 2000. The extra goes to the render thread, perhaps sorting or command-buffer growth. A 16.7 ms (60 fps) frame holds about 390 sprites and a 33 ms frame about 750, before any game logic. Atlas changes, dynamic batching (on in d2) and the 150k resident meshes change nothing: the cost is per `DrawMesh` call.
- The off-screen path costs about 15 µs per sprite, all on the main thread.
- The batched path (one `MarkDynamic` mesh, vertices and UVs filled in C# each frame, one submesh and one `DrawMesh` per run of one atlas) costs about 1.2 µs per sprite on the main thread plus about 25 µs per draw, and 2000 sprites fit in 11 ms (9 ms with one atlas). Its C# fill is plain IL2CPP code and could be faster.
- `DrawMeshInstanced` is not available: `SystemInfo.supportsInstancing` is False on the PS Vita player.
- Screenshots in runs/d1 and runs/d2 confirm the sprites render. d1's batch numbers were invalid because the batch mesh was culled (bounds taken from the zeroed vertices); d2 sets the bounds and is the one quoted.

## 3. Startup

The game's `loadFromID(-1)` + fonts + `Res` preloads (over 150k meshes) would take about 2.5 minutes (145–152 s measured) of blocking startup and leave 73 MB of main memory before any texture loads. Drawing them as the game goes on grows that to 259 MB, leaving 14 MB. The atlas report's scheme d needs about 106 MB of textures for the worst level. As they are, the meshes don't fit.

## Recommendation

Replace the draw path; don't keep per-frame meshes at all. Lazy creation (R7) alone fixes startup and most of the memory, but leaves the draw cost (14–41 µs per sprite, rising with K), which limits a 60 fps frame to about 390 sprites before game logic. It is the fallback for bring-up only.

Build-time rules (on top of the atlas report's R1–R6):

- R7 (replaces the optional R7), `BitmapData.CreateMultiMeshFrames(TextureFrames)`: replace the body with `return new Mesh[tframes.uvx.Length * 4];` (no Mesh objects). The BitmapData constructor already copies `texpack_tex`, `uvx/uvy/uvw/uvh`, `frw/frh`, `ofx/ofy`, `sfw/sfh`, `mesh_half_width/height` into fields, which is everything a quad needs. `CreateMultiMesh` (render textures and plain textures, few) can stay.
- R8, `BitmapData.unityDraw` and `unityDrawClippedWidth`, screen branch: replace the `Graphics.DrawMesh(Mesh, Matrix4x4, Material, int, Camera, int, MaterialPropertyBlock, ShadowCastingMode, bool, Transform, LightProbeUsage)` call and the `meshes[...]` load before it with `SpriteBatch.Add(_source, meshIndex, matrix, material, mpb)`. The helper builds the 4 corners as `CreateMultiMeshFrames` would (trim offsets, the flip from `meshIndex & 3`, and for `unityDrawClippedWidth` the clipped width), transforms them by the matrix on the CPU and appends them to a per-frame vertex buffer. A run ends when material, atlas or the MPB's `_Color`/`_Coloff` change. Since both methods have unrecovered parts in Rewind's output, replacing the two bodies with port C# written from the recovered logic is likely simpler than patching call sites.
- R9, flush: `SpriteBatch.Flush()` after `Main.draw()` in `UnityMain.OnRenderObject`. It issues one `Graphics.DrawMesh` per run, as submeshes of one dynamic mesh (double-buffered so a mesh still in flight isn't rewritten), with `m_RenderCamera` and increasing z per run. Order is kept because runs are flushed in submission order.
- R10, optional, off-screen branch (`DrawMeshNow`, 15 µs per sprite): the same batch, flushed with `SetPass` + `DrawMeshNow(batchMesh, submesh)` when the target changes and at the end of the draw. Only worth doing if a profile shows many off-screen draws.
- ColorTransform runs: break the batch on each colour change at first. If profiling shows many changes, carry `colmul`/`coloff` per vertex (color + uv2) in a port copy of `Unlit/ColorTransformShader` instead.

If R8–R9 can't be done at bring-up: make R7 lazy instead (create a frame's mesh on its first draw, only the flip variant that is drawn). The cost then is about 1.8 KB and 0.4–1 ms per drawn variant, with no startup cost.

## Not measured

- How many sprites the game draws per frame (tiles, particles, HUD text glyphs; fonts are images too). That number decides how far the game's path falls short. A count from the Windows build (a hook in `unityDraw`) would settle it.
- ColorTransform-heavy scenes, rotation and scale (all draws here are unrotated at scale 1; the game path takes the same `DrawMesh` call either way).
- The game's real shaders (AssetRipper exported only dummies). The test uses a plain alpha-blended unlit shader.
