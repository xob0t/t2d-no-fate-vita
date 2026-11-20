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

Pages come in two forms, with the same texels.
The repacker rounds each channel to 4 bits, and its check compares every frame against the rounded original.

- PNG pages, the default for `repack.py` and the pipeline. Each channel is stored as its 4-bit value times 17, and a `.meta` imports the page as RGBA 4444 for the PS Vita and the default platform, point filtering, Repeat wrapping, no mipmaps, not readable. The import keeps each channel's top 4 bits, which gives back the rounded value. The pages are real Texture2D assets: the player streams them from `resources.assets.resS` with no managed copy.
- Raw pages with `--raw`, the fallback (`T2D_ATLAS_PAGES=raw`). Each `pNNN.bytes` holds RGBA 4444 texels in Unity's layout, rows bottom first, a little-endian u16 per texel with R in the top nibble. Atlas.cs makes the texture with `LoadRawTextureData` and `Apply(false, true)`.

Atlas.cs loads either: it tries `Resources.Load<Texture2D>` first, then the TextAsset.
In the project the PNG pages larger than 1024 are 1024x1024 tiles instead, cut by `atlas/step.py` ([Loads without freezes](#loads-without-freezes)).
The PNG pages take 30 MB in the project, the raw ones 513 MB; in the player both are 512 MB of texels.

### RGBA 4444 in the editor and the builder

Unity 2018.2's PS Vita importer stores RGBA 4444 like the uncompressed formats, with no swizzling or tiling.

- Rows bottom first, one little-endian u16 per texel, R in the top nibble and A in the bottom.
- Each channel is the 8-bit value's top 4 bits, `v >> 4`: truncated, not rounded. That is why the PNG pages store `n * 17`.
- Any size, power of two or not. With "alpha is transparency" on, the importer fills the colour of see-through texels first, as for RGBA32.
- In `.resS`, each texture starts at a multiple of 4 bytes. An AssetBundle's `.resS` also ends at one.
- Mip levels follow one another, largest first, `floor(log2(max(w, h))) + 1` of them. Unity filters them with a box in float32, from `v * (1 / 255)`, with RGB in linear light for sRGB textures and alpha linear. It halves each level from the unrounded one before it, horizontally then vertically, and rounds half up. The builder makes them for uncompressed power-of-two textures. Odd sizes use another filter, so it refuses non-power-of-two textures with mipmaps.
- Unset sampler settings (`-1`, mip bias `-100`) are built as bilinear, anisotropy 1, bias 0, Repeat.

`tools/builder/textures.py` does the same, and `tools/builder/check_textures.py` checks it against the editor:

```sh
uv run tools/builder/check_textures.py reference   # the editor builds the generated reference (about 2 minutes)
uv run tools/builder/check_textures.py --compare   # the builder writes the same textures; byte for byte
uv run tools/builder/check_textures.py --pages <repack.py --out> <repack.py --out --raw>
```

`reference` generates 22 images from a fixed seed, no game pixels, each with one case's import settings: the atlas pages' `.meta` at 1024², 256×2048 and quantized, mipmaps sRGB and linear, sizes 1×1, 3×1, 4×4 and 300×200, alpha is transparency, readable, the default platform's format and the unset sampler settings, and RGBA32 and RGB24 with mipmaps.
Three more go into an AssetBundle.
The installed Unity 2018.2.19f1 editor with its PS Vita module builds them, under `work/builder/texture-reference`, as a PC Hosted player and a bundle.
`--compare` makes each Texture2D with `textures.texture()` in the reference's path ID order and compares its fields, its serialized bytes, its image data and the whole `.resS`.
On 2026-11-19: 21 identical, 0 differ, and the 4 non-power-of-two mipmapped textures not supported. Both `.resS` files are identical, 5,792,524 bytes in the player and 524,980 in the bundle.
With `--project` and `--media` it reads another editor build. On the texture test (`tools/assess/vita-texture-test`), TexturePack0 at 4096² comes out identical as RGBA 4444 and as RGBA32.
`--pages` imports every PNG page through the builder and compares it with the raw page of the same packing. All 106 pages are identical.

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
`Resources.Load` of a page takes about a millisecond: the PS Vita player reads a page's texels at its first draw, into video memory (CDRAM) while it has room and into main memory after, so a scene's pages cost nothing at the Enter and stall its first frames instead (the texture test: 2.6 s for a 32 MB page).

- The budget follows free video memory. `BudgetBytes` is the bytes of pages that fit in CDRAM now: those resident and drawn, those unloaded but not released yet, and what CDRAM has free beyond `CdramReserveBytes` (6 MB); on a player that doesn't report free memory (Windows), `FixedBudgetBytes`, 150 MB. Before a page loads, pages no current scene wants go, least recently used first, until the page fits beside the pages loaded but not drawn yet. Pages the scenes want load anyway, and the player puts them in main memory, which is slower to sample; the log says so (`video memory full in ...`). Free CDRAM comes from `UnityEngine.PSVita.Diagnostics` (`compat/FreeMemory.cs`).
- A mission briefing prefetches its level (`PrefetchApps`). `MissionBriefing.start` names the level through `Game.loadScriptByLevelID` (`EnterLevel` outside a level), and the atlas loads that level's pages beside the briefing's with `Resources.LoadAsync`; `SpriteBatch.Flush` then draws one a frame, smallest first, as one texel of the ColorTransform material with colour 0 and offset 0 (`NextWarm`), so the blend leaves the screen as it was while the player uploads the page. Draws are paced by `WarmBytesPerSecond` (14 MB/s). The prefetched pages stay wanted through the apps between the briefing and the level (`PrefetchKeep`: story mode's controls tutorial) and become the level's at its Enter. The probe's `-t2d-atlas-sync` loads them with `Resources.Load` instead.
  [Loads without freezes](#loads-without-freezes) has what came after: the next App's pages ahead, the background loads a few at a time, tiles for the big pages.

Measured entering Future War 1 from arcade mode's briefing (17 pages, 80 MB; runs `fw1m`, `r1a`, `r3a`, `r3b`):

| | Frames over 1 s | Worst frame | Level's Enter |
| --- | --- | --- | --- |
| Before: pages loaded at the level's Enter | 2 (5.4 s, 4.6 s in `r1a`; 5.1 s and 1.3 s in `fw1m`) | 5.4 s | `+17 pages`, 72 MB not drawn yet |
| Prefetch with `Resources.Load`, paced | 1 (6.7 s, at the briefing's start) | 6.7 s | `+0 pages, 0.0 not drawn yet` |
| Prefetch with `Resources.LoadAsync` (the default) | 0 | 0.39 s, during the briefing | `+0 pages, 0.0 not drawn yet` |

With `LoadAsync` the loading thread reads the texels, and the draw only uploads them: the 19 pages took the briefing 7.8 s, at 44 to 60 fps, and the level starts at once.
In Future War 1 two of the pages land in main memory (a 32 MB and an 8 MB one, by the log); its frame time is the same, p50 16.7 ms and p90 17.3 to 17.4 ms.
An unloaded page leaves the table at once, but its texture is released two sprite-batch flushes later (`ReleaseUnloaded`, from `SpriteBatch.Flush`): screen sprites queued earlier in the frame still draw with it when the camera renders. Released at once, it made `MaterialPropertyBlock.SetTexture` throw `ArgumentNullException` in the flush, and the frame lost all its sprites: on the console, the first level stayed black.

The player puts a page in CDRAM while it has room, whatever the budget says, so the budget alone doesn't keep CDRAM free: in Cyberdyne Factory (108 MB of pages) the prefetch took it to 0.5 MB.
Two things hold some back:

- The prefetch's `LoadAsync` pages go through `Evict` like the others and count as loading (`asyncBytes`), so the pages that don't fit are logged (`video memory full ... goes to main memory`) and unwanted pages go first.
- A ballast: while pages wait for their first draw, a 2048x1024 ARGB32 render texture (`BallastBytes`, 8 MB, more by the free-memory count) holds CDRAM, so the pages that don't fit beside it go to main memory. It goes two flushes after the last page is drawn, or at once when CDRAM is down to half the reserve outside a prefetch, and is taken only with room for it and the reserve. In a prefetch it stays: released then, it gave back room that the next pages took.

The ballast doesn't hold CDRAM above the reserve in every case: the Factory's prefetch still reaches 0.5 MB before it lets go, and the level then plays with 6.5 to 12.5 MB free.
The Factory's hang at its first frame, first taken for the player running out of CDRAM, was a game loop ([rewind-issues.md](rewind-issues.md), issue 31).

The attract demos (`LazyScenes`) are one scene of 33 pages, 190 MB, since the usage analysis merges the four demos.
Loading them at the demo's Enter froze its first frames for 17 to 19 s and took CDRAM to 0.5 MB and main memory to 55 MB (run `idle1`); now its pages load at their first draw, which the miss log lists.
The first demo, map 10, then draws about 100 MB of them, its worst frame 7.4 s (`idle2t`): splitting the scene by map is the fix still to make.

Pages in main memory cost no frame time that the heartbeat shows: Future War 2, 3 and 4, Freeway, Steel Mill, the story's Future War 1 and the Factory each have 24 to 40 MB of pages in main memory and run at p90 17.2 to 17.5 ms, as the levels with none do ([build.md](build.md#levels)).

### Loads without freezes

With the prefetch, every level still froze for 0.6 to 0.9 s two or three times before it started, though never once it had (`b4s4`, `b5a5`, `b5s2`, `t3s2`; the stalls every 20 s from 70 s on were the probe's screenshots).
The slow-frame log (below) and a `T2D_TRACE` run found three causes:

- The attract cutscene's start, at 12 s (608 to 1,082 ms): its pages loaded with `Resources.Load` while the sound loader's `LoadAsync` clips were still in flight, and a synchronous load waits for those (the Switch alone took 16 to 631 ms), then their first draws read the texels.
- The briefing's start (613 to 688 ms; 375 ms before the prefetch): the briefing's own pages, read at their first draw, and its music starting right after the prefetch had queued all 17 to 22 of the level's `LoadAsync` loads.
- During the briefing, in the levels with a 4096x4096 layer (Future War 2 and 3, Steel Mill, the Factory: 685 to 697 ms each): a `LoadAsync` texture's integration on the main thread. That frame calls nothing of the atlas, and main memory drops by the page's 32 MB in it. The warm draw after it is cheap: the Factory's two 32 MB pages, held back to the level's start, drew in one 193 ms frame (`n2s2`). On this player integration costs about 21 ms a MB, and can't be split.

What does it now, each a field that the probe's `-t2d-set` can turn off for a comparison run (`*:T2DCompat.Atlas.<field>=...`):

- Tiles (the build step): a page larger than 1024 goes to the project as 1024x1024 tiles, `pNNN_K.png`, K row by row from the bottom, with the page's import settings (`atlas/step.py`; `tiles.bytes` holds the tile size; `T2D_ATLAS_PAGES=raw` has none). 22 pages make 190 tiles. Atlas.cs numbers the tiles after the 106 pages, and a scene wants a big page's tiles instead of the page; `PageCount` stays 106. `SpriteBatch.SubmitTiled` cuts a big page's quad along the tiles' edges, one quad per tile it covers; the quad is a parallelogram, so a point's place follows its UV linearly. A flipped frame's UVs (uv - 1) move back into [0, 1] first, and tiles clamp instead of wrapping. `atlas/check/tilecheck.py` drew every frame of the 22 pages that way, from the tile files themselves, in all four flips, at 1:1 and 1.37x: 0 pixels differ at 1:1 and 0.016% at 1.37x, at texel edges where the float rounding falls either way.
- `Ahead`: an App always followed by the same one (`AheadApps`: BBSplash, ReefLogo, Attract, MainMenu, then MissionBriefing) loads and warms the next one's pages from `AheadDelayMs` (1 s) after its own start, so the next App's Enter finds them drawn (`-1 +0 pages, 0.0 not drawn yet`). The log names the next scene by number (`ahead scene 19`): drive.py's `until` reads the whole log for an App's name.
- `AsyncSwitch`: a Switch loads the scenes' own pages with `LoadAsync` too, and `Get` waits for one still loading (`WaitFor`), except the first Switch, the splash's at boot: there its pages integrated in one 0.7 to 2 s frame while the clips loaded.
- `PrefetchInFlight` (2) and `PrefetchDelayMs` (0.5 s): the prefetch's and the next App's loads wait in a queue, smallest first, and start a few at a time, so a synchronous load on the main thread (the briefing's music) waits behind few.
- `SoundLoads.HoldForPages`: the sound loader starts no clip load while pages load (`Atlas.Busy`, joined in `Probe.Install`). At boot a 2 MB page's integration took one ~200 ms frame while the clips loaded, and 40 ms after.
- `WarmAll`: every wanted page not drawn yet gets its invisible draw, not only the prefetch's, `WarmDelayMs` (0.5 s) after a Switch so that the scene's own first draws come first. A page from `Resources.Load` is warmed only under a transition's cover (its first draw reads the texels, about 80 ms a MB).
- `CoverMs` (0.3 s): `Transition.getApp` makes the next App while the fade covers the screen, so for that long after an App's Enter, and a level's after a briefing, its own pages still waiting are warmed all at once, unpaced. Pages over `WarmBigBytes` (8 MB; with tiles, none) warm only then. `CoverWaits` (off) also waits for pages still loading there; see [Story mode and restarts](#story-mode-and-restarts).
- `CoverWarmBytes` (16 MB): under the cover, at most that much is warmed in one flush, the first page whatever its size; the rest go on at the usual pace. A briefing skipped early left the level's whole scene to one flush. 0 lifts the limit.
- A page a miss loaded stays wanted until the next Switch: with CDRAM full, evictions made the Factory load p007, p043 and p065 again every few frames.
- `StickyMisses` (2): a page a miss loaded that many times stays loaded like the sticky fonts, at most `StickyMissMax` (6) of them. The scene data lacks p076 and p083, which nearly every scene draws, and each scene loaded them again in a 200 ms frame.
- `AheadIfApps`, `LevelKeep`, `FirstPages` and `SwitchQueues`: [Story mode and restarts](#story-mode-and-restarts).
- `SlowFrameMs` (100): a frame longer than that logs what the atlas did in it and in the frame before: Switches, loads with their time, waits, finished loads, first draws (`sync` or `async`) and warm draws, and the free memory (`T2D atlas: slow frame ...`).

Measured from boot to a minute of play through the arcade briefing with `-t2d-heartbeat 5 -t2d-stalls 100` and no screenshots but one at 95 s (`work/runs/hitch/run.sh`; the level-select list's 4 and 2, arcade 5). Before is the code before these changes (`b4s4`, `b5a5`, `b5s2`, with `-t2d-stalls 500` and screenshots from 70 s, so the level's worst frames there are screenshots), after is `f6s4`, `f6a5`, `f6s2` (screenshots of `m5s4` and `m5s2` checked the tiles). Times are the worst frame of each part, in ms; after, the Attract's and the briefing's first frames did no atlas work (`slow frame ... [switch app:Attract 0 ms]`, nothing loading): that is the game's own start of them.

| Level | | Attract's start | Briefing's start | Rest of the briefing | Level's start | Level's other frames over 100 ms | Level p50 / p90 | CDRAM / LPDDR free in the level | Pages resident |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| L04 Future War 2 | before | 776 | 417 | 694 | under 500 | screenshots only | 16.7 / 17.1-17.4 | 35.5-38.5 / 162 MB | 22, 74 MB, 32 in main memory |
| | after | 184 (221 later in the attract) | 329 (129 in `t4s4`) | 53 | 136 | none | 16.7 / 17.2-17.4 | 12.5 / 188 MB | 37, 74 MB |
| L08 Freeway | before | 608 | 653 | 184 | under 500 | screenshots only | 16.7 / 17.3-17.5 | 13.5-16.5 / 186 MB | 26, 72 MB |
| | after | 127 (175 later) | under 100 | 88 | under 100 | none (worst 18.8) | 16.7 / 17.3-17.5 | 12.5 / 190 MB | 36, 72 MB |
| L12 Cyberdyne Factory | before | 945 | 635 | 694, 756 | 176 | 3 misses, 175-184 each | 16.7 / 17.2-17.4 | 3.5-12.5 / 154 MB | 21-24, 108-114 MB |
| | after | 193 (151 later) | 156 | 196 | 197 | 3 misses, 216-232 each | 16.6-16.7 / 17.3-17.5 | 6.5-12.5 / 154 MB | 54-57, 108-114 MB |

What is left over 100 ms: the Attract's and the briefing's own first frames, frames of 110 to 220 ms in the attract around 23 to 25 s, when the clips finish loading, the level's first frame under the fade (136 to 197 ms), and the Factory's three misses, pages the scene data lacks. At boot the splash's frames after the first stay under 150 ms; with `AsyncSwitch` on its Switch too, one of them took 0.7 to 2 s (`m5s4`).
Tiles cost draws: the Factory runs 38 to 64 SpriteBatch runs a frame (86 at most) against 8 to 35 (55), Future War 2 18 to 21 against 7 to 9; frame time stays where it was.
Memory is the same bytes in more textures; with tiles the player puts more of a level in CDRAM, as the big layers no longer need 32 MB in one piece (Future War 2: CDRAM 12.5 MB free instead of 38.5, main memory 188 MB instead of 162).

### Story mode and restarts

A playtest of story mode (`work/runs/playtest2`) found the loads still froze, every time:

- The controls tutorial (`LevelControls`), which story mode shows between a level's briefing and the level when `Game.showTutorials` is on, loaded its 10 pages at its Enter, and the cover warmed them in one flush: 1.0 to 1.6 s, on all six levels played.
- After a death, the continue screen unloaded the level's pages, and the restart loaded the level's whole scene again: its first frames took 2.6 to 6 s.
- 3 s into a briefing, its level's portrait, a page of the level's scene, waited for the prefetch: 350 to 430 ms.

What makes a frame slow is a page drawn before its `LoadAsync` is done: its texels are then read at the draw, about 100 ms a MB, as the loading thread reads them (about 8 MB/s; a 2 MB page is done 230 to 290 ms after it starts). Waiting for one request also completes every request started before it.

- `AheadIfApps`: an App followed by another only sometimes loads the next one's pages ahead when a bool field of the App and a static bool are both true at `AheadDelayMs`, read by reflection. `MissionBriefing.start` sets `forceControlsExplanation` for the levels that show the tutorial, so the briefing loads the tutorial's pages when that and `Game.showTutorials` are true, ahead of the level's pages still waiting.
- `LevelKeep`: the continue screen keeps the level's pages, and the restart's Enter only lets the continue screen's go.
- `FirstPages`: the briefing's `previewImage` (its frames' pages, `BitmapData.texpack_tex`) goes first in its loads.
- `CoverWaits` (off): the cover warms only pages that are loaded, and no longer waits for those still loading.
- `SwitchQueues` (on): a Switch queues the scenes' own pages first among the background loads, `PrefetchInFlight` at a time, rather than starting them all; `Get` waits only for those the scene draws.

Measured on the console, story mode from the briefing after 5 s, then a death (`-t2d-invoke ...Game::changeState::3`) and a continue (`work/runs/loadanim/story.sh`; before is `base2` and the playtest, after `fix2`, `fix3h`, `fix5`), worst frames in ms:

| | Before | After |
| --- | --- | --- |
| Briefing after its start | 350 to 430 at about 3 s (playtest, Truck Chase, Hospital, Cyberdyne HQ) | none over 100 (Biker Bar, Hospital) |
| Controls tutorial's Enter | 1282 (Biker Bar); 999 to 1645 in the playtest | none over 100 |
| Level's first frames | 186 (Biker Bar) | 159 to 174 (Biker Bar), 161 and 196 (Hospital, a p083 miss) |
| Continue screen's Enter | 248 + 383 | 545, or 283 + 500 |
| Restart after the continue | 414 + 1510 (Biker Bar); 2577 to 6033 in the playtest | 241 (Biker Bar), 232 + 217 (Hospital) |

A briefing skipped before its prefetch ends still freezes the level's start: the Factory's first frame draws about 20 pages (40 MB) itself. With `SwitchQueues` its second frame took 3984 ms with 50 MB still to load (`skipB`), against 4082 with 44 MB (`skipC`, `SwitchQueues=false`) and 5767 with 62 MB (`skipA`, before).
The continue screen's own 3 pages still load at its Enter, while the game-over sting plays.


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
| R5 | `BitmapData.unityDraw`, `unityDrawClippedWidth`, unless the method calls `T2DCompat.SpriteBatch` | two `ldsfld texpack; ldloc <page>; ldelem Texture2D` each | in place: `ldloc <page>; call Atlas.Get(int); nop` |
| R6 | every `call Transition.getApp(int)` | 2 sites, both in `Transition.update` | after the call: `dup; call Atlas.EnterApp(object)` |
| R6 | `GameMap.loadFile(int, int)` | static, two int parameters | at its start: `ldarg.0; ldarg.1; call Atlas.EnterMap(int, int)` |
| R6 | `Game.loadScriptByLevelID(int)` | static, one int parameter | at its start: `ldarg.0; call Atlas.EnterLevel(int)` |

Each R6 hook is followed by `ldsfld BitmapData.TEXTURE_UNKNOWN; stsfld BitmapData.texused`.
The draw methods skip `SetTexture` when the page index equals `texused`, and after an Enter the page under an index may be a new Texture2D.

R1 also makes the 2,436 stores an older Rewind dropped from those tables irrelevant.
After R4 and R5, nothing reads `BitmapData.texpack` but `initBitmapData`, which allocates it, so that method's Rewind gap around the allocation no longer matters for textures.
The trims (`staticInit10` to `staticInit13`) and the sheet sizes stay, since trims don't change.

In the pipeline, R5 changes nothing: the sprite renderer's R8 ([drawing.md](drawing.md#the-rules)) runs first and replaces both draw methods' bodies with calls into `T2DCompat.SpriteBatch`, which takes the page from `Atlas.Get` itself.
`AtlasDraws` skips a method that calls SpriteBatch, and `DowngradeAssemblies.OptionalPasses` lists it, so its 0 sites don't fail the run.
On Rewind's output alone, as `check/check_rules.py` runs it, R5 still makes its four edits.

R7, which was planned here as a lazy bake of a frame's four meshes on its first draw, is written as part of the sprite renderer ([drawing.md](drawing.md#the-rules)).
It does less: `CreateMultiMeshFrames` returns an array of `tframes.uvx.Length * 4` nulls, and no mesh is baked at all, since R8's SpriteBatch builds each quad from the frame fields.
That takes away the 150,000 Mesh objects `Sprites.loadFromID(-1)` made at start.

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
- `atlas_overlay` reruns `repack.py` (with `--raw` when `T2D_ATLAS_PAGES=raw`) only when the recovered assembly, the packs, `scenes.json` or the atlas scripts change, by a stamp in `work/atlas`.
- It reads Rewind's assembly, not the rewritten one, because R1 replaces the very tables the repacker reads.
- `pipeline.compat_sources()` adds `atlas/compat/*.cs` to T2DCompat.
- `pipeline.REWRITER_SOURCES` adds `atlas/rewriter/AtlasPatches.cs`.
- `DowngradeAssemblies.Passes` adds, after `ListRewindGaps`, `new Pass("atlas", Scope.Game, AtlasTables)`, then `AtlasSplash`, `AtlasPackLoads`, `AtlasDraws` and `AtlasSceneHooks`. They go in the list itself, not in an array from AtlasPatches.cs, because the order of static initialisers across partial files is not defined.

The atlas passes must run after any pass that replaces a whole method body among their targets.
The sprite renderer's R8 is one: it replaces `unityDraw` and `unityDrawClippedWidth` before the atlas passes, and `AtlasDraws` then skips them.
`GameMap.loadFile` has 3 Rewind gap sites; if a repair rewrites it, `AtlasSceneHooks` must still find its pattern, or it throws and says which.

With this wiring, `uv run tools/downgrade/pipeline.py project` runs through in 43 to 52 seconds.
The rewriter's log shows `atlas: AtlasTables=6 AtlasSplash=3 AtlasPackLoads=1 AtlasDraws=4 AtlasSceneHooks=4`; with the sprite renderer's rules in the pipeline, `AtlasDraws=0`.
Its problem count stays at 245, none of them in a method the rules change.
The project gets `Assets/Resources/atlas` with 106 pages, the two tables and their metas, and no `texturepack` folder.
The pages are PNGs; `T2D_ATLAS_PAGES=raw` makes them raw `.bytes` TextAssets instead.

## What is untested

The PNG pages run on the console in every level played so far ([build.md](build.md#on-the-console)).

- Raw pages, now only the fallback, cost a managed copy of each page while it loads: 2 MB for a normal page, 32 MB for a 4096×4096 one. The managed heap grows to the largest of these and doesn't shrink.
- Load time per page for raw pages on the console; PNG pages are measured above.
- Raw pages aren't tiled, so a 4096x4096 one still integrates in one 0.7 s frame.
- The tiles' edges were checked on the console only by eye, in screenshots of the Factory and Future War 2 (`m5s2`, `m5s4`), and by `atlas/check/tilecheck.py` off it.
- Whether the Vita's GPU wraps non-power-of-two textures. All pages are powers of two, which costs 30% on the oversize layers; NPOT sizes would bring the Factory to about 90 MB.
- The scene data comes from static analysis. About 250 images, a quarter of a pack, are reachable only in ways it can't follow, through arithmetic on ids, ids in int[] tables, native jump tables that inline a constructor. Those draws miss and load at once. Play through every scene with the miss log on and feed `MissReport` back into `scenes.json`.
  In play each miss is one frame of 190 to 250 ms (the page's `Resources.Load` and its synchronous first draw): the "occasional stutters in Sarah levels" of the first playtest were these. `scene_sets.py`'s `EXTRA_IMAGES` adds the images found so far: the bullet hit spark (every level), `BULLET_H` and the hazmat guys (Research, Factory), the desert crates' debris. A miss logs only its page; to find the image, a scratch build can log the SpriteBatch source's row in the frame table when `Atlas.Misses` goes up across `Atlas.Get` (as runs `d0` to `d2` did). With them, 7 minutes of scripted play in each of Desert Town, Cyberdyne Research and the Factory had 0 misses, against 2, 1 and 3 before (runs `f0` to `f2` against `d0` to `d2`); what is left over 100 ms there is the level's first frame, the garbage collections (one every 2 to 3 minutes, 110 to 310 ms) and a frame or two of 140 to 210 ms with no atlas work.
  Round 4 added a Future War bike's parts (`FUTURE_BIKE_ENGINE`, which missed in Future War 1 in the playtest and in runs `f3`, `g3`, and its exhaust, visor and debris) to Future War 1. The playtest's other misses (an image on `p042` in Research, one on `p083` in Research, the Factory and Future War 2) didn't come back in scripted play, even with a scratch build that logged every image drawn from a page outside its scene's set (runs `er`, `ef`, `ew`), so they are still to find.
  Still missing, by page in the playtest's build: one image on `p042` in Cyberdyne Research, and on `p083` (146 images no scene claims: debris, the Factory boss's text, ...) one in Research, the Factory, Future War 1 and 2.
- Tanker Chase has no hook of its own; level 8 loads Freeway Chase's and Tanker Chase's pages together, 72 MB.
- `imageclassname` row 57 is 1 where IMAGE_TROPHY_ARRAY_21 should be, so `loadImage(57)` returns null in the original too. Its frames go to the unreached pages.
- The repacker reads one release of the game. Another release with other frames would need its own `scenes.json` check; unknown image names are reported, not fatal.
