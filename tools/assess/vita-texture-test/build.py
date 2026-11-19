"""Build the PS Vita texture test app (title TDTX00001) and its VPK.

    python -I tools/assess/vita-texture-test/build.py [--skip-unity]

Makes a Unity 2018.2.19f1 project in work/vita-texture-test/project from unity/ here and the game's atlases
(work/assetripper/export-stubs/.../Resources: the 12 TexturePack PNGs as the game ships them, and copies of
TexturePack0 that TexTestBuild.cs imports in each PS Vita format), builds the PC Hosted player into
work/vita-texture-test/build with the installed editor, adds butcher-vita's built ButcherClock plugin (a copy;
the app sets 444/222 MHz with it as the ports do) and packages work/vita-texture-test/t2d-texture-test.vpk:
eboot.bin is the player with its trial flag cleared (as butcher-vita's pipeline.py package_vita does).
Game content stays under work/.
"""

import argparse
import os
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
WORK = ROOT / 'work' / 'vita-texture-test'
PROJECT = WORK / 'project'
BUILD = WORK / 'build'
VPK = WORK / 't2d-texture-test.vpk'
EXPORT = ROOT / 'work' / 'assetripper' / 'export-stubs' / 'ExportedProject' / 'Assets' / 'Resources'
UNITY = Path(os.environ['LOCALAPPDATA']) / 'Unity' / 'Editors' / '2018.2.19f1' / 'Editor' / 'Unity.exe'
CLOCK = Path.home() / '.t3' / 'projects' / 'butcher-vita' / 'work' / 'native' / 'clock'
TITLE_ID = 'TDTX00001'
# TexturePack0 in each format (TexTestBuild.FormatOf reads the name; _2048 halves it at import).
VARIANTS = ['rgba32_4096', 'rgba32_2048', 'rgba4444_4096', 'dxt5_4096', 'dxt5_2048', 'pvrtc4_4096', 'pvrtc2_4096']
VPK_DATE = (2024, 1, 1, 0, 0, 0)


def write_if_changed(path: Path, data: bytes):
    if path.exists() and path.read_bytes() == data:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def make_project():
    assets = PROJECT / 'Assets'
    write_if_changed(assets / 'TexTest.cs', (HERE / 'unity' / 'TexTest.cs').read_bytes())
    write_if_changed(assets / 'link.xml', (HERE / 'unity' / 'link.xml').read_bytes())
    write_if_changed(assets / 'Editor' / 'TexTestBuild.cs', (HERE / 'unity' / 'TexTestBuild.cs').read_bytes())
    # Only built-in modules: the editor would fetch the default packages over the network.
    modules = ['imageconversion', 'imgui', 'jsonserialize', 'audio']
    manifest = ',\n'.join(f'    "com.unity.modules.{m}": "1.0.0"' for m in modules)
    write_if_changed(PROJECT / 'Packages' / 'manifest.json', ('{\n  "dependencies": {\n' + manifest + '\n  }\n}\n').encode())
    for n in range(12):
        source = EXPORT / 'texturepack' / f'TexturePack{n}.bytes'
        write_if_changed(assets / 'Resources' / 'texturepack' / source.name, source.read_bytes())
    pack0 = (EXPORT / 'texturepack' / 'TexturePack0.bytes').read_bytes()
    for variant in VARIANTS:
        write_if_changed(assets / 'Resources' / 'tex' / f'tp0_{variant}.png', pack0)
    print(f'project {PROJECT}')


def build():
    log = WORK / 'build.log'
    started = time.time()
    result = subprocess.run(
        [str(UNITY), '-batchmode', '-nographics', '-buildTarget', 'PSP2', '-projectPath', str(PROJECT),
         '-executeMethod', 'TexTestBuild.Build', '-buildOutput', str(BUILD), '-vitaTitleId', TITLE_ID, '-logFile', str(log)],
        check=False,
    )
    lines = log.read_text(encoding='utf-8', errors='replace').splitlines() if log.exists() else []
    for line in lines:
        if line.startswith(('TEXTEST_', 'error CS')) or 'Exception' in line[:80]:
            print(line)
    print(f'Unity exited {result.returncode} after {time.time() - started:.0f}s; log {log}')
    if result.returncode != 0 or not any(BUILD.glob('*.self')):
        sys.exit('build failed')
    plugins = BUILD / 'Media' / 'Plugins'
    plugins.mkdir(parents=True, exist_ok=True)
    for name in ('ButcherClock.suprx', 'ButcherClock.suprx.b.suprx'):
        if (CLOCK / name).exists():
            shutil.copy2(CLOCK / name, plugins / name)
        else:
            print(f'note: no {CLOCK / name}; the app runs at the default clocks')


def package():
    selfs = list(BUILD.glob('*.self'))
    if len(selfs) != 1:
        sys.exit(f'expected one .self in {BUILD}, found {selfs}')
    player = bytearray(selfs[0].read_bytes())
    player[0x80] = 0  # trial flag
    marker = player.find(b'trial.png')
    if marker >= 0:
        player[marker] = 0  # no trial watermark
    skip = {'SymbolFiles', 'configuration.psp2path'}
    with zipfile.ZipFile(VPK, 'w', zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(zipfile.ZipInfo('eboot.bin', date_time=VPK_DATE), bytes(player))
        for path in sorted(BUILD.rglob('*')):
            relative = path.relative_to(BUILD)
            if path.is_dir() or relative.parts[0] in skip or path.suffix in ('.self', '.bat'):
                continue
            info = zipfile.ZipInfo(relative.as_posix(), date_time=VPK_DATE)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, path.read_bytes())
    print(f'packaged {VPK} ({VPK.stat().st_size / 2**20:.0f} MB)')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--skip-unity', action='store_true', help='only package the existing build')
    args = parser.parse_args()
    if not args.skip_unity:
        make_project()
        build()
    package()


if __name__ == '__main__':
    main()
