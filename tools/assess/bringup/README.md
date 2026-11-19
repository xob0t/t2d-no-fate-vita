# Bring-up probe: Rewind's code as a Unity 2018.2 Windows player

Tools for running the game's recovered code, compiled by compile2018, as a Unity 2018.2.19f1 Windows x64 Mono player, and for finding what stops it.
None of these files contain game content.
Everything they write goes to `work/bringup/`, and so do the probe's game-specific hacks (`work/bringup/hacks.sh`).

| File | What it does |
| --- | --- |
| `RewindStubThrow.cs`, `RewindStubPass2Probe.cs` | `__Rewind` for a runnable build. A gap marker throws `NotSupportedException("REWIND_GAP …")` with its text and logs it once with a stack. The GC-flag load (`__load4(0x1810CD090)`) reads as 0, so write-barrier blocks are skipped. `T2D_PROBE_PASS` lets more markers through by prefix. The pass-2 stand-ins log `REWIND_SILENT`, and `InitArray` copies a field's bytes from `T2D_PROBE_FIELDDATA`. |
| `Compat2018Runtime/` | compile2018's Compat2018 shim with working bodies. Fake native pointers (`GetCachedPtr`, `Ptr`) are keys in a registry of instance IDs. The 49 `*_Injected` binding calls are mapped back to 2018.2's public API. `Unsafe` is built from `DynamicMethod` IL (Mono JIT only). |
| `emit.sh` | Builds the DLLs from a hackable copy of compile2018's pass-2 sources. Members that still don't compile throw `COMPILE_STUB <type>.<member>` (RoslynDiag `--stub-marker 1`). |
| `arrayfold.py` | Source rule. Folds `Array::NewSpecific` allocations (`__native_0x1800E2FD0`) and their element stores back into `new T[n]`: literals, constants, boxed locals, nested arrays, doubles stored as bits, `InitializeArray` blobs. Also handles `Object::New` (`__native_0x1800E3F30`). |
| `eventfold.py` | Source rule. Turns inlined field-like event accessors (compare-exchange loops) back into `Delegate.Combine` and `Delegate.Remove`. |
| `bclfold.py` | Source rules for inlined BCL code: the `ReadOnlyCollection` constructor, `ExceptionArgument`, `InitializeArray`, 2-byte bool-pair stores, `Dictionary.FindEntry`, unboxing at offset 16, `RuntimePlatform.PS5`, and Unity 6's internal `Texture2D` constructor. |
| `replace_body.py` | Replaces one method's body by brace matching. Used for the hand hacks. |
| `FieldData/` | Dumps the field-RVA blobs (array initializer data) of Rewind's assembly for `InitArray`. |
| `assemble.py` | Builds the Unity project from the AssetRipper export (2018.2 target) and the probe DLLs. Drops the Unity 6-only plugins, uses the 2018.2 ProjectSettings base, and adds the probe scripts. |
| `probe/` | `ProbeRuntime.cs` keeps runs quiet (muted, cursor unlocked, windowed, capped at 60 fps). It logs a `PROBE` status line every second, logs each distinct exception, takes screenshots, and quits on a timer. `ProbeBuild.cs` is the batch-mode build entry. |
| `build.sh` | Builds the player with the 2018.2 editor in batch mode. |
| `run_player.py`, `mute_players.ps1` | Run a player on a hidden desktop with a log, a timeout and Windows' per-app mute (adapted from butcher-vita). |
| `summarize.py` | Prints the first exceptions, gap and silent markers, and status lines of a run log. |
| `cycle.sh` | One iteration: emit, assemble, build, run, summarize. |

```sh
REWIND=tools/external/rewind/src/Rewind.Cli/bin/Release/net10.0/rewind.exe
GAME="/c/Program Files (x86)/Steam/steamapps/common/Terminator 2D NO FATE"
$REWIND decompile "$GAME" -o work/bringup/rewind73 --csharp 7.3 --assembly Assembly-CSharp --assembly InControl \
  --assembly Steamworks --signatures tools/rewind/signatures/unity-6000.0.60f1-windows-x64.sig
sh tools/assess/compile2018/run.sh pass2 work/bringup/rewind73/src work/bringup/pass2
tools/external/dotnet10/dotnet.exe build -c Release tools/assess/bringup/FieldData -o work/bin/FieldData
tools/external/dotnet10/dotnet.exe work/bin/FieldData/FieldData.dll work/bringup/rewind73/assemblies/Assembly-CSharp.dll work/bringup/fielddata
"$LOCALAPPDATA/Unity/Editors/2018.2.19f1/Editor/Unity.exe" -batchmode -nographics -quit -createProject "$(cygpath -aw work/bringup/empty2018)"
sh work/bringup/hacks.sh                     # probe-src from pass2/src, the source rules, then the hand hacks
sh tools/assess/bringup/cycle.sh <name> 45   # emit, assemble, build, run 45 s on the hidden desktop, summarize
```
