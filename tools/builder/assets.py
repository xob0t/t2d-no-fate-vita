"""The project's assets: what each file holds once Unity has imported it, and what it references.

YAML assets (prefabs, sprites, materials, clips, ...) hold their objects as written. Imported
files hold what their importer makes: a texture its Texture2D (fileID 2800000), audio an
AudioClip (8300000), a font its Font (12800000) with a material and texture, a shader its Shader
(4800000), text a TextAsset (4900000).
"""

import os
import pickle
import re
from dataclasses import dataclass, field
from functools import cached_property

import unityyaml
from paths import PROJECT, WORK, progress

GUID = re.compile(r'guid: ([0-9a-f]{32})')
REF = re.compile(r'fileID: (-?\d+), guid: ([0-9a-f]{32})')
HEADER = re.compile(r'^--- !u!(\d+) &(-?\d+)', re.MULTILINE)
YAML_EXTENSIONS = {
    '.asset', '.prefab', '.mat', '.anim', '.controller', '.overrideController', '.mixer', '.renderTexture',
    '.flare', '.physicMaterial', '.physicsMaterial2D', '.guiskin', '.fontsettings', '.mask', '.lighting',
}  # fmt: skip
IMPORTED = {
    '.png': [(28, 2800000)], '.jpg': [(28, 2800000)], '.tga': [(28, 2800000)], '.psd': [(28, 2800000)],
    '.exr': [(28, 2800000)],
    '.ogg': [(83, 8300000)], '.wav': [(83, 8300000)], '.mp3': [(83, 8300000)], '.aif': [(83, 8300000)],
    '.ttf': [(128, 12800000), (21, 2100000), (28, 2800000)], '.otf': [(128, 12800000), (21, 2100000), (28, 2800000)],
    '.shader': [(48, 4800000)], '.compute': [(72, 7200000)],
    '.txt': [(49, 4900000)], '.bytes': [(49, 4900000)], '.xml': [(49, 4900000)], '.json': [(49, 4900000)],
    '.csv': [(49, 4900000)], '.webm': [(329, 32900000)], '.mp4': [(329, 32900000)],
}  # fmt: skip
INDEX_VERSION = 2  # 2: cubemaps (textureShape 2) are class 89
SKIP = {'.cs', '.dll', '.hlsl', '.cginc', '.unity', '.meta', '.md', '.so'}


@dataclass
class Asset:
    guid: str
    path: str  # relative to the project, with forward slashes
    ext: str
    meta_path: object = None
    refs: set = field(default_factory=set)
    object_refs: set = field(default_factory=set)  # (guid, fileID) pairs this asset references
    # fileID -> class ID; YAML documents are parsed on demand
    classes: dict = field(default_factory=dict)
    # fileID -> name of an imported file's named sub-objects: a sprite sheet's sprites
    names: dict = field(default_factory=dict)

    @property
    def is_yaml(self):
        return self.ext in YAML_EXTENSIONS

    @cached_property
    def docs(self):
        if not self.is_yaml:
            return {}
        return {d.file_id: d for d in unityyaml.load(PROJECT / self.path) if not d.stripped}

    def stripped(self):
        """A copy without the parsed documents, for the index cache."""
        return Asset(
            self.guid, self.path, self.ext, self.meta_path, self.refs, self.object_refs, self.classes, self.names
        )

    def name_of(self, file_id):
        if file_id in self.names:
            return self.names[file_id]
        d = self.docs.get(file_id)
        if d is not None:
            return d.fields.get('m_Name', '')
        if self.classes.get(file_id) == 48:
            return ''  # the build keeps a shader's name in its parsed form only
        if self.ext in ('.ttf', '.otf') and file_id != 12800000:
            return {2100000: 'Font Material', 2800000: 'Font Texture'}[file_id]
        return self.path.rsplit('/', 1)[-1].rsplit('.', 1)[0]


class AssetDB:
    def __init__(self, project=PROJECT):
        self.project = project
        cache_file = WORK / f'assetdb-{INDEX_VERSION}.pickle'  # a new version reindexes every asset
        cache = {}
        if cache_file.exists():
            with open(cache_file, 'rb') as f:
                cache = pickle.load(f)
        fresh = {}
        self.by_guid = {}
        for meta, stamp in progress.track(list(walk_metas(project / 'Assets')), "Indexing the project's assets"):
            hit = cache.get(meta)
            a = hit[1] if hit and hit[0] == stamp else self.index(meta)
            fresh[meta] = (stamp, a)
            if a is not None:
                self.by_guid[a.guid] = a
        if fresh.keys() != cache.keys() or any(fresh[k][0] != cache[k][0] for k in fresh):
            WORK.mkdir(parents=True, exist_ok=True)
            with open(cache_file, 'wb') as f:
                pickle.dump({k: (v[0], v[1] and v[1].stripped()) for k, v in fresh.items()}, f)

    def index(self, meta):
        source = meta[: -len('.meta')]
        ext = os.path.splitext(source)[1]
        if ext in SKIP or (ext not in YAML_EXTENSIONS and ext not in IMPORTED) or os.path.isdir(source):
            return None
        with open(meta, encoding='utf-8', errors='replace') as f:
            text = f.read()
        guid = re.search(r'^guid: ([0-9a-f]{32})', text, re.MULTILINE)[1]
        rel = os.path.relpath(source, self.project).replace(os.sep, '/')
        a = Asset(guid, rel, ext, meta)
        if a.is_yaml:
            with open(source, encoding='utf-8', errors='replace') as f:
                body = f.read()
            a.classes = {int(fid): int(cid) for cid, fid in HEADER.findall(body)}
            a.refs = set(GUID.findall(body)) - {guid}
            a.object_refs = {(g, int(fid)) for fid, g in REF.findall(body)}
        else:
            a.classes = {fid: cid for cid, fid in IMPORTED[ext]}
            sprite = re.search(r'^  textureType: 8$', text, re.MULTILINE)
            if a.classes.get(2800000) == 28 and re.search(r'^  textureShape: 2$', text, re.MULTILINE):
                a.classes = {8900000: 89}  # a cubemap
            if a.classes.get(2800000) == 28 and sprite and re.search(r'^  spriteMode: 1$', text, re.MULTILINE):
                a.classes[21300000] = 213  # a texture imported as one sprite
            elif a.classes.get(2800000) == 28 and sprite and re.search(r'^  spriteMode: 2$', text, re.MULTILINE):
                # A sprite sheet: its sprites are the importer's recycled sub-objects of class 213.
                recycled = RECYCLE_NAMES.search(text)
                for fid, name in RECYCLE_ENTRY.findall(recycled[1] if recycled else ''):
                    if fid.startswith('213') and len(fid) == 8:
                        a.classes[int(fid)] = 213
                        a.names[int(fid)] = unityyaml_scalar(name)
            a.refs = set(GUID.findall(text)) - {guid}
        return a

    def closure(self, guids):
        """The project assets reachable from these GUIDs (DLLs and built-in resources are not assets)."""
        seen, stack = set(), [g for g in guids if g in self.by_guid]
        while stack:
            g = stack.pop()
            if g in seen:
                continue
            seen.add(g)
            stack.extend(r for r in self.by_guid[g].refs if r in self.by_guid and r not in seen)
        return seen


RECYCLE_NAMES = re.compile(r'^  fileIDToRecycleName:\n((?:    -?\d+: .*\n)*)', re.MULTILINE)
RECYCLE_ENTRY = re.compile(r'^    (-?\d+): (.*)$', re.MULTILINE)


def unityyaml_scalar(text):
    """A recycled name as the importer wrote it: plain, or quoted when YAML needs it."""
    text = text.strip()
    if len(text) > 1 and text[0] == text[-1] == "'":
        return text[1:-1].replace("''", "'")
    if len(text) > 1 and text[0] == text[-1] == '"':
        return text[1:-1].encode().decode('unicode_escape')
    return text


def walk_metas(root):
    """(meta path, (mtime of the meta, mtime of its source)) for every .meta under root."""
    stack = [str(root)]
    while stack:
        with os.scandir(stack.pop()) as it:
            entries = list(it)
        times = {e.name: e.stat().st_mtime_ns for e in entries if not e.is_dir()}
        for e in entries:
            if e.is_dir():
                stack.append(e.path)
            elif e.name.endswith('.meta'):
                yield e.path, (times[e.name], times.get(e.name[:-5], 0))
