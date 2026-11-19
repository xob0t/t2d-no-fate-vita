"""Build the PS Vita mesh test app (title TDMS00001) and its VPK.

    python -I tools/assess/vita-mesh-test/build.py [--skip-unity] [--dynamic-batching 0|1]

Makes a Unity 2018.2.19f1 project in work/vita-mesh-test/project from unity/ here (no game content: the sprites and
atlases are synthetic), builds the PC Hosted player into work/vita-mesh-test/build, adds butcher-vita's ButcherClock
plugin and packages work/vita-mesh-test/t2d-mesh-test.vpk. The packaging and the clock plugin come from the texture
test's build.py (tools/assess/vita-texture-test), run with this test's paths.
"""

import argparse
import importlib.util
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
WORK = ROOT / 'work' / 'vita-mesh-test'
PROJECT = WORK / 'project'
BUILD = WORK / 'build'
VPK = WORK / 't2d-mesh-test.vpk'
TITLE_ID = 'TDMS00001'


def texture_build():
    spec = importlib.util.spec_from_file_location('vt_build', HERE.parent / 'vita-texture-test' / 'build.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.WORK, module.PROJECT, module.BUILD, module.VPK, module.TITLE_ID = WORK, PROJECT, BUILD, VPK, TITLE_ID
    return module


def make_project(tb):
    assets = PROJECT / 'Assets'
    tb.write_if_changed(assets / 'MeshTest.cs', (HERE / 'unity' / 'MeshTest.cs').read_bytes())
    tb.write_if_changed(assets / 'MeshTestSprite.shader', (HERE / 'unity' / 'MeshTestSprite.shader').read_bytes())
    tb.write_if_changed(assets / 'link.xml', (HERE / 'unity' / 'link.xml').read_bytes())
    tb.write_if_changed(assets / 'Editor' / 'MeshTestBuild.cs', (HERE / 'unity' / 'MeshTestBuild.cs').read_bytes())
    (assets / 'Resources').mkdir(parents=True, exist_ok=True)
    modules = ['imageconversion', 'imgui', 'jsonserialize']
    manifest = ',\n'.join(f'    "com.unity.modules.{m}": "1.0.0"' for m in modules)
    tb.write_if_changed(PROJECT / 'Packages' / 'manifest.json', ('{\n  "dependencies": {\n' + manifest + '\n  }\n}\n').encode())
    print(f'project {PROJECT}')


def build(tb, dynamic_batching=None):
    log = WORK / 'build.log'
    started = time.time()
    result = subprocess.run(
        [str(tb.UNITY), '-batchmode', '-nographics', '-buildTarget', 'PSP2', '-projectPath', str(PROJECT),
         '-executeMethod', 'MeshTestBuild.Build', '-buildOutput', str(BUILD), '-vitaTitleId', TITLE_ID, '-logFile', str(log),
         *(['-dynamicBatching', str(dynamic_batching)] if dynamic_batching is not None else [])],
        check=False,
    )
    lines = log.read_text(encoding='utf-8', errors='replace').splitlines() if log.exists() else []
    for line in lines:
        if line.startswith(('MESHTEST_', 'error CS')) or 'Exception' in line[:80] or 'Shader error' in line:
            print(line)
    print(f'Unity exited {result.returncode} after {time.time() - started:.0f}s; log {log}')
    if result.returncode != 0 or not any(BUILD.glob('*.self')):
        sys.exit('build failed')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--skip-unity', action='store_true', help='only package the existing build')
    parser.add_argument('--dynamic-batching', type=int, choices=(0, 1), help="set the PS Vita player's dynamic batching")
    args = parser.parse_args()
    tb = texture_build()
    if not args.skip_unity:
        WORK.mkdir(parents=True, exist_ok=True)
        make_project(tb)
        build(tb, args.dynamic_batching)
        plugins = BUILD / 'Media' / 'Plugins'
        plugins.mkdir(parents=True, exist_ok=True)
        for name in ('ButcherClock.suprx', 'ButcherClock.suprx.b.suprx'):
            source = tb.CLOCK / name
            if source.exists():
                (plugins / name).write_bytes(source.read_bytes())
            else:
                print(f'note: no {source}; the app runs at the default clocks')
    tb.package()


if __name__ == '__main__':
    main()
