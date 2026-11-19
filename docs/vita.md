# The PS Vita

How to install the build on a PS Vita, run it, and read what it did.
`vita.py` and `drive.py` come from butcher-vita's port (same author, MIT), adapted to this port's title and folders; its `docs/vita.md` has the longer story behind each safety rule.
Prose here is one sentence per line.

The app's title ID is `TNOF00001`.
It keeps its files in `ux0:data/T2DNoFate`: the game's saves (`Terminator_SaveSettings.dat`, `Terminator_SaveStats.dat`), the probe's flags (`args.txt`, then `args.used.txt`), the player's log (`log.txt`, the two runs before in `log.prev.txt` and `log.prev2.txt`), and the probe's screenshots.

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
  "-t2d-heartbeat 5 -t2d-shots 3 ux0:data/T2DNoFate/shot 24" "wait 26; press start 300; wait 9; press cross 300" --sync
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
- `drive.py` warns when a launch wrote no log of its own.
- Only TNOF00001 and `ux0:data/T2DNoFate` are touched; `crashes` lists only this title's dumps.
- No mute flag exists, and none should be used on the console.

## The player's log and the probe

Unity's PS Vita player logs only to a development kit's console, so T2DCompat writes the log itself (`compat/Log.cs`): every message, errors with their stack, a repeated message as one line with its count (written again every 5 s while it goes on), each line with the seconds since T2DCompat started.
It stops at 8 MB.

`compat/Probe.cs` logs `T2D_BOOT` lines with the free memory at T2DCompat's start, each scene load and the first five frames.
On the PS Vita the free memory is main memory (`freeLPDDRMB`), video memory (`freeCDRAMMB`) and physically contiguous memory (`freePHYSCONTMB`), from `UnityEngine.PSVita.Diagnostics`, which `link.xml` keeps.
Its frame step runs from its own Update and, as a fallback, from `SpriteBatch.Flush`.

| Flag | Effect |
| --- | --- |
| `-t2d-heartbeat <s>` | Every s seconds a `T2D_HEARTBEAT` line: frames, fps, mean, p50, p90 and worst frame time, the managed heap, the free memory, and per frame SpriteBatch's sprites, runs (draws), runs that changed texture (`pageSwitches`) and off-screen draws. |
| `-t2d-shot <s> <path>` | A screenshot s seconds after the start. |
| `-t2d-shots <s> <prefix> [after]` | A screenshot every s seconds from `after` seconds on, `<prefix>-<seconds>.png`. On the PS Vita each stalls the game about 0.6 s. |
| `-t2d-quit-after <s>` | Quit s seconds after the start. |
| `-t2d-log-sync` | Write the log on the logging thread. |
| `-t2d-input-log`, `-t2d-store-log` | The platform layer's (`compat/Pad.cs`, `Store.cs`). |
