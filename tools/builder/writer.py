"""Write one of the player's serialized files from converted objects.

    out = SerializedWriter('sharedassets1.assets')
    out.add(path_id, class_id, data, tree, script=None)  # data: dict in UnityPy type tree form
    bytes_ = out.write(types)                            # types: TypeHashes

References in `data` are {'m_FileID', 'm_PathID', 'file'}: 'file' names the file they point into
('sharedassets0.assets', 'resources/unity_builtin_extra', ...); the writer turns it into an index
into this file's externals, which it lists in order of first use (globalgamemanagers.assets first
when anything here uses it, as Unity does). Types are listed in order of first use too, one per
class and one per script for MonoBehaviours.
"""

import struct
from functools import cache

import convert
import typehash
from container import External, HeaderInfo, ObjectEntry, ScriptType, TypeEntry, write_serialized
from UnityPy.helpers.TypeTreeHelper import write_typetree
from UnityPy.helpers.TypeTreeNode import get_common_strings
from UnityPy.streams import EndianBinaryWriter

BUILTIN_GUIDS = {
    'library/unity default resources': bytes.fromhex('00000000000000000e00000000000000'),
    'resources/unity_builtin_extra': bytes.fromhex('00000000000000000f00000000000000'),
}
MONOBEHAVIOUR = 114


class TypeHashes:
    """Type table hashes. `script_info(script)` -> (script_id, old type hash, MonoScript path ID in
    globalgamemanagers.assets) for a (assembly, namespace, class) script."""

    def __init__(self, script_info, overrides=None):
        self.script_info = script_info
        self.overrides = overrides or {}

    @cache  # noqa: B019 - one instance per build
    def builtin(self, class_id):
        if class_id in self.overrides:
            return self.overrides[class_id]
        return typehash.builtin_hash(class_id, convert.VERSION)


def tree_blob(node):
    """A type tree as a serialized file (version 17) holds it: the node count, the string buffer's size,
    the nodes (version, level, is-array, type and name as string offsets, byte size, index, meta flags)
    and the buffer. Unity's common strings are offsets into its own table, with the top bit set."""
    common = {s: offset for offset, s in get_common_strings().items()}
    strings, offsets = bytearray(), {}

    def offset(s):
        if s in common:
            return common[s] | 0x80000000
        if s not in offsets:
            offsets[s] = len(strings)
            strings.extend(s.encode() + b'\0')
        return offsets[s]

    nodes = bytearray()
    count = 0
    for n in node.traverse():
        is_array = 1 if n.m_Type == 'Array' else 0
        type_at, name_at = offset(n.m_Type), offset(n.m_Name)
        nodes += struct.pack(
            '<hBBIIiii', n.m_Version, n.m_Level, is_array, type_at, name_at, n.m_ByteSize, count, n.m_MetaFlag
        )
        count += 1
    return struct.pack('<ii', count, len(strings)) + bytes(nodes) + bytes(strings)


class SerializedWriter:
    def __init__(self, name, platform=30, type_trees=False):
        self.name, self.platform, self.type_trees = name, platform, type_trees
        self.objects = []  # (path_id, class_id, script, data, tree)
        self.externals = []

    def add(self, path_id, class_id, data, tree=None, script=None):
        self.objects.append((path_id, class_id, script, data, tree))

    def external(self, file_name):
        if file_name not in self.externals:
            self.externals.append(file_name)
        return self.externals.index(file_name) + 1

    def resolve(self, value):
        """Turns 'file' in references into m_FileID indices, in place."""
        if isinstance(value, dict):
            if 'm_PathID' in value and 'm_FileID' in value:
                target = value.pop('file', None)
                if target is not None and target != self.name and value['m_PathID'] != 0:
                    value['m_FileID'] = self.external(target)
                return
            for v in value.values():
                self.resolve(v)
        elif isinstance(value, (list, tuple)):
            for v in value:
                self.resolve(v)

    def write(self, hashes):
        self.objects.sort(key=lambda o: o[0])
        # globalgamemanagers.assets holds the MonoScripts every script type points to
        if any(o[1] == MONOBEHAVIOUR for o in self.objects) and self.name != 'globalgamemanagers.assets':
            self.external('globalgamemanagers.assets')
        types, type_index, script_types = [], {}, []
        entries = []
        for path_id, class_id, script, data, tree in self.objects:
            if class_id == MONOBEHAVIOUR and script is None:
                raise ValueError(f"{self.name}: MonoBehaviour {path_id}'s m_Script does not resolve to a script")
            self.resolve(data)
            key = (class_id, script if class_id == MONOBEHAVIOUR else None)
            if key not in type_index:
                type_index[key] = len(types)
                if class_id == MONOBEHAVIOUR:
                    script_id, old_hash, monoscript = hashes.script_info(script)
                    index = (
                        0 if self.name == 'globalgamemanagers.assets' else self.external('globalgamemanagers.assets')
                    )
                    script_types.append(ScriptType(index, monoscript))
                    types.append(TypeEntry(class_id, False, script_types[-1], script_id, old_hash))
                else:
                    types.append(TypeEntry(class_id, False, -1, None, hashes.builtin(class_id)))
            node = tree or convert.release_tree(class_id)
            if self.type_trees and types[type_index[key]].tree is None:
                types[type_index[key]].tree = tree_blob(node)
            if isinstance(data, bytes):  # written as is: what the player holds is not its whole type tree
                entries.append(ObjectEntry(path_id, type_index[key], data))
                continue
            w = EndianBinaryWriter(endian='<')
            write_typetree(data, node, w)
            entries.append(ObjectEntry(path_id, type_index[key], w.bytes))
        # Unity lists script types by MonoScript; the type entries point into that order.
        script_types.sort(key=lambda s: (s.file_index, s.local_id))
        for t in types:
            if isinstance(t.script_type_index, ScriptType):
                t.script_type_index = script_types.index(t.script_type_index)
        externals = [External(path=f, guid=BUILTIN_GUIDS.get(f, b'\0' * 16)) for f in self.externals]
        header = HeaderInfo(target_platform=self.platform, enable_type_tree=self.type_trees)
        return write_serialized(header, types, entries, externals, script_types)
