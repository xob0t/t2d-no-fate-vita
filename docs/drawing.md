# Drawing

The game draws its sprites one mesh at a time.
At start it bakes four Unity meshes for every frame of every image, over 150,000 in all, and it draws each sprite with its own `Graphics.DrawMesh`.
On the PS Vita that costs about 2.5 minutes of startup and most of main memory, and each sprite then costs 14 to 41 µs to draw ([mesh-test.md](mesh-test.md)).
The port builds no meshes for sprite sheets.
It draws through a batched quad renderer, `T2DCompat.SpriteBatch`: one dynamic mesh per frame, filled on the CPU, with one draw per run of sprites that share a texture.
This page covers what the game's meshes hold, the renderer, the rules that send the game to it, how they meet the atlas rules and the gap repairs, and how all of it was checked.
Prose here is one sentence per line.

## What the game's meshes hold

`BitmapData.CreateMultiMeshFrames` makes four meshes per frame: plain, flipped horizontally, flipped vertically and both, in that order, so frame `f` with flips `h` and `v` is mesh `4f + h + 2v`.
Each has four corners and the triangles 0 1 2 and 2 1 3.
A BitmapData made from a texture pack's image keeps the frame table's columns for its image: `texpack_tex` (the page), `texpack_uvx`, `uvy`, `uvw` and `uvh` (the rect in the page, 0 to 1), `texpack_frw` and `frh` (the trimmed size in pixels), `texpack_ofx` and `ofy` (where the trimmed rect sits in the untrimmed cell, from its top left), and `texpack_sfw` and `sfh` (the cell's size).
`mesh_half_width` and `mesh_half_height` are half the cell.

With the cell's centre `(cx, cy)` as the origin and y up, the trimmed rect runs from `left = ofx - cx` to `right = frw + ofx - cx`, and from `top = cy - ofy` down to `bottom = -(frh + ofy - cy)`.
The plain mesh's corners are, in order, right top, right bottom, left top and left bottom.
Its UVs are `(uvx + uvw, uvy + uvh)`, `(uvx + uvw, uvy)`, `(uvx, uvy + uvh)` and `(uvx, uvy)`: the rect's top is the texture's higher v.

A horizontal flip mirrors the corners' x about the cell's centre, so corner 0 sits at `-left` and corner 2 at `-right`.
It swaps the UVs' u as well, but not as a plain swap: corner 0 gets `uvx - 1` and corner 2 `uvx + uvw - 1`.
The two u run from the rect's left to its right minus one texture width, so the texture is read backwards across the rect only because it wraps.
A vertical flip does the same in y and v: corner 0's y is `-bottom`, corner 1's is `-top`, and v becomes `uvy - 1` and `uvy + uvh - 1`.
Both flips do both.
This is why the atlas pages must wrap with Repeat ([textures.md](textures.md)).

The game computes all of this in double and stores floats, and the port keeps the same operations in the same order, so its corners are the same floats.

`CreateMultiMesh` makes the four meshes of a BitmapData that has a render texture or a plain texture: one cell, the BitmapData's own size, UV `width / texture width` by `height / texture height`, flipped the same way.
Its corners don't move with the flip, as the rect fills the cell.

### Placing a mesh

`unityDraw(source, x, y, frame, columns, rows, align, ct, hflip, vflip, width, height, blendMode, blendAlpha, rotation, hscale, vscale)` draws from `source` into `this`.
The cell size it aligns by is `width` and `height`, or `source.width / columns` and `source.height / rows` (integer division) when they are -1.
`align` is -1 for none, or bits: 16 centres x, else 2 aligns the right edge; 32 centres y, else 4 aligns the bottom.
Centring takes off half the cell times the scale, and right or bottom alignment the whole cell times the scale.
The mesh goes to `(x - (width_half - source.mesh_half_width), source.mesh_half_height + height_half - h - y)` in the target's centred space, with the scale applied to both half sizes and to `h` when it isn't 1.
`rotation` is in radians, clockwise on screen, and becomes `Quaternion.AngleAxis(-degrees, Vector3.forward)`.
`blendMode` and `blendAlpha` change nothing.

On the screen, the 398x224 BitmapData, the matrix is `Matrix4x4.TRS((x, y, zorder), rotation, (hscale, vscale, 1))`, and the draw is `Graphics.DrawMesh` with `UnityMain.m_RenderCamera`.
`BitmapData.zorder` starts at 999 each frame and falls by 0.25 after each screen draw.
NormalShader and ColorTransformShader don't write depth, so the order on screen is the order of Unity's back-to-front sort of the transparent queue, which is the order of the z values, which is the order of the calls.
A ColorTransform draws with `ct_material`, its `colmul` as `_Color` and `coloff` as `_Coloff` in the material property block; anything else draws with `main_material`.
Into any other BitmapData, a render texture, the draw happens at once: `RenderTexture.active`, `GL.LoadPixelMatrix(-width_half, width_half, -height_half, height_half)`, `SetPass`, `Graphics.DrawMeshNow`.

`unityDrawClippedWidth(source, x, y, frame, columns, rows, align, ct, frameWidth)` draws a frame unflipped and unscaled, cut off at `frameWidth` pixels from the cell's left edge; the HUD's health bars use it.
It draws nothing when `frameWidth <= ofx`.
When `frameWidth` cuts the rect, the right corners move to `ofx + (frameWidth - ofx) - cx` and their u to `uvx + uvw * (frameWidth - ofx) / frw`, and the cell width it aligns by is `frameWidth`.
Otherwise it is the plain quad and the usual cell width.
Its bottom corners' y are `0.0 - (frh + ofy - cy)` rather than a negation, which differs only in the sign of a zero.
The game writes this quad into the frame's horizontally flipped mesh and draws that one, so a frame drawn clipped and later drawn flipped would show the clipped quad unflipped; the port doesn't copy that.

## The renderer

`tools/downgrade/spritebatch/compat/SpriteBatch.cs` is `T2DCompat.SpriteBatch`, C# 5, compiled into T2DCompat with the rest of the platform layer.
It is new code that reads the BitmapData's fields; it holds none of the game's methods.

- `Target(screen, rentex, width_half, height_half)` and `Source(rentex, texpack_tex, uvx, uvy, uvw, uvh, frw, frh, ofx, ofy, sfw, sfh, mesh_half_width, mesh_half_height, width, height)` take the two BitmapData's fields before each draw. `Colors(colmul, coloff)` takes a ColorTransform's.
- `FrameQuad` builds a frame's quad for one of the four flips, and `ClippedQuad` the clipped one, as above.
- `Layout` and `LayoutClipped` align and place it, rotate and scale it as Unity's `TRS` would, and return the page.
- `Draw` and `DrawClipped` take the page from `Atlas.Get`, or the source's render texture, and queue the quad for the screen or draw it into the target at once. They return the new `zorder`.
- `Flush(camera)` draws what the frame queued and empties the queue.

### Runs

A screen sprite goes into the current run when it has the same material, the same texture, for a ColorTransform the same `_Color` and `_Coloff`, and the z right after the run's last one.
Anything else starts a run.
The z rule keeps the game's other screen draws in their place: `fillRect` and `colorTransform` draw their own meshes with z from the same counter, so a run ends wherever one of them took a z.

The queue is one vertex array and one UV array per mesh of up to 16,384 quads, so that indices fit 16 bits; a frame with more sprites fills another mesh.
The arrays grow by doubling and go to the mesh whole, since Unity 2018.2 has no ranged `SetVertices`; the corners past the frame's last quad are left from earlier frames, and no index refers to them.
The corners are in the target's space, and their z is relative to the run's first sprite.
`Flush` writes each mesh, one submesh per run, and draws each run with `Graphics.DrawMesh` at its first sprite's z, with the camera the game drew for and a property block of the texture and the colours.
So the sort puts runs, and the game's draws between them, in the order the game made them, as it did with one mesh per sprite.
The bounds are set to cover any quad, since Unity would otherwise take them from the vertices it last had and cull the mesh.

There are two sets of meshes, used on alternate frames, so a mesh the render thread may still read isn't rewritten.
When a mesh's runs and its arrays have the same sizes as the last time its set was used, its indices stand, and only the vertices and UVs are written.

### Render textures

A draw into a render texture happens at once, as the game's did, with the same pixel matrix: the material's pass and the quad's two triangles through `GL`.
It draws with copies of `main_material` and `ct_material`, so that the colours it sets don't disturb the game's own record of what those materials hold (`ct_material_colmul_argb` and the others, which `fillRect` relies on).
Batching these draws (the mesh report's R10) is not done, as the report says it pays only if a profile shows many of them.

## The rules

`tools/downgrade/spritebatch/rewriter/SpriteBatchPatches.cs` holds three rewriter passes.
Each checks the IL and the fields it expects and throws if the code has changed shape.
The counts are those of the Steam release, build 24040403, as Rewind recovers it.

| Rule | Pass | Target | Expects | Emits |
| --- | --- | --- | --- | --- |
| R8 | `SpriteBatchDraws` | `BitmapData.unityDraw`, `unityDrawClippedWidth` | the parameters above by type; one `Graphics.DrawMesh`, a `DrawMeshNow`, reads of `meshes`, one store of `zorder`; no call into SpriteBatch yet | a new body each, below |
| R7 | `SpriteBatchMeshes` | `BitmapData.CreateMultiMeshFrames(TextureFrames)` | static, returns `Mesh[]`, makes Meshes; nothing but the two draw methods reads `BitmapData.meshes` | `return new Mesh[tframes.uvx.Length * 4];` |
| R9 | `SpriteBatchFlush` | `UnityMain.OnRenderObject` | one call of `Main.draw()`; a static `Camera m_RenderCamera` | after it: `ldsfld m_RenderCamera; call SpriteBatch.Flush(Camera)` |

R8's new body is:

```
if (_source == null) return;
SpriteBatch.Target(screen, rentex, width_half, height_half);
SpriteBatch.Source(_source.rentex, _source.texpack_tex, ..., _source.width, _source.height);
if (_ct != null) SpriteBatch.Colors(_ct.colmul, _ct.coloff);
zorder = SpriteBatch.Draw(_x, _y, _frame, _HFRAMES, _VFRAMES, _align, _ct != null, _hflip, _vflip,
                          _width, _height, _rotation, _hscale, _vscale, main_material, ct_material, zorder);
matused = MATERIAL_UNKNOWN;
texused = TEXTURE_UNKNOWN;
```

with `DrawClipped(_x, _y, _frame, _HFRAMES, _VFRAMES, _align, _ct != null, _frameWidth, main_material, ct_material, zorder)` in `unityDrawClippedWidth`.
The rule checks every field it loads and every argument it passes against the type of SpriteBatch's parameter, so a field that changed type fails the build, not the game.
The last two stores make the next `fillRect` or `colorTransform` set its material state again, as it did after a game draw; SpriteBatch never touches the game's property block or materials.

R7 leaves the array, all nulls, because the BitmapData constructor stores it in `meshes` and nothing reads it any more.
`CreateMultiMesh`, for render textures and plain textures, still makes its four meshes; there are few of them, and nothing draws them now either.

The passes go into `DowngradeAssemblies.Passes` before `TypeGapValues` and `ListRewindGaps`, in the order R8, R7, R9.
R8 goes first so that R7's check that nothing reads `meshes` sees the new bodies, and both go before the gap passes so that `TypeGapValues` doesn't type values in the bodies they throw away and `ListRewindGaps` doesn't report their gaps.
`TypeGapValues` changes none of the four methods R7 to R9 take on Rewind's current output, and the code stage writes the same `Recovered.T2D` with it before or after these passes.
`pipeline.REWRITER_SOURCES` adds `SpriteBatchPatches.cs`, and `pipeline.compat_sources()` adds `spritebatch/compat/*.cs` to T2DCompat.

### The atlas rules and the gap repairs

R8 replaces the whole bodies of the two draw methods, so anything else that edits them is moot.

- The atlas rule R5 rewrote their `texpack[page]` reads to `Atlas.Get(page)`. SpriteBatch calls `Atlas.Get` itself, with the page from `texpack_tex`, which R1 already makes the atlas's. `AtlasDraws` now skips a method whose body calls SpriteBatch, so with R8 it changes nothing, and `DowngradeAssemblies.OptionalPasses` lists it so that a pass with no sites doesn't fail the run. Run on Rewind's output alone, as `atlas/check` does, it still makes its four edits.
- The other atlas rules are untouched: R1's tables, R2 to R4's pack loads and R6's scene hooks apply as before.
- Rewind leaves 48 marker sites in `unityDraw`, 12 in `unityDrawClippedWidth` and 18 in `CreateMultiMeshFrames`. They need no repair rules. The repair engine runs before the passes, so a rule written for them would apply and then be thrown away with the body. Don't write one.
- Rewind's gaps elsewhere in BitmapData, such as `fillRect`'s, are unchanged.

### Checking the rules

```sh
python -I tools/downgrade/spritebatch/check/check_rules.py --asm <Rewind out>/assemblies/Assembly-CSharp.dll --work <scratch dir>
```

It compiles SpriteBatch.cs, Atlas.cs and Trace.cs with Roslyn against Unity 2018.2's unityaot class libraries and CoreModule (`-nostdlib -langversion:5 -warnaserror+`), with `check/ProbeStub.cs` for the probe tick `Flush` makes (the real probe needs the log, the flags and more of the platform layer).
Then it compiles `check/SpriteCheck.cs` with SpriteBatchPatches.cs, AtlasPatches.cs and the rewriter's `Stack.cs`, using the .NET Framework's csc and Mono.Cecil 0.10 as the pipeline does.
It runs R8, R7, R9 and the atlas passes in the pipeline's order and checks:

- each pass's sites: R8 2, R7 1, R9 1, and the atlas passes' 6, 3, 1, 0 and 4;
- that a second R8 refuses to run;
- that nothing calls `CreateMultiMeshFrames` but the BitmapData constructor, and nothing reads `meshes`, reads `texpack` or calls `loadTexturePacks`;
- the stack of every changed method, by the pipeline's own simulation;
- that the new bodies have no locals, no handlers, no Rewind marker, and call nothing but SpriteBatch;
- that the written assembly reads back.

It passes on the `work/rewind-steam-60f1` output and on a later pipeline `recover` output of the same release.
The pipeline's `code` stage with these rules logs `spritebatch: SpriteBatchDraws=2 SpriteBatchMeshes=1 SpriteBatchFlush=1` and `atlas: AtlasTables=6 AtlasSplash=3 AtlasPackLoads=1 AtlasDraws=0 AtlasSceneHooks=4`.
Its unresolved references are the same 61 as without them, and its Rewind gaps fall by 3 methods and 78 marker sites, the bodies R7 and R8 replace.

## Checking the geometry

A test on the PC compares SpriteBatch's quads with what the game's code builds.
Its reference transcribes the recovered `CreateMultiMeshFrames`, `CreateMultiMesh` and the placement in `unityDraw` and `unityDrawClippedWidth`, so it stays in `work/spritebatch/geomtest` and isn't in the repository.
It runs SpriteBatch.cs, compiled against the editor's CoreModule, on the .NET Framework, with the frame table that `atlas/frametable.py` reads from Rewind's assembly.
The reference reads Rewind's `__bits_to_double(...)` markers in these methods as the negation they are (the sign bit of the high word flipped), does Unity's `AngleAxis` and `TRS` as Unity's reference code does, and multiplies the corners by the matrix as the vertex program does.

| Case | Quads | Result |
| --- | --- | --- |
| Every frame of the table, 60,324, each in all four flips | 241,296 | bit for bit, signs of zeros included |
| Random frames with UVs that aren't multiples of 1/4096, all four flips | 200,000 | bit for bit |
| Render-texture BitmapData, sizes 1 to 512, texture as large or larger | 20,000 | identical |
| `unityDraw` on the screen, unrotated at scale 1, any alignment, cell size and fractional position | 150,167 | identical after the matrix |
| The same, scaled | 74,885 | identical |
| The same, rotated, scaled or not | 74,948 | identical, against the reference's own `AngleAxis` |
| `unityDrawClippedWidth`, cut | 148,081 | identical |
| `unityDrawClippedWidth`, not cut | 37,128 | identical |
| `unityDrawClippedWidth`, nothing left of `frameWidth` | 14,791 | nothing drawn in both |
| Frames -1 and past the end | 2 | nothing drawn in both |

961,298 checks, no difference.
Two things it can't settle: Unity's native `AngleAxis` takes a float sine where the reference and SpriteBatch take a double one, which can move a rotated corner by a float's last bit, and the GPU may fuse the matrix's multiply and add where the CPU doesn't.
Neither moves an unrotated sprite.

## On the console

`tools/assess/vita-mesh-test` has two more draw methods: `spritebatch`, which calls `Target`, `Source` and `Draw` for every sprite as R8's bodies do and `Flush` once a frame, and `drawmeshsb`, the game's per-sprite `DrawMesh` path with quads from `SpriteBatch.FrameQuad`.
With `still` the sprites don't move, so the two methods' screenshots of the same 1,000 sprites can be compared.
The test compiles SpriteBatch.cs into the app, with a stand-in for `Atlas` that returns its own textures.

Measured on the user's PS Vita on 2026-11-19, run `sb3` (`work/vita-mesh-test/runs/sb3`), with the mesh report's setup: 444/222 MHz, vsync off, the mean of 180 frames, sprites 8 to 40 pixels, a 398x224 target scaled to 960x544.
Frame time in ms, and in brackets the main thread's submit time; the empty frame is 6.42 ms.
The atlas changes every 16 sprites among 4 atlases, except in the one-atlas and the every-sprite columns.

| K | the game's path, `drawmeshsb` | `spritebatch` | `spritebatch`, 1 atlas | `spritebatch`, a new atlas every sprite | the mesh report's batch prototype |
| --- | --- | --- | --- | --- | --- |
| 200 | 9.20 (2.74) | 6.74 (1.29), 13 draws | 6.76 (1.90), 1 draw | | |
| 500 | 20.64 (7.25) | 7.12 (3.11), 32 | 7.11 (3.27), 1 | | |
| 1000 | 41.41 (14.45) | 10.26 (6.39), 63 | 8.30 (5.71), 1 | 57.87 (30.65), 1000 | 7.80 (2.58), 63 |
| 2000 | 87.96 (30.25) | 19.88 (14.07), 125 | 13.76 (10.96), 1 | 118.80 (60.17), 2000 | 11.10 (5.60), 125 |

- With runs of 16, SpriteBatch draws 2000 sprites in 20 ms against the game path's 88, and 1000 in 10 ms against 41. A 60 fps frame holds about 1,000 sprites before game logic, against about 390.
- The main thread spends about 5.5 µs per sprite, of which the calls R8's bodies make (`Target`, `Source` with 16 arguments, `Draw`) are part. That is four times the prototype's fill, which placed bare quads. The quad, the placement and the run checks are plain IL2CPP code, and there is room to make them faster.
- Each run costs about 30 µs on the main thread. A frame in which the texture changes with every sprite is slower than the game's own path: 119 ms against 88 for 2000 sprites. Whether the game's frames look like that depends on how often consecutive draws come from different pages, which nobody has counted yet.
- With `still`, the screenshots of the same 1,000 sprites drawn by `spritebatch` and by `drawmeshsb` are identical, all 522,240 pixels. That is the batch on the console GPU, in four flips and 63 runs, against one game mesh per sprite with the same quads.
- One earlier launch (run `sb1`) stopped before its first frame and left no crash dump; the same build ran at the next launch. Nothing in SpriteBatch runs before a draw step, so it was not SpriteBatch.

## What is untested

- Nothing here has run in the game. The pipeline's `code` stage applies the rules, but the build still stops on references that don't resolve ([build.md](build.md)).
- The game's real shaders and materials with SpriteBatch's property block. ColorTransform sprites take `_Color` and `_Coloff` from it, as the game's did.
- How many runs a real frame makes. A sprite whose page or ColorTransform differs from the one before starts a run, and so does any `fillRect` between sprites, at about 30 µs each. If the game's frames alternate pages often, the next step is fewer runs: pages grouped so that a scene's sprites share one, or the runs of one page merged where nothing of another page overlaps them.
- Off-screen draws through `GL`, and a frame of more than 16,384 screen sprites.
- Screen draws made outside `OnRenderObject` wait for the next `Flush`, which is where the game's `DrawMesh` calls waited too, for the render camera's next render.
