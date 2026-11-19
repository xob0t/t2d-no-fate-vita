# ruff: noqa: SIM115, TRY004, UP031
"""The game's frame table: for every image row of AssetsPack, its sheet size and, per frame, the texture pack, the
rect in that pack and the trim. Read from the IL of Rewind's Assembly-CSharp.dll (no C# text parsing):

  AssetsPack.staticInit2                 imageclassname (row -> Assets.IMAGE_* id)
  AssetsPack.staticInit                  imagewidth, imageheight, imagetexframes_sfw, imagetexframes_sfh (blobs)
  AssetsPack.staticInit5..13             imagetexframes_tex, uvx, uvy, uvw, uvh, frw, frh, ofx, ofy (jagged)
  Assets..cctor                          IMAGE_*, MAP_* constants

If Rewind left gaps in staticInit6..9 (calls to its __Rewind helpers in the method, elements nobody stored),
they are filled from GameAssembly.dll with native.py, which needs an Il2CppDumper dump.cs for the addresses.
Without gaps, an element nobody stored is 0, as the C# compiler leaves default values out of initialisers.

Rects are in pack pixels, y from the bottom (Unity's UV origin); the PNG row of a rect's top is 4096 - y - h.

Usage: python -I frametable.py --asm <Rewind>/assemblies/Assembly-CSharp.dll --out frames.json
                                [--game <game folder> --dump dump.cs]
"""

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import il

PACK = 'com.bitmapbureau.terminator.AssetsPack'
ASSETS = 'com.bitmapbureau.terminator.Assets'
PACK_SIZE = 4096
JAGGED = [
    ('tex', 'staticInit5', 'imagetexframes_tex'),
    ('uvx', 'staticInit6', 'imagetexframes_uvx'),
    ('uvy', 'staticInit7', 'imagetexframes_uvy'),
    ('uvw', 'staticInit8', 'imagetexframes_uvw'),
    ('uvh', 'staticInit9', 'imagetexframes_uvh'),
    ('frw', 'staticInit10', 'imagetexframes_frw'),
    ('frh', 'staticInit11', 'imagetexframes_frh'),
    ('ofx', 'staticInit12', 'imagetexframes_ofx'),
    ('ofy', 'staticInit13', 'imagetexframes_ofy'),
]
BLOBS = ['imagewidth', 'imageheight', 'imagetexframes_sfw', 'imagetexframes_sfh']


def constants(asm):
    """Assets' int constants: ldc.i4 then stsfld Assets::NAME, in its static constructor."""
    out = {}
    prev = None
    for ins in asm.body(asm.method(ASSETS, '.cctor')):
        if ins.op == 0x80 and prev is not None:
            _kind, (owner, name, _) = asm.token(ins.arg)
            if owner == ASSETS and (name.startswith(('IMAGE_', 'MAP_'))):
                if 0x16 <= prev.op <= 0x1E:
                    out[name] = prev.op - 0x16
                elif prev.op == 0x15:
                    out[name] = -1
                elif prev.op in (0x1F, 0x20):
                    out[name] = prev.arg
        prev = ins
    return out


def blob_fields(asm):
    """The int[] tables staticInit fills with InitializeArray: the field each ldtoken'd blob ends up in."""
    out = {}
    token = None
    for ins in asm.body(asm.method(PACK, 'staticInit')):
        if ins.op == 0xD0:
            kind, val = asm.token(ins.arg)
            if kind == 'field':
                token = val[2]
        elif ins.op == 0x80:
            kind, (owner, name, _) = asm.token(ins.arg)
            if owner == PACK and name in BLOBS and token is not None:
                out[name] = asm.blob_ints(token)
                token = None
    missing = [b for b in BLOBS if b not in out]
    if missing:
        raise ValueError(f'AssetsPack.staticInit: no blob for {missing}')
    return out


def jagged(asm, method, field):
    statics, markers = asm.run(asm.method(PACK, method))
    arr = statics.get((PACK, field))
    if not isinstance(arr, list):
        raise ValueError(f'{method}: {field} is not stored as an array')
    return arr, markers


def read(asm_path, game=None, dump=None, log=print):
    asm = il.Assembly(asm_path)
    consts = constants(asm)
    statics, _ = asm.run(asm.method(PACK, 'staticInit2'), {(ASSETS, k): v for k, v in consts.items()})
    cls = statics[(PACK, 'imageclassname')]
    n = len(cls)
    blobs = blob_fields(asm)
    tables, gaps = {}, {}
    for key, method, field in JAGGED:
        arr, markers = jagged(asm, method, field)
        if len(arr) != n:
            raise ValueError('%s has %d rows, imageclassname %d' % (field, len(arr), n))
        holes = [(i, j) for i, row in enumerate(arr) for j, v in enumerate(row or []) if v is None]
        if any(row is None for row in arr):
            raise ValueError(f'{field}: rows never stored: {[i for i, r in enumerate(arr) if r is None][:10]}')
        gaps[key] = {'markers': markers, 'unset': len(holes)}
        if holes and markers and key.startswith('uv'):
            if not (game and dump):
                raise SystemExit(
                    '%s: Rewind left %d gaps in %s; pass --game and --dump to fill them from '
                    'GameAssembly.dll' % (field, len(holes), method)
                )
            import native

            va = native.dump_vas(dump, 'AssetsPack', [method])[method]
            values, unknown = native.Native(os.path.join(game, 'GameAssembly.dll')).double_stores(va, n)
            filled = 0
            for i, j in holes:
                v = values.get(i, {}).get(j)
                if v is None:
                    raise SystemExit('%s[%d][%d]: not in the native code either' % (field, i, j))
                arr[i][j] = v
                filled += 1
            gaps[key].update(native_filled=filled, native_unknown=unknown)
            log('%s: %d gaps filled from GameAssembly.dll' % (field, filled))
        for row in arr:
            for j, v in enumerate(row):
                if v is None:
                    row[j] = 0
        tables[key] = arr
    names = {}
    for k, v in sorted(consts.items()):
        if k.startswith('IMAGE_') and not k.startswith('IMAGE_ARRAY_'):  # IMAGE_ARRAY_* index arrayentries
            names.setdefault(v, k)
    images, problems = [], []
    for i in range(n):
        frames = []
        for j in range(len(tables['tex'][i])):
            g = {k: tables[k][i][j] for k, _, _ in JAGGED}
            x, y, uw, uh = (round(g[k] * PACK_SIZE) for k in ('uvx', 'uvy', 'uvw', 'uvh'))
            for k, v in (('uvx', x), ('uvy', y), ('uvw', uw), ('uvh', uh)):
                if abs(g[k] * PACK_SIZE - v) > 1e-6:
                    problems.append('row %d frame %d: %s * 4096 = %r, not whole' % (i, j, k, g[k] * PACK_SIZE))
            if (uw, uh) != (g['frw'], g['frh']):
                problems.append(
                    'row %d frame %d: uv size %dx%d, trimmed size %dx%d' % (i, j, uw, uh, g['frw'], g['frh'])
                )
            frames.append({'pack': g['tex'], 'x': x, 'y': y, 'w': uw, 'h': uh, 'ox': g['ofx'], 'oy': g['ofy']})
        images.append(
            {
                'row': i,
                'id': cls[i],
                'name': names.get(cls[i], '#%d' % cls[i]),
                'width': blobs['imagewidth'][i],
                'height': blobs['imageheight'][i],
                'sfw': blobs['imagetexframes_sfw'][i],
                'sfh': blobs['imagetexframes_sfh'][i],
                'frames': frames,
            }
        )
    return {
        'rows': n,
        'pack_size': PACK_SIZE,
        'images': images,
        'constants': consts,
        'gaps': gaps,
        'problems': problems,
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--asm', required=True, help="Rewind's Assembly-CSharp.dll")
    ap.add_argument('--game', help='game folder (GameAssembly.dll), only needed to fill gaps')
    ap.add_argument('--dump', help='Il2CppDumper dump.cs, only needed to fill gaps')
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    t = read(a.asm, a.game, a.dump)
    json.dump(t, open(a.out, 'w'), separators=(',', ':'))
    print(
        'rows %d, frames %d, problems %d' % (t['rows'], sum(len(i['frames']) for i in t['images']), len(t['problems']))
    )
    for p in t['problems'][:20]:
        print('  ' + p)


if __name__ == '__main__':
    main()
