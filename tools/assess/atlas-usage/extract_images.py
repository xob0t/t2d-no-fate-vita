"""Build the image/frame -> texture-pack table of Terminator 2D: NO FATE.

Inputs (local, read-only):
  --src         Rewind C# root (Assembly-CSharp)
  --asm         Rewind Assembly-CSharp.dll (InitializeArray blobs)
  --native-dir  dir with native_uv{x,y,w,h}.json written by native_dstores.py
Output: images.json and images.csv in --out.
Rect y is the UV y (from the bottom, Unity convention); the PNG row of the
rect's top edge is 4096 - y - h (y_png in the CSV).
"""
import argparse, csv, json, os, re, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parse_cs import method_body, split_items, jagged_initializer, jagged_statements, jagged_any, static_ints, num
import dnfile


def blobs(asm):
    pe = dnfile.dnPE(asm)
    return {str(row.Field.row.Name): (pe, row.Rva) for row in pe.net.mdtables.FieldRva.rows}


def blob_ints(bl, token, n):
    pe, rva = bl[token]
    return list(struct.unpack('<%di' % n, pe.get_data(rva, 4 * n)))


def ldtoken_before(body, field):
    m = re.search(r'__ldtoken\(<PrivateImplementationDetails>\.([0-9A-F]+)\)\);\s*\n\s*%s = ' % re.escape(field), body)
    return m.group(1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--asm', required=True)
    ap.add_argument('--native-dir', required=True)
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    T = os.path.join(a.src, 'com/bitmapbureau/terminator')
    assets = open(os.path.join(T, 'Assets.cs'), encoding='utf-8').read()
    pack = open(os.path.join(T, 'AssetsPack.cs'), encoding='utf-8').read()
    consts = static_ints(assets, 'IMAGE_')
    byval = {}
    for k, v in consts.items():
        byval.setdefault(v, []).append(k)

    body2 = method_body(pack, 'staticInit2')
    cls = []
    for x in split_items(body2[body2.index('{') + 1:body2.rindex('}')]):
        x = x.replace('(int)(uint)', '').strip()
        cls.append(consts[x[len('Assets.'):]] if x.startswith('Assets.') else int(x))
    n = len(cls)

    bl = blobs(a.asm)
    b1 = method_body(pack, 'staticInit')
    width = blob_ints(bl, ldtoken_before(b1, 'imagewidth'), n)
    height = blob_ints(bl, ldtoken_before(b1, 'imageheight'), n)
    sfw = blob_ints(bl, ldtoken_before(b1, 'imagetexframes_sfw'), n)
    sfh = blob_ints(bl, ldtoken_before(b1, 'imagetexframes_sfh'), n)
    tex = jagged_any(method_body(pack, 'staticInit5'), 'int', n)
    ints = {f: jagged_any(method_body(pack, m), 'int', n)
            for f, m in (('frw', 'staticInit10'), ('frh', 'staticInit11'),
                         ('ofx', 'staticInit12'), ('ofy', 'staticInit13'))}
    uv, stats = {}, {}
    for f, m in (('uvx', 'staticInit6'), ('uvy', 'staticInit7'), ('uvw', 'staticInit8'), ('uvh', 'staticInit9')):
        arr = jagged_statements(method_body(pack, m), 'double', n)
        nat = {int(k): {int(j): x for j, x in v.items()}
               for k, v in json.load(open(os.path.join(a.native_dir, 'native_%s.json' % f))).items()}
        filled, missing, unparsed = 0, [], []
        for i in range(n):
            if arr[i] is None:
                unparsed.append(i)
                arr[i] = [None] * len(tex[i])
            for j in range(len(arr[i])):
                if arr[i][j] is None:
                    if j in nat.get(i, {}):
                        arr[i][j] = nat[i][j]
                        filled += 1
                    else:
                        missing.append((i, j))
        stats[f] = dict(native_filled=filled, missing=missing, unparsed=unparsed)
        uv[f] = arr
    for nm, arr in [('tex', tex)] + list(ints.items()):
        assert len(arr) == n, (nm, len(arr))

    images = []
    for i in range(n):
        names = byval.get(cls[i], [])
        frames = []
        for j in range(len(tex[i])):
            g = lambda f: uv[f][i][j] if j < len(uv[f][i]) and uv[f][i][j] is not None else None
            fr = dict(pack=tex[i][j], w=ints['frw'][i][j], h=ints['frh'][i][j],
                      ox=ints['ofx'][i][j], oy=ints['ofy'][i][j])
            for f, k in (('uvx', 'x'), ('uvy', 'y'), ('uvw', 'uw'), ('uvh', 'uh')):
                v = g(f)
                fr[k] = None if v is None else round(v * 4096)
            frames.append(fr)
        images.append(dict(id=i, cls=cls[i], name=names[0] if names else '#%d' % cls[i],
                           width=width[i], height=height[i], sfw=sfw[i], sfh=sfh[i], frames=frames))
    os.makedirs(a.out, exist_ok=True)
    json.dump(dict(images=images, stats=stats), open(os.path.join(a.out, 'images.json'), 'w'))
    with open(os.path.join(a.out, 'images.csv'), 'w', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(['image_id', 'name', 'frame', 'pack', 'x', 'y_uv', 'y_png', 'w', 'h', 'ofx', 'ofy',
                    'sheet_w', 'sheet_h', 'frame_w', 'frame_h'])
        for im in images:
            for j, fr in enumerate(im['frames']):
                w.writerow([im['id'], im['name'], j, fr['pack'], fr['x'], fr['y'], 4096 - fr['y'] - fr['h'], fr['w'], fr['h'],
                            fr['ox'], fr['oy'], im['width'], im['height'], im['sfw'], im['sfh']])
    print('images', n, 'frames', sum(len(i['frames']) for i in images),
          'classname!=index', sum(1 for i in range(n) if cls[i] != i))
    for f, s in stats.items():
        print(f, 'native_filled', s['native_filled'], 'missing', len(s['missing']), s['missing'][:20],
              'unparsed', len(s['unparsed']), s['unparsed'][:10])


if __name__ == '__main__':
    main()
