# ruff: noqa: UP031
"""The pipeline's texture step: pipeline.asset_overlay calls atlas_overlay, which runs repack.py when its inputs
changed and returns the pages and tables as overlay entries for Assets/Resources/atlas, with the exported texture
packs left out of the project (docs/textures.md).

Inputs: Rewind's Assembly-CSharp.dll (the recover stage's, not the rewritten one: the rules replace the very tables
repack.py reads) and the export's Resources/texturepack/TexturePackN.bytes. Output: work/atlas (the files, a stamp)
and artifacts/downgrade/atlas (report.json, memory.md).
"""

import hashlib
import os
import struct
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
    if not (stamp.exists() and stamp.read_text() == current and _missing(out, raw) == []):
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
        # The stamp goes first: a repack cut off part way leaves some files new and some old, which the old stamp would
        # otherwise vouch for once the inputs are back to what it names.
        stamp.unlink(missing_ok=True)
        code = subprocess.run(command, check=False).returncode
        if code:
            sys.exit(f'The texture repacker failed (exit code {code}); see {report / "report.json"}')
        stamp.write_text(current)
    # A .part is a file repack.py was writing when it was stopped (write_if_changed), not one of its files.
    overlay = {RESOURCES / 'atlas' / f.name: f for f in sorted(out.iterdir()) if f.is_file() and f.suffix != '.part'}
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


PNG_END = b'\x00\x00\x00\x00IEND\xaeB`\x82'


def _missing(out: Path, raw: bool):
    """The files repack.py writes that out lacks or has incomplete: the tables, read to their end, and every page
    frames.bytes counts (a PNG that ends with IEND and has the size frames.bytes gives it; a raw page of exactly
    w * h * 2 bytes), each with its .meta. The stamp alone let a run reuse a folder that had lost a page, and a
    check of the tables' first bytes accepted one cut short."""
    frames = out / 'frames.bytes'
    try:
        sizes = _frame_sizes(frames.read_bytes())
    except (OSError, ValueError, struct.error):
        return [frames.name]
    bad = []
    try:
        _check_scenes((out / 'scenes.bytes').read_bytes(), len(sizes))
    except (OSError, ValueError, struct.error):
        bad.append('scenes.bytes')
    for p, (w, h) in enumerate(sizes):
        page = out / ('p%03d%s' % (p, '.bytes' if raw else '.png'))
        if not _page_ok(page, w, h, raw):
            bad.append(page.name)
    names = ['frames.bytes', 'scenes.bytes', *('p%03d%s' % (p, '.bytes' if raw else '.png') for p in range(len(sizes)))]
    metas = [out / (n + '.meta') for n in names]
    return bad + [m.name for m in metas if not m.is_file() or m.stat().st_size == 0]


def _frame_sizes(data: bytes):
    """frames.bytes's page sizes, after reading it to its end (repack.frames_bytes's format); ValueError when it
    isn't whole."""
    if data[:4] != b'T2DF':
        raise ValueError('not frames.bytes')
    _format, rows, pages = struct.unpack_from('<III', data, 4)
    sizes = [struct.unpack_from('<HH', data, 16 + 4 * i) for i in range(pages)]
    pos = 16 + 4 * pages
    for _ in range(rows):
        (n,) = struct.unpack_from('<I', data, pos)
        pos += 4 + 10 * n
    if pos != len(data):
        raise ValueError('frames.bytes is %d bytes, its rows need %d' % (len(data), pos))
    return sizes


def _check_scenes(data: bytes, pages: int):
    """Reads scenes.bytes to its end (repack.scenes_bytes's format); ValueError when it isn't whole or names a page
    frames.bytes doesn't have."""
    if data[:4] != b'T2DS':
        raise ValueError('not scenes.bytes')
    pos = 12

    def u16():
        nonlocal pos
        (v,) = struct.unpack_from('<H', data, pos)
        pos += 2
        return v

    def text():
        nonlocal pos
        n = u16()
        if pos + n > len(data):
            raise ValueError('cut short')
        pos += n

    def page_list():
        for _ in range(u16()):
            if u16() >= pages:
                raise ValueError('a page past frames.bytes')

    (scenes,) = struct.unpack_from('<I', data, 8)
    for _ in range(scenes):
        text()
        page_list()
    (keys,) = struct.unpack_from('<I', data, pos)
    pos += 4
    for _ in range(keys):
        text()
        for _ in range(u16()):
            u16()
    for _ in range(u16()):
        text()
    page_list()
    if pos != len(data):
        raise ValueError('scenes.bytes has %d bytes past its end' % (len(data) - pos))


def _page_ok(page: Path, w: int, h: int, raw: bool):
    """Whether a page file is whole: a raw page of w * h * 2 bytes, or a PNG of w x h that ends with its IEND chunk."""
    try:
        size = page.stat().st_size
        if raw:
            return size == w * h * 2
        with open(page, 'rb') as f:
            head = f.read(24)
            f.seek(max(0, size - len(PNG_END)))
            end = f.read()
    except OSError:
        return False
    return _png_ok(head, end, w, h)


def _png_ok(head: bytes, end: bytes, w=None, h=None):
    if len(head) < 24 or head[:8] != b'\x89PNG\r\n\x1a\n' or head[12:16] != b'IHDR' or end != PNG_END:
        return False
    return w is None or struct.unpack('>II', head[16:24]) == (w, h)


def _png_whole(path: Path):
    """Whether a PNG file ends with its IEND chunk (a tile cut short by a stopped run doesn't)."""
    try:
        size = path.stat().st_size
        with open(path, 'rb') as f:
            head = f.read(24)
            f.seek(max(0, size - len(PNG_END)))
            return _png_ok(head, f.read())
    except OSError:
        return False


def _save_atomic(image, path: Path):
    """Saves a PNG beside path and renames it over path: a run stopped part way leaves the old file or none, never
    half a PNG."""
    part = path.with_name(path.name + '.part')
    image.save(part, 'PNG', compress_level=6)
    os.replace(part, path)


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
                    # Cut again when older than its page, or not whole (a tile written before tiles were saved
                    # atomically, stopped part way).
                    if not path.exists() or path.stat().st_mtime < png.stat().st_mtime or not _png_whole(path):
                        _save_atomic(im.crop(box), path)
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
