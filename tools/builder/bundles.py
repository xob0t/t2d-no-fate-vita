"""The AssetBundles under StreamingAssets, as the editor's BuildAssetBundles writes them
(tools/downgrade/unity/DowngradeBuild.cs: ChunkBasedCompression, DeterministicAssetBundle).

    for name in bundle_names(layout.db): write(build, name) -> bytes of Media/StreamingAssets/<name>

What the reference build shows:
- A bundle holds the assets whose importer names it (assetBundleName), every object of each but the
  editor's, and what they reference that no bundle holds: here only the built-in Sprites-Default
  material, copied in. Shaders of the built-in extra resources stay there (an external).
- One serialized file, CAB-<MD4 of the bundle's name>, with type trees, and its streams beside it
  (CAB-<...>.resS for textures, .resource for sounds), named archive:/CAB-<...>/CAB-<...>.<ext> in
  the objects that point into them.
- Path IDs are the first 8 bytes (little-endian) of an MD4: of the asset's GUID as text, its file type
  (int32: 2 for a YAML asset, 3 for an imported one) and the object's fileID (int64); of
  'resources/unity_builtin_extra' and the fileID for a copied built-in object. The AssetBundle object is 1.
- The AssetBundle object lists, in m_Container by asset path (lowercase), each asset's main object and
  its other objects but child GameObjects, all with the asset's range of m_PreloadTable: the objects it
  needs, externals first, then by path ID. The ranges follow the assets in Unity's GUID order (four
  little-endian uint32).
- The UnityFS container: blocks of 128 KB over the files one after another, each LZ4HC (level 9 of
  lz4 1.9 matches some, not all: Unity's LZ4 is older), the blocks info LZ4HC too. --compare compares
  the files inside, not the compressed bytes.
"""

import re
import struct
from pathlib import Path

import lz4.block
import lz4unity
import paths
from Crypto.Hash import MD4
from layout import BUILTIN_EXTRA, EDITOR_ONLY, builtin_extra
from placement import DEFAULT_RESOURCES
from UnityPy.streams import EndianBinaryWriter
from writer import SerializedWriter

BUNDLE_NAME = re.compile(r'^  assetBundleName: (\S+)\s*$', re.MULTILINE)
BUILTIN_EXTRA_PATH = 'resources/unity_builtin_extra'
DEFAULT_RESOURCES_PATH = 'library/unity default resources'
ASSET_BUNDLE = 142
GAME_OBJECT, TRANSFORM, SPRITE, TEXTURE, SHADER = 1, 4, 213, 28, 48
TEXTURE_FILE_ID = 2800000
BLOCK_SIZE = 128 * 1024
LZ4HC = 3  # a block's and the blocks info's compression
SERIALIZED_NODE = 4  # a directory entry's flag for the serialized file


def bundle_names(db):
    """{bundle name: [GUIDs of the assets that name it]}, from the importers' assetBundleName."""
    out = {}
    for g, a in db.by_guid.items():
        found = BUNDLE_NAME.search(Path(a.meta_path).read_text(encoding='utf-8', errors='replace'))
        if found and found[1] != "''":
            out.setdefault(found[1], []).append(g)
    return out


def cab_name(bundle):
    return 'CAB-' + MD4.new(bundle.encode()).hexdigest()


def path_id(guid, fid, yaml):
    if guid == BUILTIN_EXTRA:
        data = BUILTIN_EXTRA_PATH.encode() + struct.pack('<q', fid)
    else:
        data = guid.encode() + struct.pack('<i', 2 if yaml else 3) + struct.pack('<q', fid)
    return struct.unpack('<q', MD4.new(data).digest()[:8])[0]


def unity_guid_order(guid):
    """Unity's GUID order: four uint32, each from 8 hex digits read two by two, low digit first."""
    return struct.unpack('<4I', bytes(int(guid[i + 1] + guid[i], 16) for i in range(0, 32, 2)))


class BundlePlacement:
    """(guid, fileID) -> (file, path ID) for a bundle's objects; the built-in resources as externals."""

    def __init__(self, file_name):
        self.file = file_name
        self.where = {}

    def find(self, guid, fid):
        if (guid, fid) in self.where:
            return self.where[(guid, fid)]
        if guid == BUILTIN_EXTRA:
            return (BUILTIN_EXTRA_PATH, fid)
        if guid == DEFAULT_RESOURCES:
            return (DEFAULT_RESOURCES_PATH, fid)
        return (None, 0)


class Bundle:
    def __init__(self, build, name, guids):
        self.build, self.name = build, name
        self.db = build.layout.db
        self.cab = cab_name(name)
        self.file = f'archive:/{self.cab}/{self.cab}'
        self.placement = BundlePlacement(self.file)
        self.explicit = sorted(guids, key=lambda g: self.db.by_guid[g].path.lower())
        self.objects = {}  # (guid, fileID) -> class ID, every object the bundle holds
        for g in self.explicit:
            for fid, cid in self.own_objects(g):
                self.objects[(g, fid)] = cid
        for g in self.explicit:
            for key in self.needs(g):
                self.take(key)
        for g, fid in self.objects:
            self.placement.where[(g, fid)] = (self.file, path_id(g, fid, self.yaml(g)))

    def yaml(self, g):
        return g != BUILTIN_EXTRA and self.db.by_guid[g].is_yaml

    def own_objects(self, g):
        return [(fid, cid) for fid, cid in self.db.by_guid[g].classes.items() if cid not in EDITOR_ONLY]

    def class_of(self, g, fid):
        if g == BUILTIN_EXTRA:
            return builtin_extra()[fid][0]
        return self.db.by_guid[g].classes.get(fid)

    def refs(self, key):
        """The objects one object references (an asset's YAML: all of the asset's references)."""
        g, fid = key
        if g == BUILTIN_EXTRA:
            shader = builtin_extra()[fid][1]
            return [(BUILTIN_EXTRA, shader)] if shader else []
        a = self.db.by_guid.get(g)
        if a is None:
            return []
        if a.is_yaml:
            return sorted(a.object_refs)
        if a.classes.get(fid) == SPRITE:
            return [(g, TEXTURE_FILE_ID)]
        return []

    def stays_external(self, key):
        g, fid = key
        return g == DEFAULT_RESOURCES or (g == BUILTIN_EXTRA and self.class_of(g, fid) == SHADER)

    def take(self, key):
        """Holds a referenced object no bundle holds (and what it references) in this one."""
        if key in self.objects or self.stays_external(key) or self.class_of(*key) is None:
            return
        self.objects[key] = self.class_of(*key)
        for ref in self.refs(key):
            self.take(ref)

    def needs(self, g):
        """Every object asset g needs loaded, its own among them."""
        out, todo = set(), [(g, fid) for fid, _ in self.own_objects(g)]
        while todo:
            key = todo.pop()
            if key in out or (key[0] != BUILTIN_EXTRA and self.class_of(*key) is None):
                continue
            out.add(key)
            todo += self.refs(key)
        return out

    def main_object(self, g):
        """The object an asset's path loads: a prefab's root GameObject, else its first object by fileID."""
        a = self.db.by_guid[g]
        if a.is_yaml and a.ext == '.prefab':
            for fid, cid in sorted(a.classes.items()):
                if cid == TRANSFORM and int(a.docs[fid].fields.get('m_Father', {}).get('fileID', 0)) == 0:
                    return int(a.docs[fid].fields['m_GameObject']['fileID'])
        return min(fid for fid, cid in self.own_objects(g))

    def owner_names(self, g):
        """{fileID: lowercase name of its GameObject} of a prefab's components: the container lists them by
        class, then by that name (Mid2_3-1, Mid2_3-1 (1), Mid2Bg)."""
        a = self.db.by_guid[g]
        if not (a.is_yaml and a.ext == '.prefab'):
            return {}
        out = {}
        for fid, cid in a.classes.items():
            owner = a.docs[fid].fields.get('m_GameObject') if cid not in (GAME_OBJECT, *EDITOR_ONLY) else None
            if owner and int(owner.get('fileID', 0)) in a.docs:
                out[fid] = a.docs[int(owner['fileID'])].fields.get('m_Name', '').lower()
        return out

    def pptr(self, key):
        f, pid = self.placement.find(*key)
        return {'m_FileID': 0, 'm_PathID': pid, 'file': f}

    def asset_bundle(self):
        preload, ranges = [], {}
        for g in sorted(self.explicit, key=unity_guid_order):
            needed = self.needs(g)
            external = sorted((k for k in needed if self.stays_external(k)), key=lambda k: k[1])
            local = sorted((k for k in needed if not self.stays_external(k)), key=lambda k: self.placement.find(*k)[1])
            ranges[g] = (len(preload), len(external) + len(local))
            preload += [self.pptr(k) for k in external + local]
        container = []
        for g in self.explicit:
            a = self.db.by_guid[g]
            main = self.main_object(g)
            owner = self.owner_names(g)
            rest = sorted(
                ((fid, cid) for fid, cid in self.own_objects(g) if fid != main and cid != GAME_OBJECT),
                key=lambda o: (o[1], owner.get(o[0], ''), o[0]),
            )
            index, size = ranges[g]
            for fid in [main] + [fid for fid, _ in rest]:
                info = {'preloadIndex': index, 'preloadSize': size, 'asset': self.pptr((g, fid))}
                container.append((a.path.lower(), info))
        return {
            'm_Name': self.name,
            'm_PreloadTable': preload,
            'm_Container': container,
            'm_MainAsset': {'preloadIndex': 0, 'preloadSize': 0, 'asset': {'m_FileID': 0, 'm_PathID': 0}},
            'm_RuntimeCompatibility': 1,
            'm_AssetBundleName': self.name,
            'm_Dependencies': [],
            'm_IsStreamedSceneAssetBundle': False,
            'm_ExplicitDataLayout': 0,
            'm_PathFlags': 7,
            'm_SceneHashes': [],
        }

    def files(self):
        """[(name, bytes)] of the bundle's files: the serialized file, then its streams."""
        build = self.build
        main_placement = build.placement
        build.placement = self.placement
        try:
            out = SerializedWriter(self.file, build.target.platform, type_trees=True)
            out.add(1, ASSET_BUNDLE, self.asset_bundle())
            missing = {}
            ordered = sorted(self.objects, key=lambda k: self.placement.find(*k)[1])
            build.bundle_objects[self.file] = [(g, fid, self.objects[(g, fid)]) for g, fid in ordered]
            for g, fid in ordered:
                cid = self.objects[(g, fid)]
                data, tree, script = build.make(self.file, g, fid, cid)
                if data is None:
                    missing[cid] = missing.get(cid, 0) + 1
                    continue
                out.add(self.placement.find(g, fid)[1], cid, data, tree, script)
            serialized = out.write(build.hashes)
        finally:
            build.placement = main_placement
        files = [(self.cab, serialized)]
        for ext in ('.resS', '.resource'):
            stream = build.streams.pop(self.file + ext, None)
            if stream is not None:
                files.append((self.cab + ext, stream_file(ext, stream)))
        return files, missing


def stream_file(ext, data):
    """A bundle's stream file: a .resS ends padded to a multiple of 4 bytes, as each texture in it starts
    (tools/builder/check_textures.py: an editor bundle whose last texture is 1x1 RGBA4444)."""
    data = bytes(data)
    return data + bytes(-len(data) % 4) if ext == '.resS' else data


def unityfs(files, engine=paths.TARGET):
    """The UnityFS container of files [(name, bytes)]: the blocks info and every 128 KB block LZ4HC."""
    data = b''.join(d for _, d in files)
    blocks, compressed = [], []
    for at in range(0, len(data), BLOCK_SIZE):
        raw = data[at : at + BLOCK_SIZE]
        packed = lz4unity.compress(raw, 16)
        blocks.append((len(raw), len(packed)))
        compressed.append(packed)
    info = EndianBinaryWriter(endian='>')
    info.write_bytes(bytes(16))
    info.write_int(len(blocks))
    for raw, packed in blocks:
        info.write_u_int(raw)
        info.write_u_int(packed)
        info.write_u_short(LZ4HC)
    info.write_int(len(files))
    offset = 0
    for i, (name, d) in enumerate(files):
        info.write_long(offset)
        info.write_long(len(d))
        info.write_u_int(SERIALIZED_NODE if i == 0 else 0)
        info.write_string_to_null(name)
        offset += len(d)
    info_raw = info.bytes
    info_packed = lz4unity.compress(info_raw, 16)
    header = EndianBinaryWriter(endian='>')
    header.write_string_to_null('UnityFS')
    header.write_u_int(6)
    header.write_string_to_null('5.x.x')
    header.write_string_to_null(engine)
    size_at = header.Position
    header.write_long(0)
    header.write_u_int(len(info_packed))
    header.write_u_int(len(info_raw))
    header.write_u_int(0x40 | LZ4HC)  # the blocks info right after the header, with the directory
    out = bytearray(header.bytes) + info_packed + b''.join(compressed)
    out[size_at : size_at + 8] = struct.pack('>q', len(out))
    return bytes(out)


def read_unityfs(data):
    """[(name, bytes)] of the files in a UnityFS container (for --compare)."""
    p = data.index(b'\0') + 1 + 4
    p = data.index(b'\0', p) + 1
    p = data.index(b'\0', p) + 1
    _, packed, raw, flags = struct.unpack_from('>qIII', data, p)
    p += 20
    if flags & 0x80:
        raise NotImplementedError('a UnityFS container with its blocks info at the end')
    info = data[p : p + packed]
    info = lz4.block.decompress(info, uncompressed_size=raw) if flags & 0x3F else info  # uncompressed bundles too
    p += packed
    (count,) = struct.unpack_from('>i', info, 16)
    q, chunks = 20, []
    for _ in range(count):
        size, packed_size, flags = struct.unpack_from('>IIH', info, q)
        q += 10
        chunk = data[p : p + packed_size]
        chunks.append(lz4.block.decompress(chunk, uncompressed_size=size) if flags & 0x3F else chunk)
        p += packed_size
    whole = b''.join(chunks)
    (nodes,) = struct.unpack_from('>i', info, q)
    q += 4
    files = []
    for _ in range(nodes):
        offset, size, _ = struct.unpack_from('>qqI', info, q)
        q += 20
        end = info.index(b'\0', q)
        files.append((info[q:end].decode(), whole[offset : offset + size]))
        q = end + 1
    return files
