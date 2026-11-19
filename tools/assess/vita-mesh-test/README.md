# PS Vita mesh test

A small Unity 2018.2.19f1 PS Vita app (title `TDMS00001`) that measures what Terminator 2D: NO FATE's sprite meshes cost on the console.
The game builds 4 `Mesh` objects per sprite frame at startup (`BitmapData.CreateMultiMeshFrames`: `new Mesh()`, `MarkDynamic()`, 4 vertices, 4 UVs, 6 indices, `RecalculateNormals()`), over 150,000 in all, and draws screen sprites one `Graphics.DrawMesh` each with a `MaterialPropertyBlock` into a 398×224 camera target.
No game content is here: frames, sprites and the four 1024² atlases are synthetic.

- `unity/MeshTest.cs`: the app. It reads its steps from `ux0:data/t2dmesh/args.txt` (`create`, `free`, `draw`, `idle`; see the header comment), writes `ux0:data/t2dmesh/log.txt` and `shot-<method>.png`, and sets the clocks to 444/222 MHz with butcher-vita's ButcherClock plugin.
  - `create <flags> <N,...> [touch] [keep]` makes meshes the game's way up to each N (flags `d` MarkDynamic, `n` normals, `u` `UploadMeshData(true)`; `game` = `dn`) and logs free main (LPDDR), video (CDRAM) and physically contiguous memory, Unity's allocator and the GC heap, and the creation time. It stops when less than 40 MB of main memory is left.
  - `draw <method> <K,...> [run=R]` draws K sprites per frame with vsync off and logs the frame time and the main-thread submit time. Methods: `drawmesh` (the game's screen path), `drawnow` (the game's off-screen path, `SetPass` + `DrawMeshNow`), `batch` (one dynamic mesh rebuilt per frame, one submesh per run of one atlas), `instanced` (`DrawMeshInstanced`; skipped when the console doesn't support it), `spritebatch` (the port's `T2DCompat.SpriteBatch`, called per sprite as the rewritten `unityDraw` calls it, one `Flush` per frame; docs/drawing.md) and `drawmeshsb` (`drawmesh` with the game's exact quads, from `SpriteBatch.FrameQuad`). The atlas changes every R sprites (16 by default). `still` keeps the sprites in place, so `shot-spritebatch-still.png` and `shot-drawmeshsb-still.png` can be compared. Screenshots are `shot-<method>[-still][-run<R>].png`, at K=1000.
- `unity/AtlasStub.cs`: stands in for the port's `T2DCompat.Atlas`; `build.py` copies `tools/downgrade/spritebatch/compat/SpriteBatch.cs` into the project beside it.
- `unity/MeshTestSprite.shader`: unlit, alpha blended, with an instanced UV rect.
- `unity/MeshTestBuild.cs`: the editor's batch build (material, scene, PC Hosted player).
- `build.py`: makes the project under `work/vita-mesh-test`, builds it and packages `work/vita-mesh-test/t2d-mesh-test.vpk`. It reuses the texture test's `build.py` for packaging. `--dynamic-batching 0|1` sets the player's dynamic batching (the project's default for PS Vita is off; Unity keeps the last value in the project).
- `vt.py`: the texture test's `vt.py` with this app's title, folder and runs folder (`work/vita-mesh-test/runs/<tag>`). Run it only inside the shared `vita-device` lock:

```sh
python -I tools/assess/vita-mesh-test/build.py
python ~/.t3/projects/butcher-vita/tools/downgrade/lock.py run vita-device "t2d: mesh test" --wait 3600 -- \
  bash -c "python -I tools/assess/vita-mesh-test/vt.py install && \
           python -I tools/assess/vita-mesh-test/vt.py run c1 'create game 10000,50000,100000,150000 touch'"
```

Results of the 2026-11-19 runs: `work/vita-mesh-test/REPORT.md`. In short, each game mesh costs about 1.4 KB of main memory (1.8 KB once drawn) and 0.4–1 ms to create, and the game's `DrawMesh` path costs 14–41 µs per sprite. `UploadMeshData(true)`, dropping normals, `MarkDynamic` and dynamic batching barely change either. Instancing isn't supported.
Run `vt.py run` with steps that finish within its 15-minute limit: a `create` of 150k game meshes takes about 2.5 minutes plus 1–2 minutes to free.
