# PS Vita audio test

A small Unity 2018.2.19f1 PS Vita app (title `TDAU00001`) that measures what Terminator 2D: NO FATE's sound costs on the console with the settings of `../audio_rules.py`.
It measures load time and main memory of all 930 clips loaded as the game loads them, what a loaded streamed clip costs, and Play()'s time on the main thread.
It also measures frame times and CPU with several streams and in-memory voices playing at once, and what the importer made of each format.
No game content is here; `build.py` copies the clips from the export into the project under `work/vita-audio-test`.

- `unity/AudioTest.cs`: the app. It reads its steps from `ux0:data/t2daudio/args.txt` (`vol`, `load`, `loadasync`, `rule`, `streamcost`, `play`, `probe`, `idle`; see the header comment), writes `ux0:data/t2daudio/log.txt`, and sets the clocks to 444/222 MHz with a copy of butcher-vita's ButcherClock plugin.
- `unity/AudioTestBuild.cs`: the editor's batch build. The clips' settings come from their `.meta` files; it logs the probe clips' import, makes the scene and builds a PC Hosted player.
- `rule` runs the start-up rule's helper itself: `build.py` copies `../compat/SoundLoads.cs` and the platform layer's `compat/Sounds.cs` into the project.
- `build.py`: makes the project (the game's clips with `audio_rules.py`'s `.meta`, 20 streamed copies of one effect with and without preload, the probe clips), builds it and packages `work/vita-audio-test/t2d-audio-test.vpk`. `--no-game` leaves the game's clips out.
- `check_build.py`: reads the built player's AudioClips and their FSB5 banks with UnityPy: load type, format, codec, rate and channels as built.
- `va.py`: installs the VPK, runs a test and fetches the log into `work/vita-audio-test/runs/<tag>`. Run it only inside the shared `vita-device` lock, held as `t2d: audio`:

```sh
python -I tools/downgrade/audio/vita-audio-test/build.py
uv run --with unitypy==1.25.3 --with numpy python -I tools/downgrade/audio/vita-audio-test/check_build.py
python ~/.t3/projects/butcher-vita/tools/downgrade/lock.py run vita-device "t2d: audio" --wait 1800 -- \
  bash -c "python -I tools/downgrade/audio/vita-audio-test/va.py install && \
           python -I tools/downgrade/audio/vita-audio-test/va.py run a1 'probe; load; play 0 0 6; play 2 24 8'"
```

The sound plays at a fifth of full volume (`vol` changes it); keep runs short on a shared console.
