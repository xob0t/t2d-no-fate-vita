# PS Vita texture test

A small Unity 2018.2.19f1 PS Vita app (title `TDTX00001`) that measures what Terminator 2D: NO FATE's 4096×4096 atlases cost on the console: load time, free main memory (LPDDR) and video memory (CDRAM), how many fit, rendering and frame time.
No game content is here; `build.py` copies the atlases from `work/assetripper/export-stubs` into the project under `work/vita-texture-test`.

- `unity/TexTest.cs`: the app. It reads its steps from `ux0:data/t2dtex/args.txt` (`single`, `fill`, `draw`, `idle`; see the header comment), writes `ux0:data/t2dtex/log.txt` and `shot-<variant>.png`, and sets the clocks to 444/222 MHz with a copy of butcher-vita's ButcherClock plugin.
- `unity/TexTestBuild.cs`: the editor's batch build. It imports `tp0_<format>_<size>.png` in that PS Vita format, makes the scene and builds a PC Hosted player.
- `build.py`: makes the project, builds it and packages `work/vita-texture-test/t2d-texture-test.vpk`.
- `vt.py`: installs the VPK, runs a test and fetches the log and screenshots into `work/vita-texture-test/runs/<tag>`. Run it only inside the shared `vita-device` lock:

```sh
python -I tools/assess/vita-texture-test/build.py
python ~/.t3/projects/butcher-vita/tools/downgrade/lock.py run vita-device "t2d: texture test" --wait 1800 -- \
  bash -c "python -I tools/assess/vita-texture-test/vt.py install && \
           python -I tools/assess/vita-texture-test/vt.py run s1 'single png,dxt5_4096; fill dxt5_4096'"
```

PS Vita title IDs are four letters and five digits; the editor rejects `T2DT00001`.
The PS Vita importer stores PVRTC as DXT5, so the `pvrtc*` variants test nothing new.
