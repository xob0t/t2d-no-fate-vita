# The platform layer

The platform layer is what stands between the game's code and the PS Vita: input, the store services, saves, the screen, sound loading and the game's platform switches.
It is T2DCompat code in `tools/downgrade/compat/*.cs`, a rewriter pass with its rules in `tools/downgrade/compat/rewriter/`, and a checker in `tools/downgrade/compat/check/`.
None of it holds game code: the rules name the game's types and members, and the compat code reimplements the APIs the game calls.
Prose here is one sentence per line, so a diff shows only the sentences that changed.

In short:

- InControl is replaced, not repaired. The pipeline leaves it out, and the 19 InControl types the game names become T2DCompat's own, over Unity's Input (`InControl.cs`, `Pad.cs`, `Keys.cs`).
- Steam, Epic Games and GOG Galaxy are offline. The platform pass takes every call into Steamworks out of the game's code, so the pipeline leaves Steamworks out too, and the few Steamworks types left in signatures become empty stand-ins (`Store.cs`, `Steamworks.cs`).
- Saves stay the game's own BinaryFormatter files, in `ux0:data/T2DNoFate` on the PS Vita (`Saves.cs`).
- The game starts without Steam: its `UnityMain.Start` ran `Main.main` only once SteamManager said Steam was up.

With the layer in place, the code stage's unresolved references go from 245 to 61, InControl's 604 gap methods are gone, and `Recovered.T2D`'s go from 811 to 788.
UnityLinker gets past InControl, `PlayerPrefsX` and `UnityMain.OnPreRender`, and now stops in `Stage.setDisplayState`, on an inlined `String._firstChar` that isn't the platform layer's ([What is left](#what-is-left)).

## What the game uses

`tools/downgrade/compat/check/Inventory.cs` lists every call, field access and type token of a given API in an assembly, with the methods that use them.
The counts below are for Rewind's Assembly-CSharp of the Steam release (build 24040403).

```sh
PlatformCheck.exe inventory Assembly-CSharp.dll inventory.tsv InControl. Steamworks. UnityEngine.Screen ...
```

### Input: InControl

The game names 19 InControl types and 89 of their members, at 554 sites in 42 methods.
Assembly-CSharp itself never calls `UnityEngine.Input`: every key and pad goes through InControl.

| Where | What it uses |
| --- | --- |
| `com.twissgreen.unity.PlayerActions` | Derives from `PlayerActionSet`; 13 actions (`CreatePlayerAction`), `DeviceBindingSource` and `KeyBindingSource` fields, default bindings (`AddDefaultBinding` of a binding, a control or keys), `ListenOptions` and its callbacks for rebinding, `stateThreshold`. |
| `com.twissgreen.unity.UnityMain` | `InputManager`'s options, `SetupInternal`, `UpdateInternal` (from `FixedUpdate`, the game's main loop), `ResetInternal`, `OnApplicationFocus`, `OnLevelWasLoaded`, `OnDeviceAttached` and `OnDeviceDetached` (as fields, through `Delegate.Combine` and an interlocked exchange), `ActiveDevice`; a set's `Device`; `WasPressed`, `WasReleased`, `State`, and the `enabled`, `ownerEnabled`, `thisState` and `lastState` fields IL2CPP inlined from them. |
| `com.bitmapbureau.terminator.apps.MainMenu` | Rebinding (`ClearBindings`, `ListenForBindingReplacing`, `StopListeningForBinding`, `Save`, `Load`, `Reset`, `ClearInputState`), each binding's `Name` (which it turns into button glyphs), the device's `DeviceStyle` (which glyph set), `UpdateTick` (which player used their pad last), the sticks' dead zones (`LowerDeadZone`, `lowerDeadZone`) and `DeadZoneFunc` (`UnityMain.BetterAnalog`). |
| `Platform`, `Controls`, `MultiPlayer` | `InputDevice.Vibrate`, `SetLightColor`, `PlayerActionSet.Destroy`. |

The default bindings, from `PlayerActions.CreateWithJoystickBindings` and `CreateWithKeyboardBindings`:

| Action | Pad | Keyboard |
| --- | --- | --- |
| Up, Down, Left, Right | Left stick and d-pad (threshold 0.3) | Arrows, and W, S, A, D |
| Start | Menu, Options, Plus, Home, Start, Pause | P, Return |
| Attack | Action3 (X, Square) | Z |
| Jump | Action1 (A, Cross) | X |
| Interact | Action2 (B, Circle), left and right bumpers | Space |
| Item | Action4 (Y, Triangle) | V |
| LeftTrig, the game's Aim | Left and right triggers (threshold 0.4) | C |

`UnityMain.inputsStandaloneInControl` turns the actions into the game's own controls (`Controls.controlChange`): Aim is `LEFT_TRIGGER` or `RIGHT_TRIGGER`, which `Player.aimIsPressed` reads.
Device detection is InControl's: a device attaches, the join listener (a set with no device, reading `InputManager.ActiveDevice`) sees a button, and `UnityMain.CreatePlayer` gives that device a set of its own.
`MainMenu.controllerTypeForDevice` picks the glyphs from `DeviceStyle`: Xbox One, Xbox Series, PlayStation (PS2 to PS4 and the PS Vita), PS5, Nintendo, or other.
The player's bindings are saved as InControl's `PlayerActionSet.Save()` strings, one per controller type, in the settings file (`SettingsBlob.controllerKeyboard`, `controllerXB1`, ...).

### Steamworks

The game names 75 Steamworks members at 123 sites in 23 methods.

| Where | What |
| --- | --- |
| `SteamManager` (a MonoBehaviour made by `getInstance`) | `SteamAPI.InitEx` and `RestartAppIfNecessary` in `Awake` (it quits without Steam), callbacks (`GameOverlayActivated_t` pauses the game, `UserStatsReceived_t`), `RunCallbacks`, the leaderboards (`FindLeaderboard`, `UploadLeaderboardScore`, `DownloadLeaderboardEntries`, the persona names). |
| `UnityMain.Start` | `if (!SteamManager.getInstance().m_bInitialized) return;` before `Main.main()`: the game doesn't start without Steam. |
| `Utils.initUtils` | `STEAM_BUILD = true`; `STEAM_DECK` from `ISteamUtils.IsSteamRunningOnSteamDeck`, through Steam's interface table (a Rewind gap). |
| `Utils.systemLanguageSteam` | The language from `ISteamApps.GetCurrentGameLanguage`, else `systemLanguageStandalone` (`Application.systemLanguage`). |
| `Achievements.unlock` | After the game's own unlock (the save, the banner): `SteamUserStats.SetAchievement`, then `StoreStats` inlined through Steam's interface (Rewind gaps). |
| `Platform.awardTrophy`, `mapTrophiesAndAward` | The same, for the console builds' trophy numbering. |
| `Game.storyModeCompleted`, `Platform.sendEventTotalKills`, `sendEventCompleteGame` | `SteamUserStats.SetStat`. |
| `HighscoreTable`, `MainMenu.chooseMode` | The leaderboards, only when `STEAM_BUILD`. |
| `UnityMain.dlcSteamFound` | `BIsDlcInstalled`; nothing calls it. |

There is no Steam Cloud code: the saves are local files.

### Epic Games and GOG Galaxy

`EpicManager` and `GalaxyManager` are MonoBehaviours whose `getInstance` makes a GameObject; their other methods already return constants.
Nothing calls them, and `Utils.setEpicBuild` and `setGogBuild` are empty, so `EPIC_BUILD` and `GOG_BUILD` stay false.

### The platform class and switches

`com.twissgreen.unity.Platform` holds the console builds' leftovers (Xbox presence, connected storage, PSN, trophy packs), nearly all empty methods, plus the Steam achievement calls above and the pad's vibration and light colour.
The platform switches are static fields of `com.twissgreen.engine.utils.Utils`, set by `initUtils` (from `Main.main`) and read as build switches:

| Switch | Steam build | Read by |
| --- | --- | --- |
| `CONSOLE_BUILD` | false | `Main.main` (one player), `MainMenu.showOptions` (with `STEAM_BUILD` and `EPIC_BUILD`: the game's own achievements list in the options) |
| `STEAM_BUILD` | true | `HighscoreTable` (leaderboards), `MainMenu`, `Achievements` |
| `STEAM_DECK` | Steam's answer | `MainMenu` (glyphs) |
| `FULL_SCREEN` | true on desktop players | `MainMenu` (the full-screen option), `StageDisplayState` |
| `NO_EXIT` | false (true on consoles and phones) | `MainMenu` (the Exit item), `Main.exitGame`, `Transition.update`, `Utils.quitApp` |
| `VIBRATION_BUILD`, `CIRCLE_ENTER` | true, false | Only `Utils` |

### Saves

`SaveData.saveMethod()` returns `SAVE_MODE_UNITY_BINARY` whatever the platform, so the game saves two files with BinaryFormatter: `Application.persistentDataPath + "/Terminator_SaveSettings.dat"` (`SettingsBlob`) and `"/Terminator_SaveStats.dat"` (`StatsBlob`), `[Serializable]` classes of bools, ints, doubles, strings and their arrays.
`SaveSettings` and `SaveStats` also hold paths for PlayerPrefs (with the `PlayerPrefsX` array helpers), for the Xbox's and Switch's blobs (`BinaryWriter` into a `MemoryStream`), and for the consoles' connected storage; none of them runs on this build.
`persistentDataPath` is read only there, in four methods.
`DateTime` is never read.

Butcher's XML saves failed on the PS Vita because the class libraries' XmlSerializer compiles a serializer with Reflection.Emit.
This game has no XmlSerializer.
Its BinaryFormatter doesn't need Emit: in Unity 2018.2's unityaot `mscorlib`, nothing in `System.Runtime.Serialization.Formatters.Binary` uses `System.Reflection.Emit` (checked with `Inventory.cs` over the unityaot mscorlib: Emit's only users are its own types), so it serializes by reflection, as IL2CPP allows.
What it does need is the type names: the files name `Assembly-CSharp`, which the rewriter renames `Recovered.T2D`, and IL2CPP never asks `AssemblyResolve`.
The rewriter's BinaryFormatter rule (`Redirect.cs`) gives every `new BinaryFormatter()` T2DCompat's alias binder (`Compat2018.cs`, `TypeNames`), which maps the names both ways, so a PC save loads on the PS Vita and the other way round.

### Language

`Utils.getSystemLanguage` and the language menu's default come from `systemLanguageSteam`, which falls back to `systemLanguageStandalone` (`Application.systemLanguage`, which Unity's PS Vita player answers from the system's language).
`cs.Lib.applyCultureChanges` (from `UnityMain.Start`) sets the culture's decimal separator through `NumberFormatInfo`'s internals (`VerifyWritable`, `numberDecimalSeparator`), class library internals IL2CPP inlined; that is the API-gap work's, not this layer's.

### Screen

`UnityMain.FixedUpdate` compares `Screen.width`, `height` and `fullScreen` with what it saw last, and on a change of mode calls `Screen.SetResolution(796, 448, false)` or `Stage.forceResolutionUnity(true)`, which picks a 16:9 mode of 60 Hz or more and at most 1920 wide from `Screen.resolutions` (`Resolution.refreshRateRatio`, Unity 6's) and sets it full screen.
`Stage.setDisplayState` (the options) sets `Screen.fullScreen` and the resolution.
`UnityMain.ScreenStuff` letterboxes the camera to 16:9 by the screen's size, through `Camera.rect` (an inlined binding call, a Rewind gap).
The game draws a 398×224 picture into a render texture (`UnityMain.Start`), and with the CRT filter through a second one, 398×896, made each frame in `UnityMain.OnPreRender` with Unity 6's `RenderTextureDescriptor` internals inlined.
`Cursor.visible = false` in `Start`.
On the PS Vita the screen is 960×544: the camera gets bars of 2 pixels, and the mode never changes.

### Sound

`Sound.createSoundFromFile(name)`, which `AssetsEmbed.loadSound` calls for each sound, first looks for a loose file `Application.dataPath + "/Resources/" + name + ".wav"` and loads it with `UnityWebRequest` and `DownloadHandlerAudioClip`, waiting for it on the main thread.
The Steam release ships no such file (its data folder's `Resources` holds only Unity's built-in resources), so every sound comes from the method's fallback, `Resources.Load<AudioClip>(name)`.
That is the only use of `UnityWebRequest`, with Unity 6's `UnityWebRequest.Result` and inlined binding calls.

### Other OS-facing code

- Quitting: `Application.Quit` in `Main.exitGame`, `Transition.update` and `Utils.quitApp`, each unless `NO_EXIT`, and in `SteamManager.Awake`.
- `Application.targetFrameRate = 60` and `QualitySettings.vSyncCount = 1` (`UnityMain.Awake`); `OnApplicationFocus` and `OnApplicationPause` pause the game (`genericPause`).
- `Application.platform` only in `Utils`'s switch setters; `AudioSettings.dspTime` in `Audio.playIntroLoopChannels`.
- `UnityMain.dlcPS4LoadAssetBundle` and `dlcXB1LoadAssetBundle`: asset bundles of the console builds' DLC; nothing calls them.
- None: `Application.OpenURL`, analytics, `System.Net`, processes, environment variables, PlayerPrefs at run time.

## The design

### Input

`compat/InControl.cs` is the subset of InControl's API the game uses, written anew, in the classes the rewriter's naming convention looks for: `InControl.PlayerActionSet` is `T2DCompat.InControl_PlayerActionSet`, and so on.
Each member keeps InControl's name and signature; the fields the game's code reads and writes itself (inlined accessors) are public fields of the same names and types, and the enums keep InControl's values.
It keeps InControl's behaviour where the game sees it:

- `InputManager.UpdateInternal` advances a tick, reads each pad (`Pad.cs`) into its device's controls, then updates every live action set: an action's value is the largest of its bindings', through its dead zone and state threshold; `WasPressed`, `WasReleased` and `UpdateTick` follow from one tick to the next.
- A set without a device reads `ActiveDevice`, the pad last pressed; the game's join listener works that way.
- A stick's directions take the stick's value after its two-axis dead zone, which is the game's `DeadZoneFunc` when it set one; the directions' own dead zones only raise the stick's, as in InControl.
- Rebinding listens for the next control or key by `ListenOptions`, and calls the game's `OnBindingFound`, `OnBindingAdded` and `OnBindingRejected` as InControl does. Keys are compared from tick to tick, not with `Input.GetKeyDown`, since the game updates from `FixedUpdate`.
- `Save` and `Load` write and read the regular bindings as text (`T2D1|Up=D11,K85;...`).
- Binding names are InControl's (its profiles' control names, its keyboard provider's key names), since the game turns them into glyphs.
- `Vibrate` and `SetLightColor` do nothing; mouse and touch bindings, InControl's device profiles and its native and XInput backends aren't there.

`compat/Pad.cs` reads the pads through Unity's Input: buttons as `KeyCode.Joystick<n>Button<b>`, axes by the names InControl reads, `joystick <n> analog <a>`, which the game's `InputManager.asset` defines for joysticks 1 to 4 and axes 0 to 7 (the project keeps all 32).
It has two layouts.
The PS Vita's is the one InControl's own PS Vita profile reads, and butcher-vita read on the console:

| PS Vita | Unity | InControl control | Name (glyph) | Game action |
| --- | --- | --- | --- | --- |
| Cross | button 0 | Action1 | Cross | Jump |
| Circle | button 1 | Action2 | Circle | Interact |
| Square | button 2 | Action3 | Square | Attack |
| Triangle | button 3 | Action4 | Triangle | Item |
| L, R | buttons 4, 5 | LeftTrigger, RightTrigger | L1, R1 | Aim |
| Select | button 6 | Select | Select | none |
| Start | button 7 | Start | Start | Start |
| Left stick | axes 0, 1 | LeftStick* | | Up, Down, Left, Right |
| Right stick | axes 3, 4 | RightStick* | | none |
| D-pad | axes 5, 6 | DPad* | | Up, Down, Left, Right |

The PS Vita has no triggers and no bumpers, and the game's Aim needs a shoulder button, so L and R are the triggers.
The bumpers, which the game binds to Interact besides Circle, aren't reported; `PlayerAction.AddDefaultBinding` leaves out default bindings to controls the platform's layout never reports (`Pad.Offers`), so the controls menu shows Circle alone for Interact instead of two unknown glyphs.
L and R are named `L1` and `R1`, the PlayStation glyphs closest to the PS Vita's L and R, and the device style is `PlayStationVita`, which the game draws with its PS4 glyphs.
The PS Vita's pad is always attached, as joystick 1, since Unity names it only some seconds after the start; joysticks 2 to 4 attach when Unity names them (a PS TV's pads), and the game's second player can join on one.
The menus read the same actions as play, so they work as with a PC pad, Cross in A's place.

Elsewhere (the Windows test players) every pad Unity names is read in the layout Unity gives an XInput pad: A, B, X, Y, the bumpers, View, Menu and the stick clicks on buttons 0 to 9, the sticks and d-pad on the PS Vita's axes, and the triggers on axes 8 and 9 if the project's InputManager has them, else on axis 2.
The keyboard (`Keys.cs`) reads InControl's keys as Unity KeyCodes; on the PS Vita it reads nothing.
`-t2d-input-log` logs every button and axis change of every pad (`T2D_INPUT`), for checking the layout on a console.

### Store services

`compat/Store.cs` is the offline store: no sign-in, no achievements, stats or leaderboards on Steam, Epic or GOG, and nothing that waits for them.
The platform pass stubs the three managers, sends `SetAchievement` and `SetStat` to `Store`, cuts the Steam end off `Achievements.unlock`, and puts `Store.Ready()` (true) in `UnityMain.Start`'s Steam check, so the game starts and no SteamManager is made.
`Utils.initUtils` sets the switches from `BuildFlags` instead: `STEAM_BUILD`, `STEAM_DECK` and `CONSOLE_BUILD` false on every player, `FULL_SCREEN` on desktop players, `NO_EXIT` on the PS Vita (a PS Vita application doesn't quit itself).
With `STEAM_BUILD` and `EPIC_BUILD` false the game is its plain PC build: high scores are local, and the options menu has the game's own achievements list.
Achievements still unlock in the game, in the save and with the game's banner; `-t2d-store-log` logs what the game would have sent.
The PS Vita isn't a console here: the game's console paths are its PS4, PS5 and Xbox ones, with their stores and saves.

After the pass no method of the game calls into Steamworks, so the pipeline leaves the assembly out, with its 72 gap methods and its 57 unresolved references.
`compat/Steamworks.cs` has an empty stand-in, of the same kind and generic arity, for each of the 25 top-level Steamworks types the game's metadata names; 9 are still used, as field, parameter and attribute types.

### Saves

`compat/Saves.cs` is butcher-vita's: the rewriter's `persistentDataPath` rule sends the game's four reads there, and it answers `ux0:data/T2DNoFate` on the PS Vita, which it creates, and the game's own path on desktop players.
The files stay the game's own, so a PC save (from `%USERPROFILE%\AppData\LocalLow\<company>\<product>`) copied to `ux0:data/T2DNoFate` loads on the PS Vita.
Its `Now` and `UtcNow` (the PS Vita's file clock) are there for the rewriter's clock rule, which this game never triggers.

### Screen and display

`compat/Window.cs` is butcher-vita's: the rewriter's screen rule sends `Screen.SetResolution` and `Screen.fullScreen` there; on the PS Vita they do nothing, and with `-t2d-windowed` a test player stays a 960×544 window.
`Resolution.refreshRateRatio` is `Unity6Shims.cs`'s, from 2018.2's `refreshRate`; the PS Vita doesn't reach it.
`BuildFlags.FullScreen` keeps the full-screen option out of the PS Vita's menu.
`compat/Display.cs` makes the CRT filter's 398×896 target with 2018.2's `RenderTexture.GetTemporary`, for `UnityMain.OnPreRender`.

### Sound

`compat/Sounds.cs`: `Sound.createSoundFromFile` becomes `Resources.Load<AudioClip>(name)` wrapped in the game's own `Sound(AudioClip)` with its `clipname`, as the method did for every sound the release ships.
The loose WAV files are left out with the `UnityWebRequest` code.

### Flags

`compat/Flags.cs` reads the `-t2d-*` flags from a Windows player's command line, and on the PS Vita from the first line of `ux0:data/T2DNoFate/args.txt`.

## The rules

`compat/rewriter/PlatformRules.cs` lists the rules as data, in the game's own names; `PlatformPasses.cs` applies them in the rewriter, on the game's assembly, before the type retargeting.
Every rule counts its sites: one that matches nothing is a `NO MATCH`, and one that finds what it expects in a shape it can't change is `UNRESOLVED`, so a different release of the game stops the code stage instead of building wrong.

| Kind | Target | Becomes | Sites |
| --- | --- | --- | --- |
| Stubbed (`*`: every method of the type and its nested types but constructors) | `SteamManager` | defaults | 21 |
| | `EpicManager`, `GalaxyManager` | defaults | 7, 2 |
| | `Platform.awardTrophy`, `mapTrophiesAndAward` | defaults | 2 |
| | `UnityMain.dlcSteamFound`, `dlcPS4LoadAssetBundle`, `dlcXB1LoadAssetBundle` | defaults | 3 |
| | `Utils.setSteamDeck` | nothing | 1 |
| | `PlayerPrefsX` (the PlayerPrefs save mode, never used; Rewind gaps and a `String._firstChar`) | defaults | 14 |
| Calls | `SteamUserStats.SetAchievement(string)` | `Store.SetAchievement` | 1 (after the stubs and the cut) |
| | `SteamUserStats.SetStat(string, int)` | `Store.SetStat` | 5 |
| Sequence | `UnityMain.Start`: `SteamManager.getInstance()`, `.m_bInitialized` | `Store.Ready()` | 1 |
| Forward | `Utils.systemLanguageSteam` | `Utils.systemLanguageStandalone` | 1 |
| Stores | `Utils.initUtils` | the 7 switches from `BuildFlags` | 7 |
| | `UnityMain.OnPreRender` | `crt_4hscale = Display.CrtTarget()` | 1 |
| Cut | `Achievements.unlock`, after `SetAchievement`'s result is popped | return; the unreachable rest removed | 1 |
| Sound loader | `Sound.createSoundFromFile` | `Sounds.Clip`, then `new Sound(clip) { clipname = name }` | 1 |

That is 68 sites in 59 methods.
InControl needs no rule: the roots leave it out, and the retargeting sends its types to `compat/InControl.cs`.

## How it is wired into the pipeline

These are in place on this branch:

- `pipeline.ROOTS` is `['Assembly-CSharp', '-InControl', '-Steamworks']`: the rewriter doesn't write InControl and Steamworks, and sends their types to T2DCompat by the naming convention (`Library.X` to `T2DCompat.Library_X`). `asset_rules.REPLACED_PLUGINS` already keeps their DLLs out of the project. `STUBBED_ON` is left as it was and finds nothing to stub.
- `pipeline.REWRITER_SOURCES` lists `compat/rewriter/PlatformRules.cs` and `PlatformPasses.cs`. `compat_sources()` takes only `compat/*.cs`, so the pass and the checker stay out of T2DCompat.
- `DowngradeAssemblies.Rewrite` calls `PlatformPatches` on the game's assembly before its type retargeting, and counts it as `pass PlatformPatches`. The rules name Steamworks' members by their own names, which the retargeting would rename. The log's line for the assembly starts with `platform=<sites>`.
- `Redirect.cs`'s `OptionalRules` has `DateTime.Now`: the clock rule runs once `T2DCompat.Saves` exists, and the game never reads the clock.
- `Saves.cs` and `Window.cs` turn on `Redirect.cs`'s `persistentDataPath` and screen rules; `Compat2018.cs`'s `TypeNames` and the BinaryFormatter rule do the rest of the saves.

A Cecil reader keeps a read module's type references as the file listed them, so the retargeting still meets the Steamworks types only the removed code used; that is why `Steamworks.cs` has all 25, and why the pass runs before the retargeting rather than as one of `Passes`.
The repair engine (another branch) runs before the rewriter's passes too; its rules for methods this pass replaces (`SteamManager`'s, `PlayerPrefsX`'s, `Utils.initUtils`, `systemLanguageSteam`, `setSteamDeck`, `Platform.awardTrophy`, `mapTrophiesAndAward`, `UnityMain.dlc*`, `OnPreRender`, `Sound.createSoundFromFile`) are not needed.

## Checking it

```sh
tools/downgrade/compat/check/compile.sh <out>          # T2DCompat as the pipeline compiles it, for both players
tools/downgrade/compat/check/build.sh [<out> [<Rewind's assemblies>]]   # PlatformCheck, then its rules check
uv run tools/downgrade/pipeline.py code                # the real thing
```

`compile.sh` compiles `compat/*.cs` with the .NET 10 SDK's Roslyn at `-langversion:5` and `-nostdlib`, against Unity 2018.2's unityaot `mscorlib`, `System` and `System.Core` and the engine modules the pipeline gives T2DCompat, once with `T2D_VITA` and once without.
Extra files after the output folder are compiled in too; with the pipeline's `Compat2018.cs` and `Unity6Shims.cs` it shows no name clashes.

`build.sh` builds `PlatformCheck.exe` as the rewriter is built (the .NET Framework's csc, C# 5, Mono.Cecil 0.10.0-beta6), and runs `PlatformCheck.exe rules` against each player's T2DCompat.dll.
The check:

1. finds every type, method, field, callee and instruction run a rule names in Rewind's Assembly-CSharp, and every T2DCompat method a rule names in T2DCompat.dll, with the replaced call's parameters and result;
2. finds, for each of the 19 InControl types and 89 members the game names, its T2DCompat counterpart with the same signature once InControl's types are renamed;
3. runs the platform pass on a copy of Assembly-CSharp through a stand-in for the rewriter's helpers (`check/Harness.cs`), retargets InControl's and Steamworks' types, writes it and reads it again: no problem, no rule without sites, every changed method passes a stack check (the rewriter's rules: depths agree, no underflow, a lone value at `ret`, nothing runs off the end), every T2DCompat type and member it names resolves, nothing names InControl or Steamworks, and no member of a Steamworks stand-in is used.

It passes for both players, against the pipeline's recovery (`work/recover/assemblies`) and against the assessment's.
It fails as it should when a compat member is renamed or a replacement's result type changes (tried by hand).
ILVerify 10.0.15 finds no error in the methods the pass rewrites; the ones left in `UnityMain.Start` and `Achievements.unlock` are the methods' other gaps, the same as before the pass.

The code stage in this worktree (the pipeline's recovery and export, through junctions to the pipeline's worktree):

| | Before | After |
| --- | --- | --- |
| Unresolved references | 245 (InControl 96, Steamworks 57, `Recovered.T2D` 92) | 61, all in `Recovered.T2D` |
| `NO MATCH` | 1, the clock rule, once `Saves` exists | none |
| Gap methods | `Recovered.T2D` 811, InControl 604 | `Recovered.T2D` 788 |
| `project` | | Plugins are `Recovered.T2D.dll` and `T2DCompat.dll`; InputManager keeps the 32 joystick axes |
| `tools/builder/il2cpp.py` | UnityLinker stopped in `InControl.PlayerActionSet::Load` | stops in `Stage.setDisplayState` |

## What is left

The 61 references left are the engine's binding calls IL2CPP inlined (`*_Injected`, `UnityEngine.Bindings.ThrowHelper`, `Unmarshal`), class library internals (`String._firstChar`, `System.Number`, `ThrowHelper`, `SpanHelpers`, `CompareInfo`, `Decimal`, `Random`), and `RenderTextureDescriptor`'s graphics formats; they belong to the API-gap pass and the repairs.
Some sit in platform code the layer keeps: `UnityMain.updateCRTSize`, `updatePixelSnap`, `ScreenStuff` and `FixedUpdate` (camera and material bindings), `Stage.setDisplayState` (string comparison, where UnityLinker stops now).

These gap methods in the platform code are the repairs' (`Recovered.T2D`'s markers, after the pass):

- Input: `PlayerActions..ctor` (22 sites: the `Key[]` arrays of its key bindings) and `CreateWithKeyboardBindings` (36), `UnityMain.OnEnable` (12: the players array, the device events' interlocked add), `OnDisable` (2), `CreatePlayer` (4), `detachControllers` (3), `Controls.staticInit` (33), `Controls.update` (1), and `UnityMain.BetterAnalog`, which returns its `Vector2` as an integer (Rewind issue 19). `Keys.One(int)` gives a one-key array, for the key bindings' repairs.
- Saves: `SaveSettings.loadSettings` (11), `saveSettings` (3), `SaveStats.loadStats` (14), `saveStats` (119), `initSettings`, `initStats`, the `setController*` methods, the static constructors.
- Screen and start: `UnityMain.Start` (15), `ScreenStuff` (8), `FixedUpdate` (8), `Achievements.unlock` (6, before the cut).

`SteamManager..cctor` (12 sites) and `LeaderboardState..ctor` (4) keep their gaps, but nothing touches either type once `STEAM_BUILD` is false.

## Risks and what is untested

Nothing here has run in a player: not on the PS Vita, and not in a Windows test player, since the build stops in UnityLinker before either exists.
So these are unproven until a build runs:

- The PS Vita's layout. It comes from InControl's PS Vita profile and butcher-vita's console runs; `-t2d-input-log` is there to check it. If the d-pad turns out to be buttons on this player, `Pad.cs` reads it from the wrong place.
- InControl's behaviour as the game relies on it: repeat, `UpdateTick`, rebinding's corner cases, dead zones with the game's `BetterAnalog`. The replacement follows InControl's source as Rewind recovered it, and was only compiled and signature-checked.
- Bindings saved by the PC game (InControl's binary format, Base64) don't load: a PC save's custom bindings fall back to the defaults. The port's own format is text.
- The BinaryFormatter binder covers only `new BinaryFormatter()`. In Rewind's output `SaveSettings.loadSettings` builds its formatter as an inlined constructor (an uninitialized object, then `.ctor`, a Rewind gap): unless its repair writes `newobj`, that formatter has no alias binder, can't find `Assembly-CSharp`'s types under IL2CPP, and the settings never load. The other five sites are `newobj`.
- UnityLinker's stripping: `SettingsBlob`, `StatsBlob` and `StatsBlobV1` are made only by BinaryFormatter, by reflection. A `link.xml` that preserves `com.bitmapbureau.terminator.savedata` in `Recovered.T2D` would be cheap insurance; the pipeline has none yet.
- `Application.systemLanguage` on the PS Vita, and the game's handling of a language it doesn't have (`systemLanguageStandalone` returns -1, as on the PC for those languages).
- The second player on a PS TV: joysticks 2 to 4 are read when Unity names them, untried.
- The Steamworks stand-ins have no members. If a later recovery or repair puts a Steamworks call back into a method the pass doesn't change, the rewriter reports it as unresolved, and the rule list needs that method.
- The rules match this release's IL. They were checked against two recoveries of build 24040403 (the assessment's and the pipeline's, with different signature libraries); another release may need them adjusted, which their `NO MATCH` and `UNRESOLVED` lines will say.
