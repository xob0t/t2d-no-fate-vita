"""Assemble the Unity 2018.2 probe project from an AssetRipper export (2018.2 target) and the probe DLLs.

    python -I tools/assess/bringup/assemble.py <ExportedProject> <emit dir> <empty 2018.2 project> <project>
        [--probe-scripts <dir>]

- Assets: the export's, copied when size or time differ, minus the Unity 6-only engine modules,
  System.Runtime.CompilerServices.Unsafe.dll and __Generated.dll in Assets/Plugins. The game DLLs
  (Assembly-CSharp, InControl, Steamworks) are replaced by <emit dir>'s, keeping their .meta (GUIDs, so
  the scene's script references hold); Compat2018.dll is added with a fixed GUID.
- ProjectSettings: the export's ProjectSettings.asset is serializedVersion 28 (Unity 6), so the empty
  2018.2 project's is the base, with a few of the game's values and the probe's (product name
  T2DProbe so PlayerPrefs and saves don't touch the real game's; windowed, small, run in background;
  .NET 4.x). MultiplayerManager.asset, VFXManager.asset and RuntimeInitializeOnLoadManager.asset (the
  editor writes its own) are left out; TimeManager's Fixed Timestep,
  which the export writes as 0, is 2018.2's default. The rest of ProjectSettings is the export's.
- --probe-scripts: a folder of .cs copied into Assets/Probe (runtime) and Assets/Editor (names ending
  in Build.cs).
"""

import argparse
import hashlib
import re
import shutil
import sys
from pathlib import Path

DROP_PLUGINS = {
    'UnityEngine.AndroidJNIModule.dll', 'UnityEngine.HierarchyCoreModule.dll', 'UnityEngine.InputForUIModule.dll',
    'UnityEngine.InputLegacyModule.dll', 'UnityEngine.PropertiesModule.dll', 'UnityEngine.SubsystemsModule.dll',
    'UnityEngine.TextCoreFontEngineModule.dll', 'UnityEngine.TextCoreTextEngineModule.dll',
    'UnityEngine.UnityAnalyticsCommonModule.dll', 'UnityEngine.VFXModule.dll',
    'System.Runtime.CompilerServices.Unsafe.dll', '__Generated.dll',
}
GAME_DLLS = ('Assembly-CSharp.dll', 'InControl.dll', 'Steamworks.dll')
DROP_SETTINGS = {'MultiplayerManager.asset', 'VFXManager.asset', 'ProjectSettings.asset', 'TimeManager.asset',
                 'RuntimeInitializeOnLoadManager.asset'}

PLAYER_SETTINGS = {
    'companyName': 'probe',
    'productName': 'T2DProbe',
    'defaultScreenWidth': '480',
    'defaultScreenHeight': '270',
    'runInBackground': '1',
    'visibleInBackground': '1',
    'resizableWindow': '1',
    'fullscreenMode': '3',
    'defaultIsNativeResolution': '0',
    'displayResolutionDialog': '0',
    'forceSingleInstance': '0',
    'usePlayerLog': '1',
    'm_ShowUnitySplashScreen': '0',
    'm_ShowUnitySplashLogo': '0',
    'm_ActiveColorSpace': '0',
    'scriptingRuntimeVersion': '1',
    'apiCompatibilityLevel': '3',
    'allowUnsafeCode': '1',
}


def guid(name):
    return hashlib.md5(('t2d-probe:' + name).encode()).hexdigest()


def plugin_meta(name):
    return (f'fileFormatVersion: 2\nguid: {guid(name)}\nPluginImporter:\n  externalObjects: {{}}\n'
            '  serializedVersion: 2\n  iconMap: {}\n  executionOrder: {}\n  isPreloaded: 0\n  isOverridable: 0\n'
            '  platformData:\n  - first:\n      Any: \n    second:\n      enabled: 1\n      settings: {}\n'
            '  userData: \n  assetBundleName: \n  assetBundleVariant: \n')


def script_meta(name):
    return (f'fileFormatVersion: 2\nguid: {guid(name)}\nMonoImporter:\n  externalObjects: {{}}\n'
            '  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n'
            '  userData: \n  assetBundleName: \n  assetBundleVariant: \n')


def copy_if_changed(src: Path, dst: Path):
    if dst.exists():
        a, b = src.stat(), dst.stat()
        if a.st_size == b.st_size and int(a.st_mtime) == int(b.st_mtime):
            return False
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)
    return True


def write_if_changed(dst: Path, text: str):
    data = text.encode('utf-8')
    if dst.exists() and dst.read_bytes() == data:
        return False
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_bytes(data)
    return True


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('export', type=Path)
    ap.add_argument('emit', type=Path)
    ap.add_argument('empty', type=Path)
    ap.add_argument('project', type=Path)
    ap.add_argument('--probe-scripts', type=Path)
    a = ap.parse_args()

    copied = 0
    plugins = Path('Assets/Plugins')
    for src in (a.export / 'Assets').rglob('*'):
        if src.is_dir():
            continue
        rel = src.relative_to(a.export)
        if rel.parent == plugins and (rel.name in DROP_PLUGINS or rel.name.removesuffix('.meta') in DROP_PLUGINS):
            continue
        if rel.parent == plugins and rel.name in GAME_DLLS:
            continue  # from emit, below
        copied += copy_if_changed(src, a.project / rel)
    for name in DROP_PLUGINS:
        for p in (a.project / plugins / name, a.project / plugins / (name + '.meta')):
            p.unlink(missing_ok=True)
    for name in GAME_DLLS:
        copied += copy_if_changed(a.emit / name, a.project / plugins / name)
    copied += copy_if_changed(a.emit / 'Compat2018.dll', a.project / plugins / 'Compat2018.dll')
    write_if_changed(a.project / plugins / 'Compat2018.dll.meta', plugin_meta('Compat2018.dll'))

    if a.probe_scripts:
        for src in sorted(a.probe_scripts.glob('*.cs')):
            folder = 'Editor' if src.name.endswith('Build.cs') else 'Probe'
            dst = a.project / 'Assets' / folder / src.name
            copied += copy_if_changed(src, dst)
            write_if_changed(dst.with_name(dst.name + '.meta'), script_meta(folder + '/' + src.name))
        # The runtime scripts get an assembly of their own: in the default Assembly-CSharp they would
        # clash with the game's precompiled Assembly-CSharp.dll (CS1704).
        asmdef = a.project / 'Assets' / 'Probe' / 'T2DProbe.asmdef'
        write_if_changed(asmdef, '{\n    "name": "T2DProbe"\n}\n')
        write_if_changed(asmdef.with_name('T2DProbe.asmdef.meta'),
                         f'fileFormatVersion: 2\nguid: {guid("Probe/T2DProbe.asmdef")}\nAssemblyDefinitionImporter:\n'
                         '  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n')

    settings = a.project / 'ProjectSettings'
    settings.mkdir(parents=True, exist_ok=True)
    for src in (a.export / 'ProjectSettings').iterdir():
        if src.name not in DROP_SETTINGS:
            copy_if_changed(src, settings / src.name)
    for name in ('TimeManager.asset', 'PresetManager.asset', 'ClusterInputManager.asset', 'UnityConnectSettings.asset'):
        if (a.empty / 'ProjectSettings' / name).exists():
            copy_if_changed(a.empty / 'ProjectSettings' / name, settings / name)
    text = (a.empty / 'ProjectSettings' / 'ProjectSettings.asset').read_text(encoding='utf-8')
    for key, value in PLAYER_SETTINGS.items():
        text, n = re.subn(r'(?m)^(  %s:) .*$' % re.escape(key), lambda m: m.group(1) + ' ' + value, text)
        if n != 1:
            sys.exit(f'ProjectSettings.asset: {key} found {n} times')
    write_if_changed(settings / 'ProjectSettings.asset', text)
    (settings / 'ProjectVersion.txt').write_text('m_EditorVersion: 2018.2.19f1\n', encoding='utf-8')
    copy_if_changed(a.export / 'Packages' / 'manifest.json', a.project / 'Packages' / 'manifest.json')
    print(f'{a.project}: {copied} files copied')


if __name__ == '__main__':
    main()
