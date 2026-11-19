# Building

The pipeline turns your copy of Terminator 2D: NO FATE into a Unity 2018.2 project, and builds a PS Vita app from it without the Unity editor.
The game ships only as IL2CPP native code, so its code is first recovered with [Rewind](https://github.com/ratman-bit/Rewind-IL2CPP-Decompiler).
The pipeline only reads the game: it never runs its executables and never writes into its folder.
It writes the recovered code, the export and the Unity project to `work/`, the player and the VPK to `builds/`, and the logs to `artifacts/`.
Prose here is one sentence per line, so a diff shows only the sentences that changed.

**The port doesn't play yet.**
The pipeline runs end to end, but the code it builds still has Rewind's gaps and references Unity 2018.2 doesn't have; [Where it stands](#where-it-stands) has the details.

You need Windows 10 or 11 on x64, Git, and the game from Steam.
With [uv](https://docs.astral.sh/uv/) installed, the short version is:

```powershell
uv run build.py                 # the PS Vita app: builds/t2d-2018.2.19f1-vita.vpk
```

`build.py` runs the setup (step 1), then the pipeline's stages in order (step 2).
After a failure or a change, run it again.
It skips what is installed and what is up to date.

## 1. Set up

```powershell
uv run tools/setup.py
```

`build.py` runs setup itself.
Setup can run again at any time: it installs what is missing and skips the rest.
In this order, it:

- Runs `uv sync` for the Python packages.
- Downloads the Unity 2018.2.19f1 editor's and PS Vita module's installers and extracts what the pipeline and the builder read (CGIncludes, the engine and class libraries, IL2CPP, the built-in resources, the PS Vita player), about 1.1 GB, into `tools/external/unity-2018.2.19f1`, with 7-Zip: an installed one, or 7-Zip's own package from GitHub. Nothing of Unity is installed or run, and no Unity license is needed.
- Installs the current Visual C++ runtime when Windows has an older one: the builder's texture encoder crashes in the 2015 runtime.
- Downloads the .NET 10 SDK 10.0.404 into `tools/external/dotnet10`.
- Clones Rewind at commit `ea8e305` into `tools/external/rewind`, applies this repository's patches (`tools/rewind/patches/*.patch`, in name order, with `git apply`), and builds its command line with the .NET 10 SDK.
  The 32 patches fix most of the issues in [rewind-issues.md](rewind-issues.md); `tools/rewind/patches/NOTICE.md` lists them, as Apache-2.0 asks for modified files. The build restores from nuget.org whatever the machine's NuGet configuration says. A change of the pinned commit or of the patches builds it again.
- Downloads AssetRipper 2.0.0 into `tools/external/assetripper-2.0.0`, the .NET 8 SDK 8.0.425 into `tools/external/dotnet` (for the builder's TypeModelGen), and Mono.Cecil 0.10.0-beta6 from NuGet (for the rewriter).
- Finds [the game](#the-game).
- Finds or installs [the PS Vita SDK](#the-ps-vita-sdk).
- Prints [the doctor's checklist](#checking-the-setup).

Setup checks every download against a pinned hash (SHA-512 for the .NET SDKs, from Microsoft's release metadata; SHA-1 for archive.org's files, the only one it lists; SHA-256 for the rest), and extracts files from a Unity installer only if it carries Unity Technologies' Authenticode signature; the Visual C++ Redistributable must carry Microsoft's.
Rewind is pinned by its Git commit.

| Option | Effect |
| --- | --- |
| `--game <folder>` | Set `T2D_GAME` to this copy of the game. |
| `--vita-sdk <path>` | Use your own PS Vita SDK, an archive or a folder, instead of the download. |
| `--vita-sdk-dir <dir>` | Extract the archive or copy the folder here. The default for an archive is `C:\vitasdk`; a folder is used where it is. |
| `--vita-host <address>` | Set `T2D_VITA_HOST`, the console's address. |
| `--yes`, `-y` | Accept every offer (setting a variable for a game or SDK it found). |
| `--dry-run` | Report what setup would do, and change nothing. |
| `--keep-downloads` | Keep the installers and archives in `work/installers`. |

To download and check installers without installing them, for another machine say:

```powershell
uv run tools/setup.py download editor vita assetripper dotnet dotnet10 cecil --to <folder>
```

### The game

The pipeline reads the game from `T2D_GAME` if it is set, else from `C:\Program Files (x86)\Steam\steamapps\common\Terminator 2D NO FATE`.
Setup also searches your other Steam libraries, and offers to set `T2D_GAME` if it finds the game in one.

The rules are written for the Steam release, build 24040403, made with Unity 6000.0.60f1.
The doctor tells it from other releases by the SHA-256 of `GameAssembly.dll` and of `Terminator 2D NO FATE_Data/il2cpp_data/Metadata/global-metadata.dat`, since an IL2CPP build has no `Assembly-CSharp.dll`.
Another release is reported as optional: Rewind may well recover it, but nothing was checked on it.

### The PS Vita SDK

Setup looks for an SDK in `SCE_PSP2_SDK_DIR`, in `C:\vitasdk` and in the SCE folder.
If it finds none, it downloads `PSVITA.zip` (350 MB) from archive.org's [copy of the SDK 3.570 DevNet release](https://archive.org/details/psvita-sdk-3.570-devnet-full), extracts it to `C:\vitasdk\PSVITA\sdk`, and applies Sony's `psp2cgc` and `libc` patches from the same item, which bring it to 3.570.071.
The builder uses the SDK alone; Target Manager Server and Publishing Tools aren't needed.
Setup sets `SCE_PSP2_SDK_DIR` for your user.

### Checking the setup

```powershell
uv run tools/setup.py doctor
```

The doctor prints a checklist of what the build needs, with the command that fixes each missing item, and the free space on the repository's drive.
It changes nothing, and exits with code 1 if a required item is missing.
Items marked `optional` don't stop a build: Mono.Cecil (fetched on first use), the signature library, the console's address, and a release of the game other than Steam build 24040403.

## 2. Build

```powershell
uv run build.py                       # the PS Vita app
uv run build.py --dry-run             # print the setup command and the stages, run nothing
```

`build.py` runs `tools/setup.py`, then these stages of `tools/downgrade/pipeline.py`:

1. `recover`: Rewind rebuilds the game's 44 assemblies from `GameAssembly.dll` and `global-metadata.dat` into `work/recover/assemblies`, assemblies only, no C# source. It names IL2CPP's runtime functions with the signature library for the game's Unity, `tools/rewind/signatures/unity-6000.0.60f1-windows-x64.sig`; without that file it falls back on the nearest library Rewind ships, which recovers less.
   The library was built from four test builds of 6000.0.60f1 (Master, Release, Master for size, Master without incremental GC; MSVC 14.50) with patch 0002's helper rules, so it also names `Array::NewSpecific`, `Object::New` and `Object::NewAllocSpecific`.
   Checked against the test builds' PDB symbols it makes no wrong identification, on each build and on each build left out of the library. `build.py` skips the stage while the output comes from the same game files, Rewind build and library.
2. `rip`: AssetRipper exports the game as a 2018.2 project into `work/rip-2018.2.19f1`. AssetRipper has no way to take recovered assemblies for an IL2CPP game, so it reads a Mono-layout copy of the game in `work/rip-input`: the game's data files, Rewind's assemblies in `Managed/` and an empty executable. The export then keeps the method bodies. That folder's path stays the same from run to run, since the export's `.meta` GUIDs depend on what AssetRipper loads. `build.py` skips the stage while the export comes from the same game files, recovered assemblies, AssetRipper and settings.
3. `project`: assembles the Unity project in `work/t2d-2018.2.19f1-vita`. It runs the code stage (below), puts the rewritten assemblies and T2DCompat into `Assets/Plugins` with the exported assemblies' `.meta` files, and applies the asset rules (`asset_rules.py`).
4. `build`: the editor-free builder (`tools/builder`, from Butcher's port) writes the PS Vita player into `builds/t2d-2018.2.19f1-vita`: the player's data, the code module from UnityLinker and IL2CPP, and the files Unity's build copies.
5. `package`: makes `builds/t2d-2018.2.19f1-vita.vpk` with the title ID `TNOF00001`, with LiveArea art from the game's Steam store images (`livearea.py`).

`build.py` holds the `vita-build` lock from `tools/downgrade/lock.py` for the whole run.
If another job runs the pipeline, it waits.

| Option | Effect |
| --- | --- |
| `--game <folder>` | The game's folder, if it isn't in Steam's default folder. Setup saves it in `T2D_GAME`. |
| `--vita-sdk <path>` | Passed to setup. |
| `--skip-setup` | Go straight to the pipeline. |
| `--from <stage>` | Start at `recover`, `rip`, `project`, `build` or `package`, when the earlier stages are done. |
| `--wait <seconds>` | How long to wait for the build lock. The default is 6 hours. |
| `--dry-run` | Print the setup command and the stages, and run nothing. |

### Running the stages one at a time

```powershell
uv run tools/downgrade/pipeline.py recover    # always runs Rewind again when run alone
uv run tools/downgrade/pipeline.py rip        # always exports again when run alone
uv run tools/downgrade/pipeline.py code       # rewrite the assemblies, report what doesn't resolve
uv run tools/downgrade/pipeline.py project
uv run tools/downgrade/pipeline.py build      # refuses while code left unresolved references; --anyway builds the data
uv run tools/downgrade/pipeline.py package
uv run tools/builder/il2cpp.py                # the builder's code step alone (UnityLinker, IL2CPP)
```

Other jobs on the machine share `work/`, `builds/` and `artifacts/`, so run each stage under the build lock:

```powershell
uv run tools/downgrade/lock.py run vita-build "me: what for" --wait 1800 -- uv run tools/downgrade/pipeline.py project
```

The `vita-device` lock is the console's, and it is one file for every port on the machine (`~/.t3/vita-device.lock`), so a job of another port's and one of this port's never use the console at once.
The build locks are this project's own, in `work/`.

### The code stage

`code` (which `project` runs too) rewrites Rewind's `Assembly-CSharp`, `InControl` and `Steamworks` for Unity 2018.2 with the rewriter from Butcher's port (`DowngradeAssemblies.cs` and `rewriter/`, built with the .NET Framework's C# compiler against Mono.Cecil 0.10.0-beta6), into `work/assemblies-2018.2.19f1-vita`.
`Assembly-CSharp` becomes `Recovered.T2D`, since Unity reserves the name for project scripts.
References to the engine and class libraries go to Unity 2018.2's; what 2018.2 lacks goes to T2DCompat by name (`Compat2018.cs` describes the convention).
T2DCompat is compiled from `Compat2018.cs`, `Unity6Shims.cs` and every `.cs` file in `tools/downgrade/compat/`, the platform layer, which is written apart.
`Unity6Shims.cs` holds the easy items of the assessment's API gaps: `Mesh.RecalculateNormals(MeshUpdateFlags)`, `Object.FindObjectsByType`, `Resolution.refreshRateRatio`, the `Texture2D` constructor with a `MipmapLimitDescriptor`, and `BitConverter.SingleToInt32Bits`.
`InspectorNameAttribute` is dropped, like every attribute 2018.2 doesn't have, and InControl's references to `Input` and `Touch` move from Unity 6's `InputLegacyModule` to 2018.2's `CoreModule`.
Steamworks.NET is then stubbed: its methods do nothing and return defaults, so nothing calls `steam_api64.dll`.

In `Recovered.T2D`, the rewriter (`Rewrite` in `DowngradeAssemblies.cs`) changes the code in this order:

1. The repair engine, rules then the family patterns ([repairs](repairs.md)), on Rewind's IL as Rewind wrote it.
2. The platform layer's pass ([platform](platform.md)).
3. The fold-back passes ([foldback](../tools/downgrade/foldback/README.md)).
4. The retargeting of type references to 2018.2 and T2DCompat.
5. `Passes`, in list order: the member redirects; the sprite renderer's R8, R7, R9 ([drawing](drawing.md)); `TypeGapValues`, then `ListRewindGaps`, which counts the gaps left; the texture rules R1 to R6 ([textures](textures.md)), R5 skipping the methods R8 replaced; the sound rules A1 and A2 ([audio](audio.md)).

R3 and A1 both change `AssetsEmbed.staticInit`, each only its own instructions; the code stage writes the same assembly with the atlas passes before or after the audio passes, and with `TypeGapValues` before or after the sprite renderer's.

The stage doesn't repair the code.
It reports, in its log and in `artifacts/downgrade/code-report.txt`:

- what doesn't resolve against 2018.2 (`UNRESOLVED` lines, counted by assembly and kind),
- rules that matched nothing (`NO MATCH`),
- every method that still has a Rewind marker (`__Rewind.Opaque` or `__Rewind.Effect`), with its number of marker sites: such a method throws `NotSupportedException` when it reaches one,
- the methods Rewind notes as incomplete without a marker ([rewind-issues.md](rewind-issues.md), issue 6).

The rewriter exits with 3 while anything is unresolved; `code` passes that on, and `project` goes on with a warning, so the later stages can be tried.

### The asset rules

The export is of a Unity 6 game serialized for 2018.2, and `asset_rules.py` fixes what it still carries of Unity 6:

- `ProjectSettings.asset` is PlayerSettings serializedVersion 28; it gets 2018.2's, 15. The editor and the builder read the fields 2018.2 has by name and leave the others. The product GUID is written in 2018's form, and the PS Vita settings the export can't have (power mode, save data quota) are added.
- `MultiplayerManager.asset`, new in Unity 6, is left out.
- The scene's `LightingDataAsset` is serializedVersion 4. The game has no lightmaps, so the asset is left out and the scene's reference to it cleared.
- `Assets/Plugins`: the 10 Unity 6 engine modules 2018.2 doesn't have, `System.Runtime.CompilerServices.Unsafe.dll` and `__Generated.dll` are left out, and the game's three assemblies are replaced by the rewritten ones. A plugin no rule names stops the stage.
- The three shaders are AssetRipper's dummies, since Unity 6 keeps only their compiled Direct3D 11 programs. A `.shader` file under `tools/downgrade/shaders` with the same `Shader "name"` replaces the dummy, keeping its `.meta`; the stage lists the dummies left.
- The 930 sound clips get the PS Vita audio rule's import settings (`audio/audio_rules.py`, [audio](audio.md)): 619 forced to mono, 81 streamed.
- The twelve texture packs give way to the atlas pages under `Assets/Resources/atlas` ([textures](textures.md#the-build-step)).

### Build switches

These environment variables change what a stage writes.
The list is `BUILD_SWITCHES` in `tools/downgrade/common.py`:

| Variable | Values | Default | Read by | What it does |
| --- | --- | --- | --- | --- |
| `T2D_GFX_JOBS` | `on`, `off` | `on` | `build` | Writes `gfx-enable-gfx-jobs=1` into the PS Vita build's `Media/boot.config`, so Unity's job workers record the camera's draws beside the render thread. |
| `T2D_ATLAS_PAGES` | `png`, `raw` | `png` | `project` | The texture pages as PNGs that import as RGBA 4444 Texture2D assets, or `raw`, the fallback, as RGBA 4444 texels in TextAssets that Atlas.cs makes into textures at load time ([textures](textures.md#the-build-step)). |

## Where it stands

As of 2026-11-19 on the Steam release, `uv run build.py` makes a VPK: everything compiles and links.
Whether it runs is the next question; every gap method still throws when it reaches a marker.

| Stage | Time | Result |
| --- | --- | --- |
| Setup, from nothing | 12 min | Every doctor check passes. 2.8 GB in `tools/external`. A rerun with everything in place takes seconds. |
| `recover` | 31 s | 44 assemblies, 16 MB. Rewind, with the 27 patches, identifies 59 kinds of runtime function with the 6000.0.60f1 library and recovers 15,501 of 18,368 native bodies completely (by its own count). |
| `rip` | 11 to 29 s | 1,005 assets; the Mono-layout copy is 0.6 GB, the export 0.6 GB. |
| `code` | 4 s | Runs through: 0 problems from the rewriter, no repair errors or warnings, 100 gap methods with 649 marker sites in `Recovered.T2D`, 41 of them the well-typed stand-ins of `TypeGapValues` ([repairs](repairs.md#well-typed-stand-ins-for-what-is-left-rewritergaptypingcs)). |
| `project` | 5 s | `work/t2d-2018.2.19f1-vita`, 0.6 GB. |
| `build` | 4.6 min | The data step, then the code step: UnityLinker, IL2CPP's C++ conversion, psp2snc on every C++ file and psp2ld, in 99 s with psp2snc's object cache. `Media/Modules/il2CppAssemblies.suprx` is 27.7 MB. |
| `package` | 33 s | `builds/t2d-2018.2.19f1-vita.vpk`, 334 MB, with the LiveArea art and the patched eboot. Not tried on the console yet. |

Complete method bodies by RewindStats' count, upstream Rewind against the patched one, with the shipped 6000.0.60f1 library (the old library before it was rebuilt in brackets):

| Bodies | Upstream `ea8e305` | With patches 0001 to 0022 | With the 27 patches |
| --- | --- | --- | --- |
| Whole game, 19,693 | 14,615 (13,857) | 15,404 | 15,542 |
| Assembly-CSharp, 2,968 | 2,233 (2,157) | 2,697 | 2,767 |
| InControl, 1,874 | 1,637 (1,270) | 1,664 | 1,685 |

The code stage resolves every reference since the fold-back passes ([tools/downgrade/foldback/README.md](../tools/downgrade/foldback/README.md)).
`uv run tools/builder/il2cpp.py` runs the builder's code step alone, without the data step; it needs the project, so run `pipeline.py project` first.

How the C++ compile was cleared, compiling each of the 20 `Bulk_Recovered.T2D_*.cpp` files with IL2CPP's own psp2snc arguments:

| Step | Files failing | Errors | In methods |
| --- | ---: | ---: | ---: |
| Patches 0001 to 0022 | 18, and 1 that exits 1 at `-O3` with no message | 205 | 80 |
| With patches 0023 to 0025 (barriers, long slots, casts, struct arguments) | 14, and the `-O3` one | 50 | 24 |
| With 0026 (merged statics) | 10, and the `-O3` one | 43 | 18 |
| With 0027 (compare-exchange) and `TypeGapValues` | the `-O3` one | 0 | 0 |
| With `SplitLongMethods` | none | 0 | 0 |

- Before the patches, 126 of the 205 errors were the write barrier's card index (`address >> 12 & 0x1FFFFF`) and 73 were numbers where an object is taken; [rewind-issues.md](rewind-issues.md), issues 22 to 27, has what each patch does about them.
- The 4 gap-free methods with errors were Rewind's typing: `ElevatorDoors.awake` and `FutureWar3.setupScene` lost the cast of an inlined `castclass` (issue 24), `UnityMain.OnRenderImage` had a `Color` slot reused for a `Vector3` (issue 25), and `Cutscene.draw` handed a `long` stack slot on as a `ColorTransform` (issue 23).
- `TypeGapValues` typed what was left, all in gap methods: inlined boxes (until the `box` pattern took most of them), long slots whose address is kept in a value (`HUD.draw`), and the last 3 card indexes.
- The `-O3` failure: psp2snc exits 1 with no message on `AssetsStaticMap.staticInit1`, 66,888 lines of C++ that build a table of int arrays, one `InitializeArray` after another. Bisecting `Bulk_Recovered.T2D_2.cpp` by function found it. The file compiles in 10 s with only that function at `#pragma control %push O=0`, and functions of 12,000 lines compile at `-O3`; sharing its 6,456 temporaries doesn't help, so it is the length. The rewriter's last pass, `SplitLongMethods` (`rewriter/SplitMethods.cs`), cuts a straight-line method of more than 16,000 instructions at empty-stack points into static parts of at most 6,000, passing locals used by several parts by reference.

### On the console

On 2026-11-19 the VPK installed on the user's PS Vita (vitacompanion's `install`, 334 MB in 2.5 min) and booted to the main menu ([vita.md](vita.md) has the tools).
The splash, the publisher's logo, the attract cutscene and the menu draw right at 960x544, the cursor moves, and the menu runs at 60 fps.
Choosing a menu item stops at a gap: `Button.staticCallbacks`, the buttons' actions, is a switch Rewind loses with its case bodies ([rewind-issues.md](rewind-issues.md), issue 29).
The same issue is in `Game.start`, `GameMap.solid`, `GameObject.move`, `Player.setGroundTile` and `Sprites.addFromJSON`, so no level can run until Rewind recovers that switch; the attract demo, which the menu starts when left alone, stops in `Sprites.addFromJSON`.

The blockers met on the way, in order:

| What | Where | Kind | Fix |
| --- | --- | --- | --- |
| No player log on a retail console | Unity's PS Vita player | platform | `compat/Log.cs` writes `ux0:data/T2DNoFate/log.txt` |
| The player never left the first frame: FixedUpdate (the game's main loop) ran over and over | `TimeManager.asset`, `Fixed Timestep: 0` (Unity 6 keeps it as a rational) | asset | `asset_rules.time_overlay`: the game's 1/60 s |
| `Camera.rect` set through a cached internal-call pointer | `UnityMain.ScreenStuff`, `FixedUpdate` | Rewind (section 28) | a repair rule, then Rewind patch 0029 |
| One-character strings allocated in place | `StringExt.charAt`, `split`, `Text.processTextFile` | repair | `rules/OneCharStrings.cs` |
| Write barriers the family patterns don't take | `AssetsEmbed.loadBytes`, `SaveSettings.loadSettings` | repair | `rules/Barriers.cs`, `rules/Saves.cs` |
| Float comparison through the interface invoker | `haxe.lang.Runtime.eq` | repair | `rules/HaxeRuntime.cs` |
| Lost stack argument | `BmpFont.drawScaled` (and `TitleText.draw` until patch 0028) | Rewind (issue 21) | `rules/MissingArgs.cs` |
| BmpFont calls through slot invokers | `TextButton.draw` | repair | `rules/Invokers.cs` |
| Element fields read untyped | `Menu.moveCursor`, `Button.drawLock` | repair | `rules/UntypedFields.cs` |
| A virtual call and a dropped jump-table read | `Menu.cursorMoved`, `Button.setupEffect` | repair | `rules/Menus.cs` (`setupEffect`'s retired with patch 0032) |
| The catch-all around the settings' Deserialize | `SaveSettings.loadSettings` | repair | `rules/Saves.cs` |
| A switch through a byte-indexed jump table, case bodies lost | `Button.staticCallbacks` and five level methods | Rewind (issue 29) | Rewind patch 0032 |
| r9 taken as clobbered by the write-barrier call | `Sprites.addFromJSON` (`FWElevator.type`, `FW3BossLift.type`) | Rewind (issue 30) | `rules/Barriers.cs` |

With Rewind patches 0001 to 0031 and these rules the code stage leaves 69 gap methods with 405 marker sites; the rules for the main menu and Future War 1 (docs/repairs.md) take that to 50 methods with 319 sites, and Rewind patch 0032 with the issue 30 rule to 43 methods with 301 sites.

Measured on that console with `-t2d-heartbeat 5` (runs `work/runs/boot4`, `menu1`):

| | Value |
| --- | --- |
| First frame | 14.2 s after launch. `Sounds.staticInit` asks for most of the 930 clips one after another, so start-up waits for them (9.7 s of waits; all 930 loaded at 18 s). With the audio rule A3, which makes those `Sound`s without their clips, 4.4 s, and the attract at 11 s instead of 21 s (runs `aboot1`, `aboot2`; [audio.md](audio.md#start-up)) |
| Main menu | 26.7 s after launch, through the splash, the logo and the attract cutscene |
| Main memory free (LPDDR) | 254 MB at start, 195 MB in the attract and the menu; managed heap 20 MB |
| Video memory free (CDRAM) | 89 MB at start, 60 to 70 MB in the attract and the menu |
| Frame time | splash, while the sounds load: p50 23.5 ms, p90 68 ms; attract and menu: p50 16.7 ms, p90 17.0 ms (60 fps; the worst frames, 0.7 s, are the probe's screenshots) |
| Sprites a frame | splash 1 to 2, attract 4 to 8, menu 44 |
| SpriteBatch runs a frame | menu 4, of which 3 change texture; attract 2 to 3 |

## When something fails

`build.py` stops at the first failure and says what failed.
Fix it and run `build.py` again: it picks up where it stopped.

- Setup: a download whose hash doesn't match is deleted, and setup stops; run it again. "To do" lines at the end are steps setup couldn't take, such as an offer you declined.
- The game: "another release than the rules were written for" means your `GameAssembly.dll` or `global-metadata.dat` differs from Steam build 24040403's; have Steam verify the game's files.
- `recover`: Rewind's log is `artifacts/downgrade/rewind.log`.
- `rip`: "AssetRipper did not start" or "AssetRipper export produced no project": check `tools/external/assetripper-2.0.0` and `artifacts/downgrade/assetripper.log`.
- `code` and `project`: the rewriter's log is `artifacts/downgrade/rewrite-2018.2.19f1-vita.log`, its summary `artifacts/downgrade/code-report.txt`. "expected at least ... The export changed or the rule no longer matches it" means a rule matched less than on the export it was written for, usually another release of the game.
- `build`: "The code stage left references that don't resolve" means a rewriter rule no longer covers what Rewind wrote (`artifacts/downgrade/code-report.txt` lists them). Otherwise each step names what it is on, and a failure prints the step and the file; UnityLinker's log is `work/builder/il2cpp/unitylinker.log`.
- Locks: "Held by another run" means another pipeline holds a build lock; `uv run tools/downgrade/lock.py status` shows who.

## Time and disk space

| Step | Time |
| --- | --- |
| Setup from nothing (downloads about 2 GB) | 12 minutes |
| `recover` | 47 s |
| `rip` | 29 s |
| `code` and `project` | 10 s |
| The builder's data and code steps | 4.6 minutes with psp2snc's object cache in place (the first compile of the C++ takes longer) |
| A rerun of `build.py` with recover and rip current | seconds, up to where it stops |

After a first run the repository holds 2.8 GB of tools in `tools/external` (Unity's files 1.1 GB, the two .NET SDKs 1.5 GB), 1.8 GB in `work/` (the Mono-layout copy of the game, the export and the project, 0.6 GB each) and 0.6 GB in `builds/`.
The doctor asks for 6 GB free for a first build.
