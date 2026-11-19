"""Project rules for the exported assets and settings: each *_overlay function returns
{path in the project: new content} for pipeline.asset_overlay, which runs them in order.

The export is of a Unity 6000.0 game, serialized by AssetRipper for 2018.2. What it still carries of
Unity 6 (docs/assessment.md, "AssetRipper") is fixed here: PlayerSettings at its 2018.2 serialized
version, MultiplayerManager.asset and the scene's empty LightingDataAsset left out, and the engine
modules and helper assemblies Unity 2018.2 doesn't have dropped from Assets/Plugins. Some of
butcher-vita's rules (MIT, same author) are here as they were: the settings, the product GUID and link.xml.
"""

import re
import sys
from pathlib import Path

from common import (
    HERE,
    SHADER_NAME,
    expect,
    guid_for,
    listing,
    log,
    meta_of,
)

# T2DCompat's GUID in the project (pipeline.assembly_overlay writes its .meta with it).
COMPAT_GUID = guid_for('plugin', 'T2DCompat')


# IL2CPP strips the class libraries down to what code references. The game's assemblies are kept whole:
# Haxe's runtime finds classes by name (Type.resolveClass), and serialized types are found by name too.
# The PS Vita's free-memory readers, which T2DCompat's probe calls by reflection (compat/Probe.cs).
PLATFORM_LINK_PRESERVE = {'vita': {'UnityEngine.PSVitaModule': ['UnityEngine.PSVita.Diagnostics']}}


def link_xml(assemblies: list, platform: str):
    """The linker's link.xml for the platform; assemblies: the game's and the plugins' DLLs."""
    kept = PLATFORM_LINK_PRESERVE.get(platform, {})
    lines = ['<linker>']
    lines += [f'  <assembly fullname="{dll.stem}" preserve="all"/>' for dll in sorted(assemblies)]
    for assembly, types in sorted(kept.items()):
        lines.append(f'  <assembly fullname="{assembly}">')
        lines += [f'    <type fullname="{t}" preserve="all"/>' for t in sorted(types)]
        lines.append('  </assembly>')
    return '\n'.join([*lines, '</linker>', ''])


# ---------------------------------------------------------------- Assets/Plugins

PLUGINS = Path('Assets') / 'Plugins'
# What the export puts in Assets/Plugins that the project doesn't take: the game's assemblies, which the code
# stage's rewritten ones replace (keeping their .meta); the Unity 6 engine modules 2018.2 doesn't have (its own
# modules come from the editor's files); the class library helper and AssetRipper's generated assembly.
REPLACED_PLUGINS = {'Assembly-CSharp', 'InControl', 'Steamworks'}
UNITY6_MODULES = {
    'UnityEngine.AndroidJNIModule',
    'UnityEngine.HierarchyCoreModule',
    'UnityEngine.InputForUIModule',
    'UnityEngine.InputLegacyModule',
    'UnityEngine.PropertiesModule',
    'UnityEngine.SubsystemsModule',
    'UnityEngine.TextCoreFontEngineModule',
    'UnityEngine.TextCoreTextEngineModule',
    'UnityEngine.UnityAnalyticsCommonModule',
    'UnityEngine.VFXModule',
}
DROPPED_PLUGINS = UNITY6_MODULES | {'System.Runtime.CompilerServices.Unsafe', '__Generated'}


def exported_plugins_excluded(exported: Path):
    """The exported plugin files the project leaves out (with their .meta). Stops on a plugin no rule names:
    a game update or another AssetRipper would bring it into the project unexamined."""
    found = {path.stem for path in (exported / PLUGINS).glob('*.dll')}
    unknown = sorted(found - REPLACED_PLUGINS - DROPPED_PLUGINS)
    if unknown:
        sys.exit(f'Assets/Plugins has assemblies asset_rules.py has no rule for: {", ".join(unknown)}')
    expect(len(found & DROPPED_PLUGINS), len(DROPPED_PLUGINS), 'Unity 6 plugins dropped from Assets/Plugins')
    excluded = set()
    for name in found:
        excluded |= {PLUGINS / f'{name}.dll', PLUGINS / f'{name}.dll.meta'}
    log(f'Plugins: {len(found & DROPPED_PLUGINS)} Unity 6 assemblies dropped, {len(found & REPLACED_PLUGINS)} replaced')
    return excluded


# ---------------------------------------------------------------- settings

# PlayerSettings' serialized version in Unity 2018.2.19f1 (its type tree; the export writes Unity 6's, 28).
# The fields 2018.2 has keep their names, and their values read the same; the editor and the builder
# (tools/builder/convert.py) take the fields they know by name and leave the rest.
PLAYER_SETTINGS_VERSION = '15'
# Player settings whose 2018 defaults differ from the source version's behaviour.
SETTING_RULES = {
    # Unity 2019.3 removed the launch-time resolution dialog; 2018 shows it unless disabled.
    'displayResolutionDialog': '0',
    # The port's test tools read the player log.
    'usePlayerLog': '1',
    # The rewritten assemblies target 2018.2's .NET 4.x class libraries.
    'scriptingRuntimeVersion': '1',
    'apiCompatibilityLevel': '3',  # .NET 4.x
}
# Per platform. The PS Vita player starts in power mode A, with the GPU at 111 MHz; mode C (2) raises the GPU
# clock and gives up the camera and full screen brightness (The Drifter's port). The export has no PS Vita
# save data quota (Unity 6 has no PS Vita); 10 MB, as the earlier ports have.
PLATFORM_SETTING_RULES = {'vita': {'psp2PowerMode': '2', 'psp2SaveDataQuota': '10240'}}
# Quality settings per platform, written into every quality level. The PS Vita: vsync.
PLATFORM_QUALITY_RULES = {'vita': {'vSyncCount': '1'}}

# The export writes the game's productGUID as a mapping of its four 32-bit words, which 2018.2 doesn't read:
# the editor then made a new random one at every build. Written in 2018's form, 32 hex digits, each word's from
# its lowest four bits up.
PRODUCT_GUID = re.compile(r'^  productGUID:\n((?:    data\[\d\]: \d+\n){4})', re.MULTILINE)
SERIALIZED_VERSION = re.compile(r'^(PlayerSettings:\n  serializedVersion: )(\d+)$', re.MULTILINE)


def product_guid(text: str):
    match = PRODUCT_GUID.search(text)
    if not match:
        sys.exit('ProjectSettings has no productGUID in the form the export writes (four data[] words)')
    words = [int(word) for word in re.findall(r': (\d+)', match[1])]
    digits = ''.join(f'{(word >> (4 * i)) & 15:x}' for word in words for i in range(8))
    return text[: match.start()] + f'  productGUID: {digits}\n' + text[match.end() :]


def settings_overlay(exported: Path, platform: str):
    """ProjectSettings at 2018.2's PlayerSettings version with the port's settings, and QualitySettings with
    the platform's."""
    relative = Path('ProjectSettings') / 'ProjectSettings.asset'
    text = (exported / relative).read_text(encoding='utf-8')
    text, count = SERIALIZED_VERSION.subn(rf'\g<1>{PLAYER_SETTINGS_VERSION}', text)
    if count != 1:
        sys.exit('ProjectSettings has no PlayerSettings serializedVersion')
    text = product_guid(text)
    for key, value in {**SETTING_RULES, **PLATFORM_SETTING_RULES.get(platform, {})}.items():
        text, count = re.subn(rf'^(  {key}:).*$', rf'\g<1> {value}', text, flags=re.MULTILINE)
        if count == 0:
            # Keys the source version no longer has; the file holds one PlayerSettings mapping.
            text = text.rstrip('\n') + f'\n  {key}: {value}\n'
        elif count > 1:
            sys.exit(f'ProjectSettings has {count} entries for {key}')
    overlay = {relative: text.encode('utf-8')}
    relative = Path('ProjectSettings') / 'QualitySettings.asset'
    text = (exported / relative).read_text(encoding='utf-8')
    for key, value in PLATFORM_QUALITY_RULES.get(platform, {}).items():
        text, count = re.subn(rf'^(    {key}:).*$', rf'\g<1> {value}', text, flags=re.MULTILINE)
        if count == 0:
            sys.exit(f'QualitySettings has no {key}')
    overlay[relative] = text.encode('utf-8')
    return overlay


# ---------------------------------------------------------------- lighting and Unity 6 managers

# The scene's LightingDataAsset is serializedVersion 4 (Unity 6); 2018.2 reads its own, older layout. The
# game has no lightmaps or light probes (a 2D game drawn from one MonoBehaviour), so the asset is left out
# and the scene's reference to it cleared. MultiplayerManager.asset is a Unity 6 manager 2018.2 doesn't have.
LIGHTING_REFERENCE = re.compile(
    r'^(  m_LightingDataAsset: )\{fileID: \d+, guid: [0-9a-f]{32}, type: \d\}$', re.MULTILINE
)
UNITY6_SETTINGS = [Path('ProjectSettings') / 'MultiplayerManager.asset']


def lighting_overlay(exported: Path):
    """(overlay, excluded): the scenes without a LightingDataAsset, and the assets 2018.2 can't read."""
    overlay, excluded = {}, set()
    assets = exported / 'Assets'
    lighting = []
    for relative, stat in listing(assets).items():
        if stat is None:
            continue
        path = Path('Assets') / relative
        if relative.suffix == '.asset' and b'LightingDataAsset:' in (assets / relative).read_bytes()[:200]:
            text = (assets / relative).read_text(encoding='utf-8')
            if re.search(r'^  m_Lightmaps: \[\]$', text, re.MULTILINE) is None:
                sys.exit(f'{path}: a LightingDataAsset with lightmaps; asset_rules.lighting_overlay drops it')
            lighting.append(path)
            excluded |= {path, meta_of(path)}
        elif relative.suffix == '.unity':
            text = (assets / relative).read_text(encoding='utf-8')
            new, count = LIGHTING_REFERENCE.subn(r'\g<1>{fileID: 0}', text)
            if count:
                overlay[path] = new.encode('utf-8')
    expect(len(lighting), 1, 'LightingDataAssets left out')
    expect(len(overlay), 1, 'scenes whose LightingDataAsset reference is cleared')
    for relative in UNITY6_SETTINGS:
        if (exported / relative).exists():
            excluded.add(relative)
    log(f'Lighting: {len(lighting)} LightingDataAsset left out; {", ".join(map(str, UNITY6_SETTINGS))} left out')
    return overlay, excluded


# ---------------------------------------------------------------- shaders

# AssetRipper exports the game's three shaders (Unlit/CRTShader, Unlit/NormalShader, Unlit/ColorTransformShader)
# as dummies: Unity 6 keeps only their compiled Direct3D 11 programs. Their replacements, written for 2018.2 and
# the PS Vita, are .shader files under tools/downgrade/shaders, matched to the export's by their Shader "name"
# line; each goes over the dummy, keeping its .meta, so the materials keep their references.
SHADERS = HERE / 'shaders'


def shader_overlay(exported: Path):
    replacements = {}
    for path in sorted(SHADERS.rglob('*.shader')) if SHADERS.is_dir() else []:
        match = SHADER_NAME.match(path.read_text(encoding='utf-8-sig'))
        if match:
            replacements[match[1]] = path
    overlay, dummies = {}, []
    for relative, stat in listing(exported / 'Assets').items():
        if stat is None or relative.suffix != '.shader':
            continue
        match = SHADER_NAME.match((exported / 'Assets' / relative).read_text(encoding='utf-8-sig'))
        if not match:
            continue
        if match[1] in replacements:
            overlay[Path('Assets') / relative] = replacements[match[1]]
        else:
            dummies.append(match[1])
    log(
        f'Shaders: {len(overlay)} replaced from {SHADERS.relative_to(HERE.parent.parent)}'
        + (f"; still AssetRipper's dummies: {', '.join(sorted(dummies))}" if dummies else '')
    )
    return overlay
