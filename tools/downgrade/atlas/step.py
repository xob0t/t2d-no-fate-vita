# ruff: noqa: UP031
"""The pipeline's texture step: pipeline.asset_overlay calls atlas_overlay, which runs repack.py when its inputs
changed and returns the pages and tables as overlay entries for Assets/Resources/atlas, with the exported texture
packs left out of the project (docs/textures.md).

Inputs: Rewind's Assembly-CSharp.dll (the recover stage's, not the rewritten one: the rules replace the very tables
repack.py reads) and the export's Resources/texturepack/TexturePackN.bytes. Output: work/atlas (the files, a stamp)
and artifacts/downgrade/atlas (report.json, memory.md).
"""

import hashlib
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
RESOURCES = Path('Assets') / 'Resources'
PACKS = RESOURCES / 'texturepack'
STAMP = 't2d-atlas.txt'


def _stamp(assembly: Path, packs: Path, page: int, raw: bool):
    digest = hashlib.sha256()
    for path in [assembly, *sorted(packs.glob('TexturePack*.bytes')), HERE / 'scenes.json', *sorted(HERE.glob('*.py'))]:
        digest.update(path.name.encode() + b'\0')
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    digest.update(('%d %d' % (page, raw)).encode())
    return digest.hexdigest()


def atlas_overlay(exported: Path, assembly: Path, work: Path, report: Path, page=1024, raw=False, log=print, tile=1024):
    """(overlay, excluded): overlay maps Assets/Resources/atlas/<file> to the file in work/atlas/atlas; excluded
    lists the export's texture packs (and their folder), which the game no longer loads.
    raw: False (the default) for PNG pages with RGBA 4444 import settings, which the editor and tools/builder
    import as Texture2D assets; True for raw RGBA 4444 TextAssets (repack.py --raw) that Atlas.cs makes into
    textures at load time, the fallback (T2D_ATLAS_PAGES=raw). Both hold the same texels.
    tile: PNG pages larger than this go to the project as tiles of at most tile x tile (pNNN_K.png, K row by row from the
    bottom), which Atlas.cs loads one by one and SpriteBatch draws a frame across: the player integrates a 4096x4096
    page in one 0.7 s frame, a 1024x1024 tile in a few ms. tiles.bytes (u16 tile size) tells Atlas.cs. 0: no tiles."""
    packs = exported / PACKS
    if not packs.is_dir():
        sys.exit(f'No texture packs in {packs}: run pipeline.py rip first')
    out = work / 'atlas'
    stamp = work / STAMP
    current = _stamp(assembly, packs, page, raw)
    if not (stamp.exists() and stamp.read_text() == current and (out / 'frames.bytes').exists()):
        log('Repacking the texture packs into pages (tools/downgrade/atlas/repack.py)')
        command = [
            sys.executable,
            '-I',
            str(HERE / 'repack.py'),
            '--asm',
            str(assembly),
            '--packs',
            str(packs),
            '--out',
            str(out),
            '--report',
            str(report),
            '--page',
            str(page),
        ] + (['--raw'] if raw else [])
        code = subprocess.run(command, check=False).returncode
        if code:
            sys.exit(f'The texture repacker failed (exit code {code}); see {report / "report.json"}')
        stamp.write_text(current)
    overlay = {RESOURCES / 'atlas' / f.name: f for f in sorted(out.iterdir()) if f.is_file()}
    if not raw and tile:
        for name, path in _tiles(out, work / 'tiles', tile, log).items():
            overlay[RESOURCES / 'atlas' / name] = path
        for big in _big_pages(out, tile):
            overlay.pop(RESOURCES / 'atlas' / big.name, None)
            overlay.pop(RESOURCES / 'atlas' / (big.name + '.meta'), None)
    folder_guid = hashlib.md5(b'folder:Assets/Resources/atlas').hexdigest()
    overlay[RESOURCES / 'atlas.meta'] = (
        f'fileFormatVersion: 2\nguid: {folder_guid}\nfolderAsset: yes\nDefaultImporter:\n  userData:\n'.encode()
    )
    excluded = {PACKS, PACKS.with_name(PACKS.name + '.meta')}
    excluded |= {PACKS / f.name for f in packs.iterdir()}
    return overlay, excluded


def _big_pages(out: Path, tile: int):
    from PIL import Image

    big = []
    for png in sorted(out.glob('p[0-9][0-9][0-9].png')):
        with Image.open(png) as im:
            if im.width > tile or im.height > tile:
                big.append(png)
    return big


def _tiles(out: Path, tiles: Path, tile: int, log=print):
    """Cuts each PNG page larger than tile into PNGs of at most tile x tile in `tiles`, with the page's import settings
    (repack.TEXTURE_META), and writes tiles.bytes. The texels are the page's, byte for byte: the PNGs hold each
    4-bit channel times 17 and are only cropped. Returns {file name: path}."""
    from PIL import Image

    sys.path.insert(0, str(HERE))
    import repack  # noqa: PLC0415  (TEXTURE_META, guid, write_if_changed)

    tiles.mkdir(parents=True, exist_ok=True)
    made = {}
    for png in _big_pages(out, tile):
        with Image.open(png) as im:
            im.load()
            w, h = im.size
            tw, th = min(w, tile), min(h, tile)  # a 2048x512 page: 1024x512 tiles
            if w % tw or h % th:
                sys.exit(f'{png.name} is {w}x{h}, not a multiple of the {tile} tile')
            cols, rows = w // tw, h // th
            for r in range(rows):  # r from the bottom, as the texture's v
                for c in range(cols):
                    name = '%s_%d.png' % (png.stem, r * cols + c)
                    box = (c * tw, h - (r + 1) * th, (c + 1) * tw, h - r * th)
                    path = tiles / name
                    if not path.exists() or path.stat().st_mtime < png.stat().st_mtime:
                        im.crop(box).save(path, 'PNG', compress_level=6)
                    made[name] = path
                    meta = tiles / (name + '.meta')
                    repack.write_if_changed(str(meta), repack.TEXTURE_META.format(guid=repack.guid(name)).encode())
                    made[name + '.meta'] = meta
    table = tiles / 'tiles.bytes'
    repack.write_if_changed(str(table), tile.to_bytes(2, 'little'))
    repack.write_if_changed(str(tiles / 'tiles.bytes.meta'), repack.TEXT_META.format(guid=repack.guid('tiles.bytes')).encode())
    made['tiles.bytes'] = table
    made['tiles.bytes.meta'] = tiles / 'tiles.bytes.meta'
    log('%d tiles of %dx%d for the pages larger than that' % (sum(1 for n in made if n.endswith('.png')), tile, tile))
    return made
