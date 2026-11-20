# The PS Vita

How to install the build on a PS Vita, run it, and read what it did.
`vita.py` and `drive.py` come from butcher-vita's port (same author, MIT), adapted to this port's title and folders; its `docs/vita.md` has the longer story behind each safety rule.
Prose here is one sentence per line.

The app's title ID is `TNOF00001`.
It keeps its files in `ux0:data/T2DNoFate`: the game's saves (`Terminator_SaveSettings.dat`, `Terminator_SaveStats.dat`), the probe's flags (`args.txt`, then `args.used.txt`), the player's log (`log.txt`, the two runs before in `log.prev.txt` and `log.prev2.txt`), and the probe's screenshots.

A scripted launch, one that applies flags from `args.txt` (`drive.py`, `vita.py args`), keeps its saves apart, in `ux0:data/T2DNoFate/test` (`compat/Saves.cs`), chosen before the game reads its first save: a test that sets the story's progress (`-t2d-set ...storyBossReached=true`) and finishes a level saves there, never over the player's saves.
The first time, and whenever the test folder lacks one of the two files, the launch copies it there from the player's; deleting `ux0:data/T2DNoFate/test` starts the tests over from the player's saves.
Its flags, log and screenshots stay in `ux0:data/T2DNoFate`, and its log says which folder it saved to (`T2D_SAVES`).
A test that truly needs the player's own saves passes `-t2d-real-saves` (`drive.py --real-saves`); `drive.py`, `vita.py args` and the log (`T2D_SAVES WARNING`) say so, and the saves should be backed up first.
The player's own launch from the LiveArea reads no `args.txt` and plays on `ux0:data/T2DNoFate` as always; an empty `args.txt` (the flags cleared) counts as none.

## Console tools

The console's address is `T2D_VITA_HOST` (or the variable an earlier port set: `BUTCHER_VITA_HOST`, `HUNT_VITA_HOST`, ...).
`sync`, `log`, `args`, `files` and `crashes` need an FTP server on port 1337 (VitaShell's or vitacompanion's); `launch`, `quit`, `run`, `do`, `perf` need vitacompanion's command server on 1338, and `install` its extended build.

```powershell
uv run tools/downgrade/vita.py install                # package the build and install it (first time, or a param.sfo change)
uv run tools/downgrade/vita.py sync                   # upload what changed since the last sync
uv run tools/downgrade/vita.py args -- -t2d-heartbeat 5   # flags for the next launch only
uv run tools/downgrade/vita.py run                    # quit, sync, launch
uv run tools/downgrade/vita.py log --tail 40
uv run tools/downgrade/vita.py crashes --pull         # this title's newest crash dump, symbolized
```

`drive.py` is one session: flags, launch, a script of waits and presses, then the log and screenshots into `work/runs/<tag>`, and quit.

```powershell
uv run tools/downgrade/lock.py run vita-device "t2d: boot" --wait 3000 -- uv run tools/downgrade/drive.py menu1 `
  "-t2d-heartbeat 5 -t2d-shots 3 shot 24" "wait 26; press start 300; wait 9; press cross 300" --sync
```

## Sharing the console

The console is shared by every port on the machine through one lock, `~/.t3/vita-device.lock` (`tools/downgrade/lock.py`).
Every console action runs under it: `vita.py`'s changing commands refuse otherwise.
Wait for it; never break or steal it.
The lock isn't first-come: a waiting job polls every 5 s, and jobs that run one session after another can take it again before a waiter sees it free, so a wait of half an hour is normal while another port's agents work.
Put a session's steps in one `lock.py run` (install and launch, say) rather than taking the lock twice.

The safety rules, as on butcher-vita's:

- Uploads go to `<name>.part` and are renamed; an interrupted file is sent again. Don't kill `vita.py` while it transfers: Ctrl+C lets the file finish.
- Flags apply to one launch: the player renames `args.txt` to `args.used.txt` as it reads it (`compat/Flags.cs`), and `drive.py` clears flags a launch never read.
- `drive.py` releases every button it presses, also when the session stops part way; it has no step that leaves one held.
- `drive.py` stops when a sync, the flags or a press fails. At its end it quits the game after any launch it asked for, also one whose reply was lost. `vita.py quit` asks `perf` which app runs first and quits only TNOF00001 (vitacompanion's quit is `quit all`, which ends whatever app the console shows); when `perf` can't tell, it quits nothing and exits with 1, and a quit counts only once `perf` shows the game gone (otherwise exit 1). `sync` and `run` still quit whatever runs when `perf` can't tell, since files replaced under the running game crash it, and write files only once `perf` shows the game gone; with no command server (VitaShell's FTP alone) or no `perf`, they stop unless `T2D_GAME_NOT_RUNNING=1` vouches that the game isn't running.
- A vitacompanion reply cut off by its timeout is an error (`vita.py do` exits 1, also on no reply at all), so `drive.py` keeps a press's button as held and releases it.
- `vita.py`'s changing commands take the `vita-device` lock themselves when it is free, for the whole command, and refuse when someone else holds it.
- `drive.py` holds the console once for the whole session: inside `lock.py run` it uses that hold; run alone it takes the lock itself (and stops, launching nothing, when someone holds it) and passes it to its `vita.py` commands through `T2D_HOLDER`, so no other job can take the console between the flags and the launch or between a press and its release.
- Whether a session holds the lock is checked by who holds it, not by what the holder's text says: a `lock.py run` this process runs under (`T2D_LOCKS`, or the run's `lock.py` process among its ancestors, which covers another port's `lock.py run`), or `T2D_HOLDER` naming the holder (`lock.ours`). The test apps' helpers (`vt.py`, `va.py`, the shader test's `device.py`) check it the same way; before, any lock whose text held `t2d:` passed.
- Each step of `drive.py`'s end (releasing a button, reading and saving the log, the quit, clearing the flags, the screenshots) runs whatever the others did; a failed one makes `drive.py` exit non-zero. A console with no `log.txt` yet is a first run, not a failure (`vita.py log --missing-ok`).
- A launch that wrote no log of its own: `drive.py` keeps the log it read as `log.stale.txt`, not `log.txt`, and exits with 1.
- Only TNOF00001 and `ux0:data/T2DNoFate` are touched; `crashes` lists only this title's dumps.
- A scripted launch saves to `ux0:data/T2DNoFate/test`, not over the player's saves, unless its flags say `-t2d-real-saves` ([above](#the-ps-vita)).
- No mute flag exists, and none should be used on the console.

## The player's log and the probe

Unity's PS Vita player logs only to a development kit's console, so T2DCompat writes the log itself (`compat/Log.cs`): every message, errors with their stack, a repeated message as one line with its count (written again every 5 s while it goes on), each line with the seconds since T2DCompat started.
It stops at 8 MB.

`compat/Probe.cs` logs `T2D_BOOT` lines with the free memory at T2DCompat's start, each scene load and the first five frames.
On the PS Vita the free memory is main memory (`freeLPDDRMB`), video memory (`freeCDRAMMB`) and physically contiguous memory (`freePHYSCONTMB`), from `UnityEngine.PSVita.Diagnostics`, which `link.xml` keeps.
Its frame step runs from its own Update and, as a fallback, from `SpriteBatch.Flush`.
The heartbeat line ends with the atlas's resident pages, their megabytes, those not drawn yet and the budget.

| Flag | Effect |
| --- | --- |
| `-t2d-heartbeat <s>` | Every s seconds a `T2D_HEARTBEAT` line: frames, fps, mean, p50, p90 and worst frame time, the managed heap, the free memory, and per frame SpriteBatch's sprites, runs (draws), runs that changed texture (`pageSwitches`) and off-screen draws. |
| `-t2d-shot <s> <name>` | A screenshot s seconds after the start, `ux0:data/T2DNoFate/shots/<name>.png`. The name is letters, digits, `-` and `_`; the player refuses a path, so a flag can't overwrite a save. |
| `-t2d-shots <s> <name> [after]` | A screenshot every s seconds from `after` seconds on, `shots/<name>-<seconds>.png`. On the PS Vita each stalls the game about 0.6 s. |
| `-t2d-quit-after <s>` | Logs `T2D_PROBE quit after` s seconds after the start and calls `Application.Quit`, which the PS Vita player ignores (checked 2026-11-20): the game runs on, with no `T2D_QUIT`, until `drive.py` or `vita.py quit` ends it. |
| `-t2d-log-sync` | Write the log on the logging thread. |
| `-t2d-stalls <ms>` | During a frame longer than ms (default 1000; 0 turns it off), a thread of the probe's writes a `T2D_STALL` line every 250 ms: the free memory and, in a `T2D_TRACE=1` build, the game methods the main thread entered last; then how long the frame took. |
| `-t2d-peek <path>,...` | `T2D_PEEK` lines with static fields read by reflection (`Namespace.Type.field`, then `.field` or `[index]`, into arrays and Haxe Arrays), on the first frame and whenever a value changes. |
| `-t2d-invoke <call>,...` | At 10 s (`-t2d-invoke-at <s>` for another time), calls by reflection (`<object or type path>::<method>[::<arg>...]`, an arg an int, `@<path>` or a string) and logs `T2D_INVOKE` with the result or the exception. A call `<path>=<value>` sets a field instead, static or of the object the path reaches (`...game.Sprites.boss.hp=0`), to an int, bool or double; a call written `+<s>:<call>` waits until s seconds, so one launch can act at several times. |
| `-t2d-set <App>:<path>=<value>,...` | Sets a static field as the game enters that App, before its `start()` (`*` for the first frame). A path through a static object sets that object's field (`Game:...savedata.SaveStats.sti.storyBossReached=true`). Arcade mode with `MissionBriefing:com.bitmapbureau.terminator.game.Game.levelNumber=<n>` plays the arcade list's level n; `Game.infiniteHealth=true` keeps a scripted player alive. An int array is written `a|b|c`: `Game.levelIDs=11|13|12|0|4|1|2|3|5|6|7|8|9|14|15|10|16|17|18,...Game.mode=5` at the briefing turns arcade into the level select's list, all 19 levels in story order (`work/scratch/levels2.sh`, [build](build.md#levels)). For tests only. |
| `-t2d-real-saves` | The launch plays on the player's own saves, not `ux0:data/T2DNoFate/test` (above). Back them up first. |
| `-t2d-atlas-sync` | The briefing's prefetch loads pages with `Resources.Load`, not `LoadAsync` ([textures](textures.md#the-runtime-class)). |
| `-t2d-input-log`, `-t2d-store-log` | The platform layer's (`compat/Pad.cs`, `Store.cs`). |

An exception in the log has no stack on a retail console. Build with `T2D_TRACE=1` ([build.md](build.md#build-switches)) and each error gets the game methods entered last instead.

Driving the game: `press start` skips the intro cutscenes and the mission briefing; Cross chooses in menus; in a level the d-pad moves, Cross jumps, Square shoots. A `drive.py` `until` takes a regular expression without spaces.
