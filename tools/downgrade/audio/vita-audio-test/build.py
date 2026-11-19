"""Build the PS Vita audio test app (title TDAU00001) and its VPK.

    python -I tools/downgrade/audio/vita-audio-test/build.py [--skip-unity] [--no-game]

Makes a Unity 2018.2.19f1 project in work/vita-audio-test/project (in the checkout whose work/ holds the export,
as audio_rules.py finds it) from unity/ here and the game's clips:

  - Resources/audio: every clip of the export, with the .meta audio_rules.py writes (group "game"); --no-game
    leaves them out for a quick build of the rest
  - Resources/pre, Resources/np: 20 streamed copies of one 10 s effect each, preloadAudioData on and off
  - Resources/probe: that effect in each compression format, mono, at 32 and 22.05 kHz, and an ambience streamed
    in HEVAG, Vorbis, MP3 and ATRAC9
  - Resources/cliplist.txt: group, resource path and kind of each clip, which AudioTest.cs reads

builds the PC Hosted player into work/vita-audio-test/build with the installed editor, adds butcher-vita's built
ButcherClock plugin (the app sets 444/222 MHz with it, as the ports do) and packages
work/vita-audio-test/t2d-audio-test.vpk (eboot.bin is the player with its trial flag cleared, as butcher-vita's
pipeline.py package_vita does). Game content stays under work/.
"""

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import time
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import audio_rules as ar  # noqa: E402

EXPORT, AUDIO_WORK = ar.default_export()
WORK = AUDIO_WORK.parent / 'vita-audio-test'
PROJECT = WORK / 'project'
BUILD = WORK / 'build'
VPK = WORK / 't2d-audio-test.vpk'
UNITY = Path(os.environ['LOCALAPPDATA']) / 'Unity' / 'Editors' / '2018.2.19f1' / 'Editor' / 'Unity.exe'
CLOCK = Path.home() / '.t3' / 'projects' / 'butcher-vita' / 'work' / 'native' / 'clock'
TITLE_ID = 'TDAU00001'
VPK_DATE = (2024, 1, 1, 0, 0, 0)

EFFECT = 'sfx/cutscenes/war_scene.wav'  # 10.2 s, real stereo
AMBIENCE = 'music/ambience/ambience_0.ogg'  # 35 s
COPIES = 20
S = ar.Settings
PROBES = {
    # name: (source, settings); Vorbis and MP3 at quality 0.7
    'fx_pcm': (EFFECT, S(ar.COMPRESSED_IN_MEMORY, ar.PCM, False, None, True)),
    'fx_pcm_decompress': (EFFECT, S(ar.DECOMPRESS_ON_LOAD, ar.PCM, False, None, True)),
    'fx_adpcm': (EFFECT, S(ar.COMPRESSED_IN_MEMORY, ar.ADPCM, False, None, True)),
    'fx_vorbis': (EFFECT, S(ar.COMPRESSED_IN_MEMORY, ar.VORBIS, False, None, True)),
    'fx_mp3': (EFFECT, S(ar.COMPRESSED_IN_MEMORY, ar.MP3, False, None, True)),
    'fx_hevag': (EFFECT, S(ar.COMPRESSED_IN_MEMORY, ar.HEVAG, False, None, True)),
    'fx_hevag_decompress': (EFFECT, S(ar.DECOMPRESS_ON_LOAD, ar.HEVAG, False, None, True)),
    'fx_atrac9': (EFFECT, S(ar.COMPRESSED_IN_MEMORY, ar.ATRAC9, False, None, True)),
    'fx_hevag_mono': (EFFECT, S(ar.COMPRESSED_IN_MEMORY, ar.HEVAG, True, None, True)),
    'fx_hevag_32k': (EFFECT, S(ar.COMPRESSED_IN_MEMORY, ar.HEVAG, False, 32000, True)),
    'fx_hevag_22k': (EFFECT, S(ar.COMPRESSED_IN_MEMORY, ar.HEVAG, False, 22050, True)),
    'amb_hevag': (AMBIENCE, S(ar.STREAMING, ar.HEVAG, False, None, False)),
    'amb_vorbis': (AMBIENCE, S(ar.STREAMING, ar.VORBIS, False, None, False)),
    'amb_mp3': (AMBIENCE, S(ar.STREAMING, ar.MP3, False, None, False)),
    'amb_atrac9': (AMBIENCE, S(ar.STREAMING, ar.ATRAC9, False, None, False)),
}


def write_if_changed(path: Path, data: bytes):
    if path.exists() and path.read_bytes() == data:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    # Windows refuses to overwrite a hidden file (some exported .meta files are), so replace it.
    path.unlink(missing_ok=True)
    path.write_bytes(data)


def copy_clip(source: Path, target: Path, meta_text: str):
    write_if_changed(target, source.read_bytes())
    write_if_changed(target.with_name(target.name + '.meta'), meta_text.encode('utf-8'))


def new_meta(source_meta: str, name: str, settings, quality=0.7):
    """The source's .meta with a GUID of its own, the settings and a Vorbis/MP3 quality."""
    guid = hashlib.md5(f't2d-audio-test/{name}'.encode()).hexdigest()
    text = re.sub(r'^guid: \w+$', f'guid: {guid}', source_meta, count=1, flags=re.MULTILINE)
    text = ar.importer_text(text, settings, name)
    return re.sub(
        r'^(  defaultSettings:\n(?:    .*\n)*?    quality: )[\d.]+',
        rf'\g<1>{quality}',
        text,
        count=1,
        flags=re.MULTILINE,
    )


def make_project(with_game):
    assets = PROJECT / 'Assets'
    write_if_changed(assets / 'AudioTest.cs', (HERE / 'unity' / 'AudioTest.cs').read_bytes())
    write_if_changed(assets / 'link.xml', (HERE / 'unity' / 'link.xml').read_bytes())
    write_if_changed(assets / 'Editor' / 'AudioTestBuild.cs', (HERE / 'unity' / 'AudioTestBuild.cs').read_bytes())
    modules = ['audio', 'imgui', 'jsonserialize']
    manifest = ',\n'.join(f'    "com.unity.modules.{m}": "1.0.0"' for m in modules)
    write_if_changed(
        PROJECT / 'Packages' / 'manifest.json', ('{\n  "dependencies": {\n' + manifest + '\n  }\n}\n').encode()
    )

    resources = assets / 'Resources'
    lines = []
    clips = ar.scan(EXPORT)
    if with_game:
        overlay = ar.audio_overlay(EXPORT, clips)
        for clip in clips:
            relative = ar.AUDIO / clip.path
            meta = overlay.get(relative.with_name(relative.name + '.meta'))
            text = meta.decode('utf-8') if meta else (EXPORT / (str(relative) + '.meta')).read_text(encoding='utf-8')
            copy_clip(EXPORT / relative, PROJECT / relative, text)
            lines.append(f'game\taudio/{clip.path.rsplit(".", 1)[0]}\t{clip.kind}')
    elif (PROJECT / ar.AUDIO).exists():
        shutil.rmtree(PROJECT / ar.AUDIO)
        meta = PROJECT / ar.AUDIO.with_name('audio.meta')
        if meta.exists():
            meta.unlink()
    effect = EXPORT / ar.AUDIO / EFFECT
    effect_meta = (EXPORT / ar.AUDIO / (EFFECT + '.meta')).read_text(encoding='utf-8')
    for group, preload in (('pre', True), ('np', False)):
        for n in range(COPIES):
            name = f'{group}/s{n:02}'
            settings = S(ar.STREAMING, ar.HEVAG, False, None, preload)
            copy_clip(effect, resources / f'{name}.wav', new_meta(effect_meta, name, settings))
            lines.append(f'{group}\t{name}\tsfx')
    for name, (source, settings) in PROBES.items():
        path = EXPORT / ar.AUDIO / source
        meta = (EXPORT / ar.AUDIO / (source + '.meta')).read_text(encoding='utf-8')
        copy_clip(path, resources / 'probe' / f'{name}{path.suffix}', new_meta(meta, f'probe/{name}', settings))
        lines.append(f'probe\tprobe/{name}\tprobe')
    write_if_changed(resources / 'cliplist.txt', ('\n'.join(lines) + '\n').encode())
    print(f'project {PROJECT}: {len(lines)} clips')


def build():
    log = WORK / 'build.log'
    started = time.time()
    result = subprocess.run(
        [
            str(UNITY),
            '-batchmode',
            '-nographics',
            '-buildTarget',
            'PSP2',
            '-projectPath',
            str(PROJECT),
            '-executeMethod',
            'AudioTestBuild.Build',
            '-buildOutput',
            str(BUILD),
            '-vitaTitleId',
            TITLE_ID,
            '-logFile',
            str(log),
        ],
        check=False,
    )
    lines = log.read_text(encoding='utf-8', errors='replace').splitlines() if log.exists() else []
    for line in lines:
        if line.startswith(('AUDIOTEST_', 'error CS')) or 'Exception' in line[:80]:
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
            # The audio is already compressed (HEVAG); storing it saves the console's unzip time.
            info.compress_type = zipfile.ZIP_STORED if path.suffix == '.resource' else zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, path.read_bytes())
    print(f'packaged {VPK} ({VPK.stat().st_size / 2**20:.0f} MB)')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--skip-unity', action='store_true', help='only package the existing build')
    parser.add_argument('--no-game', action='store_true', help="leave the game's 930 clips out")
    args = parser.parse_args()
    if not args.skip_unity:
        make_project(not args.no_game)
        build()
    package()


if __name__ == '__main__':
    main()
