"""Reference clips from Unity 2018.2.19f1's PS Vita audio importer, for tools/builder/audio.py's mono and rate code.

    uv run tools/downgrade/audio/importer-reference/make.py [--skip-unity]

Writes a project into work/audio-importer-reference/project with synthetic clips only (noise, sweeps, impulses,
tones; nothing of the game), each with an AudioImporter .meta asking for one combination of format (PCM or HEVAG),
forceToMono, normalize and sample rate. The installed editor (%LOCALAPPDATA%/Unity/Editors/2018.2.19f1, with its
PS Vita module) imports them in batch mode and puts them into one uncompressed PS Vita asset bundle
(ImporterReference.cs), and this script reads the bundle back into work/audio-importer-reference/reference.pkl:
per clip its source WAV, its settings, the AudioClip's fields and its FSB5 bank.

The PCM clips are the point: their banks hold the importer's converted samples exactly, before any encoder, so
the builder's downmix and resampler can be checked sample for sample (tools/builder/check_audio.py --compare).
The HEVAG clips show the same conversion feeding the encoder (frame count, m_Length).

Running the editor needs its license, as for the audio test; nothing here touches it.
"""

import argparse
import hashlib
import io
import os
import pickle
import subprocess
import sys
import time
import wave
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
WORK = ROOT / 'work' / 'audio-importer-reference'
PROJECT = WORK / 'project'
BUNDLES = WORK / 'bundles'
REFERENCE = WORK / 'reference.pkl'
UNITY = Path(os.environ.get('LOCALAPPDATA', '')) / 'Unity' / 'Editors' / '2018.2.19f1' / 'Editor' / 'Unity.exe'

PCM, HEVAG = 0, 5
COMPRESSED_IN_MEMORY = 1


def signals():
    """{name: (rate, (channels, n) int16)}: deterministic test signals."""
    rng = np.random.default_rng(2018)
    out = {}

    def stereo(left, right):
        return np.clip(np.round(np.stack([left, right])), -32768, 32767).astype(np.int16)

    n = 44100 + 13
    out['noise'] = 44100, stereo(rng.normal(0, 6000, n), rng.normal(0, 6000, n))
    t = np.arange(66150) / 44100
    sweep = np.sin(2 * np.pi * 20 * (1102.5 ** (t / t[-1]) - 1) * t[-1] / np.log(1102.5))
    out['sweep'] = 44100, stereo(12000 * sweep, -6000 * sweep + rng.normal(0, 300, len(t)))
    imp = np.zeros((2, 30011))
    imp[0, 500::997] = 26000
    imp[1, 503::997] = -13000
    out['impulse'] = 44100, stereo(imp[0], imp[1])
    t = np.arange(40000) / 44100
    out['tones'] = 44100, stereo(9000 * np.sin(2 * np.pi * 1000 * t), 9000 * np.sin(2 * np.pi * 2500 * t))
    # Both channels near full scale and in phase: their sum leaves the 16-bit range.
    loud = np.clip(rng.normal(0, 20000, 22050), -32767, 32767)
    out['loud'] = 44100, stereo(loud, 0.9 * loud)
    out['short'] = 44100, stereo(rng.normal(0, 6000, 101), rng.normal(0, 6000, 101))
    out['noise22m'] = 22050, np.clip(np.round(rng.normal(0, 6000, (1, 22050 + 7))), -32768, 32767).astype(np.int16)
    out['noise48'] = 48000, stereo(rng.normal(0, 6000, 48000 + 5), rng.normal(0, 6000, 48000 + 5))
    out['noise11'] = 11025, stereo(rng.normal(0, 6000, 11025 + 3), rng.normal(0, 6000, 11025 + 3))
    return out


# (signal, format, mono, normalize, rate or None to keep the source's)
PROBES = [
    *(
        (s, f, m, 0, 32000)
        for s in ('noise', 'sweep', 'impulse', 'tones', 'loud', 'short')
        for f in (PCM, HEVAG)
        for m in (0, 1)
    ),
    *((s, PCM, 1, 0, None) for s in ('noise', 'sweep', 'impulse', 'tones', 'loud')),
    ('noise', PCM, 0, 0, 22050),
    ('noise', PCM, 1, 0, 22050),
    ('noise', PCM, 0, 0, 48000),
    ('noise', PCM, 0, 0, 11025),
    ('noise', PCM, 1, 1, 32000),
    ('loud', PCM, 1, 1, None),
    ('noise', HEVAG, 1, 0, None),
    ('noise22m', PCM, 0, 0, 32000),
    ('noise22m', PCM, 0, 0, 44100),
    ('noise22m', PCM, 1, 0, 32000),
    ('noise48', PCM, 0, 0, 44100),
    ('noise48', PCM, 1, 0, 32000),
    ('noise11', PCM, 0, 0, 32000),
]


def probe_name(signal, fmt, mono, normalize, rate):
    kind = 'pcm' if fmt == PCM else 'hevag'
    return f'{signal}_{kind}{"_mono" if mono else ""}{"_norm" if normalize else ""}_{rate or "src"}'


META = """fileFormatVersion: 2
guid: {guid}
AudioImporter:
  serializedVersion: 6
  defaultSettings:
    loadType: {load}
    sampleRateSetting: {rate_setting}
    sampleRateOverride: {rate}
    compressionFormat: {fmt}
    quality: 1
    conversionMode: 0
  platformSettingOverrides: {{}}
  forceToMono: {mono}
  normalize: {normalize}
  preloadAudioData: 1
  loadInBackground: 0
  ambisonic: 0
  3D: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def wav_bytes(rate, pcm):
    out = io.BytesIO()
    with wave.open(out, 'wb') as w:
        w.setnchannels(pcm.shape[0])
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(pcm.T.astype('<i2').tobytes())
    return out.getvalue()


def write_if_changed(path: Path, data: bytes):
    if path.exists() and path.read_bytes() == data:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def make_project():
    sources = signals()
    probes = {}
    for signal, fmt, mono, normalize, rate in PROBES:
        name = probe_name(signal, fmt, mono, normalize, rate)
        source_rate, pcm = sources[signal]
        meta = META.format(
            guid=hashlib.md5(f't2d-importer-reference/{name}'.encode()).hexdigest(),
            load=COMPRESSED_IN_MEMORY,
            rate_setting=2 if rate else 0,
            rate=rate or 44100,
            fmt=fmt,
            mono=mono,
            normalize=normalize,
        )
        wav = wav_bytes(source_rate, pcm)
        write_if_changed(PROJECT / 'Assets' / 'probe' / f'{name}.wav', wav)
        write_if_changed(PROJECT / 'Assets' / 'probe' / f'{name}.wav.meta', meta.encode())
        probes[name] = {'signal': signal, 'wav': wav, 'meta': meta}
    write_if_changed(
        PROJECT / 'Assets' / 'Editor' / 'ImporterReference.cs', (HERE / 'ImporterReference.cs').read_bytes()
    )
    manifest = '{\n  "dependencies": {\n    "com.unity.modules.audio": "1.0.0"\n  }\n}\n'
    write_if_changed(PROJECT / 'Packages' / 'manifest.json', manifest.encode())
    return probes


def build():
    log = WORK / 'unity.log'
    started = time.time()
    command = [
        str(UNITY),
        '-batchmode',
        '-nographics',
        '-buildTarget',
        'PSP2',
        '-projectPath',
        str(PROJECT),
        '-executeMethod',
        'ImporterReference.Build',
        '-bundleOutput',
        str(BUNDLES),
        '-logFile',
        str(log),
    ]
    result = subprocess.run(command, check=False)
    lines = log.read_text(encoding='utf-8', errors='replace').splitlines() if log.exists() else []
    for line in lines:
        if line.startswith(('IMPORTREF_', 'error CS')) or 'Exception' in line[:80]:
            print(line)
    print(f'Unity exited {result.returncode} after {time.time() - started:.0f} s; log {log}')
    if result.returncode != 0:
        sys.exit('the reference build failed')


def read_bundle(probes):
    import UnityPy

    env = UnityPy.load(str(BUNDLES / 'probes'))
    inner = {key: f for bundle in env.files.values() for key, f in bundle.files.items()}
    found = 0
    for obj in env.objects:
        if obj.type.name != 'AudioClip':
            continue
        tree = obj.read_typetree()
        name = tree['m_Name']
        resource = tree['m_Resource']
        stream = inner.get(resource['m_Source'].rsplit('/', 1)[-1])
        if stream is None:
            sys.exit(f'{name}: no {resource["m_Source"]} in the bundle')
        data = stream.bytes
        probes[name]['fields'] = tree
        probes[name]['bank'] = bytes(data[resource['m_Offset'] : resource['m_Offset'] + resource['m_Size']])
        found += 1
    missing = [n for n, p in probes.items() if 'bank' not in p]
    if missing:
        sys.exit(f'clips missing from the bundle: {missing}')
    print(f'{found} clips read from {BUNDLES / "probes"}')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--skip-unity', action='store_true', help='read the bundle the last run built')
    args = parser.parse_args()
    probes = make_project()
    print(f'{len(probes)} probes in {PROJECT}')
    if not args.skip_unity:
        if not UNITY.exists():
            sys.exit(f'no editor at {UNITY}')
        build()
    read_bundle(probes)
    REFERENCE.write_bytes(pickle.dumps(probes))
    print(f'wrote {REFERENCE}')


if __name__ == '__main__':
    main()
