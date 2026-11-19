"""Scheme d: one static re-atlas of all frames into PAGExPAGE pages grouped by
the set of scenes that use each frame, so the frame -> (page, uv) table never
changes at run time and a scene only needs the pages holding its frames.

Inputs (from --work): images.json, scenes.json. Writes cluster.json and
appends nothing else; prints per-scene resident pages and MB (RGBA4444).
"""
import argparse, collections, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from memory_model import Skyline, pow2, MB

LANGS = ('CNS', 'CNT', 'JP', 'KR')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', required=True)
    ap.add_argument('--page', type=int, default=2048)
    ap.add_argument('--global-frac', type=float, default=0.5,
                    help='frames used by at least this fraction of scenes go to shared pages')
    a = ap.parse_args()
    W, P = a.work, a.page
    images = json.load(open(os.path.join(W, 'images.json')))['images']
    scenes = json.load(open(os.path.join(W, 'scenes.json')))
    names = [s['scene'] for s in scenes]
    use = collections.defaultdict(set)          # rect -> scene indices
    for k, s in enumerate(scenes):
        for i in s['images']:
            for f in images[i]['frames']:
                use[(f['pack'], f['x'], f['y'], f['w'], f['h'])].add(k)
    # frames no scene reaches (other-language fonts, misses) still need a page
    for i, im in enumerate(images):
        for f in im['frames']:
            use.setdefault((f['pack'], f['x'], f['y'], f['w'], f['h']), set())
    n = len(scenes)

    def key(r):
        s = use[r]
        if len(s) >= a.global_frac * n:
            return (0, (), -r[4])
        if not s:
            return (2, (), -r[4])
        return (1, tuple(sorted(s)), -r[4])

    pages, cur = [], None      # page: dict(sky, rects, scenes, big)
    big = []
    for r in sorted(use, key=key):
        w, h = r[3] + 1, r[4] + 1
        if w > P or h > P:
            big.append(dict(size=(pow2(r[3]), pow2(r[4])), scenes=set(use[r])))
            continue
        k = key(r)[:2]
        if cur is None or cur['key'] != k and cur['sky'].used > 0.9 * P * P:
            cur = None
        if cur is None or not cur['sky'].place(w, h):
            cur = dict(sky=Skyline(P, P), scenes=set(), key=k)
            assert cur['sky'].place(w, h)
            pages.append(cur)
        cur['scenes'] |= use[r]
        cur['key'] = k
    per_scene = []
    for k, nm in enumerate(names):
        pg = [p for p in pages if k in p['scenes']]
        bg = [b for b in big if k in b['scenes']]
        mb = (len(pg) * P * P + sum(x * y for x, y in (b['size'] for b in bg))) * 2 / MB
        per_scene.append((nm, len(pg), len(bg), mb))
    total = (len(pages) * P * P + sum(x * y for x, y in (b['size'] for b in big))) * 2 / MB
    out = dict(page=P, pages=len(pages), big=len(big), total_mb_4444=total,
               per_scene=[dict(scene=a_, pages=b_, big=c_, mb_4444=d_) for a_, b_, c_, d_ in per_scene])
    json.dump(out, open(os.path.join(W, 'cluster_%d.json' % P), 'w'), indent=1)
    print('page %d: %d pages + %d oversize textures, all of it %.0f MB in 4444' % (P, len(pages), len(big), total))
    for nm, np_, nb, mb in per_scene:
        print('  %-36s %3d pages %d big %6.0f MB' % (nm, np_, nb, mb))


if __name__ == '__main__':
    main()
