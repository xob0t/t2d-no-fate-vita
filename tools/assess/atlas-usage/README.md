# Atlas usage

Static analysis of which texture-pack frames each level, cutscene and menu of Terminator 2D: NO FATE can draw, and how much texture memory a PS Vita port needs under different atlas schemes.
The scripts read the Steam build, Rewind's recovered C# and its assemblies, and write everything to `work/atlas-usage/`; nothing here contains game data.

Run in this order from the repository root (Python 3 with capstone, pefile, dnfile, numpy, Pillow):

```sh
GAME="/c/Program Files (x86)/Steam/steamapps/common/Terminator 2D NO FATE"
SRC=work/rewind-steam/out/src/Assembly-CSharp
ASM=work/rewind-steam/out/assemblies/Assembly-CSharp.dll
DUMP=work/rewind-steam/dump/dump.cs
EXP=work/assetripper/export-stubs/ExportedProject/Assets/Resources
OUT=work/atlas-usage

# 1. UV values Rewind drops (bounds-checked double[] stores in AssetsPack.staticInit6..9)
for p in "uvx 0x1804319E0" "uvy 0x180514150" "uvw 0x180529870" "uvh 0x18053E8D0"; do set -- $p
  python -I tools/assess/atlas-usage/native_dstores.py "$GAME/GameAssembly.dll" $2 0x1800026F0 0x180002600 1211 > $OUT/native_$1.json; done
# 2. image/frame -> pack, rect, trim table (images.json, images.csv)
python -I tools/assess/atlas-usage/extract_images.py --src $SRC --asm $ASM --native-dir $OUT --out $OUT
# 3. map objects (compiled map tables joined with the Tiled JSON)
python -I tools/assess/atlas-usage/extract_maps.py --src $SRC --asm $ASM --maps $EXP/maps --out $OUT
# 4. the three switch jump tables in Sprites.addFromJSON that Rewind leaves as __call_indirect
for t in "0x4aaf9c 0x4aae3c 183 0 0" "0x4aac94 0x4aac18 61 430 430" "0x4aad00 0x4aacd4 54 340 340"; do set -- $t
  python -I tools/assess/atlas-usage/native_dispatch.py "$GAME/GameAssembly.dll" $DUMP $1 $2 $3 $4 > $OUT/native_addfromjson_$5.json; done
# 5. class -> images / classes graph, sprite id -> image, map object -> classes (class_refs.json)
python -I tools/assess/atlas-usage/analyze_refs.py --src $SRC --work $OUT
# 6. opacity of every frame rect in the PNG packs (rect_alpha.json)
python -I tools/assess/atlas-usage/measure_alpha.py --images $OUT/images.json --packs $EXP/texturepack --out $OUT/rect_alpha.json
# 7. per-scene image and pack sets (scenes.json, scenes.csv)
python -I tools/assess/atlas-usage/build_scenes.py --work $OUT
# 8. memory per scheme (memory.csv, memory.md), all-resident variant, scene-clustered static pages
python -I tools/assess/atlas-usage/memory_model.py --work $OUT
python -I tools/assess/atlas-usage/all_resident.py --work $OUT
python -I tools/assess/atlas-usage/cluster_atlas.py --work $OUT --page 1024
```

The addresses in steps 1 and 4 are for the Steam build analysed here (`GameAssembly.dll` 18,269,184 bytes); for another build take the method VAs from `dump.cs` and the jump-table offsets from the `movzx`/`mov` pair before each `jmp rcx` in `Sprites.addFromJSON`.

- `parse_cs.py`: parsers for the literal tables in Rewind's C#.
- `csinterp.py`: a small concrete interpreter for one decompiled method (if/switch/goto, integer locals; unknown conditions fork). Used to run `Sprites.loadFromID` (sprite id to image) and `Sprites.addFromJSON` (map object to classes) for every input value.
- `native_dstores.py`: recovers the values IL2CPP stores into one-element `double[]` arrays from the native code.
- `native_dispatch.py`: resolves an IL2CPP switch jump table to the methods each case calls, with the integer arguments.
- `analyze_refs.py`: per class, the `Assets.IMAGE_*`/`IMAGE_ARRAY_*`/`Res.*` images and the sprite ids it can draw (image contexts plus a parameter-flow fixpoint over constructors and methods), and the classes it references.
- `build_scenes.py`: closure per scene from its root classes and maps, plus a global set; stops at hub classes and at other scenes.
- `memory_model.py`, `all_resident.py`, `cluster_atlas.py`: memory estimates (skyline packer, 1 px padding, power-of-two textures).
- `coverage.py`: a static check of the scene data, below.

## The coverage check

Every image a scene draws that its scene set lacks is an atlas miss on the console, a frame of about 200 ms.
`coverage.py` looks for them without the console, from Rewind's C#, the analysis and a packing's `frames.bytes`:

```sh
python -I tools/assess/atlas-usage/coverage.py --src $SRC --usage $OUT --frames work/atlas/atlas/frames.bytes \
    [--scenes tools/downgrade/atlas/scenes.json] [--later <a later scenes.json>] [--names IMAGE_X ...]
```

It writes `work/coverage/coverage.md` and `coverage.json`:

- Suspects per scene, with the pages their frames are on now and the MB those pages and the frames themselves take. It finds them independently of `build_scenes.py`'s closure: hub methods followed one by one from the scene's own code (Game's loop only in the levels), BitmapData fields of the level scripts a menu makes, field flow (an image read from a field gets every value assigned to it), arithmetic on an image id (the base's numbered family), and partial numbered families.
- Images in no scene: those code names (reached by a scene's code or not), those only the sprite table (`Sprites.loadFromID`) names, which code reaches by arithmetic, a field or not at all, and those nothing names.
- Every image site the analysis can't resolve, by kind: arithmetic, fields, other objects' members, method results, array elements, native code, delegates, classes named by namespace.
- Class names defined twice, and whether the analysis read another namespace's file for a game class (shadowed).
- With `--later`, which of the (image, scene) pairs a later scene data adds it flags: on the analysis before round 5 (771f2af^) it flags 491 of the 498 pairs round 5 added, all but the HK tank's six images and one of its shots in Future War 3, which come through the shadowed Platform class it reports.

Its suspects are broader than what a scene draws: they are where to look, not data to add.
On the analysis of round 6 (Nov 2026) the rest are the Database viewer's enemies' shots and debris (the viewer calls only their constructor, `changeState`, `databaseUpdate` and `draw`) and four literals summed to an id that aren't sprites.
