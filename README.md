# Terminator 2D: NO FATE Vita

[Terminator 2D: NO FATE](https://store.steampowered.com/app/1718460/) on a PS Vita, as a port you build from your own copy. A pipeline takes the Steam release (Unity 6000.0.60f1, IL2CPP) and rebuilds it as a Unity 2018.2 project, since 2018.2 is the last Unity with a Vita build module, then builds that for the Vita.

The game ships only as native code (IL2CPP), so its code is first recovered with [Rewind](https://github.com/ratman-bit/Rewind-IL2CPP-Decompiler). With this project's fixes to Rewind, which are commits on its fork ([the fork](https://github.com/xob0t/Rewind-IL2CPP-Decompiler), see `tools/rewind/README.md`), it recovers 86% of the methods with native code whole (15,734 of 18,368). The pipeline's repair rules mend the game's own code that's left, except 3 methods the game never runs.

**Where it stands.** Playtested by hand on a PS Vita:

- The game works from start to finish: all 19 levels, story and arcade modes, saves, menus and achievements.
- 60 fps everywhere, with no freezes on loads.
- Left: a rare single slow frame, and a briefing skipped early waits a few seconds behind its fade.

More in [docs/build.md](docs/build.md#where-it-stands).

There are no game files in this repository: no game assets, and none of the recovered code. You bring your own copy, and the pipeline only reads it.

## Building

You need Windows 10 or 11 on x64, the game from Steam, Git, about 10 GB of free disk space, and a console on HENkaku or Ensō with VitaShell.

1. Install [uv](https://docs.astral.sh/uv/): open PowerShell from the Start menu and run

   ```powershell
   powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
   ```

2. Download [t2d-no-fate-vita.zip](https://github.com/xob0t/t2d-no-fate-vita/releases/latest/download/t2d-no-fate-vita.zip), right-click it and choose **Extract All** (or clone this repository).

3. Open the extracted `t2d-no-fate-vita` folder, right-click in it, choose **Open in Terminal** (on Windows 10: Shift + right-click, **Open PowerShell window here**), and run:

   ```powershell
   uv run build.py
   ```

   The VPK is `builds/t2d-2018.2.19f1-vita.vpk`. Install it with VitaShell.

A first build takes about 20 minutes and leaves about 7.5 GB in the folder. It doesn't install Unity or need a Unity license: setup downloads Unity 2018.2.19f1's installers and extracts the files the build reads, Sony's PS Vita SDK 3.570 from archive.org (unless you have one), this project's [Rewind fork](https://github.com/xob0t/Rewind-IL2CPP-Decompiler) at a pinned commit with two .NET SDKs to build and run it, AssetRipper and Mono.Cecil. A rerun picks up where the last one stopped.

- `--game <folder>` reads the game from that folder, if it isn't in Steam's default folder.
- `--vita-sdk <archive or folder>` uses your own copy of the SDK.

[docs/build.md](docs/build.md) covers the setup, the pipeline's stages and the options.

## License

The pipeline is MIT-licensed ([LICENSE](LICENSE)); much of it comes from the author's earlier ports under the same license. Rewind, which setup downloads and builds, is Apache-2.0.
