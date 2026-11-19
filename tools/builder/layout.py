"""Which serialized file each asset goes to, and the order of objects in it.

- An asset under a Resources folder goes to resources.assets.
- A shader in GraphicsSettings' Always Included Shaders goes to globalgamemanagers.assets.
- Any other asset goes to sharedassetsN.assets, N the first scene (build order) whose
  dependencies reach it. Assets no scene reaches are left out.

Inside a file: PreloadData first, then assets by class ID, then GameObjects and components by
class ID, MonoBehaviours last. Within a class, YAML objects come first: those of assets before
those of prefabs, each by fileID then asset path (so a prefab's objects mix with other prefabs');
then the objects of imported files (textures, audio, fonts, ...) by fileID, then GUID: a texture's single
sprite (21300000) before every sheet's, sheets by their sprites' fileIDs (sprite_rules.py numbers them apart).
"""

import re
from functools import cache

import editor_settings
import unityyaml
from paths import EDITOR_DATA, PROJECT

from assets import GUID, REF, AssetDB

BUILTIN_EXTRA = '0000000000000000f000000000000000'

# Classes the player never reads: editor data (lighting data, the animator's states and
# transitions), the prefab object of legacy prefabs, and what the PS Vita lacks (compute shaders,
# video clips).
EDITOR_ONLY = {1001, 1120, 196, 72, 329, 115, 244, 1101, 1102, 1107, 1109, 1111}
# Sub-objects an importer makes that are written only when something references them.
ON_REFERENCE = {213}
COMPONENT_CLASSES = {1, 4, 20, 23, 33, 58, 60, 61, 81, 82, 95, 96, 102, 120, 124, 166, 167, 168, 169, 198, 199, 212}
COMPONENT_CLASSES |= {223, 224, 225, 331, 114, 50, 70}
COMPONENT_CLASSES |= {54, 64, 65, 135, 136, 143}  # 3D physics: Rigidbody, the colliders, CharacterController
MONOBEHAVIOUR = 114


def build_scenes(project=PROJECT):
    settings = unityyaml.meta(project / 'ProjectSettings' / 'EditorBuildSettings.asset')['EditorBuildSettings']
    return [s for s in settings['m_Scenes'] if s.get('enabled', '1') == '1']


def always_included_shaders(project=PROJECT):
    text = editor_settings.graphics_settings_text(project)
    block = text.split('m_AlwaysIncludedShaders:')[1].split('m_PreloadedShaders')[0]
    return set(GUID.findall(block))


@cache
def builtin_extra():
    """fileID -> (class ID, the fileID of its shader if a material) for the editor's built-in extra resources."""
    import UnityPy

    env = UnityPy.load(str(EDITOR_DATA / 'Resources' / 'unity_builtin_extra'))
    out = {}
    for pid, o in next(iter(env.files.values())).objects.items():
        shader = o.read_typetree()['m_Shader']['m_PathID'] if o.type.name == 'Material' else None
        out[pid] = (o.class_id, shader)
    return out


@cache
def builtin_shader_names():
    """name -> fileID of the editor's built-in extra shaders."""
    import UnityPy

    env = UnityPy.load(str(EDITOR_DATA / 'Resources' / 'unity_builtin_extra'))
    objects = next(iter(env.files.values())).objects.items()
    return {o.read_typetree()['m_ParsedForm']['m_Name']: pid for pid, o in objects if o.type.name == 'Shader'}


def shader_by_name(name, names):
    """What a shader name in Fallback or Dependency finds among names: the name, else the
    built-in shader Unity moved under Legacy Shaders/ (Transparent/VertexLit)."""
    for candidate in (name, f'Legacy Shaders/{name}'):
        if candidate in names:
            return names[candidate]
    return None


SHADER_USES = re.compile(r'^\s*(?:Fallback|Dependency\s+"[^"]*"\s*=)\s*"([^"]+)"', re.MULTILINE | re.IGNORECASE)


def rank(class_id):
    if class_id == 150:
        return (0, 0)
    if class_id == MONOBEHAVIOUR:
        return (3, 0)
    return (2 if class_id in COMPONENT_CLASSES else 1, class_id)


class Layout:
    def __init__(self, project=PROJECT):
        self.db = AssetDB(project)
        self.scenes = build_scenes(project)
        self.scene_refs = []
        for s in self.scenes:
            text = (project / s['path']).read_text(encoding='utf-8', errors='replace')
            self.scene_refs.append(set(GUID.findall(text)))
        self.closures = [self.db.closure(refs) for refs in self.scene_refs]
        self.file_of = {}
        resources = {g for g, a in self.db.by_guid.items() if re.search(r'(^|/)Resources/', a.path)}
        for g in resources:
            self.file_of[g] = 'resources.assets'
        for g in always_included_shaders(project):
            if g in self.db.by_guid:
                self.file_of.setdefault(g, 'globalgamemanagers.assets')
        for i, closure in enumerate(self.closures):
            for g in sorted(closure):
                self.file_of.setdefault(g, f'sharedassets{i}.assets')
        self.resources = resources
        self.referenced = set()
        for refs in self.scene_object_refs(project):
            self.referenced |= refs
        for a in self.db.by_guid.values():
            self.referenced |= a.object_refs
        self.place_builtins(project)

    def place_builtins(self, project):
        """Built-in extra resources the game's content uses are copied into the first file that uses
        them; the ones GraphicsSettings names for the engine stay in unity_builtin_extra, except the
        default sprite material, which goes to globalgamemanagers.assets with its shader."""
        graphics = editor_settings.graphics_settings_text(project)
        sprites_default = re.search(r'm_SpritesDefaultMaterial: \{fileID: (\d+)', graphics)
        engine = {int(fid) for fid, g in REF.findall(graphics) if g == BUILTIN_EXTRA}
        table = builtin_extra()
        self.builtin_file = {}

        def place(fid, file_name):
            if fid in self.builtin_file or fid in engine - {default} or fid not in table:
                return
            self.builtin_file[fid] = file_name
            shader = table[fid][1]
            if shader and shader not in engine:
                place(shader, file_name)

        default = int(sprites_default[1]) if sprites_default else None
        if default:
            place(default, 'globalgamemanagers.assets')
        files = ['globalgamemanagers.assets'] + [f'sharedassets{i}.assets' for i in range(len(self.scenes))]
        files.append('resources.assets')
        uses = {}
        for g, f in self.file_of.items():
            a = self.db.by_guid[g]
            for ref, fid in a.object_refs:
                if ref == BUILTIN_EXTRA:
                    uses.setdefault(f, set()).add(fid)
            # A shader's Fallback (or Dependency) on a built-in one copies it in too: Sprites/Diffuse
            # Flash falls back to Transparent/VertexLit, which resources.assets holds.
            if a.ext == '.shader':
                text = (project / a.path).read_text(encoding='utf-8', errors='replace')
                for name in SHADER_USES.findall(text):
                    fid = shader_by_name(name, builtin_shader_names())
                    if fid is not None:
                        uses.setdefault(f, set()).add(fid)
        for f in files:
            for fid in sorted(uses.get(f, ())):
                place(fid, f)

    def scene_object_refs(self, project):
        for s in self.scenes:
            text = (project / s['path']).read_text(encoding='utf-8', errors='replace')
            yield {(g, int(fid)) for fid, g in REF.findall(text)}

    def objects(self, file_name):
        """[(guid, fileID, class ID)] of the assets in a file, in file order (PreloadData not included)."""
        out = []
        for g, f in self.file_of.items():
            if f != file_name:
                continue
            a = self.db.by_guid[g]
            for fid, cid in a.classes.items():
                if cid in EDITOR_ONLY or (
                    cid in ON_REFERENCE
                    and not a.is_yaml
                    and g not in self.resources
                    and (g, fid) not in self.referenced
                ):
                    continue
                out.append((g, fid, cid))

        table = builtin_extra()
        out += [(BUILTIN_EXTRA, fid, table[fid][0]) for fid, f in self.builtin_file.items() if f == file_name]

        def key(o):
            g, fid, cid = o
            if g == BUILTIN_EXTRA:
                return (rank(cid), 0, False, fid, '')
            a = self.db.by_guid[g]
            if not a.is_yaml:
                return (rank(cid), 1, fid, g)
            return (rank(cid), 0, a.ext == '.prefab', fid, a.path)

        return sorted(out, key=key)
