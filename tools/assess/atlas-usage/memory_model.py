"""Texture-memory estimates per scene for a PS Vita port.

Inputs (from --work): images.json, scenes.json, rect_alpha.json.
Writes memory.csv and memory.md to --work.

Schemes
  a    all 12 packs resident, DXT5 (what the game does today, compressed)
  b4   per-scene whole packs, RGBA4444
  b5   per-scene whole packs, DXT5
  bmix per-scene whole packs, RGBA4444 for packs 8-11 (sprites/fonts),
       DXT5 for packs 0-7 (map layers)
  c4k  per-scene repack of the scene's trimmed frame rects into 4096^2 RGBA4444
  c2k  per-scene repack into 2048^2 RGBA4444 (rects > 2048 get their own
       power-of-two texture)
  ccom common gameplay atlas (images every level uses) + per-level repack,
       2048^2 RGBA4444; peak = common + level
  ccell like c2k, but frames larger than 256x256 that are under half opaque
       are split into 32x32 cells and only non-empty cells kept (needs
       multi-quad drawing; estimate from cell area)
"""
import argparse, collections, json, math, os

MB = 1024 * 1024
BPP = {'4444': 2, 'dxt5': 1, 'rgba32': 4}


class Skyline:
    def __init__(self, w, h):
        self.w, self.h = w, h
        self.sky = [(0, 0, w)]      # (x, y, width)
        self.used = 0

    def find(self, rw, rh):
        best = None
        for i, (x, y, w) in enumerate(self.sky):
            if x + rw > self.w:
                break
            # max height over the segments this rect spans
            top, need, j = y, rw, i
            while need > 0:
                sx, sy, sw = self.sky[j]
                top = max(top, sy)
                need -= sw
                j += 1
            if top + rh > self.h:
                continue
            if best is None or (top + rh, x) < (best[0], best[1]):
                best = (top + rh, x, i, top)
        return best

    def place(self, rw, rh):
        b = self.find(rw, rh)
        if b is None:
            return False
        _, x, i, top = b
        new = []
        for (sx, sy, sw) in self.sky:
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
        return True


def pow2(n):
    return 1 << max(0, math.ceil(math.log2(max(1, n))))


def pack(rects, size, pad=1):
    """rects: list of (w, h). Returns list of texture sizes (w, h) used."""
    textures = []
    bins = []
    for w, h in sorted(rects, key=lambda r: (-r[1], -r[0])):
        w2, h2 = w + pad, h + pad
        if w2 > size or h2 > size:
            textures.append((pow2(w), pow2(h)))
            continue
        for b in bins:
            if b.place(w2, h2):
                break
        else:
            b = Skyline(size, size)
            assert b.place(w2, h2)
            bins.append(b)
    for b in bins:
        # shrink the bin to the smallest power-of-two height that holds it
        top = max(y for _, y, _ in b.sky)
        textures.append((size, max(pow2(top), 64)))
    return textures


def tex_bytes(textures, fmt):
    return sum(w * h for w, h in textures) * BPP[fmt]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', required=True)
    ap.add_argument('--budget-mb', type=float, default=240.0)
    a = ap.parse_args()
    W = a.work
    images = json.load(open(os.path.join(W, 'images.json')))['images']
    scenes = json.load(open(os.path.join(W, 'scenes.json')))
    alpha = json.load(open(os.path.join(W, 'rect_alpha.json')))
    A, CELL = alpha['rects'], alpha['cell']

    def rects_of(ims):
        rs = set()
        for i in ims:
            for f in images[i]['frames']:
                rs.add((f['pack'], f['x'], f['y'], f['w'], f['h']))
        return rs

    levels = [s for s in scenes if s['kind'] == 'level']
    common = set.intersection(*(set(s['images']) for s in levels))
    common_rects = rects_of(common)
    common_tex = pack([(r[3], r[4]) for r in common_rects], 2048)

    rows = []
    for s in scenes:
        rs = rects_of(s['images'])
        n = len(s['packs'])
        packs_nonfont = sorted({r[0] for r in rects_of([i for i in s['images'] if 'FONT' not in images[i]['name']])})
        level_only = rects_of(set(s['images']) - common) - common_rects if s['kind'] == 'level' else None
        t4k = pack([(r[3], r[4]) for r in rs], 4096)
        t2k = pack([(r[3], r[4]) for r in rs], 2048)
        # cell split for big sparse frames
        keep, cell_area = [], 0
        for r in rs:
            st = A['%d,%d,%d,%d,%d' % r]
            if r[3] * r[4] > 256 * 256 and st['opaque'] < 0.5 * r[3] * r[4]:
                cell_area += st['cells'] * CELL * CELL
            else:
                keep.append((r[3], r[4]))
        # non-empty cells packed as 2048x32 strips (64 cells each) with the kept rects
        strips = math.ceil(cell_area / (CELL * CELL) / (2048 // CELL))
        tcell = pack(keep + [(2048 - 1, CELL)] * strips, 2048)
        cell_bytes = 0
        row = dict(scene=s['scene'], kind=s['kind'], n_packs=n, n_packs_without_fonts=len(packs_nonfont),
                   rect_atlases=s['rect_area_atlases'], opaque_atlases=s['opaque_atlases'],
                   a=12 * 4096 * 4096 * BPP['dxt5'] / MB,
                   b4=n * 4096 * 4096 * BPP['4444'] / MB,
                   b5=n * 4096 * 4096 * BPP['dxt5'] / MB,
                   bmix=sum(4096 * 4096 * (BPP['4444'] if p >= 8 else BPP['dxt5']) for p in s['packs']) / MB,
                   c4k=tex_bytes(t4k, '4444') / MB, c4k_n=len(t4k),
                   c2k=tex_bytes(t2k, '4444') / MB, c2k_n=len(t2k),
                   ccell=(tex_bytes(tcell, '4444') + cell_bytes) / MB)
        if level_only is not None:
            tl = pack([(r[3], r[4]) for r in level_only], 2048)
            row['ccom'] = (tex_bytes(common_tex, '4444') + tex_bytes(tl, '4444')) / MB
        rows.append(row)

    cols = ['scene', 'kind', 'n_packs', 'n_packs_without_fonts', 'rect_atlases', 'opaque_atlases',
            'a', 'b4', 'b5', 'bmix', 'c4k', 'c4k_n', 'c2k', 'c2k_n', 'ccom', 'ccell']
    with open(os.path.join(W, 'memory.csv'), 'w') as fh:
        fh.write(','.join(cols) + '\n')
        for r in rows:
            fh.write(','.join(('%.1f' % r[c]) if isinstance(r.get(c), float) else str(r.get(c, '')) for c in cols) + '\n')
    hdr = ('| Scene | packs (w/o fonts) | trimmed rects, atlases | opaque px, atlases | a: 12×DXT5 | b: packs 4444 | b: packs DXT5 | '
           'b: mix | c: repack 4096² | c: repack 2048² | c: common+level | c: + cell split |')
    lines = [hdr, '|' + '---|' * 12]
    for r in rows:
        lines.append('| %s | %d (%d) | %.2f | %.2f | %.0f | %.0f | %.0f | %.0f | %.0f (%d) | %.0f (%d) | %s | %.0f |' % (
            r['scene'], r['n_packs'], r['n_packs_without_fonts'], r['rect_atlases'], r['opaque_atlases'], r['a'], r['b4'], r['b5'],
            r['bmix'], r['c4k'], r['c4k_n'], r['c2k'], r['c2k_n'], ('%.0f' % r['ccom']) if 'ccom' in r else '', r['ccell']))
    worst = {k: max(r[k] for r in rows if k in r) for k in ('a', 'b4', 'b5', 'bmix', 'c4k', 'c2k', 'ccom', 'ccell')}
    lines.append('| **worst scene** | | | | %s |' % ' | '.join('%.0f' % worst[k] for k in ('a', 'b4', 'b5', 'bmix', 'c4k', 'c2k', 'ccom', 'ccell')))
    lines.append('')
    lines.append('Common gameplay set: %d images, %d rects, %.2f atlases of rects, packed in %s (2048-wide, 4444) = %.1f MB.' % (
        len(common), len(common_rects), sum(r[3] * r[4] for r in common_rects) / 4096 ** 2,
        ', '.join('%dx%d' % t for t in common_tex), tex_bytes(common_tex, '4444') / MB))
    lines.append('Budget for textures: %.0f MB (main + CDRAM). MB = 2^20 bytes.' % a.budget_mb)
    open(os.path.join(W, 'memory.md'), 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    print('wrote', os.path.join(W, 'memory.md'))


if __name__ == '__main__':
    main()
