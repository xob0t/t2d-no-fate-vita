# ruff: noqa: SIM115, UP031
"""The game's texture packs (TexturePack0..11): the PNG bytes of each, from an AssetRipper export's
Resources/texturepack folder or straight from the game's resources.assets.

The game loads them as TextAssets (Resources.Load<TextAsset>("texturepack/TexturePack" + n), then
Texture2D.LoadImage), so they are TextAsset objects (class 49) holding a PNG: m_Name, then m_Script as a
length-prefixed byte string. This reads a SerializedFile of format 22 (Unity 2022.2 and later) without type trees,
which is what a player build has, or one with type trees when TextAsset's layout is the usual one.
"""

import os
import re
import struct

TEXT_ASSET = 49
PNG = b'\x89PNG\r\n\x1a\n'
NAME = re.compile(r'TexturePack(\d+)$')


def _cstr(data, pos):
    end = data.index(b'\0', pos)
    return data[pos:end].decode('utf-8'), end + 1


def text_assets(path):
    """{name: bytes} of every TextAsset in a SerializedFile."""
    with open(path, 'rb') as fh:
        head = fh.read(48)
        version = struct.unpack_from('>I', head, 8)[0]
        if version < 22:
            raise ValueError('%s: SerializedFile format %d; only 22 and later are read' % (path, version))
        big = head[16] != 0
        meta_size, _file_size, data_offset = struct.unpack_from('>IQQ', head, 20)
        fh.seek(48)
        meta = fh.read(meta_size)
        e = '>' if big else '<'
        pos = 0
        _unity, pos = _cstr(meta, pos)
        pos += 4  # target platform
        type_tree = meta[pos] != 0
        pos += 1
        (ntypes,) = struct.unpack_from(e + 'i', meta, pos)
        pos += 4
        classes = []
        for _ in range(ntypes):
            (cid,) = struct.unpack_from(e + 'i', meta, pos)
            pos += 4 + 1 + 2
            if cid == 114:
                pos += 16
            pos += 16
            if type_tree:
                nodes, strsize = struct.unpack_from(e + 'ii', meta, pos)
                pos += 8 + nodes * 32 + strsize
                (ndeps,) = struct.unpack_from(e + 'i', meta, pos)
                pos += 4 + 4 * ndeps
            classes.append(cid)
        (nobj,) = struct.unpack_from(e + 'i', meta, pos)
        pos += 4
        out = {}
        for _ in range(nobj):
            pos = (pos + 3) & ~3
            _path_id, start, size, tindex = struct.unpack_from(e + 'qQIi', meta, pos)
            pos += 24
            if classes[tindex] != TEXT_ASSET:
                continue
            fh.seek(data_offset + start)
            obj = fh.read(size)
            (n,) = struct.unpack_from(e + 'i', obj, 0)
            name = obj[4 : 4 + n].decode('utf-8')
            p = (4 + n + 3) & ~3
            (m,) = struct.unpack_from(e + 'i', obj, p)
            out[name] = obj[p + 4 : p + 4 + m]
    return out


def from_game(game):
    """Pack number -> PNG bytes, from <game>/<name>_Data/resources.assets."""
    data = [d for d in os.listdir(game) if d.endswith('_Data') and os.path.isdir(os.path.join(game, d))]
    if len(data) != 1:
        raise ValueError('%s: %d *_Data folders' % (game, len(data)))
    out = {}
    for name, blob in text_assets(os.path.join(game, data[0], 'resources.assets')).items():
        m = NAME.match(name)
        if m and blob.startswith(PNG):
            out[int(m.group(1))] = blob
    return out


def from_folder(folder):
    """Pack number -> PNG bytes, from TexturePackN.bytes or TexturePackN.png files (AssetRipper's export keeps
    the TextAssets as .bytes)."""
    out = {}
    for f in os.listdir(folder):
        stem, ext = os.path.splitext(f)
        m = NAME.match(stem)
        if m and ext in ('.bytes', '.png'):
            blob = open(os.path.join(folder, f), 'rb').read()
            if blob.startswith(PNG):
                out[int(m.group(1))] = blob
    return out


def load(game=None, folder=None):
    packs = from_folder(folder) if folder else from_game(game)
    if not packs or sorted(packs) != list(range(len(packs))):
        raise SystemExit(f'texture packs: found {sorted(packs)}, want 0..n-1')
    return packs
