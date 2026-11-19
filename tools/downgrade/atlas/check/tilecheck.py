"""tilecheck.py <atlas dir> <tiles dir> (work/atlas/atlas, work/atlas/tiles): SpriteBatch.SubmitTiled's arithmetic, in float32 where the C# is, against the
whole page: every frame on a tiled page, in all four flips, drawn as an axis-aligned quad at 1:1 and at 1.37x,
pixel centres sampled point-wise (whole page: Repeat; tiles: Clamp). The tiled draw reads the tile files
themselves (pNNN_K.png in the tiles dir), so a tile cut from the wrong place or of the wrong size shows. Counts
texels that differ."""
import struct, sys
import numpy as np
from PIL import Image

atlas, tiles = sys.argv[1], sys.argv[2]
f = open(atlas + '/frames.bytes', 'rb').read()
rows, count = struct.unpack_from('<II', f, 8)
pos = 16
size = []
for i in range(count):
    size.append(struct.unpack_from('<HH', f, pos)); pos += 4
frames = []
for i in range(rows):
    n = struct.unpack_from('<I', f, pos)[0]; pos += 4
    for j in range(n):
        frames.append(struct.unpack_from('<HHHHH', f, pos)); pos += 10
T = struct.unpack('<H', open(tiles + '/tiles.bytes', 'rb').read())[0]
f32 = np.float32
bad = total = quads = unhit = 0
edge = 0
bad1 = 0
pages = {}
tile_files = {}
bad_tiles = []


def tile(p, k, tw, th):
    """Tile k of page p as the build ships it, row 0 at the bottom; None (and noted) if missing or not tw x th."""
    if (p, k) not in tile_files:
        try:
            t = np.asarray(Image.open(f'{tiles}/p{p:03d}_{k}.png'))[::-1]
        except OSError as error:
            t = None
            bad_tiles.append(f'p{p:03d}_{k}.png: {error}')
        if t is not None and t.shape[:2] != (th, tw):
            bad_tiles.append(f'p{p:03d}_{k}.png is {t.shape[1]}x{t.shape[0]}, not {tw}x{th}')
            t = None
        tile_files[p, k] = t
    return tile_files[p, k]


for p, x, y, w, h in sorted(set(frames)):
    PW, PH = size[p]
    if PW <= T and PH <= T:
        continue
    if p not in pages:
        pages[p] = np.asarray(Image.open(f'{atlas}/p{p:03d}.png'))[::-1]  # row 0 = bottom
    page = pages[p]
    tw, th = min(PW, T), min(PH, T)
    cols, rows_ = PW // tw, PH // th
    for flip in range(4):
        for scale in (1.0, 1.37):
            W, H = max(1, int(w * scale)), max(1, int(h * scale))
            ux0, ux1 = f32(x / PW), f32((x + w) / PW)
            vy0, vy1 = f32(y / PH), f32((y + h) / PH)
            uL, uR = (ux0, ux1) if not flip & 1 else (f32(ux1 - 1), f32(ux0 - 1))
            vB, vT = (vy0, vy1) if not flip & 2 else (f32(vy1 - 1), f32(vy0 - 1))
            # reference: the whole page, Repeat
            sx = (np.arange(W) + 0.5) / W
            sy = (np.arange(H) + 0.5) / H
            U = uL + sx * (uR - uL)
            V = vB + sy * (vT - vB)
            ref = page[np.floor((V % 1) * PH).astype(int) % PH][:, np.floor((U % 1) * PW).astype(int) % PW]
            # tiled, as SubmitTiled
            uLd, uRd, vBd, vTd = float(uL), float(uR), float(vB), float(vT)
            if min(uLd, uRd) < -1e-6: uLd += 1; uRd += 1
            if min(vBd, vTd) < -1e-6: vBd += 1; vTd += 1
            u0, u1, v0, v1 = min(uLd, uRd), max(uLd, uRd), min(vBd, vTd), max(vBd, vTd)
            c0, c1 = max(0, int(np.floor(u0 * cols))), min(cols - 1, int(np.ceil(u1 * cols)) - 1)
            r0, r1 = max(0, int(np.floor(v0 * rows_))), min(rows_ - 1, int(np.ceil(v1 * rows_)) - 1)
            out = np.zeros_like(ref); hit = np.zeros(ref.shape[:2], bool)
            for r in range(r0, r1 + 1):
                for c in range(c0, c1 + 1):
                    a, b = max(u0, c / cols), min(u1, (c + 1) / cols)
                    e, g = max(v0, r / rows_), min(v1, (r + 1) / rows_)
                    if b <= a or g <= e: continue
                    sa, sb = (a - uLd) / (uRd - uLd), (b - uLd) / (uRd - uLd)
                    qe, qg = (e - vBd) / (vTd - vBd), (g - vBd) / (vTd - vBd)
                    sL, sR, qB, qT = f32(min(sa, sb)), f32(max(sa, sb)), f32(min(qe, qg)), f32(max(qe, qg))
                    tuL = f32((uLd + float(sL) * (uRd - uLd)) * cols - c); tuR = f32((uLd + float(sR) * (uRd - uLd)) * cols - c)
                    tvB = f32((vBd + float(qB) * (vTd - vBd)) * rows_ - r); tvT = f32((vBd + float(qT) * (vTd - vBd)) * rows_ - r)
                    quads += 1
                    # pixels whose centre is in [sL, sR) x [qB, qT) (a top-left-like rule)
                    mx = (sx >= sL) & (sx < sR); my = (sy >= qB) & (sy < qT)
                    if not mx.any() or not my.any(): continue
                    tu = tuL + (sx[mx] - sL) / (sR - sL) * (tuR - tuL)
                    tv = tvB + (sy[my] - qB) / (qT - qB) * (tvT - tvB)
                    tx = np.clip(np.floor(tu * tw).astype(int), 0, tw - 1)
                    ty = np.clip(np.floor(tv * th).astype(int), 0, th - 1)
                    t = tile(p, r * cols + c, tw, th)
                    if t is None:
                        continue  # not covered: counted as differing
                    out[np.ix_(my, mx)] = t[ty][:, tx]
                    hit[np.ix_(my, mx)] = True
            d = (~hit) | (out != ref).any(-1)
            unhit += int((~hit).sum())
            # differing pixels whose reference texel is next to a tile edge (in u or v)
            RX = (np.floor((U % 1) * PW).astype(int) % PW) % tw; RY = (np.floor((V % 1) * PH).astype(int) % PH) % th
            near = ((RY[:, None] <= 0) | (RY[:, None] >= th - 1)) | ((RX[None, :] <= 0) | (RX[None, :] >= tw - 1))
            edge += int((d & near).sum())
            bad += int(d.sum()); total += d.size
            if scale == 1.0: bad1 += int(d.sum())
for line in bad_tiles:
    print('BAD TILE ' + line)
print(f'{len(pages)} tiled pages, {quads} sub-quads, {total} pixels, {bad} differ, {unhit} not covered, {edge} at a tile edge texel, {bad1} at 1:1')
# At 1.37x a few texels at tile edges differ by float rounding (docs/textures.md); at 1:1 none may.
sys.exit(1 if bad1 or bad_tiles else 0)
