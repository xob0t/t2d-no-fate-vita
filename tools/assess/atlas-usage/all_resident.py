"""Memory if every frame is repacked once and kept resident (no per-scene loading).

Usage: all_resident.py --work work/atlas-usage
"""
import argparse, json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from memory_model import pack, tex_bytes, MB

LANGS = ('CNS', 'CNT', 'JP', 'KR')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', required=True)
    W = ap.parse_args().work
    im = json.load(open(os.path.join(W, 'images.json')))['images']
    al = json.load(open(os.path.join(W, 'rect_alpha.json')))
    A, C = al['rects'], al['cell']
    sc = json.load(open(os.path.join(W, 'scenes.json')))

    def rects(ims):
        rs = set()
        for i in ims:
            for f in im[i]['frames']:
                rs.add((f['pack'], f['x'], f['y'], f['w'], f['h']))
        return rs

    latin = [i for i, x in enumerate(im) if not ('FONT' in x['name'] and x['name'].rsplit('_', 1)[-1] in LANGS)]
    used = sorted(set().union(*(set(s['images']) for s in sc)))
    for label, ims in (('all images, Latin fonts', latin), ('all images, all fonts', range(len(im))),
                       ('images some scene references', used)):
        rs = rects(ims)
        area = sum(r[3] * r[4] for r in rs) / 4096 ** 2
        t4 = pack([(r[3], r[4]) for r in rs], 4096)
        keep, cell = [], 0
        for r in rs:
            st = A['%d,%d,%d,%d,%d' % r]
            if r[3] * r[4] > 256 * 256 and st['opaque'] < 0.5 * r[3] * r[4]:
                cell += st['cells'] * C * C
            else:
                keep.append((r[3], r[4]))
        strips = math.ceil(cell / (C * C) / (2048 // C))
        tc = pack(keep + [(2047, C)] * strips, 2048)
        print('%-30s rects %.2f atlases | 4096 bins: 4444 %.0f MB (%d tex), DXT5 %.0f MB | cell-split, 2048 bins, 4444: %.0f MB' % (
            label, area, tex_bytes(t4, '4444') / MB, len(t4), tex_bytes(t4, 'dxt5') / MB, tex_bytes(tc, '4444') / MB))


if __name__ == '__main__':
    main()
