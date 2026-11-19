# Textures

The game draws all its pixel art from twelve 4096×4096 PNG atlases, the texture packs.
It decodes every pack into a 64 MB ARGB32 texture at start and keeps all twelve, 768 MB in all.
The PS Vita has about 240 MB for the whole game, and decoding one pack on the console takes about 5 seconds.
Every scene, menus included, draws from all twelve packs, because TexturePacker spread each sheet's frames over many of them.
So loading packs per scene saves nothing.

The port repacks the frames at build time into small pages grouped by the scenes that use them, and loads each scene's pages when the scene starts.
This page covers the scheme, the memory it needs, the build step, the runtime class, the code rules and how they plug into the pipeline.
Prose here is one sentence per line.
The analysis behind the scheme is in `tools/assess/atlas-usage` and its report, `work/atlas-usage/REPORT.md` (scheme d there).

## The scheme

Every frame of every image gets one place in one page for the whole game.
The game's frame table, and the four meshes per frame it bakes from it at start, never change at run time.
A scene needs only the pages that hold its frames.

- Pages are 1024×1024 and hold the frames that fit with their borders.
- A frame too big for a page gets a power-of-two texture of its own, shared with other big frames of the same scene set when they fit. There are 22 of these, mostly parallax backgrounds up to 4000×3200, and they are 344 MB of the 512 MB.
- The repacker sorts frames by the set of scenes that use them, so pages fill with frames that load together. Frames that half the scenes or more use share pages. Frames no scene reaches get pages of their own, the other languages' fonts first.
- Frames with the same pixels share one rect. In this release that saves nothing beyond the game's own sharing, since its 60,324 frames are 25,952 distinct rects and no two of those have the same pixels.
- Each rect has 2 transparent pixels on every side, from other rects and from the page edge.
- Pages are RGBA 4444, point filtered, without mipmaps. On the console RGBA 4444 looked lossless in the texture test and DXT5 showed artifacts.
- Pages wrap with Repeat, not Clamp. Two of the four meshes CreateMultiMeshFrames bakes per frame use every UV minus 1.0, and read the right texels only because textures wrap. The game's own packs wrap because `new Texture2D` defaults to Repeat. With Clamp, 1,900 of 3,064 such draws in the render check below come out wrong.

## Memory per level

Pages resident per scene, at 2 bytes per texel.
The budget is about 240 MB, 160 MB of main memory and 80 MB of CDRAM.

| Scene | pages | of them oversize | MB |
| --- | --- | --- | --- |
| L00/16 Future War 1 | 22 | 2 | 80 |
| L01 Biker Bar | 14 | 2 | 28 |
| L02 Truck Chase | 18 | 2 | 40 |
| L03 Hospital | 21 | 1 | 56 |
| L04/17 Future War 2 | 22 | 1 | 74 |
| L05-07 Cyberdyne HQ | 23 | 1 | 76 |
| L08 Freeway Chase | 21 | 1 | 56 |
| L08 with Tanker Chase | | | 72 |
| L09 Steel Mill | 25 | 1 | 80 |
| L10/18 Future War 3 | 22 | 1 | 74 |
| L11 Desert Town | 19 | 2 | 40 |
| L12 Cyberdyne Factory | 21 | 3 | 108 |
| L13 Cyberdyne Research | 23 | 1 | 76 |
| L14 Police Station | 20 | 1 | 46 |
| L15 Future War 4 | 23 | 2 | 82 |
| L48 Infinite mode | 25 | 1 | 50 |
| Controls tutorial | 14 | 1 | 28 |
| Demo, all four demos | 37 | 6 | 190 |
| Cutscene ArcadeEnding | 49 | 1 | 100 |
| Other cutscenes and menus | 5 to 21 | 0 or 1 | 10 to 42 |

The worst level is the Cyberdyne Factory at 108 MB.
72 MB of that are its three oversize layers, and CYBERDYNEFACTORY_FG_A alone is 3497×3131 and needs a 4096×4096 texture.
The report estimated 106 MB for this scheme, with a 1-pixel border instead of 2.
All 106 pages together are 512 MB, which is the size of the data on the memory card.
The page fill is 68%.

The Demo row is an upper bound, since the analysis merges the four attract demos into one scene.
At 190 MB it fits, but with little room for anything else.

After a build, `artifacts/downgrade/atlas/memory.md` and `report.json` list every scene.

## The build step

`tools/downgrade/atlas/repack.py` does the work.
It needs Pillow, numpy and dnfile, all in the project's dependencies.

1. The frame table comes from the IL of Rewind's `Assembly-CSharp.dll` (`frametable.py` with `il.py`). It evaluates `AssetsPack.staticInit2` and `staticInit5` to `staticInit13`, the blobs `staticInit` passes to InitializeArray, and the `Assets` constants. It reads no decompiled C#.
2. If a Rewind version drops the double stores in `staticInit6` to `staticInit9`, as an older one did 2,436 times, `native.py` reads the values from `GameAssembly.dll`. It needs the method's address from an Il2CppDumper `dump.cs` (`--game`, `--dump`), and capstone and pefile. The Rewind the pipeline pins leaves no gaps there, so the pipeline never needs this.
3. The packs come from the export's `Resources/texturepack/TexturePackN.bytes`, or straight from the game's `resources.assets` (`packs.py`). Both give the same bytes.
4. `scenes.json` says which images each scene draws, by `Assets.IMAGE_*` name, and which runtime keys enter each scene. `scene_sets.py` writes it from the atlas-usage analysis. It is rule data and names game members only.
5. It packs, writes the pages, `frames.bytes` and `scenes.bytes`, then reads every frame back through `frames.bytes` and the written pages and compares it with its pack byte for byte, border included. A difference stops the build.

The output is deterministic: two runs, one from `resources.assets` and one from the export, give identical files.
A run takes 35 to 60 seconds.

Pages come in two forms.

- PNG pages, the default for `repack.py`, with a `.meta` that imports them as RGBA 4444 for the PS Vita and the default platform, point filtering, Repeat wrapping, no mipmaps, not readable.
- Raw pages with `--raw`, the default in the pipeline step. Each `pNNN.bytes` holds RGBA 4444 texels in Unity's layout, rows bottom first, a little-endian u16 per texel with R in the top nibble. The repacker rounds each channel to 4 bits, and the check compares against the rounded original. Atlas.cs makes the texture with `LoadRawTextureData` and `Apply(false, true)`.

The pipeline uses raw pages because `tools/builder/textures.py` imports textures only as RGB24, RGBA32, DXT1 and DXT5, and raises on format 13.
When the builder learns RGBA 4444, `atlas_overlay(..., raw=False)` switches to PNG pages.
Atlas.cs loads either.

### Formats

`frames.bytes`: `T2DF`, u32 format 1, u32 rows, u32 pages; per page u16 width and height; per image row a u32 frame count, then per frame u16 page, x, y from the bottom, w, h.
`scenes.bytes`: `T2DS`, u32 format 1, u32 scenes; per scene a name and its pages; per key (`app:<Class>`, `level:<id>`, `map:<id>`) its scenes; the App classes during which level keys act; the sticky pages.
Strings are a u16 byte count and UTF-8.
All values are little-endian.

## The runtime class

`tools/downgrade/atlas/compat/Atlas.cs` is `T2DCompat.Atlas`, C# 5, compiled into T2DCompat with the rest of the platform layer.
It reads `Resources/atlas/frames.bytes` and `scenes.bytes` on first use.

- `Tex`, `Uvx`, `Uvy`, `Uvw`, `Uvh` return the frame table's jagged arrays, with each rect divided by its page's size. `PageCount` returns the number of pages.
- `Get(page)` returns the page's texture. If the page isn't resident it loads it at once and logs the miss with the page and the current scene, once per page and scene. `MissReport` sums the misses up by page, for the next round of scene data.
- `EnterApp(app)` takes the App's class name as the key. For `Game`, the only level app, it changes nothing and lets the level hooks choose.
- `EnterLevel(id)` acts only while the App is Game, because menus call `loadScriptByLevelID` for level previews, EndScreen in a loop.
- `EnterMap(map, level)` always acts. Maps load only to be played; one cutscene, CyberdyneHQOutro, loads a map as its backdrop.
- `Enter(key)` is the general form.

Each Enter makes the resident set the pages of the App's scene and of the level's scene.
It unloads what the new set doesn't hold before it loads the rest, so the peak is one scene, not two.
Pages a miss loaded go at the next Enter.
Sticky pages, the other languages' fonts, stay once loaded, so a player in Japanese pays one hitch, not one per scene.
Past `BudgetBytes`, 150 MB, a miss first unloads the least recently used pages no current scene wants.

## The rules

`tools/downgrade/atlas/rewriter/AtlasPatches.cs` holds five rewriter passes.
Each checks the IL it expects and throws if the code has changed shape, so a rule never applies to code it wasn't written for.
The counts are those of the Steam release, build 24040403, as Rewind recovers it.

| Rule | Target | Expects | Emits |
| --- | --- | --- | --- |
| R1 | `AssetsPack.staticInit5` to `staticInit9` | a straight-line initialiser ending in its one `stsfld imagetexframes_tex`, `_uvx`, `_uvy`, `_uvw` or `_uvh` | a new body: `call Atlas.Tex()` (or `Uvx` ... `Uvh`); `stsfld`; `ret` |
| R1 | `AssetsPack..cctor` | `ldc.i4.s 12; stsfld imagetexnumber` | `call Atlas.PageCount()` in place of the constant |
| R2 | `BBSplash.start` | one `ldsfld imagetexnumber` after a constant and before `sub` (`range = 25 - imagetexnumber`) | `ldc.i4 12`, the splash length it always had |
| R2 | `BBSplash.update` | one `call BitmapData.loadTexturePacks(int)` | `call Atlas.SplashStep(int)`, which loads nothing |
| R3 | `AssetsEmbed.staticInit` | `ldsfld AssetsPack.bblogotexnumber; call loadTexturePacks` | `ldstr "app:BBSplash"; call Atlas.Enter(string)`, since Main makes the BBSplash itself |
| R4 | `BitmapData.loadTexturePacks(int)` | `Resources.Load<TextAsset>("texturepack/TexturePack" + n)` and `ImageConversion.LoadImage` | a new body: `ldarg.0; call Atlas.Preload(int); ret` |
| R5 | `BitmapData.unityDraw`, `unityDrawClippedWidth` | two `ldsfld texpack; ldloc <page>; ldelem Texture2D` each | in place: `ldloc <page>; call Atlas.Get(int); nop` |
| R6 | every `call Transition.getApp(int)` | 2 sites, both in `Transition.update` | after the call: `dup; call Atlas.EnterApp(object)` |
| R6 | `GameMap.loadFile(int, int)` | static, two int parameters | at its start: `ldarg.0; ldarg.1; call Atlas.EnterMap(int, int)` |
| R6 | `Game.loadScriptByLevelID(int)` | static, one int parameter | at its start: `ldarg.0; call Atlas.EnterLevel(int)` |

Each R6 hook is followed by `ldsfld BitmapData.TEXTURE_UNKNOWN; stsfld BitmapData.texused`.
The draw methods skip `SetTexture` when the page index equals `texused`, and after an Enter the page under an index may be a new Texture2D.

R1 also makes the 2,436 stores an older Rewind dropped from those tables irrelevant.
After R4 and R5, nothing reads `BitmapData.texpack` but `initBitmapData`, which allocates it, so that method's Rewind gap around the allocation no longer matters for textures.
The trims (`staticInit10` to `staticInit13`) and the sheet sizes stay, since trims don't change.

R7, optional and not written: make `CreateMultiMeshFrames` bake a frame's four meshes on its first draw instead of all at start.
`Sprites.loadFromID(-1)` builds meshes for about 34,500 sprite frames at start, over 150,000 Mesh objects with fonts and `Res` images.
A parallel task measures what that costs on the console; R7 waits for its numbers.
The rule would replace the body with an array of nulls the length of `tframes.uvx`, and have `unityDraw` and `unityDrawClippedWidth` fill `meshes[frame * 4 + variant]` through a compat helper before they use it.

### Checking the rules

```sh
python -I tools/downgrade/atlas/check/check_rules.py --asm <Rewind out>/assemblies/Assembly-CSharp.dll --work <scratch dir>
```

It compiles Atlas.cs with Roslyn against Unity 2018.2's unityaot class libraries and CoreModule (`-nostdlib -langversion:5 -warnaserror+`).
Then it compiles `check/RuleCheck.cs` with AtlasPatches.cs and the rewriter's `Stack.cs`, using the .NET Framework's csc and Mono.Cecil 0.10 as the pipeline does.
It runs the passes on the assembly alone and checks each rule's site count, the stack of every changed method, and that nothing still calls `loadTexturePacks` or reads `texpack`.
It writes the assembly and reads it back.
On Rewind's output for the Steam release: R1 6 sites, R2 and R3 3, R4 1, R5 4, R6 4, every changed method's stack balanced, all rules OK.

`check/verify_render.py` draws frames the way the game's meshes do, at pixel centres with float32 interpolation and point sampling.
It draws each from the original packs through the game's table and from the pages through `frames.bytes`, in all four mesh variants, and compares.
On 1,532 frames, 1,500 random plus every frame of three chosen images, all 6,128 draws match with Repeat.

## Wiring into the pipeline

These changes are in the commit "Wire the texture pages into the pipeline", apart from the atlas files so they can be taken on their own.

- `pipeline.asset_overlay` calls `atlas.step.atlas_overlay(exported, RECOVERED / 'Assembly-CSharp.dll', WORK / 'atlas', ARTIFACTS / 'atlas', log=log)` after the shader overlay. It merges the returned overlay, the pages and tables under `Assets/Resources/atlas`, and adds the returned paths to the exclusions, the export's `Resources/texturepack` folder.
- `atlas_overlay` reruns `repack.py --raw` only when the recovered assembly, the packs, `scenes.json` or the atlas scripts change, by a stamp in `work/atlas`.
- It reads Rewind's assembly, not the rewritten one, because R1 replaces the very tables the repacker reads.
- `pipeline.compat_sources()` adds `atlas/compat/*.cs` to T2DCompat.
- `pipeline.REWRITER_SOURCES` adds `atlas/rewriter/AtlasPatches.cs`.
- `DowngradeAssemblies.Passes` adds, after `ListRewindGaps`, `new Pass("atlas", Scope.Game, AtlasTables)`, then `AtlasSplash`, `AtlasPackLoads`, `AtlasDraws` and `AtlasSceneHooks`. They go in the list itself, not in an array from AtlasPatches.cs, because the order of static initialisers across partial files is not defined.

The atlas passes must run after any pass that replaces a whole method body among their targets.
`unityDraw` has 48 Rewind gap sites and `GameMap.loadFile` 3; if a repair rewrites either method, the atlas pass after it must still find its pattern, or it throws and says which.

With this wiring, `uv run tools/downgrade/pipeline.py project` runs through in 43 to 52 seconds.
The rewriter's log shows `atlas: AtlasTables=6 AtlasSplash=3 AtlasPackLoads=1 AtlasDraws=4 AtlasSceneHooks=4`.
Its problem count stays at 245, none of them in a method the rules change.
The project gets `Assets/Resources/atlas` with 106 pages, the two tables and their metas, and no `texturepack` folder.

## What is untested

Nothing here has run on the console or in the editor.

- RGBA 4444 PNG pages through the editor's or the builder's import. The builder doesn't import format 13 yet, hence the raw pages. The texture test built with the editor, but a search of its `.resS` for the RGBA 4444 texture's rows as plain linear data found none, so the layout the builder must write is still to be found.
- Raw pages cost a managed copy of each page while it loads: 2 MB for a normal page, 32 MB for a 4096×4096 one. The managed heap grows to the largest of these and doesn't shrink.
- Load time per page on the console. A 1024×1024 raw page is 2 MB to read and upload, against 5 seconds to decode a pack.
- Whether the Vita's GPU wraps non-power-of-two textures. All pages are powers of two, which costs 30% on the oversize layers; NPOT sizes would bring the Factory to about 90 MB.
- The scene data comes from static analysis. About 250 images, a quarter of a pack, are reachable only in ways it can't follow, through arithmetic on ids, ids in int[] tables, native jump tables that inline a constructor. Those draws miss and load at once. Play through every scene with the miss log on and feed `MissReport` back into `scenes.json`.
- Tanker Chase has no hook of its own; level 8 loads Freeway Chase's and Tanker Chase's pages together, 72 MB.
- `imageclassname` row 57 is 1 where IMAGE_TROPHY_ARRAY_21 should be, so `loadImage(57)` returns null in the original too. Its frames go to the unreached pages.
- The repacker reads one release of the game. Another release with other frames would need its own `scenes.json` check; unknown image names are reported, not fatal.
