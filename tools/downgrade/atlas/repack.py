# ruff: noqa: N806, SIM115, UP031
"""Repacks the game's twelve 4096x4096 texture packs into small pages grouped by the scenes that use each frame,
for the PS Vita port (docs/textures.md).

Every frame keeps one page and one rect for the whole game, so the game's frame table and the meshes it bakes
from it never change at run time; a scene only needs the pages that hold its frames. Frames with the same pixels
share one rect. Each rect has `--pad` transparent pixels on every side (other rects and the page border), so
point sampling at a rect's edge never reads a neighbour, and the game's flipped meshes, which sample at uv - 1
and rely on Repeat wrapping, read the same texels.

Inputs: the game folder (packs from resources.assets; or --packs, an AssetRipper export's texturepack folder),
Rewind's Assembly-CSharp.dll (the frame table, frametable.py) and scenes.json (scene_sets.py).

Outputs, in --out (a Resources folder of the Unity project, e.g. Assets/Resources/atlas):
  p000.png ...       pages, 8-bit RGBA PNG with each channel rounded to 4 bits and stored as n * 17, so the
                     import's truncation to RGBA 4444 (the editor's and tools/builder's) keeps n; their .meta
                     imports them as RGBA 4444 (PS Vita and default), point filtering, Repeat wrapping, no
                     mipmaps, not readable
  or, with --raw,
  p000.bytes ...     pages as raw RGBA 4444 TextAssets (rows bottom first, u16 per texel, R in the top nibble),
                     the same texels; Atlas.cs makes the textures with LoadRawTextureData
  frames.bytes       page and rect of every frame of every image row (format in Atlas.cs)
  scenes.bytes       scenes, their keys and pages (format in Atlas.cs)
and in --report: report.json and memory.md (pages and RGBA 4444 MB per scene).

After writing, every frame is read back through frames.bytes and the written pages and compared with the
original pack rounded to 4 bits per channel, byte for byte, along with its transparent border;
any difference stops the build.

Usage: python -I repack.py --asm <Rewind>/assemblies/Assembly-CSharp.dll --game <game folder>
                           --out <project>/Assets/Resources/atlas --report <dir> [--packs <folder>] [--dump dump.cs]
"""

import argparse
import collections
import hashlib
import io
import json
import os
import struct
import sys
import time

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import frametable
import packs as packs_mod

HERE = os.path.dirname(os.path.abspath(__file__))
MB = 1024 * 1024
LANGS = ('CNS', 'CNT', 'JP', 'KR')
FRAMES_MAGIC, SCENES_MAGIC, FORMAT = b'T2DF', b'T2DS', 1
MAX_TEXTURE = 4096


def pow2(n):
    p = 1
    while p < n:
        p *= 2
    return p


class Skyline:
    """Bottom-left skyline packer (from tools/assess/atlas-usage/memory_model.py)."""

    def __init__(self, w, h):
        self.w, self.h = w, h
        self.sky = [(0, 0, w)]
        self.used = 0
        self.top = 0

    def find(self, rw, rh):
        best = None
        for i, (x, y, _w) in enumerate(self.sky):
            if x + rw > self.w:
                break
            top, need, j = y, rw, i
            while need > 0:
                _sx, sy, sw = self.sky[j]
                top = max(top, sy)
                need -= sw
                j += 1
            if top + rh > self.h:
                continue
            if best is None or (top + rh, x) < (best[0], best[1]):
                best = (top + rh, x, top)
        return best

    def place(self, rw, rh):
        b = self.find(rw, rh)
        if b is None:
            return None
        _, x, top = b
        new = []
        for sx, sy, sw in self.sky:
            ex = sx + sw
            if ex <= x or sx >= x + rw:
                new.append((sx, sy, sw))
                continue
            if sx < x:
                new.append((sx, sy, x - sx))
            if ex > x + rw:
                new.append((x + rw, sy, ex - x - rw))
        new.append((x, top + rh, rw))
        new.sort()
        merged = []
        for s in new:
            if merged and merged[-1][1] == s[1] and merged[-1][0] + merged[-1][2] == s[0]:
                merged[-1] = (merged[-1][0], s[1], merged[-1][2] + s[2])
            else:
                merged.append(s)
        self.sky = merged
        self.used += rw * rh
        self.top = max(self.top, top + rh)
        return x, top


# ---- inputs ---------------------------------------------------------------------------------------------------


def decode(png):
    im = Image.open(io.BytesIO(png))
    im = im.convert('RGBA') if im.mode != 'RGBA' else im
    return np.asarray(im)


def png_rect(arr, f):
    """A frame's pixels in its pack: rows from the top (the frame table's y is from the bottom)."""
    H = arr.shape[0]
    top = H - f['y'] - f['h']
    return arr[top : top + f['h'], f['x'] : f['x'] + f['w']]


def scene_use(table, scenes_json):
    """Per image row: the scene indices that draw it, or a tag for rows no scene reaches."""
    n = len(scenes_json['scenes'])
    consts = table['constants']
    row_of = {}
    for im in table['images']:
        row_of.setdefault(im['id'], im['row'])  # AssetsEmbed.loadImage finds the first row with the id
    use = {}
    unknown = []
    for name, v in scenes_json['images'].items():
        if name not in consts:
            unknown.append(name)
            continue
        row = row_of.get(consts[name])
        if row is None:
            unknown.append(name)
            continue
        use[row] = frozenset(range(n)) if v == '*' else frozenset(v)
    return use, unknown


def group_key(scenes, row_name, n, global_frac):
    if scenes:
        if len(scenes) >= global_frac * n:
            return (0, ())
        return (1, tuple(sorted(scenes)))
    suf = row_name.rsplit('_', 1)[-1]
    if 'FONT' in row_name and suf in LANGS:
        return (2, ('lang:' + suf,))
    return (3, ('unreached',))


# ---- packing --------------------------------------------------------------------------------------------------


def pack(items, page, pad):
    """items: dicts with w, h, key. Sets item['page'], item['px'], item['py'] (PNG coordinates, from the top).
    Returns the pages: dicts with w, h, keys, items."""
    pages = []
    small = [it for it in items if it['w'] + 2 * pad <= page and it['h'] + 2 * pad <= page]
    big = [it for it in items if not (it['w'] + 2 * pad <= page and it['h'] + 2 * pad <= page)]

    def order(it):
        return (it['key'], -it['h'], -it['w'], it['ident'])

    cur = None
    for it in sorted(small, key=order):
        rw, rh = it['w'] + pad, it['h'] + pad
        # A new page when frames no scene reaches start (language fonts, then the rest: they get pages of their
        # own), or when the scene set changes and the page is nine tenths full.
        if cur is not None and (
            (cur['key'][0] != it['key'][0] and it['key'][0] >= 2)
            or (cur['key'] != it['key'] and cur['sky'].used > 0.9 * (page - pad) ** 2)
        ):
            cur = None
        pos = cur['sky'].place(rw, rh) if cur is not None else None
        if pos is None:
            cur = {
                'sky': Skyline(page - pad, page - pad),
                'w': page,
                'h': page,
                'keys': set(),
                'items': [],
                'key': it['key'],
            }
            pages.append(cur)
            pos = cur['sky'].place(rw, rh)
            assert pos is not None
        cur['key'] = it['key']
        cur['keys'].add(it['key'])
        it['page'], it['px'], it['py'] = len(pages) - 1, pos[0] + pad, pos[1] + pad
        cur['items'].append(it)
    # Oversize frames: textures of their own, power-of-two sized, shared by frames of the same key that fit.
    by_key = collections.OrderedDict()
    for it in sorted(big, key=lambda it: (it['key'], -it['w'], -it['h'], it['ident'])):
        by_key.setdefault(it['key'], []).append(it)
    for key, group in by_key.items():
        open_pages = []
        for it in group:
            rw, rh = it['w'] + pad, it['h'] + pad
            placed = None
            for pg in open_pages:
                pos = pg['sky'].place(rw, rh)
                if pos is not None:
                    placed = (pg, pos)
                    break
            if placed is None:
                W = min(MAX_TEXTURE, pow2(it['w'] + 2 * pad))
                if it['w'] + 2 * pad > MAX_TEXTURE or it['h'] + 2 * pad > MAX_TEXTURE:
                    raise SystemExit(
                        'frame %dx%d does not fit a %d texture with its padding' % (it['w'], it['h'], MAX_TEXTURE)
                    )
                pg = {
                    'sky': Skyline(W - pad, MAX_TEXTURE - pad),
                    'w': W,
                    'h': None,
                    'keys': {key},
                    'items': [],
                    'key': key,
                }
                pages.append(pg)
                open_pages.append(pg)
                placed = (pg, pg['sky'].place(rw, rh))
            pg, pos = placed
            it['page'], it['px'], it['py'] = pages.index(pg), pos[0] + pad, pos[1] + pad
            pg['items'].append(it)
        for pg in open_pages:
            pg['h'] = pow2(pg['sky'].top + pad)
    return pages


# ---- outputs --------------------------------------------------------------------------------------------------


def guid(name):
    return hashlib.md5(('t2d-vita-atlas/' + name).encode()).hexdigest()


TEXTURE_META = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  fileIDToRecycleName: {{}}
  externalObjects: {{}}
  serializedVersion: 7
  mipmaps:
    mipMapMode: 0
    enableMipMap: 0
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 4096
  textureSettings:
    serializedVersion: 2
    filterMode: 0
    aniso: -1
    mipBias: -100
    wrapU: 0
    wrapV: 0
    wrapW: 0
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 0
  alphaUsage: 1
  alphaIsTransparency: 0
  spriteTessellationDetail: -1
  textureType: 0
  textureShape: 1
  singleChannelComponent: 0
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  platformSettings:
  - serializedVersion: 2
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 4096
    resizeAlgorithm: 0
    textureFormat: 13
    textureCompression: 0
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    androidETC2FallbackOverride: 0
  - serializedVersion: 2
    buildTarget: PSP2
    maxTextureSize: 4096
    resizeAlgorithm: 0
    textureFormat: 13
    textureCompression: 0
    compressionQuality: 100
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 1
    androidETC2FallbackOverride: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    physicsShape: []
    bones: []
    spriteID:
    vertices: []
    indices:
    edges: []
    weights: []
  spritePackingTag:
  pSDRemoveMatte: 0
  pSDShowRemoveMatteOption: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""

TEXT_META = """fileFormatVersion: 2
guid: {guid}
TextScriptImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def write_if_changed(path, data):
    """Writes data unless path already holds it: beside path, then renamed over it, so a run stopped part way
    leaves the old file or the new one, never part of it (step.py reuses what it finds)."""
    if os.path.exists(path):
        with open(path, 'rb') as fh:
            if fh.read() == data:
                return
    part = path + '.part'
    with open(part, 'wb') as fh:
        fh.write(data)
    os.replace(part, path)


def page_name(i):
    return 'p%03d' % i


def quantize4(arr):
    """8-bit channels to 4 bits, rounded to nearest."""
    return ((arr.astype(np.uint16) * 15 + 127) // 255).astype(np.uint8)


def raw4444(canvas):
    """Unity's RGBA4444 as Texture2D.LoadRawTextureData takes it: rows bottom first, one little-endian u16 per
    texel, R in the top nibble, A in the bottom."""
    q = quantize4(canvas[::-1]).astype(np.uint16)
    return ((q[..., 0] << 12) | (q[..., 1] << 8) | (q[..., 2] << 4) | q[..., 3]).astype('<u2').tobytes()


def read_raw4444(data, w, h):
    """raw4444 back to 8-bit RGBA, rows from the top (each nibble times 17)."""
    v = np.frombuffer(data, '<u2').reshape(h, w)[::-1]
    return (np.stack([(v >> 12) & 15, (v >> 8) & 15, (v >> 4) & 15, v & 15], axis=-1) * 17).astype(np.uint8)


def frames_bytes(table, frame_item, pages):
    """Format 1, little-endian:
    'T2DF' u32 format, u32 rows, u32 pages; per page u16 width, u16 height;
    per row u32 frames, then per frame u16 page, u16 x, u16 y (from the bottom), u16 w, u16 h."""
    out = bytearray(FRAMES_MAGIC + struct.pack('<III', FORMAT, table['rows'], len(pages)))
    for pg in pages:
        out += struct.pack('<HH', pg['w'], pg['h'])
    for im in table['images']:
        out += struct.pack('<I', len(im['frames']))
        for j, f in enumerate(im['frames']):
            it = frame_item[(im['row'], j)]
            pg = pages[it['page']]
            y = pg['h'] - it['py'] - it['h']
            out += struct.pack('<HHHHH', it['page'], it['px'], y, f['w'], f['h'])
    return bytes(out)


def scenes_bytes(scenes_json, scene_pages, sticky):
    """Format 1, little-endian. Strings are a u16 byte count and UTF-8.
    'T2DS' u32 format, u32 scenes; per scene: string name, u16 count, u16 pages...;
    u32 keys; per key: string key ('app:Class', 'level:N', 'map:N'), u16 count, u16 scenes...;
    u16 level apps, strings (App classes during which EnterLevel acts);
    u16 sticky pages, u16 pages... (kept once loaded: other languages' fonts)."""

    def s(text):
        b = text.encode('utf-8')
        return struct.pack('<H', len(b)) + b

    out = bytearray(SCENES_MAGIC + struct.pack('<II', FORMAT, len(scenes_json['scenes'])))
    keys = collections.OrderedDict()
    for k, sc in enumerate(scenes_json['scenes']):
        out += (
            s(sc['name'])
            + struct.pack('<H', len(scene_pages[k]))
            + b''.join(struct.pack('<H', p) for p in scene_pages[k])
        )
        for key in sc['keys']:
            keys.setdefault(key, []).append(k)
    out += struct.pack('<I', len(keys))
    for key in sorted(keys):
        out += s(key) + struct.pack('<H', len(keys[key])) + b''.join(struct.pack('<H', k) for k in keys[key])
    apps = scenes_json.get('level_apps', [])
    out += struct.pack('<H', len(apps)) + b''.join(s(a) for a in apps)
    out += struct.pack('<H', len(sticky)) + b''.join(struct.pack('<H', p) for p in sticky)
    return bytes(out)


# ---- the check ------------------------------------------------------------------------------------------------


def read_frames_bytes(data):
    assert data[:4] == FRAMES_MAGIC
    _fmt, rows, npages = struct.unpack_from('<III', data, 4)
    pos = 16
    sizes = [struct.unpack_from('<HH', data, pos + 4 * i) for i in range(npages)]
    pos += 4 * npages
    table = []
    for _ in range(rows):
        (n,) = struct.unpack_from('<I', data, pos)
        pos += 4
        table.append([struct.unpack_from('<HHHHH', data, pos + 10 * j) for j in range(n)])
        pos += 10 * n
    assert pos == len(data)
    return sizes, table


def check(out_dir, table, decoded, pad, raw=False):
    """Every frame through frames.bytes and the written PNGs, against its pack: the same pixels, and `pad`
    transparent pixels around it unless another frame's rect is there (frames with the same pixels share one)."""
    sizes, rows = read_frames_bytes(open(os.path.join(out_dir, 'frames.bytes'), 'rb').read())
    by_page = collections.defaultdict(list)
    for im in table['images']:
        for j, f in enumerate(im['frames']):
            by_page[rows[im['row']][j][0]].append((im['row'], j, f))
    bad, frames, border_bad = [], 0, []
    for p in range(len(sizes)):
        if raw:
            W, H = sizes[p]
            arr = read_raw4444(open(os.path.join(out_dir, page_name(p) + '.bytes'), 'rb').read(), W, H)
        else:
            arr = np.asarray(Image.open(os.path.join(out_dir, page_name(p) + '.png')))
        H, W = arr.shape[:2]
        assert sizes[p] == (W, H), (p, (W, H), sizes[p])
        cover = np.zeros((H, W), bool)
        rects = set()
        for row, j, f in by_page[p]:
            _, x, y, w, h = rows[row][j]
            assert (w, h) == (f['w'], f['h'])
            top = H - y - h
            got = arr[top : top + h, x : x + w]
            want = png_rect(decoded[f['pack']], f)
            want = quantize4(want) * 17
            frames += 1
            if got.shape != want.shape or not np.array_equal(got, want):
                bad.append((row, j))
            cover[top : top + h, x : x + w] = True
            rects.add((x, top, w, h))
        for x, top, w, h in rects:
            y0, y1, x0, x1 = top - pad, top + h + pad, x - pad, x + w + pad
            if y0 < 0 or x0 < 0 or y1 > H or x1 > W:
                border_bad.append((p, x, top, 'too close to the page edge'))
                continue
            if np.any(arr[y0:y1, x0:x1][~cover[y0:y1, x0:x1]] != 0):
                border_bad.append((p, x, top, 'border not transparent'))
            if cover[y0:y1, x0:x1].sum() != w * h:
                border_bad.append((p, x, top, 'another rect within the padding'))
    return {'frames': frames, 'mismatched': bad, 'border_problems': border_bad}


# ---- main -----------------------------------------------------------------------------------------------------


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--asm', required=True, help="Rewind's Assembly-CSharp.dll")
    ap.add_argument('--game', help='game folder: packs from resources.assets, GameAssembly.dll for gaps')
    ap.add_argument('--packs', help='folder with TexturePackN.bytes or .png instead of --game')
    ap.add_argument('--dump', help='Il2CppDumper dump.cs (only if Rewind left gaps in the tables)')
    ap.add_argument('--scenes', default=os.path.join(HERE, 'scenes.json'))
    ap.add_argument('--out', required=True, help='output folder (Assets/Resources/atlas)')
    ap.add_argument('--report', required=True, help='folder for report.json and memory.md')
    ap.add_argument('--page', type=int, default=1024)
    ap.add_argument('--pad', type=int, default=2)
    ap.add_argument(
        '--global-frac', type=float, default=0.5, help='frames in at least this share of scenes go to shared pages'
    )
    ap.add_argument('--no-check', action='store_true')
    ap.add_argument(
        '--raw',
        action='store_true',
        help='pages as raw RGBA 4444 TextAssets (pNNN.bytes) instead of PNGs with RGBA 4444 import settings, '
        'the same texels; Atlas.cs loads either',
    )
    a = ap.parse_args()
    if not (a.game or a.packs):
        ap.error('--game or --packs')
    t0 = time.time()

    def log(m):
        return print(f'[{time.time() - t0:5.1f}s] {m}', flush=True)

    table = frametable.read(a.asm, a.game, a.dump, log=log)
    if table['problems']:
        raise SystemExit('frame table: %d problems, e.g. %s' % (len(table['problems']), table['problems'][:5]))
    log('frame table: %d rows, %d frames' % (table['rows'], sum(len(i['frames']) for i in table['images'])))
    pngs = packs_mod.load(a.game, a.packs)
    decoded = {k: decode(v) for k, v in sorted(pngs.items())}
    for k, arr in decoded.items():
        if arr.shape[:2] != (table['pack_size'], table['pack_size']):
            raise SystemExit('pack %d is %s, not %d square' % (k, arr.shape, table['pack_size']))
    log('%d packs decoded' % len(decoded))
    scenes_json = json.load(open(a.scenes))
    n = len(scenes_json['scenes'])
    use, unknown = scene_use(table, scenes_json)
    if unknown:
        log(
            'scenes.json: %d images no row loads (not in Assets, or AssetsPack.imageclassname lacks them): %s'
            % (len(unknown), unknown[:5])
        )

    # items: one per distinct (size, pixels)
    items, by_hash, frame_item = [], {}, {}
    for im in table['images']:
        for j, f in enumerate(im['frames']):
            if f['pack'] not in decoded:
                raise SystemExit('row %d frame %d: pack %d missing' % (im['row'], j, f['pack']))
            px = png_rect(decoded[f['pack']], f)
            hsh = hashlib.sha1(struct.pack('<HH', f['w'], f['h']) + px.tobytes()).digest()
            it = by_hash.get(hsh)
            if it is None:
                it = {'w': f['w'], 'h': f['h'], 'scenes': set(), 'names': set(), 'ident': (f['pack'], f['x'], f['y'])}
                by_hash[hsh] = it
                items.append(it)
            it['scenes'] |= use.get(im['row'], frozenset())
            it['names'].add(im['name'])
            frame_item[(im['row'], j)] = it
    for it in items:
        it['key'] = min(group_key(it['scenes'], nm, n, a.global_frac) for nm in sorted(it['names']))
    log('%d distinct frames (%d frame entries)' % (len(items), len(frame_item)))

    pages = pack(items, a.page, a.pad)
    log('%d pages' % len(pages))

    # page contents
    os.makedirs(a.out, exist_ok=True)
    # Pages of an earlier run with more pages, or of the other mode (PNG or raw).
    stale = [
        f
        for f in os.listdir(a.out)
        if f.startswith('p') and f[1:4].isdigit() and (int(f[1:4]) >= len(pages) or f[4:].startswith('.png') == a.raw)
    ]
    for f in stale:
        os.remove(os.path.join(a.out, f))
    src_of = {}
    for (row, j), it in frame_item.items():
        src_of.setdefault(id(it), table['images'][row]['frames'][j])
    for p, pg in enumerate(pages):
        canvas = np.zeros((pg['h'], pg['w'], 4), np.uint8)
        for it in pg['items']:
            f = src_of[id(it)]
            canvas[it['py'] : it['py'] + it['h'], it['px'] : it['px'] + it['w']] = png_rect(decoded[f['pack']], f)
        if a.raw:
            write_if_changed(os.path.join(a.out, page_name(p) + '.bytes'), raw4444(canvas))
            write_if_changed(
                os.path.join(a.out, page_name(p) + '.bytes.meta'),
                TEXT_META.format(guid=guid(page_name(p) + '.bytes')).encode(),
            )
            continue
        buf = io.BytesIO()
        Image.fromarray(quantize4(canvas) * 17, 'RGBA').save(buf, 'PNG', compress_level=6)
        write_if_changed(os.path.join(a.out, page_name(p) + '.png'), buf.getvalue())
        write_if_changed(
            os.path.join(a.out, page_name(p) + '.png.meta'),
            TEXTURE_META.format(guid=guid(page_name(p) + '.png')).encode(),
        )
    log('pages written')

    scene_pages = [
        sorted({p for p, pg in enumerate(pages) if any(k in it['scenes'] for it in pg['items'])}) for k in range(n)
    ]
    sticky = sorted(p for p, pg in enumerate(pages) if all(it['key'][0] == 2 for it in pg['items']))
    write_if_changed(os.path.join(a.out, 'frames.bytes'), frames_bytes(table, frame_item, pages))
    write_if_changed(os.path.join(a.out, 'scenes.bytes'), scenes_bytes(scenes_json, scene_pages, sticky))
    for nm in ('frames.bytes', 'scenes.bytes'):
        write_if_changed(os.path.join(a.out, nm + '.meta'), TEXT_META.format(guid=guid(nm)).encode())

    # report
    def size(p):
        return pages[p]['w'] * pages[p]['h'] * 2

    per_scene = []
    for k, sc in enumerate(scenes_json['scenes']):
        ps = scene_pages[k]
        per_scene.append(
            {
                'scene': sc['name'],
                'keys': sc['keys'],
                'pages': len(ps),
                'oversize': sum(1 for p in ps if (pages[p]['w'], pages[p]['h']) != (a.page, a.page)),
                'mb_4444': sum(size(p) for p in ps) / MB,
            }
        )
    level8 = sorted(
        set().union(*[scene_pages[k] for k, sc in enumerate(scenes_json['scenes']) if 'level:8' in sc['keys']])
    )
    groups = collections.Counter(pg['key'][0] for pg in pages)
    report = {
        'page': a.page,
        'pad': a.pad,
        'pages': len(pages),
        'oversize_pages': sum(1 for pg in pages if (pg['w'], pg['h']) != (a.page, a.page)),
        'pages_by_group': {'shared': groups[0], 'scene': groups[1], 'language': groups[2], 'unreached': groups[3]},
        'total_mb_4444': sum(size(p) for p in range(len(pages))) / MB,
        'fill': sum(it['w'] * it['h'] for it in items) / sum(pg['w'] * pg['h'] for pg in pages),
        'distinct_frames': len(items),
        'frame_entries': len(frame_item),
        'sticky_pages': sticky,
        'level8_union_mb_4444': sum(size(p) for p in level8) / MB,
        'page_sizes': [(pg['w'], pg['h']) for pg in pages],
        'per_scene': per_scene,
        'unknown_scene_images': unknown,
        'gaps': table['gaps'],
    }
    if not a.no_check:
        res = check(a.out, table, decoded, a.pad, a.raw)
        report['check'] = {
            'frames': res['frames'],
            'mismatched': len(res['mismatched']),
            'border_problems': len(res['border_problems']),
            'examples': [list(map(str, x)) for x in (res['mismatched'] + res['border_problems'])[:10]],
        }
        log(
            'check: %d frames compared, %d differ, %d border problems'
            % (res['frames'], len(res['mismatched']), len(res['border_problems']))
        )
    os.makedirs(a.report, exist_ok=True)
    json.dump(report, open(os.path.join(a.report, 'report.json'), 'w'), indent=1)
    with open(os.path.join(a.report, 'memory.md'), 'w', newline='\n') as fh:
        fh.write('| Scene | pages | oversize | MB (RGBA 4444) |\n|---|---|---|---|\n')
        fh.writelines(
            '| %s | %d | %d | %.1f |\n' % (s['scene'], s['pages'], s['oversize'], s['mb_4444']) for s in per_scene
        )
    worst = max(
        (s for s in per_scene if s['scene'].startswith(('L', 'Tanker', 'Controls'))), key=lambda s: s['mb_4444']
    )
    log(
        '%d pages (%d oversize), %.0f MB in all; worst level %s %.1f MB; level 8 with Tanker Chase %.1f MB'
        % (
            len(pages),
            report['oversize_pages'],
            report['total_mb_4444'],
            worst['scene'],
            worst['mb_4444'],
            report['level8_union_mb_4444'],
        )
    )
    if not a.no_check and (report['check']['mismatched'] or report['check']['border_problems']):
        raise SystemExit('the check failed; see {}'.format(os.path.join(a.report, 'report.json')))


if __name__ == '__main__':
    main()
