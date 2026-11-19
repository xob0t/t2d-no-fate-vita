"""Per-rectangle opacity stats for the texture packs.

For every unique frame rectangle in images.json: pixels with alpha > 0, the
tight bounding box of those pixels, and the number of non-empty CELLxCELL
cells (what a cell-split repack would keep).

Usage: measure_alpha.py --images images.json --packs DIR_WITH_TexturePackN.bytes --out rect_alpha.json [--cell 32]
"""
import argparse, io, json
import numpy as np
from PIL import Image


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--images', required=True)
    ap.add_argument('--packs', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--cell', type=int, default=32)
    a = ap.parse_args()
    d = json.load(open(a.images))
    rects = {}
    for im in d['images']:
        for f in im['frames']:
            rects.setdefault(f['pack'], set()).add((f['x'], f['y'], f['w'], f['h']))
    out = {}
    C = a.cell
    for p in sorted(rects):
        raw = open('%s/TexturePack%d.bytes' % (a.packs, p), 'rb').read()
        alpha = np.asarray(Image.open(io.BytesIO(raw)).convert('RGBA'))[:, :, 3] > 0
        tot = int(alpha.sum())
        for (x, y, w, h) in rects[p]:
            # uvy counts from the bottom (Unity UV origin); PNG rows count from the top
            top = 4096 - y - h
            sub = alpha[top:top + h, x:x + w]
            n = int(sub.sum())
            if n:
                ys, xs = np.nonzero(sub.any(axis=1))[0], np.nonzero(sub.any(axis=0))[0]
                bb = [int(xs[0]), int(ys[0]), int(xs[-1] - xs[0] + 1), int(ys[-1] - ys[0] + 1)]
            else:
                bb = [0, 0, 0, 0]
            hh, ww = -(-h // C) * C, -(-w // C) * C
            pad = np.zeros((hh, ww), bool)
            pad[:h, :w] = sub
            cells = int(pad.reshape(hh // C, C, ww // C, C).any(axis=(1, 3)).sum())
            out['%d,%d,%d,%d,%d' % (p, x, y, w, h)] = dict(opaque=n, bbox=bb, cells=cells)
        print('pack', p, 'opaque px %.1f%%' % (100 * tot / 4096 / 4096))
    json.dump(dict(cell=C, rects=out), open(a.out, 'w'))


if __name__ == '__main__':
    main()
