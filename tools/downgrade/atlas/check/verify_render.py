# ruff: noqa: ARG005, N806, S311, SIM115, UP031
"""Renders frames the way the game draws them, once from the original packs through the game's frame table and once
from the repacked pages through frames.bytes, and compares the two pixel for pixel.

A frame is a quad at its trim offset inside its untrimmed size, textured by four UV corners. CreateMultiMeshFrames
bakes four meshes per frame: two with the UVs as they are (one mirrored) and two with every UV minus 1.0, which
read the same texels only because the texture wraps (Repeat). Each variant here is rasterised at pixel centres with
float32 interpolation, point sampling and the given wrap mode, which is what the GPU does with these meshes at 1x.

Usage: python -I verify_render.py --asm <Assembly-CSharp.dll> --game <game folder> --atlas <out/atlas> --out <dir>
                                  [--sample N] [--images NAME,...]
Writes <out>/render.json and <out>/render.png (original | repacked, a few frames).
"""

import argparse
import json
import os
import random
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import frametable
import packs as packs_mod
import repack

F32 = np.float32
# (mirror u, mirror v, minus one): the four meshes per frame, as CreateMultiMeshFrames bakes them (cases 0..3).
VARIANTS = [(False, False, False), (True, False, False), (True, False, True), (False, False, True)]


def sample(tex, u, v, wrap):
    """Point-sample an RGBA array (rows from the top) at UVs (v from the bottom)."""
    H, W = tex.shape[:2]
    if wrap == 'repeat':
        u = u - np.floor(u)
        v = v - np.floor(v)
    else:
        u = np.clip(u, 0, F32(1) - F32(1e-7))
        v = np.clip(v, 0, F32(1) - F32(1e-7))
    x = np.minimum((u * F32(W)).astype(np.int64), W - 1)
    y = np.minimum((v * F32(H)).astype(np.int64), H - 1)
    return tex[H - 1 - y, x]


def render(tex, rect, size, frame, sheet, variant, wrap):
    """The frame drawn into its untrimmed sfw x sfh box. rect: (x, y, w, h) in tex pixels, y from the bottom."""
    W, H = size
    x, y, w, h = rect
    mirror_u, mirror_v, minus = variant
    u0, u1 = F32(x / W), F32((x + w) / W)
    v0, v1 = F32(y / H), F32((y + h) / H)
    if mirror_u:
        u0, u1 = u1, u0
    if mirror_v:
        v0, v1 = v1, v0
    if minus:
        u0, u1, v0, v1 = u0 - F32(1), u1 - F32(1), v0 - F32(1), v1 - F32(1)
    t = (np.arange(w, dtype=F32) + F32(0.5)) / F32(w)
    s = ((np.arange(h, dtype=F32) + F32(0.5)) / F32(h))[::-1]  # screen rows from the top, v from the bottom
    uu = u0 + (u1 - u0) * t[None, :]
    vv = v0 + (v1 - v0) * s[:, None]
    uu, vv = np.broadcast_to(uu, (h, w)), np.broadcast_to(vv, (h, w))
    out = np.zeros((sheet[1], sheet[0], 4), np.uint8)
    ox, oy = frame['ox'], frame['oy']
    out[oy : oy + h, ox : ox + w] = sample(tex, uu, vv, wrap)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--asm', required=True)
    ap.add_argument('--game')
    ap.add_argument('--packs')
    ap.add_argument('--atlas', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--sample', type=int, default=1500)
    ap.add_argument('--images', default='IMAGE_POLICE_HELICOPTER,IMAGE_CYBERDYNEFACTORY_FG_A,IMAGE_FUTURE_WAR_1_A')
    a = ap.parse_args()
    table = frametable.read(a.asm, a.game, None, log=lambda m: None)
    decoded = {k: repack.decode(v) for k, v in packs_mod.load(a.game, a.packs).items()}
    sizes, rows = repack.read_frames_bytes(open(os.path.join(a.atlas, 'frames.bytes'), 'rb').read())
    page_cache = {}

    def page(p):
        if p not in page_cache:
            if len(page_cache) > 24:
                page_cache.clear()
            page_cache[p] = np.asarray(Image.open(os.path.join(a.atlas, 'p%03d.png' % p)))
        return page_cache[p]

    every = [(im['row'], j) for im in table['images'] for j in range(len(im['frames']))]
    rnd = random.Random(1)
    picks = set(rnd.sample(every, min(a.sample, len(every))))
    wanted = set(a.images.split(','))
    for im in table['images']:
        if im['name'] in wanted:
            picks.update((im['row'], j) for j in range(len(im['frames'])))
    picks = sorted(picks, key=lambda rj: (rows[rj[0]][rj[1]][0], rj))
    result = {
        'frames': len(picks),
        'variants': len(VARIANTS),
        'repeat_diff': 0,
        'clamp_diff_variants_minus': 0,
        'clamp_diff_variants_plain': 0,
        'examples': [],
    }
    gallery = []
    for row, j in picks:
        im = table['images'][row]
        f = im['frames'][j]
        p, x, y, w, h = rows[row][j]
        sheet = (max(im['sfw'], f['ox'] + f['w']), max(im['sfh'], f['oy'] + f['h']))
        orig_tex = decoded[f['pack']]
        for vi, var in enumerate(VARIANTS):
            a_img = render(orig_tex, (f['x'], f['y'], f['w'], f['h']), (4096, 4096), f, sheet, var, 'repeat')
            b_img = render(page(p), (x, y, w, h), sizes[p], f, sheet, var, 'repeat')
            if not np.array_equal(a_img, b_img):
                result['repeat_diff'] += 1
                if len(result['examples']) < 10:
                    result['examples'].append({'row': row, 'frame': j, 'name': im['name'], 'variant': vi})
            # The same with Clamp, which the minus-one meshes break.
            c_img = render(page(p), (x, y, w, h), sizes[p], f, sheet, var, 'clamp')
            if not np.array_equal(a_img, c_img):
                result['clamp_diff_variants_minus' if var[2] else 'clamp_diff_variants_plain'] += 1
            if vi == 0 and im['name'] in wanted and len(gallery) < 6 and f['w'] * f['h'] < 300 * 300:
                gallery.append((a_img, b_img))
    os.makedirs(a.out, exist_ok=True)
    json.dump(result, open(os.path.join(a.out, 'render.json'), 'w'), indent=1)
    if gallery:
        hgt = max(g[0].shape[0] for g in gallery)
        tiles = []
        for a_img, b_img in gallery:

            def pad(m):
                return np.pad(m, ((0, hgt - m.shape[0]), (0, 4), (0, 0)))

            tiles += [pad(a_img), pad(b_img), np.zeros((hgt, 8, 4), np.uint8)]
        Image.fromarray(np.concatenate(tiles, axis=1), 'RGBA').save(os.path.join(a.out, 'render.png'))
    print(
        'rendered %d frames x %d variants: %d differ with Repeat; with Clamp %d minus-one and %d plain variants differ'
        % (
            len(picks),
            len(VARIANTS),
            result['repeat_diff'],
            result['clamp_diff_variants_minus'],
            result['clamp_diff_variants_plain'],
        )
    )
    sys.exit(1 if result['repeat_diff'] else 0)


if __name__ == '__main__':
    main()
