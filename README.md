<img src="assets/icon.svg" width="64" height="64" alt="">

# Terminator 2D: NO FATE Vita

A work in progress: [Terminator 2D: NO FATE](https://store.steampowered.com/app/1718460/) on a PS Vita. A pipeline takes the Steam release (Unity 6000.0.60f1, IL2CPP) and rebuilds it as a Unity 2018.2 project, since 2018.2 is the last Unity with a Vita build module, then builds that for the Vita. It follows [Huntdown's port](https://github.com/xob0t/huntdown-vita) and Butcher's, and reuses their tools where they fit.

The game ships only as native code (IL2CPP), so its code is first recovered with [Rewind](https://github.com/ratman-bit/Rewind-IL2CPP-Decompiler). With this repository's patches to Rewind (`tools/rewind/patches`), it gets 79% of all the game's methods back whole, and 93% of the game's own; the rest have gaps that need repair by hand or in Rewind.

**It isn't finished.** The pipeline runs from your copy of the game to a VPK, and on a PS Vita the game boots to its menu and plays its first levels, Future War 1 and Desert Town, but 39 recovered methods still have gaps, so parts of the game stop at them. [docs/build.md](docs/build.md#where-it-stands) says how far the build gets, and [docs/assessment.md](docs/assessment.md) how much work is left.

There are no game files in this repository: no game assets, and none of the recovered code. You bring your own copy, and the pipeline only reads it.

## Building

You need Windows 10 or 11 on x64, the game from Steam, and about 10 GB of free disk space.

1. Install [uv](https://docs.astral.sh/uv/): open PowerShell from the Start menu and run

   ```powershell
   powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
   ```

2. Clone this repository, open its folder in a terminal, and run:

   ```powershell
   uv run build.py
   ```

   The VPK is `builds/t2d-2018.2.19f1-vita.vpk`.

It doesn't install Unity or need a Unity license: setup downloads Unity 2018.2.19f1's installers and extracts the files the build reads, Sony's PS Vita SDK 3.570 from archive.org (unless you have one), Rewind at a pinned commit with two .NET SDKs to build and run it, AssetRipper and Mono.Cecil. Git must be installed. A rerun picks up where the last one stopped.

- `--game <folder>` reads the game from that folder, if it isn't in Steam's default folder.
- `--vita-sdk <archive or folder>` uses your own copy of the SDK.

[docs/build.md](docs/build.md) covers the setup, the pipeline's stages and the options.

## License

The pipeline is MIT-licensed ([LICENSE](LICENSE)); much of it comes from the author's earlier ports under the same license. Rewind, which setup downloads and builds, is Apache-2.0.
