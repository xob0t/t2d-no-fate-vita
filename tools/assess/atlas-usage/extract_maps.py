"""Per-map object lists of Terminator 2D: NO FATE.

Joins the compiled map tables (AssetsStaticMap: blobs_name / blobs_id / blobs_param,
InitializeArray blobs in Rewind's Assembly-CSharp.dll) with the Tiled JSON maps
(object id -> object name), giving each map's objects as (name string, name id).

Usage: extract_maps.py --src SRC --asm DLL --maps MAPS_DIR --out OUT_DIR
Writes OUT_DIR/maps.json.
"""
import argparse, collections, json, os, re, struct, sys
import dnfile


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--asm', required=True)
    ap.add_argument('--maps', required=True)
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    T = os.path.join(a.src, 'com/bitmapbureau/terminator')
    sm = open(os.path.join(T, 'AssetsStaticMap.cs'), encoding='utf-8').read()
    assets = open(os.path.join(T, 'Assets.cs'), encoding='utf-8').read()
    mapconst = {m.group(1): int(m.group(2)) for m in re.finditer(r'\n\t\tMAP_(T2_[A-Z0-9_]+) = (\d+);', assets)}
    pe = dnfile.dnPE(a.asm)
    rva = {str(r.Field.row.Name): r.Rva for r in pe.net.mdtables.FieldRva.rows}
    pat = re.compile(r'__native_0x1800E2FD0\(__klass\(\), (\d+)L[^"]*"\);\s*\n\s*RuntimeHelpers\.InitializeArray\(\(Array\)\w+, __ldtoken\(<PrivateImplementationDetails>\.([0-9A-F]+)\)\);\s*\n(?:\s*int num\d+ = [^\n]*\n)?\s*DATA_(T2_[A-Z0-9_]+?)_blobs_(name|id|param|flag|diff|width|height) = ')
    tables = collections.defaultdict(dict)
    for m in pat.finditer(sm):
        n, tok, mp, fld = int(m.group(1)), m.group(2), m.group(3), m.group(4)
        tables[mp][fld] = list(struct.unpack('<%di' % n, pe.get_data(rva[tok], 4 * n)))
    out = {}
    name_ids = collections.defaultdict(collections.Counter)
    for mp, const in sorted(mapconst.items(), key=lambda x: x[1]):
        fn = os.path.join(a.maps, mp[:3] + mp[3:].lower() + '.json')
        js = json.load(open(fn, encoding='utf-8'))
        objs = {}
        for L in js['layers']:
            for o in L.get('objects', []) or []:
                objs[o['id']] = (L.get('name'), o.get('name') or o.get('type') or '')
        t = tables.get(mp, {})
        rows = []
        if 'name' in t and 'id' in t:
            for i, (nid, oid) in enumerate(zip(t['name'], t['id'])):
                layer, nm = objs.get(oid, (None, None))
                rows.append(dict(name_id=nid, tiled_id=oid, name=nm, layer=layer,
                                 param=t.get('param', [None] * len(t['name']))[i]))
                if nm:
                    name_ids[nm][nid] += 1
        imagelayers = [L.get('image') for L in js['layers'] if L.get('type') == 'imagelayer']
        out[mp] = dict(map_const=const, load_id=const + 1, objects=rows,
                       json_objects=len(objs), static_objects=len(rows), imagelayers=imagelayers,
                       have=sorted(t))
    conflicts = {k: dict(v) for k, v in name_ids.items() if len(v) > 1}
    os.makedirs(a.out, exist_ok=True)
    json.dump(dict(maps=out, name_ids={k: v.most_common(1)[0][0] for k, v in name_ids.items()},
                   conflicts=conflicts), open(os.path.join(a.out, 'maps.json'), 'w'), indent=1)
    for mp, d in out.items():
        print('%-28s load=%2d static=%4d json=%4d tables=%s' % (mp, d['load_id'], d['static_objects'], d['json_objects'], d['have']))
    print('distinct names', len(name_ids), 'conflicting', len(conflicts), list(conflicts.items())[:5])


if __name__ == '__main__':
    main()
