"""Built-in extra resources the build copies into its own files (layout.place_builtins): the
default sprite material and the sprite mask material. Their data comes from the editor's
Resources/unity_builtin_extra, mapped onto the player's type tree by field name."""

from functools import cache

import classes
import convert
import UnityPy
from layout import BUILTIN_EXTRA
from paths import EDITOR_DATA


@cache
def editor_objects():
    env = UnityPy.load(str(EDITOR_DATA / 'Resources' / 'unity_builtin_extra'))
    return next(iter(env.files.values())).objects


def release(node, value):
    """The editor's value reduced to the release tree's fields."""
    if isinstance(value, dict) and node.m_Children and node.m_Type not in ('vector', 'map'):
        return {c.m_Name: release(c, value.get(c.m_Name, convert.default(c))) for c in node.m_Children}
    return value


def shader_properties(shader):
    """The properties a built-in shader declares (m_PropInfo's)."""
    return editor_objects()[shader].read_typetree()['m_ParsedForm']['m_PropInfo']['m_Props']


# A shader property's m_Type: 0 Color, 1 Vector, 2 Float, 3 Range, 4 Texture.
PROPERTY_KIND = {0: 'm_Colors', 1: 'm_Colors', 2: 'm_Floats', 3: 'm_Floats', 4: 'm_TexEnvs'}


def shader_property_sheet(saved, properties):
    """m_SavedProperties as the build writes them: every property the shader declares, by name
    (ordinal order), with the material's value or else the shader's default; no others."""
    out = {'m_TexEnvs': {}, 'm_Floats': {}, 'm_Colors': {}}
    for p in properties:
        kind = PROPERTY_KIND[p['m_Type']]
        given = dict(saved[kind]).get(p['m_Name'])
        if given is not None:
            value = given
        elif kind == 'm_TexEnvs':
            value = {
                'm_Texture': {'m_FileID': 0, 'm_PathID': 0},
                'm_Scale': {'x': 1.0, 'y': 1.0},
                'm_Offset': {'x': 0.0, 'y': 0.0},
            }
        elif kind == 'm_Floats':
            value = p['m_DefValue[0]']
        else:
            value = dict(zip('rgba', (p[f'm_DefValue[{i}]'] for i in range(4)), strict=True))
        out[kind][p['m_Name']] = value
    return {**saved, **{kind: sorted(values.items(), key=lambda kv: kv[0].encode()) for kind, values in out.items()}}


def material(build, file_name, guid, file_id, asset):
    """Build.IMPORTED[21] for built-in materials: (data, None), or None for other materials. The
    build writes the shader's properties (shader_property_sheet): Default-Diffuse loses _Detail,
    Sprites-Default _DecalTex and gains _AlphaTex, _EnableExternalAlpha, _Flip and _RendererColor."""
    if guid != BUILTIN_EXTRA:
        return None
    data = release(convert.release_tree(21), editor_objects()[file_id].read_typetree())
    shader = data['m_Shader']['m_PathID']
    data['m_SavedProperties'] = shader_property_sheet(data['m_SavedProperties'], shader_properties(shader))
    found = build.placement.where.get((BUILTIN_EXTRA, shader))
    data['m_Shader'] = (
        {'m_FileID': 0, 'm_PathID': found[1], 'file': found[0]}
        if found
        else {'m_FileID': 0, 'm_PathID': shader, 'file': 'resources/unity_builtin_extra'}
    )
    classes.sprite_material(data, (guid, file_id), build.project)
    return data, None
