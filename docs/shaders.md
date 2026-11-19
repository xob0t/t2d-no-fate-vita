# Shaders

The game has three shaders of its own: `Unlit/CRTShader`, `Unlit/NormalShader` and `Unlit/ColorTransformShader`.
It ships them compiled for Direct3D 11 only, so AssetRipper exports them as placeholders (`DummyShaderTextExporter`), which draw the texture and nothing else.
`tools/downgrade/shaders` holds the three written again for Unity 2018.2, from the game's programs, and they compile for the PS Vita and Direct3D 11.
The pipeline puts them into the project in place of the placeholders ([below](#in-the-pipeline)).

## Where they come from

The three are in the game's `resources.assets` (path ids 1, 2 and 3), each with one subshader, one pass and one Direct3D 11 program per stage (`vs_4_0`, `ps_4_0`).
None has keywords of its own: the only keywords are Unity's four stereo keywords, which no program uses.
Two tools read them, and they agree.

- huntdown-vita's patched USCSandbox (`tools/external/USCSandbox`, built there) decompiles them to ShaderLab with HLSL, given `--version 6000.0.60f1`.
- `tools/downgrade/shaders/extract.py` reads the shaders with UnityPy, writes each pass's state, properties and keywords as JSON, and the DXBC with its disassembly by Windows' `d3dcompiler_47.dll`.

```sh
python -I tools/downgrade/shaders/extract.py "C:/Program Files (x86)/Steam/steamapps/common/Terminator 2D NO FATE/Terminator 2D NO FATE_Data" work/shaders/extract
```

It only reads the game; its output is the game's compiled programs, so it stays in `work/`.
USCSandbox leaves out states that are at their defaults, so the culling and depth test below come from `extract.py`.
The game's color space is gamma (`m_ActiveColorSpace: 0`), as the PS Vita's is, so colors reach the shaders as they are set.

## What each one does

### Unlit/NormalShader

Properties: `_MainTex` ("Base (RGB) Trans (A)", white).
Tags: `IGNOREPROJECTOR`, `QUEUE` Transparent, `RenderType` Transparent, on the subshader and the pass.
State: `Blend SrcAlpha OneMinusSrcAlpha, One One`, `ZWrite Off`, and the defaults: `Cull Back`, `ZTest LEqual`, `ZClip On`.
The vertex program transforms the position's xyz by `unity_ObjectToWorld` and `unity_MatrixVP` (w taken as 1) and passes the UV on as it is, without `_MainTex_ST`.
The fragment program is one texture read: `o0 = tex2D(_MainTex, uv)`.

The game draws almost everything with it.
`BitmapData` keeps it as `main_material` (`Shader.Find("Unlit/NormalShader")`) and draws every sprite without a color transform with it, the texture set through a `MaterialPropertyBlock`.
`UnityMain.OnRenderImage` draws the 398x224 frame with it, and with the CRT option on, the frame and `crt_layer` (in the CRTShader section).

### Unlit/ColorTransformShader

Properties: `_MainTex`, `_Color` ("Main Color", a Color, default 0) and `_Coloff` ("Color Offsets", a Vector, default 0).
Tags and state: the same as NormalShader's.
The vertex program is NormalShader's.
The fragment program is Flash's ColorTransform: `o0 = tex2D(_MainTex, uv) * _Color + _Coloff`, all four channels.

`BitmapData` keeps it as `ct_material`.
`_Color` is a `ColorTransform`'s multipliers, and `_Coloff` its offsets divided by 255, so -1 to 1 (`ColorTransform.calcColorOffset`).
`fillRect` uses it with `_Color` 0 and `_Coloff` the fill color, so the texture doesn't matter.
The game writes the two into the material and into the property block, and skips both when they haven't changed.

### Unlit/CRTShader

Properties: `_MainTex` ("Texture") and `_OutputSize` ("OutputSize", a Vector, default (398, 224, 0, 0)).
No tags.
State: `ZTest Always`, `ZWrite Off`, `Cull Off`, no blending (the output's alpha is 1).
The vertex program is NormalShader's.

The fragment program draws a 398x224 source as CRT scanlines over `_OutputSize` pixels.
The source size is in the program, not a property.
With `p` the output pixel (`uv * _OutputSize`) and `s` the same point in source pixels (`p * (398, 224) / _OutputSize`):

1. The first of four source columns is `x0 = floor(s.x - 1.5) + 0.5` and the scanline above is `y0 = floor(s.y - 0.5) + 0.5`, both at texel centres; `dx = s.x - x0` is in [1, 2) and `dy = s.y - y0` in [0, 1).
2. Across a line, columns `x0` to `x0 + 3` weigh `exp2(-4 (dx - k)^2)`, a Gaussian about 0.42 pixels wide, normalised by the four weights' sum.
3. Between lines, the line above weighs `0.5 + 0.5 cos(2 pi min(0.7 dy, 0.5))` and the line below the same of `1 - dy`: a raised cosine that is 1 on the line and 0 from 0.714 lines away, so the dark gap between lines is about 0.57 lines.
4. That is eight texture reads, two lines of four.
5. The aperture mask: with `f = frac(p.x / 3)`, the channel of the output column's place in its group of three keeps full strength and the other two are halved: (1, 1/2, 1/2), (1/2, 1, 1/2), (1/2, 1/2, 1).
6. The brightness: with `m` the brightest channel (at least 2^-24), the color becomes `c / m * m / (0.4878 m + 0.5122)`, which maps `m` to `41 m / (20 m + 21)`: 1.32 times brighter at 0.5, the same at 1, about what the mask took.

`BitmapData` makes the material (`crt_material`) and sets `_OutputSize` to the main camera's pixel size, and `UnityMain.updateCRTSize` sets it again (from `ScreenStuff`, `FixedUpdate` and the CRT setting).
Nothing draws with it.
In Rewind's recovered code (which has no gap in `OnRenderImage`), the CRT option (`UnityMain.crt_shader`, from `Main.crtFilterOn`) does something else:

1. `Graphics.Blit` stretches the 398x224 frame (`cam_rt`, point-filtered) into a 398x896 temporary (`crt_4hscale`, from `OnPreRender`): each line four times.
2. NormalShader draws that, bilinear-filtered, over the screen.
3. NormalShader draws `crt_layer` over it: a 398x896 black image whose rows repeat alpha 6, 0, 11 and 33 (of 255), a light scanline every four rows.

So the game's CRT look costs two NormalShader passes at the screen's size, and CRTShader is left over from an earlier effect.
Both are recreated: the port can keep the game's path as it is, and CRTShader is there if a material asks for it.

## The replacements

`tools/downgrade/shaders/NormalShader.shader`, `ColorTransformShader.shader` and `CRTShader.shader` are ShaderLab with Cg, written from the disassembly, with the same names, properties, tags and pass state.
The vertex programs use `UnityObjectToClipPos(v.vertex.xyz)`, which is the original's transform, and pass the UV on without `_MainTex_ST`.
ColorTransformShader's output is `saturate`d: Direct3D 11 clamps a UNORM target's output to [0, 1] before blending, so an alpha past 1 blends as 1, and `saturate` makes the PS Vita do the same.
All of the game's targets are UNORM (ARGB32 render textures and the back buffer), so on Direct3D 11 it changes nothing.
CRTShader's faithful variant follows the fragment program step by step, in `float`.
Cg reserves `row` and `column`, which Direct3D 11's compiler accepts, so the PS Vita build is the one that catches such names.

### T2D_CRT_FAST

CRTShader has a second variant behind the global keyword `T2D_CRT_FAST` (`#pragma multi_compile __ T2D_CRT_FAST`), which `Shader.EnableKeyword("T2D_CRT_FAST")` switches on for every material.
Without the keyword the faithful variant draws.
The fast one is how the effect could run on the PS Vita, if a port draws with CRTShader at all; the game doesn't.
On the console it takes 6 ms per frame at 960x544, against the faithful variant's 16 ms ([Cost](#cost)).
It differs in these ways:

- Across a line it weighs only the two nearest columns, as the Gaussian does, and leaves out the outer two. At a column's centre those two are 1/16 of the weight between them.
- It reads the two columns on both lines with one bilinear fetch. The fetch lands between the columns where the Gaussian splits its weight (`1 / (1 + exp2(4 - 8 d))` to the right one), and between the lines where the beams split theirs. Then the beams' sum scales it. That is the same sum of four texels, with the filter's own weights.
- So it needs a bilinear-filtered `_MainTex`. On a point-filtered one it draws the nearest texel, without the beam's blend.
- The raised cosine is a fitted odd quintic, `0.5 - t (1.5703204 + t^2 (-2.5684607 + 1.1498051 t^2))` with `t = min(1.4 d, 1) - 0.5`, within 0.00004 of it.
- The mask is arithmetic, not a branch, and the source and mask coordinates are interpolated from the vertex program.
- The brightness step is `c / (0.4878 m + 0.5122)`, which is the original's expression with `m` cancelled, in half precision.

## Verification

`tools/downgrade/shaders/test` checks the shaders against the originals:

```sh
python -I tools/downgrade/shaders/test/run.py all   # project, render (Direct3D 11), reference, compare, sheet, PS Vita build
python ~/.t3/projects/butcher-vita/tools/downgrade/lock.py run vita-device "t2d: shaders" --wait 3600 -- \
  bash -c "python -I tools/downgrade/shaders/test/device.py install && python -I tools/downgrade/shaders/test/device.py run"
python -I tools/downgrade/shaders/test/run.py compare vita
python -I tools/downgrade/shaders/test/run.py sheet vita
```

`run.py project` makes a Unity 2018.2.19f1 project in `work/shaders/test/project`.
It holds the three shaders, the inputs and `cases.txt`, and the test app (`unity/ShaderTest.cs`, `unity/ShaderTestBuild.cs`).
The inputs are 398x224:

- `synthetic`: flat blocks, one-pixel lines and dots, a gradient and noise.
- `rgba`: noise with every alpha from 0 to 255.
- `pixelart`: a crop of the game's first atlas, taken from the AssetRipper export in `work/`.

The 24 cases cover:

- CRTShader at 960x544 (the PS Vita's screen), 967x544 (2.43 times the canvas), 1920x1080 and 398x224, on both opaque inputs, faithful on a point-filtered source and fast on a bilinear one, plus the faithful variant on a bilinear source.
- NormalShader on `rgba` and `pixelart`, blended over a cleared background.
- ColorTransformShader with five transforms: identity, a tint with negative offsets, `fillRect`'s, a white flash, and an offset that takes alpha past 1.

`run.py render` has the editor render every case with Direct3D 11, in batch mode with its graphics device, as `work/shaders/test/d3d11/<case>.png`.
`run.py reference` computes the same cases from the originals' DXBC, ported to NumPy one instruction per line in float32, including the blend for the two blended shaders.
That checks the new shaders against the game's programs, not against themselves.
The fast variant also has a model of its own math, to check that it is written as designed.
`run.py compare` writes `work/shaders/test/report-<renders>.txt`, with difference images in `diff-<renders>/`.
`run.py sheet` writes `compare-<renders>.png`, a crop of the pixel-art CRT case: the input stretched, the original, the faithful and fast renders, and the faithful render's difference times 32.

### Direct3D 11

In the editor, in levels of 255:

| Cases | Worst difference | Pixels more than 1 level off |
| --- | --- | --- |
| CRTShader, faithful, 9 cases | 1 | 0 |
| NormalShader, 2 cases | 0 | 0 |
| ColorTransformShader, 5 cases | 1 | 0 |
| CRTShader, fast, against its own model, 6 cases at screen sizes | 1 | 0 |

The fast variant against the original:

| Input | Size | Worst | Mean | Pixels more than 8 levels off | PSNR |
| --- | --- | --- | --- | --- | --- |
| pixelart | 960x544 | 16 | 0.11 | 0.03% | 55.5 dB |
| synthetic | 960x544 | 25 | 0.31 | 1.06% | 46.5 dB |
| pixelart | 1920x1080 | 20 | 0.11 | 0.02% | 55.6 dB |

The large differences are at one-pixel features, where the two left-out columns carry real color.
At 398x224, one output pixel per texel, every sample lands on a texel's centre, and which neighbour the fast variant keeps flips with the interpolator's rounding.
There it is up to 28 levels from its own model; that size isn't a use of a CRT effect.

The background's alpha is 0.4, not 0.5, because Direct3D 11 clears 0.5 to 127, which the reference can't predict.

### The PS Vita

`run.py vita` builds the test app as a PC Hosted PS Vita player (title `TDSH00001`) with the installed editor.
That build compiles the shaders with the PS Vita module's psp2cgc, as a game build does, and fails on any shader error.
Unity's build report calls a build with shader errors "Succeeded" and leaves those programs out, so the script counts the errors itself.
It packages `work/shaders/test/t2d-shader-test.vpk`, takes the GXP programs out of the built data and runs `psp2shaderperf` on each (`work/shaders/test/gxp`).

`device.py` installs the VPK and runs it on the console, in the shared `vita-device` lock.
The app renders the cases with the PS Vita's GPU, then times the shaders, and `device.py` fetches the renders into `work/shaders/test/vita` and deletes them on the console.

It ran on 2026-11-19 at 444/222 MHz (CPU/GPU, set with a copy of butcher-vita's ButcherClock plugin).
Its renders against the same references, in levels of 255 (`work/shaders/test/report-vita.txt`, `compare-vita.png`):

| Cases | Worst | Pixels more than 1 level off |
| --- | --- | --- |
| NormalShader, 2 cases | 0 | 0 |
| ColorTransformShader, 5 cases | 1 | 0 |
| CRTShader, faithful, point-filtered source, 960x544 and 967x544, 4 cases | 3 | 0.5% at most (0.004% on pixel art) |
| CRTShader, faithful, point-filtered source, 398x224, 2 cases | 1 | 0 |
| CRTShader, faithful, bilinear source, 960x544 | 14 | 22%, 0.05% more than 8 |
| CRTShader, fast, against its own model, 960x544 and 967x544 | 14 | 3.5% (pixel art) and 22% (synthetic) |
| CRTShader, fast, against the original, pixel art, 960x544 | 16 | 6%, mean 0.23, 52.7 dB |

The faithful variant on a point-filtered source, which is what it would get in the game (`cam_rt` is point-filtered), is within 3 levels.
On a bilinear source the PS Vita's texture unit doesn't return a texel at its centre: a least-squares fit of the differences gives about 1.9% of the left neighbour and 1.4% of the one below, a sample point about 1/64 texel low.
The fast variant depends on bilinear filtering, so it carries the same shift, and differs from its model by as much as the faithful variant on a bilinear source.
On pixel art its difference from the original is small (mean 0.23 levels); it shows at one-pixel lines.

Two things about the test app, not the shaders:

- `ReadPixels` from an ARGB32 render texture into an RGBA32 texture keeps the render texture's byte order on the PS Vita, so the PNGs hold A, R, G, B as R, G, B, A. `device.py` keeps them in `vita/raw` and puts the channels back.
- The 1920x1080 cases came back corrupted (the right picture squeezed into the top half, garbage below), so the app now skips cases larger than the screen there. That change is built but has not run on the console.

## Cost

`psp2shaderperf`'s estimate per fragment (the vertex programs are 6 or 7 cycles, for four vertices):

| Program | Cycles | Mode | Texture reads | Temporaries |
| --- | --- | --- | --- | --- |
| NormalShader | 0 | parallel | 1, not dependent | |
| ColorTransformShader | 5 | parallel | 1, not dependent | |
| CRTShader, faithful | 87 | per-instance | 8, dependent | 46 |
| CRTShader, `T2D_CRT_FAST` | 36 | parallel | 1, dependent | 8 |

NormalShader's and ColorTransformShader's reads are non-dependent: the UV comes straight from the vertex program, so the hardware fetches the texel before the shader runs.
CRTShader's faithful program needs so many temporaries that it runs per instance, and its eight reads come from computed coordinates.

The console's times, from frames with 0, 2 and 6 full-screen passes at 960x544, each pass into its own render texture so the GPU's hidden surface removal can't drop one (vsync off, 240 frames each, `log.txt` in `work/shaders/test/vita`):

| Shader | GPU time per 960x544 pass |
| --- | --- |
| NormalShader, 398x224 source | 1.54 ms |
| CRTShader, `T2D_CRT_FAST`, bilinear source | 6.05 ms |
| CRTShader, faithful | 15.69 ms |

The times include each pass's render-texture switch (a load and a store of 2 MB), which is in NormalShader's 1.54 ms too.
The frame without passes took 2.76 ms, and the spread between frames was under 0.4 ms.

The faithful CRTShader takes almost a whole 60 fps frame (16.7 ms) on its own at the PS Vita's resolution, so it isn't usable there.
`T2D_CRT_FAST` is 2.6 times cheaper but still 6 ms, more than a third of a frame.
The game's own CRT option is two NormalShader-like passes, about 3 ms by the table (the second one blended and the first reading a 398x896 texture, neither measured as such).
That is the path the port should keep.

## In the pipeline

The placeholders are `Assets/Resources/crtshader.shader`, `normalshader.shader` and `colortransformshader.shader` in the AssetRipper export, each with a `.meta`.
The pipeline should replace each placeholder's text with the file of the same shader name from `tools/downgrade/shaders`, and keep its `.meta`, as huntdown-vita's `shader_rules.shader_overlay` does:

1. For every `.shader` under the export's `Assets` that has `DummyShaderTextExporter` in it, read its name from the `Shader "..."` line.
2. Take `tools/downgrade/shaders/<last part of the name>.shader` (`Unlit/CRTShader` is `CRTShader.shader`), and check that its own `Shader "..."` line has the same name.
3. Write it over the placeholder, under the placeholder's file name, and leave the `.meta` (its GUID) alone.
4. Stop when a placeholder has no file, or a file no placeholder, so a new release of the game can't pass unnoticed.

Matching by name, not by GUID, matters here: AssetRipper's GUIDs change between exports from different input folders (`docs/assessment.md`).
No material refers to these shaders; the game finds them with `Shader.Find`, which a player can only do for shaders it includes.
They are in `Resources`, which includes them, so they must stay there (or go into the graphics settings' always-included shaders).
Both CRTShader variants are compiled in (`multi_compile`).

The game's own CRT path needs NormalShader only.
CRTShader's fast variant matters only if a port draws with CRTShader; then `T2D_CRT_FAST` should be switched on (in a compat shim or `UnityMain`'s start-up code), and its source given bilinear filtering.

`extract.py` and the test read the game and write only under `work/`; nothing from the game is committed.
